using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.TestHost;
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
    /// Ensures a test without any <c>[LiveLlm]</c> marker is not live-gated.
    /// </summary>
    [Fact]
    public void IsLiveLlmTest_ReturnsFalse_WhenNoMarkerIsPresent()
    {
        MethodInfo method = GetLiveLlmFixtureMethod(typeof(LiveLlmUnmarkedFixture), nameof(LiveLlmUnmarkedFixture.OrdinaryTest));

        bool result = InvokePrivateStatic<bool>("IsLiveLlmTest", method);

        Assert.False(result);
    }

    /// <summary>
    /// Ensures a method-level <c>[LiveLlm]</c> marker marks the test as live.
    /// </summary>
    [Fact]
    public void IsLiveLlmTest_ReturnsTrue_WhenMethodIsMarked()
    {
        MethodInfo method = GetLiveLlmFixtureMethod(typeof(LiveLlmMethodMarkedFixture), nameof(LiveLlmMethodMarkedFixture.LiveTest));

        bool result = InvokePrivateStatic<bool>("IsLiveLlmTest", method);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures a class-level <c>[LiveLlm]</c> marker marks unmarked methods of the declaring class as live.
    /// </summary>
    [Fact]
    public void IsLiveLlmTest_ReturnsTrue_WhenDeclaringClassIsMarked()
    {
        MethodInfo method = GetLiveLlmFixtureMethod(typeof(LiveLlmClassMarkedFixture), nameof(LiveLlmClassMarkedFixture.LiveTest));

        bool result = InvokePrivateStatic<bool>("IsLiveLlmTest", method);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures class-level and method-level <c>[LiveLlm]</c> markers combine with OR.
    /// </summary>
    [Fact]
    public void IsLiveLlmTest_ReturnsTrue_WhenMethodAndDeclaringClassAreMarked()
    {
        MethodInfo method = GetLiveLlmFixtureMethod(typeof(LiveLlmMethodAndClassMarkedFixture), nameof(LiveLlmMethodAndClassMarkedFixture.LiveTest));

        bool result = InvokePrivateStatic<bool>("IsLiveLlmTest", method);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures a method declared on a class inside a <c>[LiveLlm]</c>-marked hierarchy is live-marked through
    /// its declaring type without changing method identity.
    /// </summary>
    [Fact]
    public void IsLiveLlmTest_ReturnsTrue_WhenMethodIsDeclaredOnDerivedClassOfMarkedBase()
    {
        MethodInfo method = GetLiveLlmFixtureMethod(typeof(LiveLlmDerivedFixture), nameof(LiveLlmDerivedFixture.DerivedLiveTest));

        bool result = InvokePrivateStatic<bool>("IsLiveLlmTest", method);

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

    /// <summary>
    /// Ensures live-marked tests produce no run nodes and launch no process when <c>--live-llm</c> is absent,
    /// even when explicitly selected by UID.
    /// </summary>
    [Fact]
    public void ExecuteRequestAsync_Run_ExcludesLiveMarkedTests_WhenLiveLlmFlagIsAbsent()
    {
        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(ImportPreflightEnvironmentVariable, null);

            var factory = new FakeGodotProcessFactory();
            GodotTestFramework framework = CreateFramework(factory, liveLlmEnabled: false);
            var messageBus = new RecordingMessageBus();
            var request = new RunTestExecutionRequest(
                CreateTestSessionContext(),
                new TestNodeUidListFilter([GetLiveLlmFixtureUid(typeof(LiveLlmMethodMarkedFixture), nameof(LiveLlmMethodMarkedFixture.LiveTest))]));

            ExecuteRequest(framework, CreateRequestContext(request, messageBus));

            Assert.Empty(messageBus.Published);
            Assert.Empty(factory.Invocations);
        }
    }

    /// <summary>
    /// Ensures ordinary tests still run when <c>--live-llm</c> is absent and a mixed selection also names a
    /// live-marked test.
    /// </summary>
    [Fact]
    public void ExecuteRequestAsync_Run_DoesNotExcludeOrdinaryTests_WhenLiveLlmFlagIsAbsent()
    {
        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(ImportPreflightEnvironmentVariable, null);

            TestNodeUid ordinaryUid = GetLiveLlmFixtureUid(typeof(LiveLlmUnmarkedFixture), nameof(LiveLlmUnmarkedFixture.OrdinaryTest));
            TestNodeUid liveUid = GetLiveLlmFixtureUid(typeof(LiveLlmMethodMarkedFixture), nameof(LiveLlmMethodMarkedFixture.LiveTest));

            var session = FakeGodotProcess.CreateSession(commandStages: [ResultStage(ordinaryUid)]);
            var factory = new FakeGodotProcessFactory(CreateSuccessfulProbeProcess(), session);
            GodotTestFramework framework = CreateFramework(factory, liveLlmEnabled: false);
            var messageBus = new RecordingMessageBus();
            var request = new RunTestExecutionRequest(
                CreateTestSessionContext(),
                new TestNodeUidListFilter([ordinaryUid, liveUid]));

            ExecuteRequest(framework, CreateRequestContext(request, messageBus));

            Assert.Equal(2, factory.Invocations.Count);
            Assert.Contains("--integration-probe", factory.Invocations[0]);

            List<string> runRequestIds = GetRunRequestIds(session);
            Assert.Equal([ordinaryUid.Value], runRequestIds);

            List<TestNode> nodes = NodesOf(messageBus);
            Assert.Equal(2, nodes.Count);
            AssertNodeState(nodes[0], ordinaryUid, IsInProgressState);
            AssertNodeState(nodes[1], ordinaryUid, IsPassedState);
        }
    }

    /// <summary>
    /// Ensures <c>--live-llm</c> permits live-marked tests to run and report passed nodes while ordinary tests
    /// remain selected, preserving session routing.
    /// </summary>
    [Fact]
    public void ExecuteRequestAsync_Run_IncludesLiveMarkedTests_WhenLiveLlmFlagIsPresent()
    {
        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(ImportPreflightEnvironmentVariable, null);

            TestNodeUid liveUid = GetLiveLlmFixtureUid(typeof(LiveLlmMethodMarkedFixture), nameof(LiveLlmMethodMarkedFixture.LiveTest));
            TestNodeUid ordinaryUid = GetLiveLlmFixtureUid(typeof(LiveLlmUnmarkedFixture), nameof(LiveLlmUnmarkedFixture.OrdinaryTest));

            var session = FakeGodotProcess.CreateSession(commandStages: [ResultStage(liveUid), ResultStage(ordinaryUid)]);
            var factory = new FakeGodotProcessFactory(CreateSuccessfulProbeProcess(), session);
            GodotTestFramework framework = CreateFramework(factory, liveLlmEnabled: true);
            var messageBus = new RecordingMessageBus();
            var request = new RunTestExecutionRequest(
                CreateTestSessionContext(),
                new TestNodeUidListFilter([liveUid, ordinaryUid]));

            ExecuteRequest(framework, CreateRequestContext(request, messageBus));

            Assert.Equal(2, factory.Invocations.Count);
            Assert.Contains("--integration-probe", factory.Invocations[0]);
            Assert.Contains(GodotSessionProtocol.SessionCommandArg, factory.Invocations[1]);
            Assert.DoesNotContain("--headless", factory.Invocations[1]);

            List<string> runRequestIds = GetRunRequestIds(session);
            Assert.Equal([liveUid.Value, ordinaryUid.Value], runRequestIds);

            List<TestNode> nodes = NodesOf(messageBus);
            Assert.Equal(4, nodes.Count);
            AssertNodeState(nodes[0], liveUid, IsInProgressState);
            AssertNodeState(nodes[1], liveUid, IsPassedState);
            AssertNodeState(nodes[2], ordinaryUid, IsInProgressState);
            AssertNodeState(nodes[3], ordinaryUid, IsPassedState);
        }
    }

    /// <summary>
    /// Ensures an exact <c>--test-method</c> selector cannot bypass the live gate.
    /// </summary>
    [Fact]
    public void ExecuteRequestAsync_Run_ExactMethodSelectorCannotBypassLiveGate()
    {
        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(ImportPreflightEnvironmentVariable, null);

            var factory = new FakeGodotProcessFactory();
            GodotTestFramework framework = CreateFramework(
                factory,
                liveLlmEnabled: false,
                selector: GodotCliTestSelector.ForMethod(
                    typeof(LiveLlmMethodMarkedFixture).FullName!,
                    nameof(LiveLlmMethodMarkedFixture.LiveTest)));
            var messageBus = new RecordingMessageBus();
            var request = new RunTestExecutionRequest(CreateTestSessionContext());

            ExecuteRequest(framework, CreateRequestContext(request, messageBus));

            Assert.Empty(messageBus.Published);
            Assert.Empty(factory.Invocations);
        }
    }

    /// <summary>
    /// Ensures an exact <c>--test-class</c> selector cannot bypass the live gate for class-marked tests.
    /// </summary>
    [Fact]
    public void ExecuteRequestAsync_Run_ExactClassSelectorCannotBypassLiveGate()
    {
        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(ImportPreflightEnvironmentVariable, null);

            var factory = new FakeGodotProcessFactory();
            GodotTestFramework framework = CreateFramework(
                factory,
                liveLlmEnabled: false,
                selector: GodotCliTestSelector.ForClass(typeof(LiveLlmClassMarkedFixture).FullName!));
            var messageBus = new RecordingMessageBus();
            var request = new RunTestExecutionRequest(CreateTestSessionContext());

            ExecuteRequest(framework, CreateRequestContext(request, messageBus));

            Assert.Empty(messageBus.Published);
            Assert.Empty(factory.Invocations);
        }
    }

    /// <summary>
    /// Ensures discovery excludes live-marked tests when <c>--live-llm</c> is absent, even when explicitly
    /// selected by UID, and launches no process.
    /// </summary>
    [Fact]
    public async Task ExecuteRequestAsync_Discovery_ExcludesLiveMarkedTests_WhenLiveLlmFlagIsAbsent()
    {
        var factory = new FakeGodotProcessFactory();
        GodotTestFramework framework = CreateFramework(factory, liveLlmEnabled: false);
        var messageBus = new RecordingMessageBus();
        var request = new DiscoverTestExecutionRequest(
            CreateTestSessionContext(),
            new TestNodeUidListFilter([GetLiveLlmFixtureUid(typeof(LiveLlmMethodMarkedFixture), nameof(LiveLlmMethodMarkedFixture.LiveTest))]));

        await framework.ExecuteRequestAsync(CreateRequestContext(request, messageBus));

        Assert.Empty(messageBus.Published);
        Assert.Empty(factory.Invocations);
    }

    /// <summary>
    /// Ensures discovery keeps ordinary tests while excluding only the live-marked entries of a mixed selection.
    /// </summary>
    [Fact]
    public async Task ExecuteRequestAsync_Discovery_OnlyExcludesLiveMarkedTests_WhenLiveLlmFlagIsAbsent()
    {
        GodotTestFramework framework = CreateFramework(new FakeGodotProcessFactory(), liveLlmEnabled: false);
        var messageBus = new RecordingMessageBus();
        TestNodeUid ordinaryUid = GetLiveLlmFixtureUid(typeof(LiveLlmUnmarkedFixture), nameof(LiveLlmUnmarkedFixture.OrdinaryTest));
        TestNodeUid liveUid = GetLiveLlmFixtureUid(typeof(LiveLlmMethodMarkedFixture), nameof(LiveLlmMethodMarkedFixture.LiveTest));
        var request = new DiscoverTestExecutionRequest(
            CreateTestSessionContext(),
            new TestNodeUidListFilter([ordinaryUid, liveUid]));

        await framework.ExecuteRequestAsync(CreateRequestContext(request, messageBus));

        List<TestNode> nodes = NodesOf(messageBus);
        TestNode discoveredNode = Assert.Single(nodes);
        AssertNodeState(discoveredNode, ordinaryUid, IsDiscoveredState);
    }

    /// <summary>
    /// Ensures discovery reports live-marked tests alongside ordinary tests when <c>--live-llm</c> is present.
    /// </summary>
    [Fact]
    public async Task ExecuteRequestAsync_Discovery_IncludesLiveMarkedTests_WhenLiveLlmFlagIsPresent()
    {
        GodotTestFramework framework = CreateFramework(new FakeGodotProcessFactory(), liveLlmEnabled: true);
        var messageBus = new RecordingMessageBus();
        TestNodeUid liveUid = GetLiveLlmFixtureUid(typeof(LiveLlmClassMarkedFixture), nameof(LiveLlmClassMarkedFixture.LiveTest));
        var request = new DiscoverTestExecutionRequest(
            CreateTestSessionContext(),
            new TestNodeUidListFilter([liveUid]));

        await framework.ExecuteRequestAsync(CreateRequestContext(request, messageBus));

        List<TestNode> nodes = NodesOf(messageBus);
        TestNode discoveredNode = Assert.Single(nodes);
        AssertNodeState(discoveredNode, liveUid, IsDiscoveredState);
    }

    /// <summary>
    /// Ensures the builder policy fails closed for every flag when the command-line service is absent.
    /// </summary>
    [Fact]
    public void GetCliPolicy_FailsClosed_WhenCommandLineServiceIsAbsent()
    {
        (GodotCliTestSelector selector, bool headlessOverride, bool liveLlmEnabled) =
            InvokeGetCliPolicy(new StubServiceProvider(service: null));

        Assert.Equal(GodotCliTestSelector.None, selector);
        Assert.False(headlessOverride);
        Assert.False(liveLlmEnabled);
    }

    /// <summary>
    /// Ensures the builder policy enables live permission alone when only <c>--live-llm</c> is supplied.
    /// </summary>
    [Fact]
    public void GetCliPolicy_EnablesLiveLlmOnly_WhenLiveLlmFlagIsSet()
    {
        var commandLineOptions = new StubCommandLineOptions(
            new Dictionary<string, string[]> { [GodotTestCommandLineOptions.LiveLlmOptionName] = [] });

        (GodotCliTestSelector selector, bool headlessOverride, bool liveLlmEnabled) =
            InvokeGetCliPolicy(new StubServiceProvider(commandLineOptions));

        Assert.Equal(GodotCliTestSelector.None, selector);
        Assert.False(headlessOverride);
        Assert.True(liveLlmEnabled);
    }

    /// <summary>
    /// Ensures the builder policy carries headless and live permissions together without interference.
    /// </summary>
    [Fact]
    public void GetCliPolicy_CombinesHeadlessAndLiveLlmFlags()
    {
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.HeadlessOptionName] = [],
            [GodotTestCommandLineOptions.LiveLlmOptionName] = [],
        });

        (GodotCliTestSelector selector, bool headlessOverride, bool liveLlmEnabled) =
            InvokeGetCliPolicy(new StubServiceProvider(commandLineOptions));

        Assert.Equal(GodotCliTestSelector.None, selector);
        Assert.True(headlessOverride);
        Assert.True(liveLlmEnabled);
    }

    /// <summary>
    /// Ensures the builder policy parses exact selectors alongside the live permission.
    /// </summary>
    [Fact]
    public void GetCliPolicy_ParsesMethodSelectorAlongsideLiveLlmFlag()
    {
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestMethodOptionName] =
            [$"{typeof(LiveLlmMethodMarkedFixture).FullName!}.{nameof(LiveLlmMethodMarkedFixture.LiveTest)}"],
            [GodotTestCommandLineOptions.LiveLlmOptionName] = [],
        });

        (GodotCliTestSelector selector, bool headlessOverride, bool liveLlmEnabled) =
            InvokeGetCliPolicy(new StubServiceProvider(commandLineOptions));

        Assert.True(selector.Matches(GetLiveLlmFixtureMethod(typeof(LiveLlmMethodMarkedFixture), nameof(LiveLlmMethodMarkedFixture.LiveTest))));
        Assert.False(headlessOverride);
        Assert.True(liveLlmEnabled);
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

    /// <summary>
    /// Creates a framework instance through the live-permission constructor overload used by the builder hook.
    /// </summary>
    private static GodotTestFramework CreateFramework(
        FakeGodotProcessFactory factory,
        bool liveLlmEnabled = false,
        GodotCliTestSelector? selector = null)
        => new(
            Assembly.GetExecutingAssembly(),
            selector ?? GodotCliTestSelector.None,
            factory,
            headlessOverride: false,
            liveLlmEnabled);

    private static TestSessionContext CreateTestSessionContext()
    {
        ConstructorInfo constructor = typeof(TestSessionContext)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 1);

        return (TestSessionContext)constructor.Invoke([new SessionUid("live-llm-gate-session")]);
    }

    private static ExecuteRequestContext CreateRequestContext(IRequest request, RecordingMessageBus messageBus)
