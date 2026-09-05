using System.Reflection;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Requests;
using Xunit;

namespace AlleyCat.TestFramework;

internal sealed class GodotTestFramework : ITestFramework, IDataProducer
{
    private const string ProbeCommandArg = "--integration-probe";
    private const string ProbeAssemblyArg = "--probe-assembly";
    private const string ProbeTypeArg = "--probe-type";
    private const string ProbeTypeName = "AlleyCat.IntegrationTests.Probe.DynamicLoadProbeNode";
    private const string ProbeSuccessMarker = "ALLEYCAT_INTEGRATION_PROBE_SUCCESS";
    private const int DefaultPreflightTimeoutMs = 30_000;
    private const int DefaultImportTimeoutMs = 120_000;
    private const int DefaultRequestTimeoutMs = 120_000;
    private const int DefaultCleanupTimeoutMs = 5_000;
    private const string GodotBinaryEnvironmentVariable = "GODOT_PATH";
    private const string GodotPreflightTimeoutEnvironmentVariable = "ALLEYCAT_GODOT_PREFLIGHT_TIMEOUT_MS";
    private const string GodotImportTimeoutEnvironmentVariable = "ALLEYCAT_GODOT_IMPORT_TIMEOUT_MS";
    private const string GodotRequestTimeoutEnvironmentVariable = "ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS";
    private const string GodotCleanupTimeoutEnvironmentVariable = "ALLEYCAT_GODOT_CLEANUP_TIMEOUT_MS";
    private const string GodotImportPreflightEnvironmentVariable = "ALLEYCAT_INTEGRATION_IMPORT_PREFLIGHT";

    private readonly Assembly _testAssembly;
    private readonly GodotCliTestSelector _cliSelector;
    private readonly IGodotProcessFactory _processFactory;
    private readonly string _godotBinaryPath;
    private readonly string _workspaceRootPath;
    private readonly int _preflightTimeoutMs;
    private readonly int _importTimeoutMs;
    private readonly int _requestTimeoutMs;
    private readonly int _cleanupTimeoutMs;
    private readonly bool _headlessOverride;
    private readonly Dictionary<TestNodeUid, MethodInfo> _testsByUid;

    internal GodotTestFramework(Assembly testAssembly, GodotCliTestSelector cliSelector)
        : this(testAssembly, cliSelector, processFactory: null)
    {
    }

    internal GodotTestFramework(
        Assembly testAssembly,
        GodotCliTestSelector cliSelector,
        IGodotProcessFactory? processFactory)
        : this(testAssembly, cliSelector, processFactory, headlessOverride: false)
    {
    }

    internal GodotTestFramework(
        Assembly testAssembly,
        GodotCliTestSelector cliSelector,
        IGodotProcessFactory? processFactory,
        bool headlessOverride)
    {
        _testAssembly = testAssembly;
        _cliSelector = cliSelector;
        _headlessOverride = headlessOverride;
        _godotBinaryPath = ResolveGodotBinaryPath();
        _workspaceRootPath = ResolveWorkspaceRootPath(testAssembly);
        _preflightTimeoutMs = ResolveTimeout(
            GodotPreflightTimeoutEnvironmentVariable,
            DefaultPreflightTimeoutMs);
        _importTimeoutMs = ResolveTimeout(
            GodotImportTimeoutEnvironmentVariable,
            DefaultImportTimeoutMs);
        _requestTimeoutMs = ResolveTimeout(
            GodotRequestTimeoutEnvironmentVariable,
            DefaultRequestTimeoutMs);
        _cleanupTimeoutMs = ResolveTimeout(
            GodotCleanupTimeoutEnvironmentVariable,
            DefaultCleanupTimeoutMs);
        _testsByUid = DiscoverTests(testAssembly)
            .ToDictionary(testCase => testCase.Uid, testCase => testCase.Method);
        _processFactory = processFactory
            ?? new SystemGodotProcessFactory(_godotBinaryPath, _workspaceRootPath);
    }

    public string Uid => "AlleyCat.TestFramework.GodotTestFramework";

    public string Version => typeof(GodotTestFramework).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public string DisplayName => "AlleyCat Godot Test Framework";

    public string Description => "Godot-backed test framework for AlleyCat integration tests.";

