using AlleyCat.IntegrationTests.Mind.AI.Decision;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Decision;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Speech;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using static AlleyCat.IntegrationTests.Mind.AI.Decision.ScriptedWaitVerdicts;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Wake-worthy observations (AI-015 TR-14 as amended): an <c>arrived</c> or <c>failed</c> navigation outcome ends a
/// parked wait at once, so the agent's next turn runs and receives the outcome through the ordinary
/// <c>scene_update</c> exchange exactly once; a running turn is never interrupted, an outcome that lands during a
/// turn still ends the wait that turn starts until a request has presented it, and <c>stopped</c> or <c>replaced</c>
/// outcomes never wake. The wait decision client answers "no reason" with no deadlines and a cadence outside the
/// observation window, so only the outcome itself can end a wait.
/// </summary>
public sealed partial class AgenticMindSessionLifecycleIntegrationTests
{
    /// <summary>
    /// Frames a parked mind is observed for after a non-waking observation: well above the handful of frames a waking
    /// outcome takes to start the next request (the waking cases below finish within 300 frames).
    /// </summary>
    private const int NoWakeObservationFrames = 120;

    /// <summary>
    /// The reported symptom: a mind parked in a model-called <c>wait</c> starts a fresh turn when its walk arrives, and
    /// that turn delivers the arrival once through <c>scene_update</c> after the wait's ordinary result.
    /// </summary>
    [Fact]
    public Task ArrivedOutcome_WhileParkedInWait_EndsWaitAndDeliversOnce()
        => AssertParkedWaitWokenBy(NavigationOutcomeKind.Arrived, "Your `walk_to` move arrived");

    /// <summary>A failed move wakes a parked mind exactly like an arrival.</summary>
    [Fact]
    public Task FailedOutcome_WhileParkedInWait_EndsWaitAndDeliversOnce()
        => AssertParkedWaitWokenBy(NavigationOutcomeKind.Failed, "Your `walk_to` move failed");

    /// <summary>
    /// The decision-wait session park — entered automatically after a parking tool's settled batch — ends on an
    /// arrival exactly like a model-called wait, and the next turn delivers the arrival once.
    /// </summary>
    [Fact]
    public async Task ArrivedOutcome_WhileSessionParked_EndsParkAndDeliversOnce()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        await using var fixture = ParkedWakeFixture.Start(sceneTree, [CommittingActionTool.ToolNameValue], parkAfterTool: true);
        await WaitUntilAsync(sceneTree, () => fixture.Mind.HasActiveObservationWait);
        _ = Assert.Single(fixture.Provider.Requests);

        fixture.Mind.ObserveForTest(Outcome(NavigationOutcomeKind.Arrived));
        await WaitUntilAsync(sceneTree, () => fixture.Provider.Requests.Count >= 2);
        await TestUtils.WaitForFramesAsync(sceneTree, 10);