#pragma warning disable TPEXP // IExecuteRequestCompletionNotifier is MTP preview API.
        => new(request, messageBus, new CompletionNotifier(), CancellationToken.None);
#pragma warning restore TPEXP

    private static (GodotCliTestSelector Selector, bool HeadlessOverride, bool LiveLlmEnabled) InvokeGetCliPolicy(
        IServiceProvider serviceProvider)
    {
        MethodInfo method = typeof(TestingPlatformBuilderHook)
            .GetMethod("GetCliPolicy", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(TestingPlatformBuilderHook), "GetCliPolicy");

        object? result = method.Invoke(null, [serviceProvider]);
        Assert.NotNull(result);

        ITuple tuple = Assert.IsAssignableFrom<ITuple>(result);
        return ((GodotCliTestSelector)tuple[0]!, (bool)tuple[1]!, (bool)tuple[2]!);
    }

    private static MethodInfo GetLiveLlmFixtureMethod(Type fixtureType, string methodName)
        => fixtureType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(fixtureType.FullName, methodName);

    private static TestNodeUid GetLiveLlmFixtureUid(Type fixtureType, string methodName)
        => TestCaseUidFactory.Create(GetLiveLlmFixtureMethod(fixtureType, methodName));

    private static List<TestNode> NodesOf(RecordingMessageBus messageBus)
        => [.. messageBus.Published.OfType<TestNodeUpdateMessage>().Select(message => message.TestNode)];

    private static void AssertNodeState(TestNode node, TestNodeUid expectedUid, Action<IProperty> assertStateProperty)
    {
        Assert.Equal(expectedUid, node.Uid);
        assertStateProperty(node.Properties.Single<IProperty>());
    }

    private static void IsInProgressState(IProperty stateProperty)
        => Assert.IsType<InProgressTestNodeStateProperty>(stateProperty);

    private static void IsPassedState(IProperty stateProperty)
        => Assert.IsType<PassedTestNodeStateProperty>(stateProperty);

    private static void IsDiscoveredState(IProperty stateProperty)
        => Assert.IsType<DiscoveredTestNodeStateProperty>(stateProperty);

    /// <summary>
    /// Executes one framework request synchronously under the shared environment lock pattern.
    /// </summary>
    private static void ExecuteRequest(GodotTestFramework framework, ExecuteRequestContext context)
