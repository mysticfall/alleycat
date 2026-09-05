using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Testing;

/// <summary>
/// Host-driven integration-test command runner supporting a one-shot dynamic assembly loading probe
/// (<c>--integration-probe</c>) and a persistent stdin/stdout command session (<c>--integration-test-session</c>).
/// </summary>
public partial class TestRuntimeRunner : Node
{
    private const string ProbeCommandArg = "--integration-probe";
    private const string SessionCommandArg = "--integration-test-session";
    private const string ProbeAssemblyArg = "--probe-assembly";
    private const string ProbeTypeArg = "--probe-type";
    private const string ProbeReadyPropertyName = "ReadyRan";
    private const string SuccessMarker = "ALLEYCAT_INTEGRATION_PROBE_SUCCESS";
    private const string FailureMarker = "ALLEYCAT_INTEGRATION_PROBE_FAILURE";
    private const string SessionLinePrefix = "ALLEYCAT_INTEGRATION_SESSION:";
    private const string GlobalAutoloadName = "Global";
    private const string GlobalAutoloadSettingPath = "autoload/Global";
    private const string MainSceneSettingPath = "application/run/main_scene";
    private const int ProbeReadyFrameLimit = 5;
    private const int SessionFrameSettleCount = 2;
    private const int SessionStartupSceneFrameLimit = 10;
    private const int SessionMalformedLinePreviewLength = 200;
    private const string SessionReadyEventKind = "ready";
    private const string SessionResultEventKind = "result";
    private const string SessionShutdownCompleteEventKind = "shutdown-complete";
    private const string SessionPassedOutcome = "passed";
    private const string SessionFailedOutcome = "failed";
    private const string SessionErrorOutcome = "error";

    private static readonly JsonSerializerOptions _sessionResultPayloadOptions = new();
    private static readonly JsonSerializerOptions _sessionCompactPayloadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lock _dispatchSync = new();
    private static TestRuntimeRunner? _activeRunner;

    private volatile bool _sessionShutdownRequested;

    /// <summary>
    /// Gets the managed thread ID that executed the active test runner's Godot <see cref="_Ready"/> callback.
    /// </summary>
    public static int? MainThreadId
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets whether the current managed thread is the thread that executed the active runner's Godot callbacks.
    /// </summary>
    public static bool IsOnMainThread => MainThreadId == System.Environment.CurrentManagedThreadId;

    /// <summary>
    /// Starts the probe or session only when the matching command-line arguments are provided.
    /// </summary>
    public override void _Ready()
    {
        lock (_dispatchSync)
        {
            _activeRunner = this;
            MainThreadId = System.Environment.CurrentManagedThreadId;
        }

        _ = RunCommandIfRequestedAsync();
    }

