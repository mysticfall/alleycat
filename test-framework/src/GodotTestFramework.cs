using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Requests;
using Xunit;

namespace AlleyCat.TestFramework;

internal sealed class GodotTestFramework : ITestFramework, IDataProducer
{
    private const string ProbeCommandArg = "--integration-probe";
    private const string RunFactCommandArg = "--integration-run-fact";
    private const string ProbeAssemblyArg = "--probe-assembly";
    private const string ProbeTypeArg = "--probe-type";
    private const string ProbeMethodArg = "--probe-method";
    private const string ProbeTypeName = "AlleyCat.IntegrationTests.Probe.DynamicLoadProbeNode";
    private const string ProbeSuccessMarker = "ALLEYCAT_INTEGRATION_PROBE_SUCCESS";
    private const string RunFactResultMarkerPrefix = "ALLEYCAT_INTEGRATION_TEST_RESULT:";
    private const string RuntimeContextEnvironmentVariable = "ALLEYCAT_RUNTIME_CONTEXT";
    private const string RuntimeContextIntegrationTestValue = "integration-test";
    private const int DefaultPreflightTimeoutMs = 30_000;
    private const int DefaultRunFactTimeoutMs = 120_000;
    private const int DefaultCleanupTimeoutMs = 5_000;
    private const int StructuredResultExitGraceTimeoutMs = 500;
    private const string GodotBinaryEnvironmentVariable = "GODOT_PATH";
    private const string GodotPreflightTimeoutEnvironmentVariable = "ALLEYCAT_GODOT_PREFLIGHT_TIMEOUT_MS";
    private const string GodotRunFactTimeoutEnvironmentVariable = "ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS";
    private const string GodotCleanupTimeoutEnvironmentVariable = "ALLEYCAT_GODOT_CLEANUP_TIMEOUT_MS";

    private readonly Assembly _testAssembly;
    private readonly GodotCliTestSelector _cliSelector;
    private readonly IGodotProcessFactory _processFactory;
    private readonly string _godotBinaryPath;
    private readonly string _workspaceRootPath;
    private readonly int _preflightTimeoutMs;
    private readonly int _runFactTimeoutMs;
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
        _runFactTimeoutMs = ResolveTimeout(
            GodotRunFactTimeoutEnvironmentVariable,
            DefaultRunFactTimeoutMs);
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

        foreach ((TestNodeUid uid, MethodInfo method) in testsToRun)
        {
            TestNode inProgressNode = CreateTestNode(uid, method, InProgressTestNodeStateProperty.CachedInstance);
            await context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(request.Session.SessionUid, inProgressNode));