#pragma warning disable xUnit1031
        => framework.ExecuteRequestAsync(context).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

    private static List<string> GetRunRequestIds(FakeGodotProcess session)
        =>
        [
            .. ParseRunCommands(session.WrittenLines)
                .Select(command => command.GetProperty("RequestId").GetString()!),
        ];

    private static string SessionLinePrefix => "ALLEYCAT_INTEGRATION_SESSION:";

    private static string ReadyLine()
        => SessionLinePrefix + JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "ready",
        });

    private static string ResultLine(TestNodeUid uid, string outcome)
        => SessionLinePrefix + JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "result",
            RequestId = uid.Value,
            Outcome = outcome,
            Message = (string?)null,
            Stack = (string?)null,
            SessionReusable = true,
        });

    private static FakeSessionStage ResultStage(TestNodeUid uid, string outcome = "passed")
        => new(OutputEvents:
        [
            new FakeOutputEvent(TimeSpan.Zero, FakeOutputStream.StdOut, ResultLine(uid, outcome)),
        ]);

    private static List<JsonElement> ParseRunCommands(IReadOnlyList<string> writtenLines)
        =>
        [
            .. writtenLines
                .Select(ParseCommand)
                .Where(command => string.Equals(command.GetProperty("Kind").GetString(), "run", StringComparison.Ordinal)),
        ];

    private static JsonElement ParseCommand(string line) => JsonSerializer.Deserialize<JsonElement>(line);

    private sealed class StubServiceProvider(object? service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => service;
    }

    private sealed class StubCommandLineOptions(IReadOnlyDictionary<string, string[]> options) : ICommandLineOptions
    {
        public bool IsOptionSet(string optionName) => options.ContainsKey(optionName);

        public bool TryGetOptionArgumentList(string optionName, out string[] arguments)
        {
            if (options.TryGetValue(optionName, out string[]? configuredArguments))
            {
                arguments = configuredArguments;
                return true;
            }

            arguments = [];
            return false;
        }
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<IData> Published { get; } = [];

        public Task PublishAsync(IDataProducer dataProducer, IData data)
        {
            Published.Add(data);
            return Task.CompletedTask;
        }
    }

