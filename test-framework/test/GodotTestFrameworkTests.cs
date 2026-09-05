using System.Reflection;
using Xunit;

namespace AlleyCat.TestFramework.Tests;

/// <summary>
/// Behaviour-focused tests for key Godot test framework logic.
/// </summary>
public sealed class GodotTestFrameworkTests
{
    private const string ProbeSuccessMarker = "ALLEYCAT_INTEGRATION_PROBE_SUCCESS";
    private const string ImportPreflightEnvironmentVariable = "ALLEYCAT_INTEGRATION_IMPORT_PREFLIGHT";
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
            .GetMethod(nameof(ResolveGodotBinaryPath_ReturnsDefault_WhenEnvironmentVariableIsUnset))!;
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
    /// Ensures default preflight execution uses only the runtime integration probe.
    /// </summary>
    [Fact]
    public void RunPreflightAsync_RunsProbeOnly_WhenImportPreflightIsUnset()
    {
        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(ImportPreflightEnvironmentVariable, null);
            var factory = new FakeGodotProcessFactory(CreateSuccessfulProbeProcess());
            object framework = CreateFrameworkInstance(factory);

#pragma warning disable xUnit1031
            Exception? preflightError = InvokePrivateInstanceAsync<Exception?>(
                    framework,
                    "RunPreflightAsync",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
#pragma warning restore xUnit1031

            Assert.Null(preflightError);
            IReadOnlyList<IReadOnlyList<string>> invocations = factory.Invocations;
            IReadOnlyList<string> probeArgs = Assert.Single(invocations);
            Assert.DoesNotContain("--import", probeArgs);
            Assert.Contains("--integration-probe", probeArgs);
        }
    }

    /// <summary>
    /// Ensures opt-in import preflight runs before the runtime integration probe.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void RunPreflightAsync_RunsImportBeforeProbe_WhenImportPreflightIsTruthy(string configuredValue)
    {
        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(ImportPreflightEnvironmentVariable, configuredValue);
            var factory = new FakeGodotProcessFactory(
                FakeGodotProcess.Create(outputEvents: [], naturalExitDelay: TimeSpan.Zero),
                CreateSuccessfulProbeProcess());
            object framework = CreateFrameworkInstance(factory);

#pragma warning disable xUnit1031
            Exception? preflightError = InvokePrivateInstanceAsync<Exception?>(
                    framework,
                    "RunPreflightAsync",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
#pragma warning restore xUnit1031

            Assert.Null(preflightError);
            Assert.Equal(2, factory.Invocations.Count);
            Assert.Contains("--import", factory.Invocations[0]);
            Assert.Contains("--integration-probe", factory.Invocations[1]);
        }
    }

    /// <summary>
    /// Ensures non-truthy values do not accidentally enable import preflight.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("enabled")]
    public void RunPreflightAsync_RunsProbeOnly_WhenImportPreflightIsNotTruthy(string configuredValue)
    {
        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(ImportPreflightEnvironmentVariable, configuredValue);
            var factory = new FakeGodotProcessFactory(CreateSuccessfulProbeProcess());
            object framework = CreateFrameworkInstance(factory);

#pragma warning disable xUnit1031
            Exception? preflightError = InvokePrivateInstanceAsync<Exception?>(
                    framework,
                    "RunPreflightAsync",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
#pragma warning restore xUnit1031

            Assert.Null(preflightError);
            IReadOnlyList<string> probeArgs = Assert.Single(factory.Invocations);
            Assert.DoesNotContain("--import", probeArgs);
            Assert.Contains("--integration-probe", probeArgs);
        }
    }

    /// <summary>
    /// Ensures opt-in import preflight uses safer editor startup arguments.
    /// </summary>
    [Fact]
    public void CreateImportArguments_IncludesHeadlessXrPathImportAndRecoveryMode()
    {
        IReadOnlyList<string> args = InvokePrivateStatic<IReadOnlyList<string>>("CreateImportArguments");

        Assert.Contains("--headless", args);
        Assert.Contains("--import", args);
        Assert.Contains("--recovery-mode", args);

        int xrModeIndex = args.ToList().IndexOf("--xr-mode");
        Assert.True(xrModeIndex >= 0, "Expected --xr-mode in arguments.");
        Assert.Equal("off", args[xrModeIndex + 1]);

        int pathIndex = args.ToList().IndexOf("--path");
        Assert.True(pathIndex >= 0, "Expected --path in arguments.");
        Assert.Equal("game", args[pathIndex + 1]);
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
    /// Ensures <c>--headless</c> is excluded for windowed sessions when no attribute or override is present.
    /// </summary>
    [Fact]
    public void CreateSessionArguments_ExcludesHeadless_WhenWindowedModeIsRequested()
    {
        object framework = CreateFrameworkInstance(headlessOverride: false);

        IReadOnlyList<string> args = InvokePrivateInstance<IReadOnlyList<string>>(framework, "CreateSessionArguments", false);

        Assert.DoesNotContain("--headless", args);
        Assert.Contains(GodotSessionProtocol.SessionCommandArg, args);
    }

    /// <summary>
    /// Ensures <c>--headless</c> is included for headless sessions.
    /// </summary>
    [Fact]
    public void CreateSessionArguments_IncludesHeadless_WhenHeadlessModeIsRequested()
    {
        object framework = CreateFrameworkInstance(headlessOverride: false);

        IReadOnlyList<string> args = InvokePrivateInstance<IReadOnlyList<string>>(framework, "CreateSessionArguments", true);

        Assert.Contains("--headless", args);
    }

    /// <summary>
    /// Ensures session launches always disable XR and point at the game project and test assembly.
    /// </summary>
    [Fact]
    public void CreateSessionArguments_IncludesXrModeOffGamePathAndProbeAssembly()
    {
        object framework = CreateFrameworkInstance(headlessOverride: false);

        IReadOnlyList<string> args = InvokePrivateInstance<IReadOnlyList<string>>(framework, "CreateSessionArguments", true);

        int xrModeIndex = args.ToList().IndexOf("--xr-mode");
        Assert.True(xrModeIndex >= 0, "Expected --xr-mode in arguments.");
        Assert.Equal("off", args[xrModeIndex + 1]);

        int pathIndex = args.ToList().IndexOf("--path");
        Assert.True(pathIndex >= 0, "Expected --path in arguments.");
        Assert.Equal("game", args[pathIndex + 1]);

        int sessionIndex = args.ToList().IndexOf(GodotSessionProtocol.SessionCommandArg);
        Assert.True(sessionIndex >= 0, "Expected the session command argument.");
        Assert.Equal("--probe-assembly", args[sessionIndex + 1]);
        Assert.Equal(Assembly.GetExecutingAssembly().Location, args[sessionIndex + 2]);
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

    private static FakeGodotProcess CreateSuccessfulProbeProcess()
    {
        return FakeGodotProcess.Create(
            outputEvents: [new FakeOutputEvent(TimeSpan.Zero, Stream: FakeOutputStream.StdOut, Line: ProbeSuccessMarker)],
            naturalExitDelay: TimeSpan.Zero);
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

        Task task = Assert.IsType<Task>(invocationResult, exactMatch: false);
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