            TestNode completedNode = await ExecuteTestAsync(uid, method, cancellationToken);
            await context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(request.Session.SessionUid, completedNode));
        }
    }

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

    private async Task<TestNode> ExecuteTestAsync(TestNodeUid uid, MethodInfo method, CancellationToken cancellationToken)
    {
        RunFactExecutionResult runFactResult = await RunFactAsync(method, cancellationToken);

        return runFactResult.Outcome switch
        {
            RunFactOutcome.Passed => CreateTestNode(uid, method, PassedTestNodeStateProperty.CachedInstance),
            RunFactOutcome.Failed => CreateTestNode(uid, method, new FailedTestNodeStateProperty(runFactResult.Error!)),
            RunFactOutcome.Error => CreateTestNode(uid, method, new ErrorTestNodeStateProperty(runFactResult.Error!)),
            _ => throw new InvalidOperationException($"Unexpected run-fact outcome '{runFactResult.Outcome}'."),
        };
    }

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
        GodotProcessRunResult runResult = await RunGodotProcessAsync(CreateProbeArguments(), _preflightTimeoutMs, cancellationToken);

        return runResult.FailureException is not null
            ? runResult.FailureException
            : runResult.ExitCode == 0
            && runResult.StdOut.Any(line => line.Contains(ProbeSuccessMarker, StringComparison.Ordinal))
            ? null
            : (Exception)new InvalidOperationException(
            $"Godot integration preflight failed. ExitCode={runResult.ExitCode}. {BuildOutputSummary(runResult)}");
    }

    private async Task<RunFactExecutionResult> RunFactAsync(MethodInfo method, CancellationToken cancellationToken)
    {
        GodotProcessRunResult runResult = await RunGodotProcessAsync(
            CreateRunFactArguments(method),
            _runFactTimeoutMs,
            cancellationToken,
            IsRunFactEarlyExitSignalLine);

        return BuildRunFactExecutionResult(runResult);
    }

    private static RunFactExecutionResult BuildRunFactExecutionResult(GodotProcessRunResult runResult)
    {
        StructuredRunFactResult? structuredResult = TryParseStructuredRunFactResult(runResult.StdOut)
            ?? TryParseStructuredRunFactResult(runResult.StdErr);

        if (structuredResult is not null)
        {
            return BuildStructuredRunFactExecutionResult(structuredResult);
        }

        if (runResult.FailureException is not null)
        {
            return new RunFactExecutionResult(RunFactOutcome.Error, runResult.FailureException);
        }

        var exception = new InvalidOperationException(
            $"Godot run-fact did not emit a structured result line. ExitCode={runResult.ExitCode}. {BuildOutputSummary(runResult)}");
        return new RunFactExecutionResult(RunFactOutcome.Error, exception);
    }

    private static RunFactExecutionResult BuildStructuredRunFactExecutionResult(StructuredRunFactResult structuredResult)
    {
        if (string.Equals(structuredResult.Outcome, "passed", StringComparison.OrdinalIgnoreCase))
        {
            return new RunFactExecutionResult(RunFactOutcome.Passed, null);
        }

        string message = BuildStructuredErrorMessage(structuredResult);
        Exception structuredException = new(message);

        return string.Equals(structuredResult.Outcome, "failed", StringComparison.OrdinalIgnoreCase)
            ? new RunFactExecutionResult(RunFactOutcome.Failed, structuredException)
            : new RunFactExecutionResult(RunFactOutcome.Error, structuredException);
    }

    private async Task<GodotProcessRunResult> RunGodotProcessAsync(
        IReadOnlyList<string> commandLineArguments,
        int timeoutMs,
        CancellationToken cancellationToken,
        Func<string, bool>? earlyExitSignalPredicate = null)
    {
        using IGodotProcess process = _processFactory.Create(commandLineArguments);

        TaskCompletionSource<bool>? earlyExitSignal = earlyExitSignalPredicate is null
            ? null
            : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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

                if (earlyExitSignalPredicate is not null && earlyExitSignalPredicate(line))
                {
                    _ = earlyExitSignal?.TrySetResult(true);
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
            Task waitForExitTask = process.WaitForExitAsync(linkedCancellationTokenSource.Token);

            if (earlyExitSignal is null)
            {
                await waitForExitTask;
            }
            else
            {
                Task completedTask = await Task.WhenAny(waitForExitTask, earlyExitSignal.Task);
                if (completedTask == earlyExitSignal.Task && !process.HasExited)
                {
                    await StopProcessAfterStructuredResultAsync(process);
                }
                else
                {
                    await waitForExitTask;
                }
            }
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

    private async Task StopProcessAfterStructuredResultAsync(IGodotProcess process)
    {
        using var gracefulExitCancellationTokenSource = new CancellationTokenSource(StructuredResultExitGraceTimeoutMs);
        try
        {
            await process.WaitForExitAsync(gracefulExitCancellationTokenSource.Token);
            return;
        }
        catch (OperationCanceledException)
        {
            // Fall back to forced cleanup if the process does not exit promptly.
        }

        await CleanupTimedOutProcessAsync(process);
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

    private IReadOnlyList<string> CreateRunFactArguments(MethodInfo method)
    {
        bool useHeadless = _headlessOverride || ResolveHeadlessMode(method);

        var args = new List<string>();

        if (useHeadless)
        {
            args.Add("--headless");
        }

        args.Add("--xr-mode");
        args.Add("off");
        args.Add("--path");
        args.Add("game");
        args.Add("--");
        args.Add(RunFactCommandArg);
        args.Add(ProbeAssemblyArg);
        args.Add(_testAssembly.Location);
        args.Add(ProbeTypeArg);
        args.Add(method.DeclaringType?.FullName ?? string.Empty);
        args.Add(ProbeMethodArg);
        args.Add(method.Name);

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

    private static StructuredRunFactResult? TryParseStructuredRunFactResult(IEnumerable<string> lines)
    {
        foreach (string line in lines.Reverse())
        {
            if (!line.StartsWith(RunFactResultMarkerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line[RunFactResultMarkerPrefix.Length..];
            try
            {
                StructuredRunFactResult? parsedResult = JsonSerializer.Deserialize<StructuredRunFactResult>(payload);
                if (parsedResult is null)
                {
                    continue;
                }

                return parsedResult;
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private static bool IsStructuredRunFactResultLine(string line)
        => line.StartsWith(RunFactResultMarkerPrefix, StringComparison.Ordinal);

    private static bool IsRunFactEarlyExitSignalLine(string line)
        => IsStructuredRunFactResultLine(line);

    private static string BuildStructuredErrorMessage(StructuredRunFactResult structuredResult)
    {
        return string.IsNullOrWhiteSpace(structuredResult.Message)
            && string.IsNullOrWhiteSpace(structuredResult.Stack)
            ? "Godot run-fact reported an unknown failure."
            : string.IsNullOrWhiteSpace(structuredResult.Stack)
            ? structuredResult.Message!
            : string.IsNullOrWhiteSpace(structuredResult.Message)
            ? structuredResult.Stack!
            : $"{structuredResult.Message}{Environment.NewLine}{structuredResult.Stack}";
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

    private sealed record StructuredRunFactResult(string Outcome, string? Message, string? Stack);

    private sealed record RunFactExecutionResult(RunFactOutcome Outcome, Exception? Error);

    private sealed record GodotProcessRunResult(
        int? ExitCode,
        IReadOnlyList<string> StdOut,
        IReadOnlyList<string> StdErr,
        Exception? FailureException);

    private enum RunFactOutcome
    {
        Passed,
        Failed,
        Error,
    }

    internal interface IGodotProcessFactory
    {
        IGodotProcess Create(IReadOnlyList<string> commandLineArguments);
    }

    internal interface IGodotProcess : IDisposable
    {
        bool Start();

        bool HasExited
        {
            get;
        }

        int ExitCode
        {
            get;
        }

        Task WaitForExitAsync(CancellationToken cancellationToken);

        Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken);

        Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken);

        void Kill(bool entireProcessTree);
    }

    private sealed class SystemGodotProcessFactory(string godotBinaryPath, string workspaceRootPath) : IGodotProcessFactory
    {
        public IGodotProcess Create(IReadOnlyList<string> commandLineArguments)
        {
            ProcessStartInfo processStartInfo = new()
            {
                FileName = godotBinaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workspaceRootPath,
            };

            processStartInfo.EnvironmentVariables[RuntimeContextEnvironmentVariable] = RuntimeContextIntegrationTestValue;

            foreach (string argument in commandLineArguments)
            {
                processStartInfo.ArgumentList.Add(argument);
            }

            return new SystemGodotProcess(processStartInfo);
        }
    }

    private sealed class SystemGodotProcess(ProcessStartInfo startInfo) : IGodotProcess
    {
        private readonly Process _process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        public bool Start() => _process.Start();

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _process.WaitForExitAsync(cancellationToken);

        public Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken)
            => _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        public Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken)
            => _process.StandardError.ReadLineAsync(cancellationToken).AsTask();

        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

        public void Dispose() => _process.Dispose();
    }
}