#pragma warning disable TPEXP // IExecuteRequestCompletionNotifier is MTP preview API.
    private sealed class CompletionNotifier : IExecuteRequestCompletionNotifier
    {
        public bool IsCompleted
        {
            get;
            private set;
        }

        public void Complete() => IsCompleted = true;
    }
#pragma warning restore TPEXP

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

    // Live-LLM gate fixtures are deliberately private so the xUnit runner does not execute them, while the
    // framework's reflection-based discovery still sees their [Fact] methods through assembly.GetTypes().
#pragma warning disable xUnit1000 // These are framework-discovery fixtures, not xUnit test classes.
    private sealed class LiveLlmUnmarkedFixture
    {
        [Fact]
        public static void OrdinaryTest()
        {
        }
    }

    private sealed class LiveLlmMethodMarkedFixture
    {
        [Fact]
        [LiveLlm]
        public static void LiveTest()
        {
        }
    }

    [LiveLlm]
    private sealed class LiveLlmClassMarkedFixture
    {
        [Fact]
        public static void LiveTest()
        {
        }
    }

    [LiveLlm]
    private sealed class LiveLlmMethodAndClassMarkedFixture
    {
        [Fact]
        [LiveLlm]
        public static void LiveTest()
        {
        }
    }

    [LiveLlm]
    private abstract class LiveLlmMarkedBaseFixture
    {
    }

    private sealed class LiveLlmDerivedFixture : LiveLlmMarkedBaseFixture
    {
        [Fact]
        public static void DerivedLiveTest()
        {
        }
    }
#pragma warning restore xUnit1000
}