    public Type[] DataTypesProduced => [typeof(TestNodeUpdateMessage)];

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public Task<CreateTestSessionResult> CreateTestSessionAsync(CreateTestSessionContext context) =>
        Task.FromResult(new CreateTestSessionResult { IsSuccess = true });

    public Task<CloseTestSessionResult> CloseTestSessionAsync(CloseTestSessionContext context) =>
        Task.FromResult(new CloseTestSessionResult { IsSuccess = true });

    public async Task ExecuteRequestAsync(ExecuteRequestContext context)
    {
        try
        {
            switch (context.Request)
            {
                case DiscoverTestExecutionRequest discoverRequest:
                    await DiscoverTestsAsync(context, discoverRequest);
                    break;
                case RunTestExecutionRequest runRequest:
                    await RunTestsAsync(context, runRequest);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported request: {context.Request.GetType().FullName}");
            }
        }
        finally
        {
            context.Complete();
        }
    }

    private async Task DiscoverTestsAsync(ExecuteRequestContext context, DiscoverTestExecutionRequest request)
    {
        foreach ((TestNodeUid uid, MethodInfo method) in FilteredTests(request.Filter))
        {
            TestNode discoveredNode = CreateTestNode(uid, method, DiscoveredTestNodeStateProperty.CachedInstance);
            await context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(request.Session.SessionUid, discoveredNode));
        }
    }

    private async Task RunTestsAsync(ExecuteRequestContext context, RunTestExecutionRequest request)
    {
        (TestNodeUid Uid, MethodInfo Method)[] testsToRun = [.. FilteredTests(request.Filter)];
        if (testsToRun.Length == 0)
        {
            return;
        }

        CancellationToken cancellationToken = GetContextCancellationToken(context);
        Exception? preflightError = await RunPreflightAsync(cancellationToken);

        if (preflightError is not null)
        {
            foreach ((TestNodeUid uid, MethodInfo method) in testsToRun)
            {
                TestNode inProgressNode = CreateTestNode(uid, method, InProgressTestNodeStateProperty.CachedInstance);
                await context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(request.Session.SessionUid, inProgressNode));

                TestNode failedNode = CreateTestNode(uid, method, new ErrorTestNodeStateProperty(preflightError));
                await context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(request.Session.SessionUid, failedNode));
            }

            return;
        }

        await RunSelectedTestsAsync(
            testsToRun,
            (node, _) => context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(request.Session.SessionUid, node)),
            cancellationToken);
    }

    /// <summary>
    /// Executes the selected tests against lazily created reusable sessions, routing each test by its effective
    /// headless mode and publishing one InProgress and one terminal node per test UID.
    /// </summary>
    /// <param name="tests">The tests to execute, in selection order.</param>
    /// <param name="publishNodeAsync">Publishes one test node update on the host message bus.</param>
    /// <param name="cancellationToken">Host cancellation token stopping scheduling and killing live sessions.</param>
    internal async Task RunSelectedTestsAsync(
        IReadOnlyList<(TestNodeUid Uid, MethodInfo Method)> tests,
        Func<TestNode, CancellationToken, Task> publishNodeAsync,
        CancellationToken cancellationToken)
    {
        Dictionary<bool, GodotSessionClient?> sessionsByHeadlessMode = [];

        try
        {
            foreach ((TestNodeUid uid, MethodInfo method) in tests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TestNode inProgressNode = CreateTestNode(uid, method, InProgressTestNodeStateProperty.CachedInstance);
                await publishNodeAsync(inProgressNode, cancellationToken);

                TestNode completedNode = await ExecuteTestInSessionAsync(sessionsByHeadlessMode, uid, method, cancellationToken);
                await publishNodeAsync(completedNode, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host cancellation stops scheduling; live sessions are killed below.
        }
        finally
        {
            await ShutdownSessionsAsync(sessionsByHeadlessMode);
        }
    }

    private async Task<TestNode> ExecuteTestInSessionAsync(
        Dictionary<bool, GodotSessionClient?> sessionsByHeadlessMode,
        TestNodeUid uid,
        MethodInfo method,
        CancellationToken cancellationToken)
    {
        bool headless = _headlessOverride || ResolveHeadlessMode(method);

        GodotSessionClient? session = sessionsByHeadlessMode.GetValueOrDefault(headless);

        if (session is not null && !session.IsUsable)
        {
            sessionsByHeadlessMode[headless] = null;
            session.Dispose();
            session = null;
        }

        if (session is null)
        {
            Exception? startError = await TryStartSessionAsync(sessionsByHeadlessMode, headless, cancellationToken);
            if (startError is not null)
            {
                return CreateTestNode(uid, method, new ErrorTestNodeStateProperty(startError));
            }

            session = sessionsByHeadlessMode[headless];
        }

        GodotSessionTestResult result = await session!.RunTestAsync(
            uid.Value,
            method.DeclaringType?.FullName ?? string.Empty,
            method.Name,
            _requestTimeoutMs,
            cancellationToken);

        if (!result.SessionReusable)
        {
            sessionsByHeadlessMode[headless] = null;
            session.Dispose();
        }

        return result.Outcome switch
        {
            GodotSessionTestOutcome.Passed => CreateTestNode(uid, method, PassedTestNodeStateProperty.CachedInstance),
            GodotSessionTestOutcome.Failed => CreateTestNode(uid, method, new FailedTestNodeStateProperty(EnsureErrorDetail(result))),
            GodotSessionTestOutcome.Error => CreateTestNode(uid, method, new ErrorTestNodeStateProperty(EnsureErrorDetail(result))),
            _ => throw new InvalidOperationException($"Unexpected session test outcome '{result.Outcome}'."),
        };
    }

    private async Task<Exception?> TryStartSessionAsync(
        Dictionary<bool, GodotSessionClient?> sessionsByHeadlessMode,
        bool headless,
        CancellationToken cancellationToken)
    {
        GodotSessionClient? startedSession = null;

        try
        {
            startedSession = GodotSessionClient.Start(_processFactory, CreateSessionArguments(headless));
            await startedSession.WaitForReadyAsync(_preflightTimeoutMs, cancellationToken);
            sessionsByHeadlessMode[headless] = startedSession;
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            startedSession?.Dispose();
            sessionsByHeadlessMode[headless] = null;
            return exception;
        }
    }

    private async Task ShutdownSessionsAsync(Dictionary<bool, GodotSessionClient?> sessionsByHeadlessMode)
    {
        foreach ((bool headless, GodotSessionClient? session) in sessionsByHeadlessMode)
        {
            if (session is null)
            {
                continue;
            }

            try
            {
                await session.ShutdownAsync(_cleanupTimeoutMs);
            }
            catch
            {
                // Session shutdown is best effort; Dispose force-kills below.
            }

            session.Dispose();
            sessionsByHeadlessMode[headless] = null;
        }
    }

    private static Exception EnsureErrorDetail(GodotSessionTestResult result)
        => result.Error ?? new InvalidOperationException($"Session test outcome '{result.Outcome}' carried no error detail.");

    private static TestNode CreateTestNode(TestNodeUid uid, MethodInfo method, IProperty stateProperty) =>
        new()
        {
            Uid = uid,
            DisplayName = GetDisplayName(method),
            Properties = new PropertyBag(stateProperty),
        };

    private static IEnumerable<(TestNodeUid Uid, MethodInfo Method)> DiscoverTests(Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (!IsSupportedFactMethod(method))
                {
                    continue;
                }

                yield return (CreateTestUid(method), method);
            }
        }
    }

    private static bool IsSupportedFactMethod(MethodInfo method) =>
        method.GetCustomAttribute<FactAttribute>() is not null
        && method.GetParameters().Length == 0;

    private static string GetDisplayName(MethodInfo method) => TestCaseUidFactory.GetFullyQualifiedMethodName(method);

    private static TestNodeUid CreateTestUid(MethodInfo method) => TestCaseUidFactory.Create(method);

    private IEnumerable<(TestNodeUid Uid, MethodInfo Method)> FilteredTests(ITestExecutionFilter filter)
    {
        IEnumerable<(TestNodeUid Uid, MethodInfo Method)> candidates =
            filter is not TestNodeUidListFilter testNodeUidListFilter
                ? _testsByUid.Select(entry => (entry.Key, entry.Value))
                : testNodeUidListFilter.TestNodeUids
                    .Where(_testsByUid.ContainsKey)
                    .Select(uid => (uid, _testsByUid[uid]));

        return candidates.Where(candidate => _cliSelector.Matches(candidate.Method));
    }

    private async Task<Exception?> RunPreflightAsync(CancellationToken cancellationToken)
    {
        if (IsImportPreflightEnabled())
        {
            GodotProcessRunResult importResult = await RunGodotProcessAsync(CreateImportArguments(), _importTimeoutMs, cancellationToken);
            if (importResult.FailureException is not null)
            {
                return importResult.FailureException;
            }

            if (importResult.ExitCode != 0)
            {
                return new InvalidOperationException(
                    $"Godot integration import preflight failed. ExitCode={importResult.ExitCode}. {BuildOutputSummary(importResult)}");
            }
        }

        GodotProcessRunResult runResult = await RunGodotProcessAsync(CreateProbeArguments(), _preflightTimeoutMs, cancellationToken);

        return runResult.FailureException is not null
            ? runResult.FailureException
            : runResult.ExitCode == 0
            && runResult.StdOut.Any(line => line.Contains(ProbeSuccessMarker, StringComparison.Ordinal))
            ? null
            : (Exception)new InvalidOperationException(
            $"Godot integration preflight failed. ExitCode={runResult.ExitCode}. {BuildOutputSummary(runResult)}");
    }

    private static IReadOnlyList<string> CreateImportArguments() =>
    [
        "--headless",
        "--xr-mode",
        "off",
        "--path",
        "game",
        "--import",
        "--recovery-mode",
    ];

    private static bool IsImportPreflightEnabled()
    {
        string? rawValue = Environment.GetEnvironmentVariable(GodotImportPreflightEnvironmentVariable);
        return string.Equals(rawValue, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawValue, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GodotProcessRunResult> RunGodotProcessAsync(
        IReadOnlyList<string> commandLineArguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using IGodotProcess process = _processFactory.Create(commandLineArguments);

        List<string> stdOutLines = [];
        List<string> stdErrLines = [];
        object stdOutLock = new();
        object stdErrLock = new();

        Task ReadStreamAsync(Func<CancellationToken, Task<string?>> readLineAsync, IList<string> lines, object syncLock)
        {
            return Task.Run(async () =>
        {
            while (true)
            {
                string? line = await readLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                lock (syncLock)
                {
                    lines.Add(line);
                }
            }
        }, cancellationToken);
        }

        try
        {
            if (!process.Start())
            {
                return new GodotProcessRunResult(
                    ExitCode: null,
                    StdOut: [],
                    StdErr: [],
                    FailureException: new InvalidOperationException("Godot process failed to start."));
            }
        }
        catch (Exception exception)
        {
            return new GodotProcessRunResult(
                ExitCode: null,
                StdOut: [],
                StdErr: [],
                FailureException: new InvalidOperationException(
                    $"Unable to start Godot process '{_godotBinaryPath}': {exception.Message}",
                    exception));
        }

        Task stdOutTask = ReadStreamAsync(process.ReadStandardOutputLineAsync, stdOutLines, stdOutLock);
        Task stdErrTask = ReadStreamAsync(process.ReadStandardErrorLineAsync, stdErrLines, stdErrLock);

        using var timeoutCancellationTokenSource = new CancellationTokenSource(timeoutMs);
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellationTokenSource.Token);

        Exception? failureException = null;

        try
        {
            await process.WaitForExitAsync(linkedCancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (timeoutCancellationTokenSource.IsCancellationRequested)
        {
            failureException = new TimeoutException($"Godot process timed out after {timeoutMs}ms.");
            await CleanupTimedOutProcessAsync(process);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failureException = new OperationCanceledException("Godot process execution was cancelled.", cancellationToken);
            await CleanupTimedOutProcessAsync(process);
        }

        await Task.WhenAll(
            IgnoreCancellationAsync(stdOutTask),
            IgnoreCancellationAsync(stdErrTask));

        List<string> stdOutSnapshot;
        lock (stdOutLock)
        {
            stdOutSnapshot = [.. stdOutLines];
        }

        List<string> stdErrSnapshot;
        lock (stdErrLock)
        {
            stdErrSnapshot = [.. stdErrLines];
        }

        return new GodotProcessRunResult(
            ExitCode: process.HasExited ? process.ExitCode : null,
            StdOut: stdOutSnapshot,
            StdErr: stdErrSnapshot,
            FailureException: failureException);
    }

    private IReadOnlyList<string> CreateProbeArguments() =>
    [
        "--headless",
        "--xr-mode",
        "off",
        "--path",
        "game",
        "--",
        ProbeCommandArg,
        ProbeAssemblyArg,
        _testAssembly.Location,
        ProbeTypeArg,
        ProbeTypeName,
    ];

    private IReadOnlyList<string> CreateSessionArguments(bool headless)
    {
        List<string> args = [];

        if (headless)
        {
            args.Add("--headless");
        }

        args.Add("--xr-mode");
        args.Add("off");
        args.Add("--path");
        args.Add("game");
        args.Add("--");
        args.Add(GodotSessionProtocol.SessionCommandArg);
        args.Add(ProbeAssemblyArg);
        args.Add(_testAssembly.Location);

        return args;
    }

    /// <summary>
    /// Resolves the headless mode for a test method by checking method-level
    /// then class-level <see cref="HeadlessAttribute"/>. Defaults to <c>false</c> (non-headless).
    /// </summary>
    private static bool ResolveHeadlessMode(MethodInfo method)
    {
        HeadlessAttribute? methodAttribute = method.GetCustomAttribute<HeadlessAttribute>();
        if (methodAttribute is not null)
        {
            return methodAttribute.Enabled;
        }

        HeadlessAttribute? classAttribute = method.DeclaringType?.GetCustomAttribute<HeadlessAttribute>();

        return classAttribute?.Enabled ?? false;
    }

    private async Task CleanupTimedOutProcessAsync(IGodotProcess process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            return;
        }

        using var cleanupCancellationTokenSource = new CancellationTokenSource(_cleanupTimeoutMs);
        try
        {
            await process.WaitForExitAsync(cleanupCancellationTokenSource.Token);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string BuildOutputSummary(GodotProcessRunResult runResult)
    {
        string renderedStdOut = runResult.StdOut.Count == 0 ? "<empty>" : string.Join(Environment.NewLine, runResult.StdOut);
        string renderedStdErr = runResult.StdErr.Count == 0 ? "<empty>" : string.Join(Environment.NewLine, runResult.StdErr);

        return $"StdOut:{Environment.NewLine}{renderedStdOut}{Environment.NewLine}StdErr:{Environment.NewLine}{renderedStdErr}";
    }

    private static string ResolveGodotBinaryPath()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(GodotBinaryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configuredPath) ? "godot-mono" : configuredPath;
    }

    private static int ResolveTimeout(string environmentVariable, int fallbackMs)
    {
        string? rawValue = Environment.GetEnvironmentVariable(environmentVariable);
        return int.TryParse(rawValue, out int timeoutMs) && timeoutMs > 0 ? timeoutMs : fallbackMs;
    }

    private static string ResolveWorkspaceRootPath(Assembly assembly)
    {
        string[] candidateRoots =
        [
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(assembly.Location) ?? string.Empty,
        ];

        foreach (string candidateRoot in candidateRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            DirectoryInfo? cursor = new(candidateRoot);
            while (cursor is not null)
            {
                string gameProjectPath = Path.Combine(cursor.FullName, "game", "project.godot");
                if (File.Exists(gameProjectPath))
                {
                    return cursor.FullName;
                }

                cursor = cursor.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to resolve workspace root containing game/project.godot.");
    }

    private static CancellationToken GetContextCancellationToken(ExecuteRequestContext context)
    {
        PropertyInfo? cancellationTokenProperty = context.GetType().GetProperty("CancellationToken", BindingFlags.Public | BindingFlags.Instance);
        return cancellationTokenProperty?.GetValue(context) is CancellationToken cancellationToken
            ? cancellationToken
            : CancellationToken.None;
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Stream drain cancellation is expected during forced shutdown.
        }
    }

    private sealed record GodotProcessRunResult(
        int? ExitCode,
        IReadOnlyList<string> StdOut,
        IReadOnlyList<string> StdErr,
        Exception? FailureException);
}
