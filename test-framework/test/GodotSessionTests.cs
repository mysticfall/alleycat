using System.Reflection;
using System.Text.Json;
using Microsoft.Testing.Platform.Extensions.Messages;
using Xunit;

namespace AlleyCat.TestFramework.Tests;

/// <summary>
/// Session-orchestration tests driving <see cref="GodotTestFramework.RunSelectedTestsAsync"/> over fake Godot
/// session processes.
/// </summary>
public sealed class GodotSessionTests
{
    private const string SessionLinePrefix = "ALLEYCAT_INTEGRATION_SESSION:";
    private const string RequestTimeoutEnvironmentVariable = "ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS";
    private const string PreflightTimeoutEnvironmentVariable = "ALLEYCAT_GODOT_PREFLIGHT_TIMEOUT_MS";

    private static readonly Lock _environmentLock = new();

    // ---------------------------------------------------------------------------------------------
    // Session reuse and routing
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ensures same-mode tests share one session process, in selection order, while unprefixed stdout and stderr
    /// output is retained as diagnostics instead of being parsed as protocol.
    /// </summary>
    [Fact]
    public async Task RunTests_ReusesOneSessionProcess_ForSameModeTests()
    {
        (TestNodeUid uid, MethodInfo method)[] tests =
        [
            CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest)),
            CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest)),
        ];

        var session = FakeGodotProcess.CreateSession(
            startupEvents:
            [
                new FakeOutputEvent(TimeSpan.Zero, FakeOutputStream.StdErr, SessionLinePrefix + "not-a-protocol-stream"),
                new FakeOutputEvent(TimeSpan.FromMilliseconds(5), FakeOutputStream.StdOut, "GODOT IS STARTING"),
                new FakeOutputEvent(TimeSpan.FromMilliseconds(10), FakeOutputStream.StdOut, ReadyLine()),
            ],
            commandStages:
            [
                ResultStage(tests[0].uid, "passed"),
                new FakeSessionStage(
                    OutputEvents:
                    [
                        new FakeOutputEvent(TimeSpan.Zero, FakeOutputStream.StdOut, "some godot noise"),
                        new FakeOutputEvent(TimeSpan.FromMilliseconds(5), FakeOutputStream.StdOut, ResultLine(tests[1].uid.Value, "passed")),
                    ]),
            ]);

        var factory = new FakeGodotProcessFactory(session);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            tests,
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Same(session, Assert.Single(factory.CreatedProcesses));
        Assert.All(factory.Invocations, args => Assert.Contains(GodotSessionProtocol.SessionCommandArg, args));

        List<JsonElement> runCommands = ParseCommands(session.WrittenLines, "run");
        Assert.Equal([tests[0].uid.Value, tests[1].uid.Value], runCommands.Select(command => command.GetProperty("RequestId").GetString()));
        Assert.All(runCommands, command => Assert.Equal(typeof(WindowedSessionFixture).FullName, command.GetProperty("Type").GetString()));

        Assert.Equal(4, published.Count);
        AssertNode(published[0], tests[0].uid, IsInProgress);
        AssertNode(published[1], tests[0].uid, node => Assert.IsType<PassedTestNodeStateProperty>(node));
        AssertNode(published[2], tests[1].uid, IsInProgress);
        AssertNode(published[3], tests[1].uid, node => Assert.IsType<PassedTestNodeStateProperty>(node));
    }

    /// <summary>
    /// Ensures mixed-mode runs use one session per rendering mode and preserve selection order.
    /// </summary>
    [Fact]
    public async Task RunTests_UsesOneSessionPerMode_ForMixedModeTests()
    {
        (TestNodeUid uid, MethodInfo method) windowedFirst = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) headlessFirst = CreateTest(typeof(HeadlessSessionFixture), nameof(HeadlessSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) windowedSecond = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var windowedSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(windowedFirst.uid, "passed"), ResultStage(windowedSecond.uid, "passed")]);
        var headlessSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(headlessFirst.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(windowedSession, headlessSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [windowedFirst, headlessFirst, windowedSecond],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, factory.CreatedProcesses.Count);
        Assert.Same(windowedSession, factory.CreatedProcesses[0]);
        Assert.Same(headlessSession, factory.CreatedProcesses[1]);

        Assert.DoesNotContain("--headless", factory.Invocations[0]);
        Assert.Contains("--headless", factory.Invocations[1]);

        Assert.Equal(2, ParseCommands(windowedSession.WrittenLines, "run").Count);
        JsonElement headlessRunCommand = Assert.Single(ParseCommands(headlessSession.WrittenLines, "run"));
        Assert.Equal(typeof(HeadlessSessionFixture).FullName, headlessRunCommand.GetProperty("Type").GetString());

        Assert.Equal(6, published.Count);
        AssertNode(published[0], windowedFirst.uid, IsInProgress);
        AssertNode(published[1], windowedFirst.uid, IsPassed);
        AssertNode(published[2], headlessFirst.uid, IsInProgress);
        AssertNode(published[3], headlessFirst.uid, IsPassed);
        AssertNode(published[4], windowedSecond.uid, IsInProgress);
        AssertNode(published[5], windowedSecond.uid, IsPassed);
    }

    /// <summary>
    /// Ensures the CLI <c>--headless</c> override collapses windowed-attributed tests into one headless session.
    /// </summary>
    [Fact]
    public async Task RunTests_CollapsesAllTestsIntoHeadlessSession_WhenHeadlessOverrideIsSet()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var session = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(first.uid, "passed"), ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(session);
        GodotTestFramework framework = CreateFramework(factory, headlessOverride: true);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Same(session, Assert.Single(factory.CreatedProcesses));
        Assert.Contains("--headless", Assert.Single(factory.Invocations));
        Assert.Equal(4, published.Count);
        AssertNode(published[3], second.uid, IsPassed);
    }

    // ---------------------------------------------------------------------------------------------
    // Outcome publication and correlation
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ensures a failed test publishes its failure diagnostics and the following test runs in the same session.
    /// </summary>
    [Fact]
    public async Task RunTests_ContinuesSameSession_AfterAssertionFailure()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var session = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents:
                    [
                        new FakeOutputEvent(
                            TimeSpan.Zero,
                            FakeOutputStream.StdOut,
                            ResultLine(first.uid.Value, "failed", message: "Expected head but got tail.", stack: "at Tests.Line1()")),
                    ]),
                ResultStage(second.uid, "passed"),
            ]);

        var factory = new FakeGodotProcessFactory(session);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Same(session, Assert.Single(factory.CreatedProcesses));
        Assert.Equal(2, ParseCommands(session.WrittenLines, "run").Count);

        Assert.Equal(4, published.Count);
        AssertNode(published[1], first.uid, node =>
        {
            FailedTestNodeStateProperty failure = Assert.IsType<FailedTestNodeStateProperty>(node);
            Assert.Equal($"Expected head but got tail.{Environment.NewLine}at Tests.Line1()", failure.Exception!.Message);
        });
        AssertNode(published[3], second.uid, IsPassed);
    }

    // ---------------------------------------------------------------------------------------------
    // Session restart semantics
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ensures a per-request timeout reports the active test as an error, kills the session, and lazily starts a
    /// replacement for the remaining tests.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsErrorAndReplacesSession_WhenTestRequestTimesOut()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var timedOutSession = FakeGodotProcess.CreateSession(
            commandStages: [new FakeSessionStage(OutputEvents: [])]);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(timedOutSession, replacementSession);
        GodotTestFramework framework = CreateFrameworkWithEnvironment(
            factory,
            RequestTimeoutEnvironmentVariable,
            "250");
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(timedOutSession.KillCalled);
        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("timed out after 250ms", error.Exception!.Message);
        });
        Assert.Same(replacementSession, factory.CreatedProcesses[1]);
        AssertNode(published[3], second.uid, IsPassed);
    }

    /// <summary>
    /// Ensures a session exiting before the correlated result reports the active test as an error with the exit
    /// code and starts a replacement for the remaining tests.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsErrorAndReplacesSession_WhenProcessExitsBeforeResult()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var crashedSession = FakeGodotProcess.CreateSession(
            commandStages: [new FakeSessionStage(OutputEvents: [], ExitCode: 1, ExitDelay: TimeSpan.FromMilliseconds(10))]);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(crashedSession, replacementSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, factory.CreatedProcesses.Count);
        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("ExitCode=1", error.Exception!.Message, StringComparison.Ordinal);
        });
        AssertNode(published[3], second.uid, IsPassed);
    }

    /// <summary>
    /// Ensures a complete result emitted just before process exit is kept, and the session is replaced for the
    /// next test.
    /// </summary>
    [Fact]
    public async Task RunTests_KeepsResult_AndReplacesSession_WhenProcessExitsAfterResult()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var exitingSession = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents: [new FakeOutputEvent(TimeSpan.Zero, FakeOutputStream.StdOut, ResultLine(first.uid.Value, "passed"))],
                    ExitCode: 0,
                    ExitDelay: TimeSpan.FromMilliseconds(20)),
            ]);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(exitingSession, replacementSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            async (node, _) =>
            {
                published.Add(node);

                // Let the dying session finish exiting before the next test is dispatched.
                if (published.Count == 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50));
                }
            },
            CancellationToken.None);

        Assert.Equal(2, factory.CreatedProcesses.Count);
        AssertNode(published[1], first.uid, IsPassed);
        AssertNode(published[3], second.uid, IsPassed);
    }

    /// <summary>
    /// Ensures a non-reusable session still publishes its test-owned failure as-is, then replaces the session.
    /// </summary>
    [Fact]
    public async Task RunTests_PublishesResult_AndReplacesSession_WhenSessionIsNotReusable()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var taintedSession = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents:
                    [
                        new FakeOutputEvent(
                            TimeSpan.Zero,
                            FakeOutputStream.StdOut,
                            ResultLine(first.uid.Value, "failed", sessionReusable: false, message: "assertion broke")),
                    ],
                    ExitCode: 1,
                    ExitDelay: TimeSpan.FromMilliseconds(20)),
            ]);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(taintedSession, replacementSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, factory.CreatedProcesses.Count);
        AssertNode(published[1], first.uid, node =>
        {
            FailedTestNodeStateProperty failure = Assert.IsType<FailedTestNodeStateProperty>(node);
            Assert.Equal("assertion broke", failure.Exception!.Message);
        });
        AssertNode(published[3], second.uid, IsPassed);
    }

    /// <summary>
    /// Ensures a passed test whose baseline restoration failed is reported as a framework error carrying the
    /// runtime diagnostics, and the session is replaced.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsError_AndReplacesSession_WhenSessionIsNotReusableAfterPassedTest()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var taintedSession = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents:
                    [
                        new FakeOutputEvent(
                            TimeSpan.Zero,
                            FakeOutputStream.StdOut,
                            ResultLine(first.uid.Value, "passed", sessionReusable: false, message: "Session baseline could not be restored.")),
                    ],
                    ExitCode: 1,
                    ExitDelay: TimeSpan.FromMilliseconds(20)),
            ]);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(taintedSession, replacementSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, factory.CreatedProcesses.Count);
        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("Session baseline could not be restored.", error.Exception!.Message, StringComparison.Ordinal);
        });
        AssertNode(published[3], second.uid, IsPassed);
    }

    /// <summary>
    /// Ensures a malformed protocol line reports the active test as an error and replaces the session.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsErrorAndReplacesSession_WhenProtocolLineIsMalformed()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var faultySession = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents: [new FakeOutputEvent(TimeSpan.Zero, FakeOutputStream.StdOut, SessionLinePrefix + "{not-valid-json")]),
            ]);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(faultySession, replacementSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, factory.CreatedProcesses.Count);
        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("malformed session protocol line", error.Exception!.Message, StringComparison.Ordinal);
        });
        AssertNode(published[3], second.uid, IsPassed);
    }

    /// <summary>
    /// Ensures an incomplete startup error event is treated as a protocol fault rather than a ready event with a
    /// deserialised default message.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsStartupErrorAndReplacesSession_WhenReadyEventIsIncomplete()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var faultySession = FakeGodotProcess.CreateSession(
            startupEvents:
            [
                new FakeOutputEvent(
                    TimeSpan.Zero,
                    FakeOutputStream.StdOut,
                    SessionLinePrefix + JsonSerializer.Serialize(new
                    {
                        Version = 1,
                        Kind = "ready",
                        Outcome = "error",
                    })),
            ]);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(faultySession, replacementSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(faultySession.KillCalled);
        Assert.Equal(2, factory.CreatedProcesses.Count);
        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("'ready' must omit Outcome and Message", error.Exception!.Message, StringComparison.Ordinal);
        });
        AssertNode(published[3], second.uid, IsPassed);
    }

    /// <summary>
    /// Ensures omitting the boolean session-reuse verdict is a protocol fault, rather than treating its deserialised
    /// <see langword="false"/> default as a valid non-reusable result.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsErrorAndReplacesSession_WhenResultOmitsSessionReusable()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var faultySession = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents:
                    [
                        new FakeOutputEvent(
                            TimeSpan.Zero,
                            FakeOutputStream.StdOut,
                            IncompleteResultLine(first.uid.Value)),
                    ]),
            ]);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(faultySession, replacementSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(faultySession.KillCalled);
        Assert.Equal(2, factory.CreatedProcesses.Count);
        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("'result' requires SessionReusable", error.Exception!.Message, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(
            published.Where(node => node.Uid == first.uid),
            node => node.Properties.Single<IProperty>() is PassedTestNodeStateProperty);
        AssertNode(published[3], second.uid, IsPassed);
    }

    /// <summary>
    /// Ensures an incomplete shutdown acknowledgement faults the session and follows the established forced-cleanup
    /// path instead of being silently accepted.
    /// </summary>
    [Fact]
    public async Task Shutdown_KillsSession_WhenShutdownCompleteEventIsIncomplete()
    {
        var session = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents:
                    [
                        new FakeOutputEvent(
                            TimeSpan.Zero,
                            FakeOutputStream.StdOut,
                            SessionLinePrefix + JsonSerializer.Serialize(new
                            {
                                Version = 1,
                                Kind = "shutdown-complete",
                            })),
                    ]),
            ]);
        var factory = new FakeGodotProcessFactory(session);
        using var client = GodotSessionClient.Start(factory, []);

        await client.WaitForReadyAsync(timeoutMs: 1_000, CancellationToken.None);
        GodotSessionException exception = await Assert.ThrowsAsync<GodotSessionException>(
            () => client.ShutdownAsync(cleanupTimeoutMs: 100));

        Assert.False(client.IsUsable);
        Assert.True(session.KillCalled);
        Assert.Contains("'shutdown-complete' requires a non-empty RequestId", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures a result correlated to a different request id is reported as a protocol error for the active test
    /// and never misattributed.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsErrorAndReplacesSession_WhenResultRequestIdMismatches()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var faultySession = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents:
                    [
                        new FakeOutputEvent(TimeSpan.Zero, FakeOutputStream.StdOut, ResultLine("someone-else", "passed")),
                    ]),
            ]);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(faultySession, replacementSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, factory.CreatedProcesses.Count);
        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("unknown request 'someone-else'", error.Exception!.Message, StringComparison.Ordinal);
        });
        AssertNode(published[3], second.uid, IsPassed);
    }

    // ---------------------------------------------------------------------------------------------
    // Session startup failures
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ensures a session whose startup reports an error fails only the affected test, and the next test may start
    /// a fresh session.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsErrorForAffectedTest_AndStartsFreshSession_WhenReadyReportsError()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var failedStartupSession = FakeGodotProcess.Create(
            outputEvents:
            [
                new FakeOutputEvent(
                    TimeSpan.FromMilliseconds(5),
                    FakeOutputStream.StdOut,
                    SessionLinePrefix + JsonSerializer.Serialize(new
                    {
                        Version = 1,
                        Kind = "ready",
                        Outcome = "error",
                        Message = "Loading integration test assembly failed.",
                    })),
            ],
            naturalExitDelay: TimeSpan.FromMilliseconds(10),
            naturalExitCode: 1);
        var replacementSession = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(second.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(failedStartupSession, replacementSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, factory.CreatedProcesses.Count);
        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("Loading integration test assembly failed.", error.Exception!.Message, StringComparison.Ordinal);
        });
        AssertNode(published[3], second.uid, IsPassed);
    }

    /// <summary>
    /// Ensures a session process exiting before the ready event fails the affected test only.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsErrorForAffectedTest_WhenSessionExitsBeforeReady()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));

        var crashedSession = FakeGodotProcess.Create(
            outputEvents: [],
            naturalExitDelay: TimeSpan.FromMilliseconds(10),
            naturalExitCode: 1);

        var factory = new FakeGodotProcessFactory(crashedSession);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("exited before reporting ready", error.Exception!.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Ensures a session that never reports ready within the preflight timeout fails the affected test only.
    /// </summary>
    [Fact]
    public async Task RunTests_ReportsErrorForAffectedTest_WhenReadyTimesOut()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));

        var silentSession = FakeGodotProcess.Create(
            outputEvents: [new FakeOutputEvent(TimeSpan.FromMilliseconds(10), FakeOutputStream.StdOut, "booting...")],
            naturalExitDelay: TimeSpan.FromSeconds(30),
            naturalExitCode: 1);

        var factory = new FakeGodotProcessFactory(silentSession);
        GodotTestFramework framework = CreateFrameworkWithEnvironment(
            factory,
            PreflightTimeoutEnvironmentVariable,
            "250");
        List<TestNode> published = [];

        await framework.RunSelectedTestsAsync(
            [first],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        AssertNode(published[1], first.uid, node =>
        {
            ErrorTestNodeStateProperty error = Assert.IsType<ErrorTestNodeStateProperty>(node);
            Assert.Contains("did not report ready within 250ms", error.Exception!.Message, StringComparison.Ordinal);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Shutdown and cancellation
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ensures graceful shutdown sends a shutdown command, closes standard input, and exits without a forced kill.
    /// </summary>
    [Fact]
    public async Task RunTests_SendsShutdownAndClosesStandardInput_ForLiveSessions()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));

        var session = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(first.uid, "passed")]);

        var factory = new FakeGodotProcessFactory(session);
        GodotTestFramework framework = CreateFramework(factory);

        await framework.RunSelectedTestsAsync(
            [first],
            (node, _) => Task.CompletedTask,
            CancellationToken.None);

        IReadOnlyList<string> writtenLines = session.WrittenLines;
        Assert.Equal(2, writtenLines.Count);

        JsonElement shutdownCommand = ParseCommand(writtenLines[1]);
        Assert.Equal(GodotSessionProtocol.ShutdownCommandKind, shutdownCommand.GetProperty("Kind").GetString());
        Assert.Equal(GodotSessionProtocol.Version, shutdownCommand.GetProperty("Version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(shutdownCommand.GetProperty("RequestId").GetString()));

        Assert.True(session.StandardInputClosed);
        Assert.True(session.HasExited);
        Assert.Equal(0, session.ExitCode);
        Assert.False(session.KillCalled);
    }

    /// <summary>
    /// Ensures a matching shutdown acknowledgement and clean exit completes shutdown without forced termination.
    /// </summary>
    [Fact]
    public async Task Shutdown_CompletesCleanly_WhenMatchingAcknowledgementAndExitArrive()
    {
        var session = FakeGodotProcess.CreateSession();
        var factory = new FakeGodotProcessFactory(session);
        using var client = GodotSessionClient.Start(factory, []);

        await client.WaitForReadyAsync(timeoutMs: 1_000, CancellationToken.None);
        await client.ShutdownAsync(cleanupTimeoutMs: 1_000);

        JsonElement shutdownCommand = Assert.Single(ParseCommands(session.WrittenLines, GodotSessionProtocol.ShutdownCommandKind));
        Assert.False(string.IsNullOrWhiteSpace(shutdownCommand.GetProperty("RequestId").GetString()));
        Assert.True(session.StandardInputClosed);
        Assert.True(session.HasExited);
        Assert.Equal(0, session.ExitCode);
        Assert.False(session.KillCalled);
    }

    /// <summary>
    /// Ensures a shutdown stdin-write failure faults and terminates the session without retrospectively changing a
    /// result that completed before cleanup began.
    /// </summary>
    [Fact]
    public async Task Shutdown_FaultsAndKillsSession_WhenShutdownCommandWriteFails()
    {
        var session = FakeGodotProcess.CreateSession(
            commandStages: [ResultStage(new TestNodeUid("completed-test"), "passed")],
            shutdownWriteException: new IOException("stdin is unavailable"));
        var factory = new FakeGodotProcessFactory(session);
        using var client = GodotSessionClient.Start(factory, []);

        await client.WaitForReadyAsync(timeoutMs: 1_000, CancellationToken.None);
        GodotSessionTestResult completedResult = await client.RunTestAsync(
            "completed-test",
            typeof(WindowedSessionFixture).FullName!,
            nameof(WindowedSessionFixture.FirstTest),
            timeoutMs: 1_000,
            CancellationToken.None);

        GodotSessionException exception = await Assert.ThrowsAsync<GodotSessionException>(
            () => client.ShutdownAsync(cleanupTimeoutMs: 100));

        Assert.Equal(GodotSessionTestOutcome.Passed, completedResult.Outcome);
        Assert.Null(completedResult.Error);
        Assert.True(completedResult.SessionReusable);
        Assert.True(session.StandardInputClosed);
        Assert.True(session.KillCalled);
        Assert.True(session.KillTreeCalled);
        Assert.False(client.IsUsable);
        Assert.Contains("Failed to send shutdown request", exception.Message, StringComparison.Ordinal);
        Assert.Contains("stdin is unavailable", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures a correctly correlated shutdown acknowledgement is insufficient without process exit: cleanup faults,
    /// force-kills the live process at its deadline, and leaves the client unusable.
    /// </summary>
    [Fact]
    public async Task Shutdown_KillsAndFaultsSession_WhenMatchingAcknowledgementArrivesWithoutExit()
    {
        var session = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents: [],
                    EmitMatchingShutdownComplete: true),
            ]);
        var factory = new FakeGodotProcessFactory(session);
        using var client = GodotSessionClient.Start(factory, []);

        await client.WaitForReadyAsync(timeoutMs: 1_000, CancellationToken.None);
        Assert.False(session.HasExited);
        GodotSessionException exception = await Assert.ThrowsAsync<GodotSessionException>(
            () => client.ShutdownAsync(cleanupTimeoutMs: 100));

        Assert.True(session.StandardInputClosed);
        Assert.True(session.KillCalled);
        Assert.True(session.KillTreeCalled);
        Assert.True(session.HasExited);
        Assert.False(client.IsUsable);
        Assert.Contains("matching shutdown completion and exit within 100ms", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures a zero-exit session which closes stdin without acknowledging shutdown is condemned even though no
    /// physical kill is possible after it has already exited.
    /// </summary>
    [Fact]
    public async Task Shutdown_RejectsCleanExit_WhenMatchingAcknowledgementIsMissing()
    {
        var session = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents: [],
                    ExitCode: 0,
                    ExitDelay: TimeSpan.FromMilliseconds(10)),
            ]);
        var factory = new FakeGodotProcessFactory(session);
        using var client = GodotSessionClient.Start(factory, []);

        await client.WaitForReadyAsync(timeoutMs: 1_000, CancellationToken.None);
        GodotSessionException exception = await Assert.ThrowsAsync<GodotSessionException>(
            () => client.ShutdownAsync(cleanupTimeoutMs: 100));

        Assert.True(session.StandardInputClosed);
        Assert.True(session.HasExited);
        Assert.Equal(0, session.ExitCode);
        Assert.False(session.KillCalled);
        Assert.False(client.IsUsable);
        Assert.Contains("did not report matching shutdown completion", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures a live session which omits shutdown acknowledgement is forcibly terminated at the cleanup deadline.
    /// </summary>
    [Fact]
    public async Task Shutdown_KillsLiveSession_WhenMatchingAcknowledgementIsMissing()
    {
        var session = FakeGodotProcess.CreateSession(commandStages: [new FakeSessionStage(OutputEvents: [])]);
        var factory = new FakeGodotProcessFactory(session);
        using var client = GodotSessionClient.Start(factory, []);

        await client.WaitForReadyAsync(timeoutMs: 1_000, CancellationToken.None);
        _ = await Assert.ThrowsAsync<GodotSessionException>(() => client.ShutdownAsync(cleanupTimeoutMs: 100));

        Assert.True(session.StandardInputClosed);
        Assert.True(session.KillCalled);
        Assert.False(client.IsUsable);
    }

    /// <summary>
    /// Ensures a shutdown acknowledgement for another request faults and terminates the session rather than
    /// completing the active shutdown.
    /// </summary>
    [Fact]
    public async Task Shutdown_KillsSession_WhenShutdownCompleteRequestIdMismatches()
    {
        var session = FakeGodotProcess.CreateSession(
            commandStages:
            [
                new FakeSessionStage(
                    OutputEvents:
                    [
                        new FakeOutputEvent(
                            TimeSpan.Zero,
                            FakeOutputStream.StdOut,
                            ShutdownCompleteLine("another-shutdown-request")),
                    ]),
            ]);
        var factory = new FakeGodotProcessFactory(session);
        using var client = GodotSessionClient.Start(factory, []);

        await client.WaitForReadyAsync(timeoutMs: 1_000, CancellationToken.None);
        GodotSessionException exception = await Assert.ThrowsAsync<GodotSessionException>(
            () => client.ShutdownAsync(cleanupTimeoutMs: 100));

        Assert.True(session.KillCalled);
        Assert.False(client.IsUsable);
        Assert.Contains("unknown request 'another-shutdown-request'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures host cancellation stops scheduling after the in-flight test, and live sessions are shut down.
    /// </summary>
    [Fact]
    public async Task RunTests_StopsSchedulingAndShutsSessionsDown_WhenHostCancels()
    {
        (TestNodeUid uid, MethodInfo method) first = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.FirstTest));
        (TestNodeUid uid, MethodInfo method) second = CreateTest(typeof(WindowedSessionFixture), nameof(WindowedSessionFixture.SecondTest));

        var session = FakeGodotProcess.CreateSession(
            commandStages: [new FakeSessionStage(OutputEvents: [])]);

        var factory = new FakeGodotProcessFactory(session);
        GodotTestFramework framework = CreateFramework(factory);
        List<TestNode> published = [];
        using CancellationTokenSource cancellationSource = new();

        Task runTask = framework.RunSelectedTestsAsync(
            [first, second],
            (node, _) =>
            {
                published.Add(node);
                return Task.CompletedTask;
            },
            cancellationSource.Token);

        cancellationSource.CancelAfter(TimeSpan.FromMilliseconds(100));
        await runTask;

        TestNode inProgressNode = Assert.Single(published);
        AssertNode(inProgressNode, first.uid, IsInProgress);

        IReadOnlyList<string> writtenLines = session.WrittenLines;
        JsonElement runCommand = Assert.Single(ParseCommands(writtenLines, "run"));
        Assert.Equal(first.uid.Value, runCommand.GetProperty("RequestId").GetString());

        JsonElement shutdownCommand = Assert.Single(ParseCommands(writtenLines, GodotSessionProtocol.ShutdownCommandKind));
        Assert.StartsWith("shutdown-", shutdownCommand.GetProperty("RequestId").GetString());
        Assert.True(session.StandardInputClosed);
    }

    // ---------------------------------------------------------------------------------------------
    // Failure message composition
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ensures message and stack values are combined into one failure string.
    /// </summary>
    [Fact]
    public void BuildFailureMessage_CombinesMessageAndStack_WhenBothArePresent()
    {
        string message = GodotSessionClient.BuildFailureMessage("Assertion failed", "stack trace");

        Assert.Equal($"Assertion failed{Environment.NewLine}stack trace", message);
    }

    /// <summary>
    /// Ensures a fallback failure message is returned when no details are present.
    /// </summary>
    [Fact]
    public void BuildFailureMessage_UsesFallbackMessage_WhenNoDetailsAreProvided()
    {
        string message = GodotSessionClient.BuildFailureMessage(" ", "\t");

        Assert.Equal("The Godot session reported an unknown failure.", message);
    }

    /// <summary>
    /// Ensures a lone stack value is used directly when no message is present.
    /// </summary>
    [Fact]
    public void BuildFailureMessage_UsesStack_WhenOnlyStackIsProvided()
    {
        string message = GodotSessionClient.BuildFailureMessage(null, "trace-only");

        Assert.Equal("trace-only", message);
    }

    private static (TestNodeUid uid, MethodInfo method) CreateTest(Type fixtureType, string methodName)
    {
        MethodInfo method = fixtureType.GetMethod(methodName)
            ?? throw new MissingMethodException(fixtureType.FullName, methodName);

        return (TestCaseUidFactory.Create(method), method);
    }

    private static GodotTestFramework CreateFramework(FakeGodotProcessFactory factory, bool headlessOverride = false)
        => new(Assembly.GetExecutingAssembly(), GodotCliTestSelector.None, factory, headlessOverride);

    /// <summary>
    /// Creates a framework while a timeout environment variable is temporarily set, mirroring configuration read
    /// eagerly in the constructor.
    /// </summary>
    private static GodotTestFramework CreateFrameworkWithEnvironment(
        FakeGodotProcessFactory factory,
        string environmentVariable,
        string value)
    {
        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(environmentVariable, value);

            return CreateFramework(factory);
        }
    }

    private static string ReadyLine()
        => SessionLinePrefix + JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "ready",
        });

    private static string ResultLine(
        string requestId,
        string outcome,
        bool sessionReusable = true,
        string? message = null,
        string? stack = null)
        => SessionLinePrefix + JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "result",
            RequestId = requestId,
            Outcome = outcome,
            Message = message,
            Stack = stack,
            SessionReusable = sessionReusable,
        });

    private static string IncompleteResultLine(string requestId)
        => SessionLinePrefix + JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "result",
            RequestId = requestId,
            Outcome = "passed",
        });

    private static string ShutdownCompleteLine(string requestId)
        => SessionLinePrefix + JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "shutdown-complete",
            RequestId = requestId,
        });

    private static FakeSessionStage ResultStage(TestNodeUid uid, string outcome)
        => new(OutputEvents: [new FakeOutputEvent(TimeSpan.Zero, FakeOutputStream.StdOut, ResultLine(uid.Value, outcome))]);

    private static List<JsonElement> ParseCommands(IReadOnlyList<string> writtenLines, string kind)
        =>
        [
            .. writtenLines
                .Select(ParseCommand)
                .Where(command => string.Equals(command.GetProperty("Kind").GetString(), kind, StringComparison.Ordinal)),
        ];

    private static JsonElement ParseCommand(string line) => JsonSerializer.Deserialize<JsonElement>(line);

    private static void AssertNode(TestNode node, TestNodeUid expectedUid, Action<IProperty> assertStateProperty)
    {
        Assert.Equal(expectedUid, node.Uid);
        assertStateProperty(node.Properties.Single<IProperty>());
    }

    private static void IsInProgress(IProperty stateProperty) => Assert.IsType<InProgressTestNodeStateProperty>(stateProperty);

    private static void IsPassed(IProperty stateProperty) => Assert.IsType<PassedTestNodeStateProperty>(stateProperty);

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

    private sealed class WindowedSessionFixture
    {
        public static void FirstTest()
        {
        }

        public static void SecondTest()
        {
        }
    }

    [Headless]
    private sealed class HeadlessSessionFixture
    {
        public static void FirstTest()
        {
        }
    }
}