        Assert.False(fixture.Mind.HasActiveObservationWait);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(1, CountOccurrences(GetDeliveredSceneUpdates(fixture.Provider.Requests[1]), "Your `walk_to` move arrived"));
    }

    /// <summary>
    /// AI-002 criterion 27, armed timer: a wait parked on an explicit 60 s quiet time ends at once on an arrival with the
    /// immediate act-now wake, not the timer's, long before the timer could expire.
    /// </summary>
    [Fact]
    public async Task ArrivedOutcome_WhileWaitHasAnArmedTimer_EndsWithTheImmediateWake()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        await using var fixture = ParkedWakeFixture.Start(
            sceneTree,
            [],
            provider => provider.EnqueueCallBatch(new FunctionCallContent(
                "call-1", "wait", new Dictionary<string, object?> { ["seconds"] = 60 })));
        await WaitUntilAsync(sceneTree, () => fixture.Mind.HasActiveObservationWait);

        fixture.Mind.ObserveForTest(Outcome(NavigationOutcomeKind.Arrived));
        await WaitUntilAsync(sceneTree, () => fixture.Provider.Requests.Count >= 2);

        string waitResult = Assert.Single(WaitResults(fixture.Provider.Requests[1]));
        Assert.StartsWith("Wait ended: circumstances may call for you to act.", waitResult, StringComparison.Ordinal);
        Assert.DoesNotContain("chosen quiet time elapsed", waitResult, StringComparison.Ordinal);
        Assert.True(ElapsedSeconds(waitResult) < 30d, $"The armed 60 s timer must not have run out: {waitResult}");
        Assert.Equal(1, CountOccurrences(GetDeliveredSceneUpdates(fixture.Provider.Requests[1]), "Your `walk_to` move arrived"));
    }

    /// <summary>
    /// AI-002 criterion 27, evaluation in flight: an arrival during a held wait-decision evaluation ends the wait with the
    /// act-now wake; when the abandoned evaluation later returns an act-now verdict, it is not applied — the next wait the
    /// woken turn starts stays parked.
    /// </summary>
    [Fact]
    public async Task ArrivedOutcome_WhileDecisionEvaluationIsHeld_EndsWaitAndDropsTheLateVerdict()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        HeldWaitDecisionClient decisions = new();
        await using var fixture = ParkedWakeFixture.Start(
            sceneTree,
            ["wait", "wait"],
            configure: mind =>
            {
                mind.SetSceneDecisionClientForTesting(decisions);
                mind.WaitEvaluationIntervalSeconds = 0.1f;
            });
        await decisions.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Mind.HasActiveObservationWait);

        fixture.Mind.ObserveForTest(Outcome(NavigationOutcomeKind.Arrived));
        await WaitUntilAsync(sceneTree, () => fixture.Provider.Requests.Count >= 2);
        await WaitUntilAsync(sceneTree, () => fixture.Mind.HasActiveObservationWait);
        Assert.StartsWith(
            "Wait ended: circumstances may call for you to act.",
            Assert.Single(WaitResults(fixture.Provider.Requests[1])),
            StringComparison.Ordinal);

        _ = decisions.ReleaseFirst.TrySetResult(WaitVerdict.ActNow);
        await TestUtils.WaitForFramesAsync(sceneTree, NoWakeObservationFrames);

        Assert.True(fixture.Mind.HasActiveObservationWait);
        Assert.Equal(2, fixture.Provider.Requests.Count);
    }

    /// <summary>
    /// AI-002 criterion 27, failed provider response: the request that first presents a pending arrival returns an
    /// invalid response and is discarded; the wake stays pending through the recovery backoff, and the accepted recovery
    /// request delivers the arrival exactly once and clears the wake, so the wait it starts stays parked.
    /// </summary>
    [Fact]
    public async Task ArrivedOutcome_WithADiscardedRequest_StaysPendingUntilAnAcceptedRequestDeliversIt()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        await using var fixture = ParkedWakeFixture.Start(
            sceneTree,
            [],
            provider =>
            {
                provider.EnqueueCall("wait");
                provider.EnqueueInvalidResponse();
                provider.EnqueueCall("wait");
            },
            configure: static mind => mind.InvalidResponseRecoveryBackoffSeconds = [1.5f]);
        await WaitUntilAsync(sceneTree, () => fixture.Mind.HasActiveObservationWait);

        fixture.Mind.ObserveForTest(Outcome(NavigationOutcomeKind.Arrived));
        await WaitUntilAsync(sceneTree, () => fixture.Provider.Requests.Count >= 2);
        await TestUtils.WaitForFramesAsync(sceneTree, 10);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.True(fixture.Mind.HasPendingWakeObservation, "A discarded request must leave the wake pending.");

        await WaitUntilAsync(sceneTree, () => fixture.Provider.Requests.Count >= 3, maxFrames: 900);
        await WaitUntilAsync(sceneTree, () => fixture.Mind.HasActiveObservationWait);
        await TestUtils.WaitForFramesAsync(sceneTree, NoWakeObservationFrames);

        Assert.False(fixture.Mind.HasPendingWakeObservation);
        Assert.True(fixture.Mind.HasActiveObservationWait);
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.Equal(1, CountOccurrences(GetDeliveredSceneUpdates(fixture.Provider.Requests[2]), "Your `walk_to` move arrived"));
    }

    /// <summary>
    /// User acceptance 3 (AI-015 UR-6): the model can speak while a walk runs. One batch starts a production
    /// <c>walk_to</c> and speaks; the speech commits and parks the session before the walk ends, and the walk's arrival
    /// later wakes the park, with the next request delivering the outcome exactly once beside the start text.
    /// </summary>
    [Fact]
    public async Task SpeakDuringWalk_OutcomeArrivesLaterExactlyOnce()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        LifecycleNavigationDouble navigation = new();
        TestCharacter owner = new(hearing, ownerVoice, navigation);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("walk-1", WalkToTool.ProductionToolName, new Dictionary<string, object?> { ["x"] = 2, ["z"] = -1 }),
            new FunctionCallContent("speak-1", "speak", new Dictionary<string, object?> { ["speech"] = "On my way." }));
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        // The park after the speech holds until something wakes it: only the walk's arrival can.
        mind.SetSceneDecisionClientForTesting(new ScriptedWaitDecisionClient(WaitVerdict.NoReason));
        mind.SetWaitTimingForTesting(NoDeadlines);
        mind.WaitEvaluationIntervalSeconds = 120f;
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => mind.HasActiveObservationWait);
            await WaitUntilAsync(sceneTree, () => mind.GetTimelineForTest()
                .OfType<ObservedSpeech>()
                .Any(speech => speech.ActorId == owner.FullId && speech.Content == "On my way."));
            Assert.True(navigation.CurrentStatus.HasActiveRequest);
            _ = Assert.Single(clientProvider.Requests);
            Assert.Empty(mind.GetTimelineForTest().OfType<ObservedNavigationOutcome>());

            Assert.True(navigation.CompleteActive());
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await TestUtils.WaitForFramesAsync(sceneTree, NoWakeObservationFrames);

            Assert.Equal(2, clientProvider.Requests.Count);
            IReadOnlyList<ChatMessage> next = clientProvider.Requests[1];
            Assert.Contains(
                next.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
                static result => (result.Result?.ToString() ?? string.Empty).StartsWith("Started walking to (2, -1) at walk pace.", StringComparison.Ordinal));
            Assert.Equal(1, CountOccurrences(GetDeliveredSceneUpdates(next), "Your `walk_to` move arrived"));
            _ = Assert.Single(mind.GetTimelineForTest().OfType<ObservedNavigationOutcome>());
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    private static IEnumerable<string> WaitResults(IReadOnlyList<ChatMessage> request)
        => request.SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Select(static result => result.Result?.ToString() ?? string.Empty)
            .Where(static text => text.StartsWith("Wait ended:", StringComparison.Ordinal));

    private static double ElapsedSeconds(string waitResult)
    {
        const string marker = "Elapsed game time: ";
        int start = waitResult.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        int end = waitResult.IndexOf(" seconds", start, StringComparison.Ordinal);
        return double.Parse(waitResult[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The outcome of the NPC's own <c>stop</c> never wakes a parked mind.</summary>
    [Fact]
    public Task StoppedOutcome_WhileParkedInWait_KeepsWaiting() => AssertParkedWaitKeptBy(NavigationOutcomeKind.Stopped);

    /// <summary>The outcome of a move the NPC itself replaced never wakes a parked mind.</summary>
    [Fact]
    public Task ReplacedOutcome_WhileParkedInWait_KeepsWaiting() => AssertParkedWaitKeptBy(NavigationOutcomeKind.Replaced);

    private static async Task AssertParkedWaitKeptBy(NavigationOutcomeKind kind)
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        await using var fixture = ParkedWakeFixture.Start(sceneTree, ["wait"]);
        await WaitUntilAsync(sceneTree, () => fixture.Mind.HasActiveObservationWait);

        fixture.Mind.ObserveForTest(Outcome(kind));
        await TestUtils.WaitForFramesAsync(sceneTree, NoWakeObservationFrames);

        Assert.True(fixture.Mind.HasActiveObservationWait);
        _ = Assert.Single(fixture.Provider.Requests);
    }

    /// <summary>
    /// An arrival during a running turn neither cancels nor replaces it; when that turn then calls <c>wait</c>, the
    /// still-undelivered arrival ends the wait at once and the next request delivers it exactly once — no double turn.
    /// </summary>
    [Fact]
    public async Task ArrivedOutcome_DuringRunningTurn_DoesNotInterruptAndEndsTheFollowingWait()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = ParkedWakeFixture.Start(
            sceneTree,
            [],
            provider => provider.EnqueueHoldUntilReleasedCall(started, release, "wait"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Mind.ObserveForTest(Outcome(NavigationOutcomeKind.Arrived));
        await TestUtils.WaitForFramesAsync(sceneTree, 10);
        _ = Assert.Single(fixture.Provider.Requests);
        Assert.False(fixture.Provider.EndedByCancellation());

        _ = release.TrySetResult();
        await WaitUntilAsync(sceneTree, () => fixture.Provider.Requests.Count >= 2);
        await TestUtils.WaitForFramesAsync(sceneTree, NoWakeObservationFrames);

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(0, GetSceneUpdateExchangeCount(fixture.Provider.Requests[0]));
        Assert.Equal(1, CountOccurrences(GetDeliveredSceneUpdates(fixture.Provider.Requests[1]), "Your `walk_to` move arrived"));
        Assert.Contains(
            fixture.Provider.Requests[1].SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
            result => (result.Result?.ToString() ?? string.Empty).StartsWith("Wait ended: circumstances may call for you to act.", StringComparison.Ordinal));
    }

    /// <summary>
    /// An arrival already presented by a request does not wake a later wait: the turn that delivered it calls
    /// <c>wait</c> and stays parked, with the arrival delivered exactly once.
    /// </summary>
    [Fact]
    public async Task ArrivedOutcome_AlreadyDelivered_DoesNotEndALaterWait()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = ParkedWakeFixture.Start(
            sceneTree,
            ["wait"],
            provider => provider.EnqueueHoldUntilReleasedCall(started, release, "capture_context"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Mind.ObserveForTest(Outcome(NavigationOutcomeKind.Arrived));
        _ = release.TrySetResult();
        await WaitUntilAsync(sceneTree, () => fixture.Mind.HasActiveObservationWait);
        await TestUtils.WaitForFramesAsync(sceneTree, NoWakeObservationFrames);

        Assert.True(fixture.Mind.HasActiveObservationWait);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(1, CountOccurrences(GetDeliveredSceneUpdates(fixture.Provider.Requests[1]), "Your `walk_to` move arrived"));
    }

    private static async Task AssertParkedWaitWokenBy(NavigationOutcomeKind kind, string expectedText)
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        await using var fixture = ParkedWakeFixture.Start(sceneTree, ["wait"]);
        await WaitUntilAsync(sceneTree, () => fixture.Mind.HasActiveObservationWait);
        _ = Assert.Single(fixture.Provider.Requests);

        fixture.Mind.ObserveForTest(Outcome(kind));
        await WaitUntilAsync(sceneTree, () => fixture.Provider.Requests.Count >= 2);
        await TestUtils.WaitForFramesAsync(sceneTree, 10);

        Assert.False(fixture.Mind.HasActiveObservationWait);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Empty(fixture.WaitDecision.Requests);
        IReadOnlyList<ChatMessage> woken = fixture.Provider.Requests[1];
        Assert.Contains(
            woken.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
            result => (result.Result?.ToString() ?? string.Empty).StartsWith("Wait ended: circumstances may call for you to act.", StringComparison.Ordinal));
        Assert.Equal(1, GetSceneUpdateExchangeCount(woken));
        Assert.Equal(1, CountOccurrences(GetDeliveredSceneUpdates(woken), expectedText));
    }

    private static ObservedNavigationOutcome Outcome(NavigationOutcomeKind kind)
        => new(kind, CoordinateMoveKind.Walk, kind == NavigationOutcomeKind.Failed ? "the way was blocked" : null, 1.5, -2, 90);

    /// <summary>
    /// A running AgenticMind session whose scripted provider answers the queued calls and then holds forever, with a
    /// wait decision gate that can never end a wait on its own within the observation window.
    /// </summary>
    private sealed class ParkedWakeFixture : IAsyncDisposable
    {
        private readonly SceneTree _sceneTree;
        private readonly FixturePlayerCharacter _player = new();
        private readonly CapturingTool _tool = new();
        private readonly CommittingActionTool _actionTool = new();

        private ParkedWakeFixture(SceneTree sceneTree)
        {
            _sceneTree = sceneTree;
            Owner = new TestCharacter();
            Mind = new TestAgenticMind(Owner)
            {
                SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
                ClientProvider = Provider,
                Tools = [_tool, _actionTool],
            };
        }

        public TestCharacter Owner
        {
            get;
        }

        public TestAgenticMind Mind
        {
            get;
        }

        public ScriptedSessionClientProvider Provider { get; } = new();

        public ScriptedWaitDecisionClient WaitDecision { get; } = new(WaitVerdict.NoReason);

        public static ParkedWakeFixture Start(
            SceneTree sceneTree,
            IReadOnlyList<string> calls,
            Action<ScriptedSessionClientProvider>? firstStep = null,
            bool parkAfterTool = false,
            Action<TestAgenticMind>? configure = null)
        {
            ParkedWakeFixture fixture = new(sceneTree);
            // A committed behaviour action parks the session through the decision-wait gate (session park).
            fixture._actionTool.ParksSessionByDefault = parkAfterTool;
            firstStep?.Invoke(fixture.Provider);
            foreach (string call in calls)
            {
                fixture.Provider.EnqueueCall(call);
            }

            fixture.Provider.EnqueueHoldForever();
            fixture.Mind.SetSceneContextLoaderForTesting(() => new AlleyCat.Scene.SceneContext([fixture.Owner, fixture._player]));
            fixture.Mind.SetVisualContextModeLoaderForTesting(() => VisualContextMode.Off);
            fixture.Mind.SetSceneDecisionClientForTesting(fixture.WaitDecision);
            fixture.Mind.SetWaitTimingForTesting(NoDeadlines);
            fixture.Mind.WaitEvaluationIntervalSeconds = 120f;
            configure?.Invoke(fixture.Mind);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(fixture.Mind);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            if (Mind.GetParent() is { } parent)
            {
                parent.RemoveChild(Mind);
            }

            await TestUtils.WaitForFramesAsync(_sceneTree, 2);
            Mind.Free();
            _tool.Free();
            _actionTool.Free();
            Provider.Free();
            _player.Free();
        }
    }

    /// <summary>
    /// Behaviour-tool double that commits its action at once, as the speech tool does at its playback hand-off, so a
    /// parking-by-default binding parks the session after its settled batch.
    /// </summary>
    private sealed partial class CommittingActionTool : AgentTool
    {
        public const string ToolNameValue = "commit_action";

        public CommittingActionTool()
        {
            ToolName = ToolNameValue;
            ToolDescription = "Commit one in-game action.";
        }

        protected override Delegate CreateDelegate() => Commit;

        private ValueTask<AgentToolResult> Commit(ScenarioContext context, AgentSessionInvocationSignals? signals = null)
        {
            _ = context;
            signals?.MarkActionCommitted();
            return ValueTask.FromResult(new AgentToolResult());
        }
    }

    /// <summary>
    /// Wait-decision client whose first evaluation is held until the fixture releases it with a verdict (completing
    /// even after cancellation, like a late provider answer); later evaluations answer "no reason" at once.
    /// </summary>
    private sealed class HeldWaitDecisionClient : IDecisionClient
    {
        private int _calls;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<WaitVerdict> ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) != 1)
            {
                return Answer(request, WaitVerdict.NoReason);
            }

            _ = FirstStarted.TrySetResult();
            WaitVerdict verdict = await ReleaseFirst.Task.ConfigureAwait(false);
            return Answer(request, verdict);
        }
    }
}
