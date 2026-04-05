using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace AlleyCat.TestFramework.Tests;

/// <summary>
/// Behaviour-focused tests for key Godot test framework logic.
/// </summary>
public sealed class GodotTestFrameworkTests
{
    private const string ResultMarkerPrefix = "ALLEYCAT_INTEGRATION_TEST_RESULT:";
    private static readonly Type _godotTestFrameworkType = typeof(TestingPlatformBuilderHook).Assembly
        .GetType("AlleyCat.TestFramework.GodotTestFramework", throwOnError: true)!;
    private static readonly Lock _environmentLock = new();

    /// <summary>
    /// Ensures the Godot binary path defaults to <c>godot-mono</c>.
    /// </summary>
    [Fact]
    public void ResolveGodotBinaryPath_ReturnsDefault_WhenEnvironmentVariableIsUnset()
    {
        const string environmentVariable = "GODOT_PATH";

        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(environmentVariable, null);

            string resolvedPath = InvokePrivateStatic<string>("ResolveGodotBinaryPath");

            Assert.Equal("godot-mono", resolvedPath);
        }
    }

    /// <summary>
    /// Ensures the configured Godot binary path is honoured.
    /// </summary>
    [Fact]
    public void ResolveGodotBinaryPath_ReturnsConfiguredValue_WhenEnvironmentVariableIsSet()
    {
        const string environmentVariable = "GODOT_PATH";
        const string expectedPath = "custom-godot";

        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(environmentVariable, expectedPath);

            string resolvedPath = InvokePrivateStatic<string>("ResolveGodotBinaryPath");

            Assert.Equal(expectedPath, resolvedPath);
        }
    }

    /// <summary>
    /// Ensures timeout values fall back unless explicitly configured to a positive integer.
    /// </summary>
    [Theory]
    [InlineData("1200", 500, 1200)]
    [InlineData("0", 500, 500)]
    [InlineData("-15", 500, 500)]
    [InlineData("not-a-number", 500, 500)]
    public void ResolveTimeout_UsesPositiveConfiguredValue_OtherwiseFallsBack(string configuredValue, int fallbackMs, int expectedMs)
    {
        const string environmentVariable = "ALLEYCAT_FRAMEWORK_TEST_TIMEOUT_MS";

        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(environmentVariable, configuredValue);

            int resolvedTimeout = InvokePrivateStatic<int>("ResolveTimeout", environmentVariable, fallbackMs);

            Assert.Equal(expectedMs, resolvedTimeout);
        }
    }

    /// <summary>
    /// Ensures only parameterless <see cref="FactAttribute"/> methods are considered supported.
    /// </summary>
    [Fact]
    public void IsSupportedFactMethod_OnlyAcceptsParameterlessFactMethods()
    {
        MethodInfo supportedMethod = typeof(GodotTestFrameworkTests)
            .GetMethod(nameof(BuildStructuredErrorMessage_UsesFallbackMessage_WhenNoDetailsAreProvided))!;
        MethodInfo parameterisedFactMethod = typeof(GodotTestFrameworkTests)
            .GetMethod(nameof(ResolveTimeout_UsesPositiveConfiguredValue_OtherwiseFallsBack))!;
        MethodInfo nonFactMethod = typeof(GodotTestFrameworkTests)
            .GetMethod(nameof(HelperMethodWithoutTestAttribute), BindingFlags.NonPublic | BindingFlags.Static)!;

        bool isSupported = InvokePrivateStatic<bool>("IsSupportedFactMethod", supportedMethod);
        bool isParameterisedSupported = InvokePrivateStatic<bool>("IsSupportedFactMethod", parameterisedFactMethod);
        bool isNonFactSupported = InvokePrivateStatic<bool>("IsSupportedFactMethod", nonFactMethod);

        Assert.True(isSupported);
        Assert.False(isParameterisedSupported);
        Assert.False(isNonFactSupported);
    }

    /// <summary>
    /// Ensures the parser consumes the most recent structured run-fact line.
    /// </summary>
    [Fact]
    public void TryParseStructuredRunFactResult_ParsesTheLastStructuredResultLine()
    {
        string failedPayload = JsonSerializer.Serialize(new
        {
            Outcome = "failed",
            Message = "first",
            Stack = "trace-1"
        });
        string passedPayload = JsonSerializer.Serialize(new
        {
            Outcome = "passed",
            Message = (string?)null,
            Stack = (string?)null
        });

        string[] outputLines =
        [
            "non-structured-line",
            ResultMarkerPrefix + failedPayload,
            ResultMarkerPrefix + passedPayload,
        ];

        object? parsedResult = InvokePrivateStatic<object?>("TryParseStructuredRunFactResult", (object)outputLines);

        Assert.NotNull(parsedResult);
        Assert.Equal("passed", ReadStructuredResultProperty(parsedResult!, "Outcome"));
        Assert.Null(ReadStructuredResultProperty(parsedResult!, "Message"));
        Assert.Null(ReadStructuredResultProperty(parsedResult!, "Stack"));
    }

    /// <summary>
    /// Ensures malformed structured run-fact payloads are rejected.
    /// </summary>
    [Fact]
    public void TryParseStructuredRunFactResult_ReturnsNull_WhenStructuredPayloadIsInvalidJson()
    {
        string[] outputLines =
        [
            $"{ResultMarkerPrefix}{{not-valid-json}}",
        ];

        object? parsedResult = InvokePrivateStatic<object?>("TryParseStructuredRunFactResult", (object)outputLines);

        Assert.Null(parsedResult);
    }

    /// <summary>
    /// Ensures message and stack values are combined into one failure string.
    /// </summary>
    [Fact]
    public void BuildStructuredErrorMessage_CombinesMessageAndStack_WhenBothArePresent()
    {
        object structuredResult = CreateStructuredResult("failed", "Assertion failed", "stack trace");

        string message = InvokePrivateStatic<string>("BuildStructuredErrorMessage", structuredResult);

        Assert.Equal($"Assertion failed{Environment.NewLine}stack trace", message);
    }

    /// <summary>
    /// Ensures a fallback failure message is returned when no details are present.
    /// </summary>
    [Fact]
    public void BuildStructuredErrorMessage_UsesFallbackMessage_WhenNoDetailsAreProvided()
    {
        object structuredResult = CreateStructuredResult("failed", " ", "\t");

        string message = InvokePrivateStatic<string>("BuildStructuredErrorMessage", structuredResult);

        Assert.Equal("Godot run-fact reported an unknown failure.", message);
    }

    /// <summary>
    /// Ensures structured test failures take precedence over timeout wrappers.
    /// </summary>
    [Fact]
    public void BuildRunFactExecutionResult_ReturnsFailedOutcome_WhenStructuredFailureWasEmittedBeforeTimeout()
    {
        string failedPayload = JsonSerializer.Serialize(new
        {
            Outcome = "failed",
            Message = "boom",
            Stack = "trace"
        });

        object runResult = CreateGodotProcessRunResult(
            exitCode: null,
            stdOut: [ResultMarkerPrefix + failedPayload],
            stdErr: [],
            failureException: new TimeoutException("Godot process timed out after 120000ms."));

        object executionResult = InvokePrivateStatic<object>("BuildRunFactExecutionResult", runResult);

        Assert.Equal("Failed", ReadStructuredResultProperty(executionResult, "Outcome")?.ToString());
        var capturedError = ReadStructuredResultProperty(executionResult, "Error") as Exception;
        Assert.NotNull(capturedError);
        Assert.Equal($"boom{Environment.NewLine}trace", capturedError.Message);
    }

    /// <summary>
    /// Ensures run-fact early-exit detection includes both structured result and Godot error lines.
    /// </summary>
    [Fact]
    public void IsRunFactEarlyExitSignalLine_RecognisesStructuredResultAndGodotErrors()
    {
        bool structuredResultMatch = InvokePrivateStatic<bool>(
            "IsRunFactEarlyExitSignalLine",
            $"{ResultMarkerPrefix}{{}}");
        bool godotErrorMatch = InvokePrivateStatic<bool>(
            "IsRunFactEarlyExitSignalLine",
            "ERROR: test runtime fault");
        bool nonSignalMatch = InvokePrivateStatic<bool>(
            "IsRunFactEarlyExitSignalLine",
            "INFO: unrelated line");

        Assert.True(structuredResultMatch);
        Assert.True(godotErrorMatch);
        Assert.False(nonSignalMatch);
    }

    /// <summary>
    /// Ensures process execution short-circuits once a structured run-fact line is emitted.
    /// </summary>
    [Fact]
    public void RunGodotProcessAsync_CompletesEarly_WhenStructuredRunFactResultLineIsObserved()
    {
        const int timeoutMs = 4_000;

        var fakeProcess = FakeGodotProcess.Create(
            outputEvents:
            [
                new FakeOutputEvent(TimeSpan.FromMilliseconds(20), Stream: FakeOutputStream.StdOut, Line: "boot"),
                new FakeOutputEvent(TimeSpan.FromMilliseconds(60), Stream: FakeOutputStream.StdOut, Line: ResultMarkerPrefix + JsonSerializer.Serialize(new
                {
                    Outcome = "failed",
                    Message = "boom",
                    Stack = (string?)null,
                })),
            ],
            naturalExitDelay: TimeSpan.FromSeconds(5));

        object framework = CreateFrameworkInstance(new FakeGodotProcessFactory(fakeProcess));

        var stopwatch = Stopwatch.StartNew();
#pragma warning disable xUnit1031
        object runResult = InvokePrivateInstanceAsync<object>(
                framework,
                "RunGodotProcessAsync",
                (IReadOnlyList<string>)["--ignored"],
                timeoutMs,
                CancellationToken.None,
                new Func<string, bool>(line => line.StartsWith(ResultMarkerPrefix, StringComparison.Ordinal)))
            .GetAwaiter()
            .GetResult();
#pragma warning restore xUnit1031
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(timeoutMs),
            $"Expected early completion before timeout, elapsed {stopwatch.Elapsed.TotalMilliseconds}ms.");
        Assert.Null(ReadStructuredResultProperty(runResult, "FailureException") as Exception);
        Assert.True(fakeProcess.KillCalled);
    }

    /// <summary>
    /// Ensures process execution short-circuits when a Godot ERROR line is emitted on stderr.
    /// </summary>
    [Fact]
    public void RunGodotProcessAsync_CompletesEarly_WhenGodotErrorLineIsObservedOnStandardError()
    {
        const int timeoutMs = 4_000;

        var fakeProcess = FakeGodotProcess.Create(
            outputEvents:
            [
                new FakeOutputEvent(TimeSpan.FromMilliseconds(30), Stream: FakeOutputStream.StdErr, Line: "ERROR: simulated runtime fault"),
            ],
            naturalExitDelay: TimeSpan.FromSeconds(5));

        object framework = CreateFrameworkInstance(new FakeGodotProcessFactory(fakeProcess));

        var stopwatch = Stopwatch.StartNew();
#pragma warning disable xUnit1031
        object runResult = InvokePrivateInstanceAsync<object>(
                framework,
                "RunGodotProcessAsync",
                (IReadOnlyList<string>)["--ignored"],
                timeoutMs,
                CancellationToken.None,
                new Func<string, bool>(line => line.StartsWith("ERROR:", StringComparison.Ordinal)))
            .GetAwaiter()
            .GetResult();
#pragma warning restore xUnit1031
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(timeoutMs),
            $"Expected early completion before timeout, elapsed {stopwatch.Elapsed.TotalMilliseconds}ms.");
        Assert.Null(ReadStructuredResultProperty(runResult, "FailureException") as Exception);
        Assert.True(fakeProcess.KillCalled);
        Assert.Contains("ERROR: simulated runtime fault", (IReadOnlyList<string>)ReadStructuredResultProperty(runResult, "StdErr")!);
    }

    /// <summary>
    /// Ensures headless mode defaults to <c>false</c> when no attribute is present.
    /// </summary>
    [Fact]
    public void ResolveHeadlessMode_ReturnsFalse_WhenNoAttributeIsPresent()
    {
        MethodInfo method = typeof(HeadlessFixtureNoAttribute)
            .GetMethod(nameof(HeadlessFixtureNoAttribute.TestMethod))!;

        bool result = InvokePrivateStatic<bool>("ResolveHeadlessMode", method);

        Assert.False(result);
    }

    /// <summary>
    /// Ensures <c>[Headless(false)]</c> on a method disables headless mode.
    /// </summary>
    [Fact]
    public void ResolveHeadlessMode_ReturnsFalse_WhenMethodHasHeadlessFalse()
    {
        MethodInfo method = typeof(HeadlessFixtureNoAttribute)
            .GetMethod(nameof(HeadlessFixtureNoAttribute.NonHeadlessMethod))!;

        bool result = InvokePrivateStatic<bool>("ResolveHeadlessMode", method);

        Assert.False(result);
    }

    /// <summary>
    /// Ensures <c>[Headless(true)]</c> on a method explicitly enables headless mode.
    /// </summary>
    [Fact]
    public void ResolveHeadlessMode_ReturnsTrue_WhenMethodHasHeadlessTrue()
    {
        MethodInfo method = typeof(HeadlessFixtureNoAttribute)
            .GetMethod(nameof(HeadlessFixtureNoAttribute.ExplicitHeadlessMethod))!;

        bool result = InvokePrivateStatic<bool>("ResolveHeadlessMode", method);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures class-level <c>[Headless]</c> is used when the method has no attribute.
    /// </summary>
    [Fact]
    public void ResolveHeadlessMode_FallsBackToClassAttribute_WhenMethodHasNoAttribute()
    {
        MethodInfo method = typeof(HeadlessFixtureClassDisabled)
            .GetMethod(nameof(HeadlessFixtureClassDisabled.TestMethod))!;

        bool result = InvokePrivateStatic<bool>("ResolveHeadlessMode", method);

        Assert.False(result);
    }

    /// <summary>
    /// Ensures method-level attribute takes precedence over class-level attribute.
    /// </summary>
    [Fact]
    public void ResolveHeadlessMode_MethodAttributeTakesPrecedenceOverClassAttribute()
    {
        MethodInfo method = typeof(HeadlessFixtureClassDisabled)
            .GetMethod(nameof(HeadlessFixtureClassDisabled.HeadlessMethod))!;

        bool result = InvokePrivateStatic<bool>("ResolveHeadlessMode", method);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures <c>--headless</c> is excluded when no attribute is present and no CLI override is set.
    /// </summary>
    [Fact]
    public void CreateRunFactArguments_ExcludesHeadless_WhenNoAttributeAndNoOverride()
    {
        object framework = CreateFrameworkInstance(headlessOverride: false);
        MethodInfo method = typeof(HeadlessFixtureNoAttribute)
            .GetMethod(nameof(HeadlessFixtureNoAttribute.TestMethod))!;

        IReadOnlyList<string> args = InvokePrivateInstance<IReadOnlyList<string>>(framework, "CreateRunFactArguments", method);

        Assert.DoesNotContain("--headless", args);
    }

    /// <summary>
    /// Ensures <c>--headless</c> is included when the <c>--headless</c> CLI override is set.
    /// </summary>
    [Fact]
    public void CreateRunFactArguments_IncludesHeadless_WhenHeadlessOverrideIsSet()
    {
        object framework = CreateFrameworkInstance(headlessOverride: true);
        MethodInfo method = typeof(HeadlessFixtureNoAttribute)
            .GetMethod(nameof(HeadlessFixtureNoAttribute.TestMethod))!;

        IReadOnlyList<string> args = InvokePrivateInstance<IReadOnlyList<string>>(framework, "CreateRunFactArguments", method);

        Assert.Contains("--headless", args);
    }

    /// <summary>
    /// Ensures <c>--headless</c> is excluded when the method explicitly sets <c>[Headless(false)]</c>.
    /// </summary>
    [Fact]
    public void CreateRunFactArguments_ExcludesHeadless_WhenMethodHasHeadlessFalse()
    {
        object framework = CreateFrameworkInstance(headlessOverride: false);
        MethodInfo method = typeof(HeadlessFixtureNoAttribute)
            .GetMethod(nameof(HeadlessFixtureNoAttribute.NonHeadlessMethod))!;

        IReadOnlyList<string> args = InvokePrivateInstance<IReadOnlyList<string>>(framework, "CreateRunFactArguments", method);

        Assert.DoesNotContain("--headless", args);
    }

    /// <summary>
    /// Ensures the CLI <c>--headless</c> override takes precedence over a method-level
    /// <c>[Headless(false)]</c> attribute.
    /// </summary>
    [Fact]
    public void CreateRunFactArguments_OverridesMethodAttribute_WhenHeadlessOverrideIsSet()
    {
        object framework = CreateFrameworkInstance(headlessOverride: true);
        MethodInfo method = typeof(HeadlessFixtureNoAttribute)
            .GetMethod(nameof(HeadlessFixtureNoAttribute.NonHeadlessMethod))!;

        IReadOnlyList<string> args = InvokePrivateInstance<IReadOnlyList<string>>(framework, "CreateRunFactArguments", method);

        Assert.Contains("--headless", args);
    }

    /// <summary>
    /// Ensures <c>--xr-mode off</c> is always included regardless of headless settings.
    /// </summary>
    [Fact]
    public void CreateRunFactArguments_IncludesXrModeOff_RegardlessOfHeadlessMode()
    {
        object framework = CreateFrameworkInstance(headlessOverride: true);
        MethodInfo method = typeof(HeadlessFixtureNoAttribute)
            .GetMethod(nameof(HeadlessFixtureNoAttribute.TestMethod))!;

        IReadOnlyList<string> args = InvokePrivateInstance<IReadOnlyList<string>>(framework, "CreateRunFactArguments", method);

        int xrModeIndex = args.ToList().IndexOf("--xr-mode");
        Assert.True(xrModeIndex >= 0, "Expected --xr-mode in arguments.");
        Assert.Equal("off", args[xrModeIndex + 1]);
    }

    private static object CreateFrameworkInstance(object? processFactory = null)
    {
        Type selectorType = _godotTestFrameworkType.Assembly
            .GetType("AlleyCat.TestFramework.GodotCliTestSelector", throwOnError: true)!;
        object selector = selectorType.GetProperty("None", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new MissingMemberException(selectorType.FullName, "None");

        ConstructorInfo constructor = _godotTestFrameworkType
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .Single(candidate => candidate.GetParameters().Length == 3);

        return constructor.Invoke([Assembly.GetExecutingAssembly(), selector, processFactory]);
    }

    private static object CreateFrameworkInstance(bool headlessOverride, object? processFactory = null)
    {
        Type selectorType = _godotTestFrameworkType.Assembly
            .GetType("AlleyCat.TestFramework.GodotCliTestSelector", throwOnError: true)!;
        object selector = selectorType.GetProperty("None", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new MissingMemberException(selectorType.FullName, "None");

        ConstructorInfo constructor = _godotTestFrameworkType
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .Single(candidate => candidate.GetParameters().Length == 4);

        return constructor.Invoke([Assembly.GetExecutingAssembly(), selector, processFactory, headlessOverride]);
    }

    private sealed class FakeGodotProcessFactory(FakeGodotProcess process) : GodotTestFramework.IGodotProcessFactory
    {
        public GodotTestFramework.IGodotProcess Create(IReadOnlyList<string> commandLineArguments) => process;
    }

    private enum FakeOutputStream
    {
        StdOut,
        StdErr,
    }

    private sealed record FakeOutputEvent(TimeSpan Delay, FakeOutputStream Stream, string Line);

    private sealed class FakeGodotProcess : GodotTestFramework.IGodotProcess
    {
        private readonly Channel<string> _stdOutChannel = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _stdErrChannel = Channel.CreateUnbounded<string>();
        private readonly IReadOnlyList<FakeOutputEvent> _outputEvents;
        private readonly TimeSpan _naturalExitDelay;
        private readonly TaskCompletionSource _exitTaskSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();

        private FakeGodotProcess(IReadOnlyList<FakeOutputEvent> outputEvents, TimeSpan naturalExitDelay)
        {
            _outputEvents = outputEvents;
            _naturalExitDelay = naturalExitDelay;
        }

        public bool KillCalled
        {
            get; private set;
        }

        public bool HasExited => _exitTaskSource.Task.IsCompleted;

        public int ExitCode
        {
            get; private set;
        }

        public static FakeGodotProcess Create(IReadOnlyList<FakeOutputEvent> outputEvents, TimeSpan naturalExitDelay)
            => new(outputEvents, naturalExitDelay);

        public bool Start()
        {
            _ = RunLifecycleAsync();
            return true;
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken) => await _exitTaskSource.Task.WaitAsync(cancellationToken);

        public async Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken)
        {
            return await _stdOutChannel.Reader.WaitToReadAsync(cancellationToken)
                ? await _stdOutChannel.Reader.ReadAsync(cancellationToken)
                : null;
        }

        public async Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken)
        {
            return await _stdErrChannel.Reader.WaitToReadAsync(cancellationToken)
                ? await _stdErrChannel.Reader.ReadAsync(cancellationToken)
                : null;
        }

        public void Kill(bool entireProcessTree)
        {
            if (HasExited)
            {
                return;
            }

            KillCalled = true;
            _lifetimeCancellationTokenSource.Cancel();
            Complete(exitCode: -1);
        }

        public void Dispose()
        {
            _lifetimeCancellationTokenSource.Cancel();
            Complete(exitCode: ExitCode);
            _lifetimeCancellationTokenSource.Dispose();
        }

        private async Task RunLifecycleAsync()
        {
            try
            {
                foreach (FakeOutputEvent outputEvent in _outputEvents)
                {
                    await Task.Delay(outputEvent.Delay, _lifetimeCancellationTokenSource.Token);
                    Channel<string> channel = outputEvent.Stream == FakeOutputStream.StdOut
                        ? _stdOutChannel
                        : _stdErrChannel;
                    await channel.Writer.WriteAsync(outputEvent.Line, _lifetimeCancellationTokenSource.Token);
                }

                await Task.Delay(_naturalExitDelay, _lifetimeCancellationTokenSource.Token);
                Complete(exitCode: 0);
            }
            catch (OperationCanceledException)
            {
                Complete(exitCode: -1);
            }
        }

        private void Complete(int exitCode)
        {
            if (HasExited)
            {
                return;
            }

            ExitCode = exitCode;
            _ = _stdOutChannel.Writer.TryComplete();
            _ = _stdErrChannel.Writer.TryComplete();
            _ = _exitTaskSource.TrySetResult();
        }
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        MethodInfo method = _godotTestFrameworkType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(_godotTestFrameworkType.FullName, methodName);

        object? result = method.Invoke(null, args);
        return (T)result!;
    }

    private static async Task<T> InvokePrivateInstanceAsync<T>(object instance, string methodName, params object?[] args)
    {
        MethodInfo method = instance.GetType()
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(candidate => candidate.Name == methodName && candidate.GetParameters().Length == args.Length);

        object? invocationResult = method.Invoke(instance, args);
        Assert.NotNull(invocationResult);

        Task task = Assert.IsAssignableFrom<Task>(invocationResult);
        await task;

        object? result = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(task);
        return (T)result!;
    }

    private static T InvokePrivateInstance<T>(object instance, string methodName, params object?[] args)
    {
        MethodInfo method = instance.GetType()
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(candidate => candidate.Name == methodName && candidate.GetParameters().Length == args.Length);

        return (T)method.Invoke(instance, args)!;
    }

    private static object CreateStructuredResult(string outcome, string? message, string? stack)
    {
        Type structuredResultType = _godotTestFrameworkType.GetNestedType("StructuredRunFactResult", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(_godotTestFrameworkType.FullName, "StructuredRunFactResult");

        return Activator.CreateInstance(structuredResultType, outcome, message, stack)
            ?? throw new InvalidOperationException("Failed to construct structured result instance.");
    }

    private static object CreateGodotProcessRunResult(
        int? exitCode,
        IReadOnlyList<string> stdOut,
        IReadOnlyList<string> stdErr,
        Exception? failureException)
    {
        Type runResultType = _godotTestFrameworkType.GetNestedType("GodotProcessRunResult", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(_godotTestFrameworkType.FullName, "GodotProcessRunResult");

        return Activator.CreateInstance(runResultType, exitCode, stdOut, stdErr, failureException)
            ?? throw new InvalidOperationException("Failed to construct process run result instance.");
    }

    private static object? ReadStructuredResultProperty(object structuredResult, string propertyName)
    {
        PropertyInfo property = structuredResult.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(structuredResult.GetType().FullName, propertyName);
        return property.GetValue(structuredResult);
    }

    private static void HelperMethodWithoutTestAttribute()
    {
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _variableName;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string variableName, string? value)
        {
            _variableName = variableName;
            _previousValue = Environment.GetEnvironmentVariable(variableName);
            Environment.SetEnvironmentVariable(variableName, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_variableName, _previousValue);
    }

    private sealed class HeadlessFixtureNoAttribute
    {
        public static void TestMethod()
        {
        }

        [Headless(false)]
        public static void NonHeadlessMethod()
        {
        }

        [Headless(true)]
        public static void ExplicitHeadlessMethod()
        {
        }
    }

    [Headless(false)]
    private sealed class HeadlessFixtureClassDisabled
    {
        public static void TestMethod()
        {
        }

        [Headless(true)]
        public static void HeadlessMethod()
        {
        }
    }

}