    /// <summary>
    /// Runs the provided action through Godot's deferred-call queue when the caller is not already on the runner thread.
    /// </summary>
    /// <param name="action">The action to execute on the Godot runner thread.</param>
    /// <param name="forceDeferred">Whether to enqueue the action even when already on the runner thread.</param>
    /// <returns>A task that completes when the action has run.</returns>
    public static Task RunOnMainThreadAsync(Action action, bool forceDeferred = false)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsOnMainThread && !forceDeferred)
        {
            action();
            return Task.CompletedTask;
        }

        TestRuntimeRunner runner;
        lock (_dispatchSync)
        {
            runner = _activeRunner
                ?? throw new InvalidOperationException("Godot test main-thread dispatcher is not available before TestRuntimeRunner._Ready().");
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var callable = Callable.From(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        _ = runner.CallDeferred(nameof(InvokeDeferredCallback), callable);
        return completion.Task;
    }

    /// <summary>
    /// Runs the provided asynchronous action through Godot's deferred-call queue when the caller is not already on the
    /// runner thread, completing only once the asynchronous action has finished.
    /// </summary>
    /// <param name="asyncAction">The asynchronous action to execute on the Godot runner thread.</param>
    /// <param name="forceDeferred">Whether to enqueue the action even when already on the runner thread.</param>
    /// <returns>A task that completes when the asynchronous action has run to completion.</returns>
    public static Task RunOnMainThreadAsync(Func<Task> asyncAction, bool forceDeferred = false)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);

        if (IsOnMainThread && !forceDeferred)
        {
            return asyncAction();
        }

        TestRuntimeRunner runner;
        lock (_dispatchSync)
        {
            runner = _activeRunner
                ?? throw new InvalidOperationException("Godot test main-thread dispatcher is not available before TestRuntimeRunner._Ready().");
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The lambda must be void-returning (Action); an expression body would bind to
        // Callable.From(Func<Task>) and Godot would fail to convert the returned Task to a Variant.
        var callable = Callable.From(() =>
        {
            _ = CompleteDispatchedAsyncActionAsync(asyncAction, completion);
        });

        _ = runner.CallDeferred(nameof(InvokeDeferredCallback), callable);
        return completion.Task;
    }

    private static async Task CompleteDispatchedAsyncActionAsync(Func<Task> asyncAction, TaskCompletionSource completion)
    {
        try
        {
            await asyncAction();
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    /// <summary>
    /// Invokes a deferred C# callback scheduled by <see cref="RunOnMainThreadAsync(Action, bool)"/>.
    /// </summary>
    public void InvokeDeferredCallback(Callable callable)
    {
        if (!IsInsideTree())
        {
            throw new InvalidOperationException("Deferred test callback reached a runner outside the SceneTree.");
        }

        _ = callable.Call();
    }

    private async Task RunCommandIfRequestedAsync()
    {
        string[] args = OS.GetCmdlineUserArgs();

        if (args.Contains(SessionCommandArg, StringComparer.Ordinal))
        {
            await RunSessionAsync(args);
            return;
        }

        if (!args.Contains(ProbeCommandArg, StringComparer.Ordinal))
        {
            return;
        }

        await RunProbeIfRequestedAsync(args);
    }

    /// <summary>
    /// Loads the integration test assembly once, validates it, captures the startup baseline, emits the
    /// <c>ready</c> protocol line, and serves stdin commands until shutdown or host disconnect.
    /// </summary>
    private async Task RunSessionAsync(IReadOnlyList<string> args)
    {
        string? assemblyPath = GetArgumentValue(args, ProbeAssemblyArg);

        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            EmitSessionStartupFailure("Session argument --probe-assembly was missing or empty.");
            return;
        }

        if (!TryLoadTestAssembly(assemblyPath, out Assembly? testAssembly, out string? loadError))
        {
            EmitSessionStartupFailure(loadError);
            return;
        }

        if (!TryValidateAssemblyTypes(testAssembly, out string? validationError))
        {
            EmitSessionStartupFailure(validationError);
            return;
        }

        SessionBaseline baseline = await CaptureSessionBaselineAsync();

        EmitSessionLine(
            new SessionReadyPayload(SessionCommandParser.ProtocolVersion, SessionReadyEventKind),
            _sessionCompactPayloadOptions);

        await RunSessionCommandLoopAsync(testAssembly, baseline);
    }

    private static bool TryLoadTestAssembly(
        string assemblyPath,
        [NotNullWhen(true)] out Assembly? testAssembly,
        [NotNullWhen(false)] out string? error)
    {
        testAssembly = null;
        error = null;

        var godotLoadContext = AssemblyLoadContext.GetLoadContext(typeof(Node).Assembly);
        if (godotLoadContext is null)
        {
            error = "Could not resolve Godot assembly load context.";
            return false;
        }

        if (!TryGetLoadTarget(assemblyPath, out string loadTarget, out AssemblyDependencyResolver? dependencyResolver))
        {
            error = $"Test assembly path was missing or invalid: '{assemblyPath}'.";
            return false;
        }

        EnsureRuntimeAssemblyReferencesLoaded(typeof(Game).Assembly);

        AssemblyDependencyResolver resolver = dependencyResolver!;
        godotLoadContext.Resolving += (context, assemblyName) => ResolveTestAssemblyDependency(context, assemblyName, resolver);

        try
        {
            testAssembly = godotLoadContext.LoadFromAssemblyPath(loadTarget);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Loading integration test assembly '{loadTarget}' failed: {ex.Message}";
            return false;
        }
    }

    private static bool TryValidateAssemblyTypes(Assembly testAssembly, [NotNullWhen(false)] out string? error)
    {
        try
        {
            _ = testAssembly.GetTypes();
            error = null;
            return true;
        }
        catch (ReflectionTypeLoadException ex)
        {
            error = $"Integration test assembly types failed to load: {ex.Message} "
                + $"({ex.LoaderExceptions.Length} loader exception(s); first: {ex.LoaderExceptions.FirstOrDefault()?.Message})";
            return false;
        }
    }

    /// <summary>
    /// Captures the fresh-process root arrangement the session restores after every test: engine time scale, the
    /// configured main scene path, and the startup-owned root children.
    /// </summary>
    private async Task<SessionBaseline> CaptureSessionBaselineAsync()
    {
        SceneTree tree = GetTree();

        // Autoload _Ready runs before the main scene is attached, so wait briefly for the startup scene arrangement
        // to match a settled fresh process before snapshotting it.
        for (int frame = 0; frame < SessionStartupSceneFrameLimit && tree.CurrentScene is null; frame++)
        {
            _ = await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        var baseline = new SessionBaseline
        {
            TimeScale = Engine.TimeScale,
            MainScenePath = ProjectSettings.GetSetting(MainSceneSettingPath).AsString(),
        };

        Window root = tree.Root;
        for (int index = 0; index < root.GetChildCount(); index++)
        {
            Node child = root.GetChild(index);
            if (string.Equals(child.Name, GlobalAutoloadName, StringComparison.Ordinal))
            {
                baseline.GlobalInstance = child;
            }
            else if (ReferenceEquals(child, tree.CurrentScene))
            {
                baseline.MainSceneInstance = child;
            }
            else
            {
                _ = baseline.ProtectedRootChildren.Add(child);
            }
        }

        return baseline;
    }

    private Task RunSessionCommandLoopAsync(Assembly testAssembly, SessionBaseline baseline)
    {
        TaskCompletionSource<bool> loopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var readerThread = new Thread(() => ReadSessionCommands(testAssembly, baseline, loopCompletion))
        {
            IsBackground = true,
            Name = "AlleyCat Integration Session Reader",
        };

        readerThread.Start();

        return loopCompletion.Task;
    }

    /// <summary>
    /// Reads session command lines on the dedicated reader thread, dispatching each parsed command to the Godot
    /// main thread and awaiting its completion before reading the next line so commands run serially.
    /// </summary>
    private void ReadSessionCommands(Assembly testAssembly, SessionBaseline baseline, TaskCompletionSource<bool> loopCompletion)
    {
        try
        {
            while (!_sessionShutdownRequested)
            {
                string? line = Console.ReadLine();

                if (line is null)
                {
                    HandleSessionHostDisappearance();
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!SessionCommandParser.TryParse(line, out SessionCommand? command, out string? parseError))
                {
                    LogSessionWarning($"Ignoring malformed session command ({parseError}): {TruncateSessionLine(line)}");
                    continue;
                }

                DispatchSessionCommandAsync(command, testAssembly, baseline).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            LogSessionWarning($"Session command loop failed: {ex}");
            _ = RunOnMainThreadAsync(() =>
            {
                GetTree().Quit(1);
                return Task.CompletedTask;
            });
        }
        finally
        {
            _ = loopCompletion.TrySetResult(true);
        }
    }

    /// <summary>
    /// Dispatches one parsed command to the Godot main thread and awaits its completion, converting runtime
    /// failures into a terminal error result so the host can restart a replacement session.
    /// </summary>
    private async Task DispatchSessionCommandAsync(SessionCommand command, Assembly testAssembly, SessionBaseline baseline)
    {
        try
        {
            await RunOnMainThreadAsync(() => HandleSessionCommandAsync(command, testAssembly, baseline));
        }
        catch (Exception ex)
        {
            LogSessionWarning($"Session command dispatch failed: {ex}");
            _sessionShutdownRequested = true;

            try
            {
                _ = RunOnMainThreadAsync(() =>
                {
                    if (command.Version == SessionCommandParser.ProtocolVersion
                        && string.Equals(command.Kind, SessionCommandParser.RunCommandKind, StringComparison.Ordinal))
                    {
                        EmitSessionResult(
                            command.RequestId ?? string.Empty,
                            SessionErrorOutcome,
                            $"Session runtime failed while executing the command: {ex.Message}",
                            ex.StackTrace,
                            sessionReusable: false);
                    }

                    GetTree().Quit(1);
                    return Task.CompletedTask;
                });
            }
            catch (Exception quitDispatchEx)
            {
                LogSessionWarning($"Session quit dispatch failed: {quitDispatchEx}");

                // No Godot deferred-call path remains once the runner node is gone (for example when a
                // test freed it), so terminate the process directly; the host observes session death and
                // starts a replacement per the session restart semantics.
                System.Environment.Exit(1);
            }
        }
    }

    private async Task HandleSessionCommandAsync(SessionCommand command, Assembly testAssembly, SessionBaseline baseline)
    {
        switch (command.Kind)
        {
            case SessionCommandParser.RunCommandKind:
                await HandleSessionRunCommandAsync(command, testAssembly, baseline);
                return;
            case SessionCommandParser.ShutdownCommandKind:
                HandleSessionShutdownCommand(command.RequestId!);
                return;
            default:
                throw new InvalidOperationException($"Validated session command kind '{command.Kind}' was not dispatchable.");
        }
    }

    private async Task HandleSessionRunCommandAsync(SessionCommand command, Assembly testAssembly, SessionBaseline baseline)
    {
        string requestId = command.RequestId!;
        string typeName = command.Type!;
        string methodName = command.Method!;

        string outcome = SessionPassedOutcome;
        string? failureMessage = null;
        string? failureStack = null;
        PerTestLifecycleExecutionResult? executionResult = null;

        try
        {
            Type? type = testAssembly.GetType(typeName, throwOnError: false);
            if (type is null)
            {
                EmitSessionResult(
                    requestId,
                    SessionErrorOutcome,
                    $"Type '{typeName}' was not found in the test assembly.",
                    stack: null,
                    sessionReusable: true);
                return;
            }

            MethodInfo? method = type.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (method is null)
            {
                EmitSessionResult(
                    requestId,
                    SessionErrorOutcome,
                    $"Method '{typeName}.{methodName}()' was not found.",
                    stack: null,
                    sessionReusable: true);
                return;
            }

            await RemoveRuntimeGlobalAutoloadForIntegrationTestAsync(typeName);

            executionResult = await PerTestLifecycleExecutor.ExecuteAsync(method);
            if (!executionResult.Passed)
            {
                (failureMessage, failureStack) = PerTestLifecycleExecutor.BuildFailureDiagnostics(executionResult);
                outcome = SessionFailedOutcome;
            }
        }
        catch (Exception ex)
        {
            outcome = SessionErrorOutcome;
            failureMessage = $"Unexpected session test-execution error: {ex.Message}";
            failureStack = ex.StackTrace;
        }

        bool baselineRestored;
        string? baselineDiagnostics;

        try
        {
            (baselineRestored, baselineDiagnostics) = await RestoreSessionBaselineAsync(baseline);
        }
        catch (Exception ex)
        {
            baselineRestored = false;
            baselineDiagnostics = $"Session baseline restoration crashed: {ex}";
        }

        bool sessionReusable = SessionReusability.IsSessionReusable(executionResult, baselineRestored);

        if (!sessionReusable && outcome == SessionPassedOutcome)
        {
            failureMessage = baselineDiagnostics;
        }

        EmitSessionResult(requestId, outcome, failureMessage, failureStack, sessionReusable);

        if (!sessionReusable)
        {
            GetTree().Quit(1);
        }
    }

    private void HandleSessionShutdownCommand(string requestId)
    {
        _sessionShutdownRequested = true;

        EmitSessionLine(
            new SessionShutdownCompletePayload(SessionCommandParser.ProtocolVersion, SessionShutdownCompleteEventKind, requestId),
            _sessionCompactPayloadOptions);

        GetTree().Quit(0);
    }

    /// <summary>
    /// Handles stdin closing without a shutdown command by quitting the session, because the host process that
    /// owns the session has disappeared.
    /// </summary>
    private void HandleSessionHostDisappearance()
    {
        if (_sessionShutdownRequested)
        {
            return;
        }

        LogSessionWarning("Stdin closed without a shutdown command; the host process disappeared. Quitting the session.");

        _ = RunOnMainThreadAsync(() =>
        {
            if (!_sessionShutdownRequested)
            {
                GetTree().Quit(1);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Restores the session startup arrangement after a test: unpauses the tree, restores the engine time scale,
    /// removes the current scene and test-created root children, re-creates a fresh <c>Global</c> autoload and main
    /// scene, and validates the restored arrangement.
    /// </summary>
    /// <remarks>
    /// The <c>Global</c> autoload is always re-created because preserving it would leak its service provider,
    /// logging providers, XR services, and <see cref="Game"/> singleton state into later tests. The following
    /// remain test-owned cleanup the framework cannot reset: CLR static fields, static event subscriptions, native
    /// singletons, ResourceLoader cache entries and mutated shared resources, detached background tasks, and
    /// arbitrary InputMap, audio, rendering, or project-setting mutations.
    /// </remarks>
    private async Task<(bool Restored, string? Diagnostics)> RestoreSessionBaselineAsync(SessionBaseline baseline)
    {
        SceneTree tree = GetTree();
        List<string> diagnostics = [];

        tree.Paused = false;
        Engine.TimeScale = baseline.TimeScale;

        HashSet<Node> nodesToFree = [];

        if (tree.CurrentScene is { } currentScene)
        {
            _ = nodesToFree.Add(currentScene);
        }

        tree.CurrentScene = null;

        Window root = tree.Root;
        for (int index = 0; index < root.GetChildCount(); index++)
        {
            Node child = root.GetChild(index);
            if (baseline.ProtectedRootChildren.Contains(child) || ReferenceEquals(child, baseline.GlobalInstance))
            {
                continue;
            }

            _ = nodesToFree.Add(child);
        }

        foreach (Node node in nodesToFree)
        {
            if (IsInstanceValid(node))
            {
                node.QueueFree();
            }
        }

        await WaitForProcessFramesAsync(SessionFrameSettleCount);

        Node? existingGlobal = root.GetNodeOrNull<Node>(GlobalAutoloadName);
        if (existingGlobal is not null)
        {
            existingGlobal.QueueFree();
            await WaitForProcessFramesAsync(SessionFrameSettleCount);
        }

        if (!TryRecreateGlobalAutoload(root, baseline, diagnostics))
        {
            return (false, BuildBaselineFailureMessage(diagnostics));
        }

        if (!TryRecreateMainScene(tree, root, baseline, diagnostics))
        {
            return (false, BuildBaselineFailureMessage(diagnostics));
        }

        await WaitForProcessFramesAsync(SessionFrameSettleCount);

        ValidateSessionBaseline(tree, baseline, diagnostics);

        return diagnostics.Count == 0 ? (true, null) : (false, BuildBaselineFailureMessage(diagnostics));
    }

    private static bool TryRecreateGlobalAutoload(Window root, SessionBaseline baseline, List<string> diagnostics)
    {
        string autoloadSetting = ProjectSettings.GetSetting(GlobalAutoloadSettingPath).AsString();
        string globalScenePath = autoloadSetting.TrimStart('*');
        PackedScene? globalScene = string.IsNullOrWhiteSpace(globalScenePath)
            ? null
            : GD.Load<PackedScene>(globalScenePath);

        if (globalScene is null)
        {
            diagnostics.Add($"The Global autoload scene '{autoloadSetting}' could not be loaded for baseline restoration.");
            return false;
        }

        try
        {
            Node freshGlobal = globalScene.Instantiate();
            freshGlobal.Name = GlobalAutoloadName;
            root.AddChild(freshGlobal);
            baseline.GlobalInstance = freshGlobal;
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Re-creating the Global autoload failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryRecreateMainScene(SceneTree tree, Window root, SessionBaseline baseline, List<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(baseline.MainScenePath))
        {
            baseline.MainSceneInstance = null;
            return true;
        }

        PackedScene? mainScene = GD.Load<PackedScene>(baseline.MainScenePath);
        if (mainScene is null)
        {
            diagnostics.Add($"The main scene '{baseline.MainScenePath}' could not be loaded for baseline restoration.");
            return false;
        }

        try
        {
            Node freshScene = mainScene.Instantiate();
            root.AddChild(freshScene);
            tree.CurrentScene = freshScene;
            baseline.MainSceneInstance = freshScene;
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Re-creating the main scene failed: {ex.Message}");
            return false;
        }
    }

    private void ValidateSessionBaseline(SceneTree tree, SessionBaseline baseline, List<string> diagnostics)
    {
        if (!IsInsideTree())
        {
            diagnostics.Add("The test runtime runner left the scene tree.");
        }

        if (tree.Paused)
        {
            diagnostics.Add("The scene tree remained paused after baseline restoration.");
        }

        Node? global = tree.Root.GetNodeOrNull<Node>(GlobalAutoloadName);
        if (global is null || !ReferenceEquals(global, baseline.GlobalInstance) || !IsInstanceValid(global))
        {
            diagnostics.Add("The fresh Global autoload is missing or invalid after baseline restoration.");
        }

        Node? currentScene = tree.CurrentScene is { } scene && IsInstanceValid(scene) ? scene : null;
        if (!ReferenceEquals(currentScene, baseline.MainSceneInstance))
        {
            diagnostics.Add("The current scene does not match the restored baseline scene.");
        }

        Window root = tree.Root;
        for (int index = 0; index < root.GetChildCount(); index++)
        {
            Node child = root.GetChild(index);
            if (IsBaselineOwnedRootChild(child, baseline))
            {
                continue;
            }

            diagnostics.Add($"Unexpected root child '{child.Name}' ({child.GetType().FullName}) remained after baseline restoration.");
        }
    }

    private static bool IsBaselineOwnedRootChild(Node child, SessionBaseline baseline)
        => baseline.ProtectedRootChildren.Contains(child)
            || ReferenceEquals(child, baseline.GlobalInstance)
            || ReferenceEquals(child, baseline.MainSceneInstance);

    private static string BuildBaselineFailureMessage(IReadOnlyList<string> diagnostics)
        => $"Session baseline could not be restored or validated: {string.Join("; ", diagnostics)}";

    private async Task RunProbeIfRequestedAsync(IReadOnlyList<string> args)
    {
        string? assemblyPath = GetArgumentValue(args, ProbeAssemblyArg);
        string? probeTypeName = GetArgumentValue(args, ProbeTypeArg);

        if (string.IsNullOrWhiteSpace(assemblyPath) || string.IsNullOrWhiteSpace(probeTypeName))
        {
            FailProbeAndQuit("Probe arguments were missing or empty.");
            return;
        }

        try
        {
            var godotLoadContext = AssemblyLoadContext.GetLoadContext(typeof(Node).Assembly);
            if (godotLoadContext is null)
            {
                FailProbeAndQuit("Could not resolve Godot assembly load context.");
                return;
            }

            if (!TryGetLoadTarget(assemblyPath, out string loadTarget, out AssemblyDependencyResolver? dependencyResolver))
            {
                FailProbeAndQuit("Probe assembly path was missing or invalid.");
                return;
            }

            Assembly? OnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
            {
                return ResolveTestAssemblyDependency(context, assemblyName, dependencyResolver!);
            }

            godotLoadContext.Resolving += OnResolving;
            try
            {
                Assembly assembly = godotLoadContext.LoadFromAssemblyPath(loadTarget);
                Type? probeType = assembly.GetType(probeTypeName, throwOnError: false);
                if (probeType is null)
                {
                    FailProbeAndQuit($"Probe type '{probeTypeName}' was not found in '{loadTarget}'.");
                    return;
                }

                if (!typeof(Node).IsAssignableFrom(probeType))
                {
                    FailProbeAndQuit($"Probe type '{probeTypeName}' does not inherit from Godot.Node.");
                    return;
                }

                if (Activator.CreateInstance(probeType) is not Node probeNode)
                {
                    FailProbeAndQuit($"Unable to instantiate probe node '{probeTypeName}'.");
                    return;
                }

                probeNode.RequestReady();
                AddChild(probeNode);

                (bool ready, string readyFlagError) = await WaitForReadyFlagAsync(probeType);
                if (!ready && !TryInvokeReadyCallback(probeNode, probeType, out readyFlagError))
                {
                    FailProbeAndQuit(readyFlagError);
                    return;
                }

                GD.Print(SuccessMarker);
                GetTree().Quit(0);
            }
            finally
            {
                godotLoadContext.Resolving -= OnResolving;
            }
        }
        catch (Exception ex)
        {
            FailProbeAndQuit($"Unexpected probe error: {ex}");
        }
    }

    private async Task RemoveRuntimeGlobalAutoloadForIntegrationTestAsync(string testTypeName)
    {
        if (!RuntimeContext.IsIntegrationTest() || !RequiresIsolatedGameSingleton(testTypeName))
        {
            return;
        }

        Node? global = GetTree().Root.GetNodeOrNull<Node>("Global");
        if (global is null)
        {
            return;
        }

        global.QueueFree();
        _ = await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static bool RequiresIsolatedGameSingleton(string testTypeName)
        => testTypeName is "AlleyCat.IntegrationTests.GameStartupIntegrationTests"
            or "AlleyCat.IntegrationTests.UI.UIOverlayIntegrationTests"
            or "AlleyCat.IntegrationTests.UI.LoadingScreenIntegrationTests"
            or "AlleyCat.IntegrationTests.UI.GameMenuIntegrationTests"
            or "AlleyCat.IntegrationTests.Speech.TranscriberIntegrationTests"
            or "AlleyCat.IntegrationTests.Control.PlayerControllerGrabInputIntegrationTests"
            or "AlleyCat.IntegrationTests.Testing.ReusableSessionIsolatedGameIntegrationTests";

    private static Assembly? ResolveTestAssemblyDependency(
        AssemblyLoadContext context,
        AssemblyName assemblyName,
        AssemblyDependencyResolver dependencyResolver)
    {
        if (TryGetLoadedAssembly(assemblyName, out Assembly? loadedAssembly))
        {
            return loadedAssembly;
        }

        if (AssemblyName.ReferenceMatchesDefinition(typeof(Node).Assembly.GetName(), assemblyName))
        {
            return typeof(Node).Assembly;
        }

        string? dependencyPath = dependencyResolver.ResolveAssemblyToPath(assemblyName);
        return dependencyPath is null ? null : context.LoadFromAssemblyPath(dependencyPath);
    }

    private static bool TryGetLoadTarget(string assemblyPath,
        out string absoluteAssemblyPath,
        out AssemblyDependencyResolver? dependencyResolver)
    {
        absoluteAssemblyPath = Path.GetFullPath(assemblyPath);

        if (!File.Exists(absoluteAssemblyPath))
        {
            dependencyResolver = null;
            return false;
        }

        dependencyResolver = new AssemblyDependencyResolver(absoluteAssemblyPath);

        return true;
    }

    private static void EnsureRuntimeAssemblyReferencesLoaded(Assembly assembly)
    {
        foreach (AssemblyName referenceName in assembly.GetReferencedAssemblies())
        {
            if (TryGetLoadedAssembly(referenceName, out _))
            {
                continue;
            }

            try
            {
                _ = AssemblyLoadContext.GetLoadContext(assembly)?.LoadFromAssemblyName(referenceName)
                    ?? Assembly.Load(referenceName);
            }
            catch
            {
                // The dynamic test assembly resolver will report a concrete load failure if this dependency is required.
            }
        }
    }

    private static bool TryGetLoadedAssembly(AssemblyName requestedAssemblyName, out Assembly? loadedAssembly)
    {
        loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), requestedAssemblyName));

        return loadedAssembly is not null;
    }

    private static string? GetArgumentValue(IReadOnlyList<string> args, string argumentName)
    {
        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (string.Equals(argument, argumentName, StringComparison.Ordinal))
            {
                return index + 1 < args.Count ? args[index + 1] : null;
            }

            string prefix = $"{argumentName}=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }

    private async Task<(bool Ready, string Error)> WaitForReadyFlagAsync(Type probeType)
    {
        string readyFlagError = string.Empty;
        for (int frame = 0; frame < ProbeReadyFrameLimit; frame++)
        {
            _ = await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (TryReadReadyFlag(probeType, out readyFlagError))
            {
                return (true, string.Empty);
            }
        }

        return (false, readyFlagError);
    }

    private static bool TryInvokeReadyCallback(Node probeNode, Type probeType, out string error)
    {
        MethodInfo? readyMethod = probeType.GetMethod(
            nameof(_Ready),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (readyMethod is null)
        {
            error = $"Probe node '{probeType.FullName}' did not report ready execution and does not expose a public _Ready callback.";
            return false;
        }

        try
        {
            _ = readyMethod.Invoke(probeNode, null);
        }
        catch (Exception ex)
        {
            error = $"Probe node '{probeType.FullName}' did not report ready execution and invoking _Ready failed: {ex.Message}";
            return false;
        }

        return TryReadReadyFlag(probeType, out error);
    }

    private static bool TryReadReadyFlag(Type probeType, out string error)
    {
        PropertyInfo? property = probeType.GetProperty(ProbeReadyPropertyName, BindingFlags.Public | BindingFlags.Static);
        if (property is null)
        {
            error = $"Probe type '{probeType.FullName}' does not define public static bool {ProbeReadyPropertyName}.";
            return false;
        }

        if (property.PropertyType != typeof(bool))
        {
            error = $"Probe property '{probeType.FullName}.{ProbeReadyPropertyName}' is not a bool.";
            return false;
        }

        object? value = property.GetValue(null);
        if (value is not bool readyRan)
        {
            error = $"Probe property '{probeType.FullName}.{ProbeReadyPropertyName}' returned a non-boolean value.";
            return false;
        }

        if (!readyRan)
        {
            error = $"Probe node '{probeType.FullName}' did not report ready execution.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task WaitForProcessFramesAsync(int frameCount)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            _ = await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static string TruncateSessionLine(string line)
        => line.Length <= SessionMalformedLinePreviewLength ? line : $"{line[..SessionMalformedLinePreviewLength]}[...]";

    private static void LogSessionWarning(string message)
    {
        if (GameLoggerResolver.TryResolve(out ILogger<TestRuntimeRunner>? logger) && logger is not null)
        {
            logger.LogWarning("Integration test session: {SessionMessage}", message);
        }
    }

    private static void EmitSessionLine<T>(T payload, JsonSerializerOptions options)
        => GD.Print($"{SessionLinePrefix}{JsonSerializer.Serialize(payload, options)}");

    private void EmitSessionStartupFailure(string details)
    {
        EmitSessionLine(
            new SessionReadyPayload(SessionCommandParser.ProtocolVersion, SessionReadyEventKind, SessionErrorOutcome, details),
            _sessionCompactPayloadOptions);

        GetTree().Quit(1);
    }

    private static void EmitSessionResult(string requestId, string outcome, string? message, string? stack, bool sessionReusable)
        => EmitSessionLine(
            new SessionResultPayload(
                SessionCommandParser.ProtocolVersion,
                SessionResultEventKind,
                requestId,
                outcome,
                message,
                stack,
                sessionReusable),
            _sessionResultPayloadOptions);

    private void FailProbeAndQuit(string details)
    {
        GD.PrintErr($"{FailureMarker}: {details}");
        GetTree().Quit(1);
    }

    /// <summary>
    /// Payload emitted for the <c>ready</c> protocol event, where null failure fields are omitted.
    /// </summary>
    private sealed record SessionReadyPayload(int Version, string Kind, string? Outcome = null, string? Message = null);

    /// <summary>
    /// Payload emitted for the <c>result</c> protocol event after each run command completes.
    /// </summary>
    private sealed record SessionResultPayload(
        int Version,
        string Kind,
        string RequestId,
        string Outcome,
        string? Message,
        string? Stack,
        bool SessionReusable);

    /// <summary>
    /// Payload emitted for the <c>shutdown-complete</c> protocol event.
    /// </summary>
    private sealed record SessionShutdownCompletePayload(int Version, string Kind, string RequestId);

    /// <summary>
    /// Snapshot of the fresh-process scene-tree arrangement restored after every session test.
    /// </summary>
    private sealed class SessionBaseline
    {
        /// <summary>Engine time scale captured at session start.</summary>
        public double TimeScale
        {
            get;
            init;
        }

        /// <summary>Configured main-scene resource path captured at session start; empty when no main scene is configured.</summary>
        public string MainScenePath
        {
            get;
            init;
        } = string.Empty;

        /// <summary>
        /// Startup-owned root children other than the <c>Global</c> autoload and the main scene, which are always
        /// re-created fresh; membership is by node instance.
        /// </summary>
        public HashSet<Node> ProtectedRootChildren { get; } = [];

        /// <summary>The root child currently owning the <c>Global</c> autoload role; replaced by each restoration.</summary>
        public Node? GlobalInstance
        {
            get;
            set;
        }

        /// <summary>The node currently acting as the baseline main scene; replaced by each restoration.</summary>
        public Node? MainSceneInstance
        {
            get;
            set;
        }
    }
}

/// <summary>
/// Validates one host-to-runtime session command before it can be dispatched to Godot's main thread.
/// </summary>
internal static class SessionCommandParser
{
    internal const int ProtocolVersion = 1;
    internal const string RunCommandKind = "run";
    internal const string ShutdownCommandKind = "shutdown";

    private static readonly JsonSerializerOptions _parsingOptions = new() { PropertyNameCaseInsensitive = true };

    internal static bool TryParse(
        string line,
        [NotNullWhen(true)] out SessionCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                command = null;
                error = "payload must be a JSON object";
                return false;
            }

            SessionCommand? parsed = document.RootElement.Deserialize<SessionCommand>(_parsingOptions);
            if (parsed is null)
            {
                command = null;
                error = "payload deserialised to null";
                return false;
            }

            if (!TryValidate(parsed, out error))
            {
                command = null;
                error ??= "command validation failed";
                return false;
            }

            command = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            command = null;
            error = $"JSON parsing failed: {ex.Message}";
            return false;
        }
    }

    private static bool TryValidate(SessionCommand command, out string? error)
    {
        if (command.Version != ProtocolVersion)
        {
            error = $"Version must be {ProtocolVersion}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(command.Kind))
        {
            error = "Kind was missing or empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(command.RequestId))
        {
            error = "RequestId was missing or empty";
            return false;
        }

        switch (command.Kind)
        {
            case RunCommandKind:
                if (string.IsNullOrWhiteSpace(command.Type) || string.IsNullOrWhiteSpace(command.Method))
                {
                    error = "'run' requires non-empty Type and Method";
                    return false;
                }

                break;
            case ShutdownCommandKind:
                break;
            default:
                error = $"Kind '{command.Kind}' was not recognised";
                return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// One command line received from the session host on stdin.
/// </summary>
internal sealed record SessionCommand(int? Version, string? Kind, string? RequestId, string? Type, string? Method);
