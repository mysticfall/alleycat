using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.Core.Time;
using AlleyCat.IntegrationTests.Mind.AI.Decision;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Decision;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for the production session tools: <c>speak</c> turn-taking and cut-short boundaries,
/// and <c>wait</c> result composition.
/// </summary>
[Headless]
public sealed partial class SessionToolsIntegrationTests
{
    private const string CutShortBeforeSpoken =
        "Your speech was cut short by another event before it could be spoken.";

    /// <summary>
    /// Blank speech is rejected through the voice contract without submitting or observing anything
    /// (AI-002 TR-12).
    /// </summary>
    [Fact]
    public async Task Speak_WithBlankInput_RejectsWithoutSubmissionOrObservation()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => fixture.InvokeSpeakAsync("   ", CancellationToken.None).AsTask());

        Assert.Empty(fixture.OwnerVoice.Submissions);
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Speech blocks while an attended speaker's window is open, is unblocked by the attended-speaker-finished
    /// cue, and commits exactly one actor-stamped self observation at playback hand-off (SPCH-005 TR-37; AI-002 TR-14).
    /// </summary>
    [Fact]
    public async Task Speak_WhileAttendedSpeakerSpeaks_BlocksUntilCueThenSpeaksOnce()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.SpeakerVoice.BeginSpeech();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Hello there.", CancellationToken.None).AsTask();
        await ToolFixture.WaitForFramesAsync(2);
        Assert.False(speakTask.IsCompleted, "Speech must block while the attended speaker speaks.");
        Assert.Empty(fixture.OwnerVoice.Submissions);

        fixture.SpeakerVoice.EndSpeech();
        await fixture.OwnerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.OwnerVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.Equal(["Hello there."], fixture.OwnerVoice.Submissions);
        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Equal(fixture.Owner.FullId, committed.ActorId);
        Assert.Equal("Hello there.", committed.Content);
    }

    /// <summary>
    /// The owning character's own speaking voice and an unattributable voice never block speech
    /// (SPCH-005 TR-37).
    /// </summary>
    [Fact]
    public async Task Speak_WithOwnOrUnattributableVoiceSpeaking_NeverBlocks()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.OwnerVoice.BeginSpeakingWindow();
        fixture.TwinVoice.BeginSpeech();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("No blocking.", CancellationToken.None).AsTask();
        await fixture.OwnerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.OwnerVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.Equal(["No blocking."], fixture.OwnerVoice.Submissions);
    }

    /// <summary>
    /// Interruption while speech is blocked returns the non-throwing cut-short result: nothing was submitted and
    /// nothing was observed (AI-002 TR-22).
    /// </summary>
    [Fact]
    public async Task Speak_CancelledWhileBlocked_ReturnsCutShortResultWithoutObservation()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.SpeakerVoice.BeginSpeech();
        using CancellationTokenSource cancellation = new();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Interrupted.", cancellation.Token).AsTask();
        await ToolFixture.WaitForFramesAsync(2);
        cancellation.Cancel();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CutShortBeforeSpoken, result);
        Assert.Empty(fixture.OwnerVoice.Submissions);
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Cancellation before playback hand-off withdraws the submission silently: no observed speech, no failure
    /// broadcast, and the cut-short result (AI-002 TR-14/22, SPCH-005 TR-25).
    /// </summary>
    [Fact]
    public async Task Speak_CancelledBeforeHandOff_ReturnsCutShortResultSilently()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        using CancellationTokenSource cancellation = new();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Withdrawn.", cancellation.Token).AsTask();
        await fixture.OwnerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CutShortBeforeSpoken, result);
        Assert.True(fixture.OwnerVoice.CancellationObserved, "The pre-hand-off submission must observe the caller token.");
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Cancellation landing after playback hand-off commits the speech: playback stays active — never cut by
    /// cancellation or freshness — and exactly one self observation is ingested with the delivered result
    /// (AI-002 TR-14, SPCH-005 UR-14/TR-25).
    /// </summary>
    [Fact]
    public async Task Speak_CancelledAfterHandOff_KeepsCommittedSpeechUncutAndObservesOnce()
    {
        await using ToolFixture fixture = new(addAiVoice: true);
        await fixture.ReadyAsync();
        using CancellationTokenSource cancellation = new();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Committed mid-flight.", cancellation.Token).AsTask();
        await fixture.HandOffVoice!.HandOffStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.HandOffVoice.IsSpeaking, "Hand-off opens the speaking window before cancellation.");
        cancellation.Cancel();
        fixture.HandOffVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.True(fixture.HandOffVoice.IsSpeaking, "Committed speech must never be cut by cancellation or freshness.");
        Assert.Equal(0, fixture.HandOffVoice.SpeechEndedCount);
        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Equal(fixture.Owner.FullId, committed.ActorId);
        Assert.Equal("Committed mid-flight.", committed.Content);
    }

    /// <summary>
    /// The speak tool resolves the admission capability from the authored voice projection without any
    /// concrete-voice dependency: a capable voice receives the runner-owned admission transaction, its admitted
    /// submission commits at playback hand-off with exactly one self observation, and the ordinary cancellable
    /// path stays untouched (SPCH-005 TR-37/38, AC-27).
    /// </summary>
    [Fact]
    public async Task Speak_WithAdmissionCapableVoice_CommitsAtHandOffThroughTheCapability()
    {
        ToolAdmissionBroker admission = CreateUnstartedAdmissionBroker();
        AdmissionCapableVoice ownerVoice = new("capable-voice");
        await using ToolFixture fixture = new(speechTool: new SpeechTool(admission), ownerVoiceOverride: ownerVoice);
        await fixture.ReadyAsync();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Capable words.", CancellationToken.None).AsTask();
        await ownerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ownerVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.Equal(["Capable words."], ownerVoice.AdmittedSubmissions);
        _ = Assert.Single(ownerVoice.ReceivedTransactions);
        Assert.Empty(ownerVoice.OrdinarySubmissions);
        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Equal(fixture.Owner.FullId, committed.ActorId);
        Assert.Equal("Capable words.", committed.Content);
    }

    /// <summary>
    /// A cue-first admission refusal through the capability surfaces the non-throwing not-delivered result with no
    /// ordinary submission and no self observation (AI-002 TR-22, SPCH-005 TR-37): the transaction the tool
    /// resolved onto the capable voice refuses while the runner owns no pending arbitrated phase.
    /// </summary>
    [Fact]
    public async Task Speak_WithAdmissionCapableVoice_CueFirstRefusalSurfacesNotDeliveredResult()
    {
        ToolAdmissionBroker admission = CreateUnstartedAdmissionBroker();
        AdmissionCapableVoice ownerVoice = new("capable-voice")
        {
            AttemptTransaction = true,
        };
        await using ToolFixture fixture = new(speechTool: new SpeechTool(admission), ownerVoiceOverride: ownerVoice);
        await fixture.ReadyAsync();

        object? result = await fixture.InvokeSpeakAsync("Refused at the gate.", CancellationToken.None).AsTask();

        Assert.Equal(CutShortBeforeSpoken, result);
        Assert.Equal(["Refused at the gate."], ownerVoice.AdmittedSubmissions);
        _ = Assert.Single(ownerVoice.ReceivedTransactions);
        Assert.False(ownerVoice.TransactionAdmitted, "The transaction must refuse without a pending arbitrated phase.");
        Assert.Empty(ownerVoice.OrdinarySubmissions);
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// An editor-authored speech tool resource — constructed outside the AgenticMind composition — takes the
    /// specified fallback path even for a capability voice: admission arbitration applies only to composition-bound
    /// tools, so the submission runs through the ordinary cancellable path with no admission transaction resolved,
    /// commits at hand-off, and observes exactly once (AI-002 TR-14, SPCH-005 TR-37/38).
    /// </summary>
    [Fact]
    public async Task Speak_WithEditorAuthoredToolAndCapableVoice_TakesOrdinarySubmissionPath()
    {
        AdmissionCapableVoice ownerVoice = new("capable-voice");
        await using ToolFixture fixture = new(ownerVoiceOverride: ownerVoice);
        await fixture.ReadyAsync();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Unbound words.", CancellationToken.None).AsTask();
        await ownerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ownerVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.Equal(["Unbound words."], ownerVoice.OrdinarySubmissions);
        Assert.Empty(ownerVoice.AdmittedSubmissions);
        Assert.Empty(ownerVoice.ReceivedTransactions);
        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Equal(fixture.Owner.FullId, committed.ActorId);
        Assert.Equal("Unbound words.", committed.Content);
    }

    /// <summary>
    /// Creates the composition admission binding with an attached but never-run session runner, so tool-level
    /// fixtures resolve real admission transactions while no provider is ever contacted (AI-002 TR-13/14).
    /// </summary>
    private static ToolAdmissionBroker CreateUnstartedAdmissionBroker()
    {
        ToolAdmissionBroker admission = new();
        admission.AttachRunner(new AgentSessionRunner(
            new UnstartedSessionClient(),
            "Unused tool-fixture instructions.",
            [],
            [],
            NullLogger.Instance));
        return admission;
    }

    /// <summary>
    /// Valid declared recipients ride the ordinary cancellable submission and the committed self observation: the
    /// voice receives exactly the declared canonical set and the owner-stamped observation records the same
    /// addressees in declaration order.
    /// </summary>
    [Fact]
    public async Task Speak_WithDeclaredRecipients_SubmitsAndSelfObservesTheCanonicalSet()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();

        Task<object?> speakTask = fixture
            .InvokeSpeakAsync("Addressed words.", ["char:speaker", "char:twin_first"], CancellationToken.None)
            .AsTask();
        await fixture.OwnerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.OwnerVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.Equal(
            [["char:speaker", "char:twin_first"]],
            fixture.OwnerVoice.SubmissionRecipientSets.Select(static set => (string[])[.. set!]));
        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Equal(fixture.Owner.FullId, committed.ActorId);
        Assert.Equal("Addressed words.", committed.Content);
        Assert.Equal(["char:speaker", "char:twin_first"], committed.Addressees);
    }

    /// <summary>
    /// An admission-capable voice receives the declared recipients through the capability's admitted submission,
    /// keeping the recipient declaration on the arbitrated commit path.
    /// </summary>
    [Fact]
    public async Task Speak_WithAdmissionCapableVoice_CarriesDeclaredRecipientsThroughAdmission()
    {
        ToolAdmissionBroker admission = CreateUnstartedAdmissionBroker();
        AdmissionCapableVoice ownerVoice = new("capable-voice");
        await using ToolFixture fixture = new(speechTool: new SpeechTool(admission), ownerVoiceOverride: ownerVoice);
        await fixture.ReadyAsync();

        Task<object?> speakTask = fixture
            .InvokeSpeakAsync("Capable addressed words.", ["char:twin_second"], CancellationToken.None)
            .AsTask();
        await ownerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ownerVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.Equal(
            [["char:twin_second"]],
            ownerVoice.AdmittedRecipientSets.Select(static set => (string[])[.. set!]));
        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Equal(["char:twin_second"], committed.Addressees);
    }

    /// <summary>
    /// An omitted recipients argument means unspecified: the submission carries no recipient set and the committed
    /// self observation stays undecided with an empty addressee set.
    /// </summary>
    [Fact]
    public async Task Speak_WithoutRecipients_SubmitsUnspecifiedAndSelfObservesUndecided()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Unspecified words.", CancellationToken.None).AsTask();
        await fixture.OwnerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.OwnerVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.Equal([null], fixture.OwnerVoice.SubmissionRecipientSets);
        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Empty(committed.Addressees);
    }

    /// <summary>
    /// A recipient unknown to the live scene roster is refused with a model-correctable error naming the problem
    /// ID: nothing is submitted and no observation is ingested.
    /// </summary>
    [Fact]
    public async Task Speak_WithUnknownRecipient_ReturnsCorrectableErrorWithoutSubmitting()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();

        object? result = await fixture
            .InvokeSpeakAsync("Refused words.", ["char:missing"], CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        string message = Assert.IsType<string>(result);
        Assert.Contains("'char:missing' is not a character in the current scene", message, StringComparison.Ordinal);
        Assert.Empty(fixture.OwnerVoice.Submissions);
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Addressing the speaker's own identity is refused with a model-correctable error: a speaker cannot declare
    /// themselves as their own addressee.
    /// </summary>
    [Fact]
    public async Task Speak_WithSelfRecipient_ReturnsCorrectableErrorWithoutSubmitting()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();

        object? result = await fixture
            .InvokeSpeakAsync("Self-addressed words.", ["char:owner"], CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        string message = Assert.IsType<string>(result);
        Assert.Contains("'char:owner' is you", message, StringComparison.Ordinal);
        Assert.Empty(fixture.OwnerVoice.Submissions);
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// A blank recipient entry is refused with a model-correctable error rather than an exception across the tool
    /// boundary.
    /// </summary>
    [Fact]
    public async Task Speak_WithBlankRecipientEntry_ReturnsCorrectableErrorWithoutSubmitting()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();

        object? result = await fixture
            .InvokeSpeakAsync("Blank entry words.", [" "], CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        string message = Assert.IsType<string>(result);
        Assert.Contains("blank entry", message, StringComparison.Ordinal);
        Assert.Empty(fixture.OwnerVoice.Submissions);
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Duplicate recipients collapse into one distinct set preserving first-declaration order, so a repeated ID
    /// neither fails the call nor duplicates the observation's addressee set.
    /// </summary>
    [Fact]
    public async Task Speak_WithDuplicateRecipients_ResolvesOneDistinctOrderedSet()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();

        Task<object?> speakTask = fixture
            .InvokeSpeakAsync("Deduplicated words.", ["char:twin_first", "char:speaker", "char:twin_first"], CancellationToken.None)
            .AsTask();
        await fixture.OwnerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.OwnerVoice.CompleteHandOff();
        _ = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Equal(["char:twin_first", "char:speaker"], committed.Addressees);
    }

    /// <summary>
    /// The wait tool requires a session game clock and fails clearly without one.
    /// </summary>
    [Fact]
    public async Task Wait_WithoutSessionClock_FailsClearly()
    {
        await using ToolFixture fixture = new(clock: null);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.InvokeWaitReadylessAsync(CancellationToken.None).AsTask());

        Assert.Contains("game clock", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Only a successfully started explicit wait marks its entry, exactly once.</summary>
    [Fact]
    public async Task Wait_MarksEntryOnceAndOnlyAfterSuccessfulAcquisition()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        int entries = 0;
        fixture.Mind.SetSceneDecisionClientLoaderForTesting(() => throw new DecisionException(
            DecisionFailure.Configuration, "Scripted acquisition failure."));
        _ = await Assert.ThrowsAsync<DecisionException>(() =>
            fixture.Mind.WaitForNextTurnAsync(CancellationToken.None, 30, () => entries++));
        Assert.Equal(0, entries);

        fixture.Mind.SetSceneDecisionClientForTesting(new ScriptedWaitDecisionClient(WaitVerdict.ActNow));
        fixture.Mind.WaitEvaluationIntervalSeconds = 0.05f;
        MindBase.WaitOutcome outcome = await fixture.Mind.WaitForNextTurnAsync(CancellationToken.None, 30, () => entries++)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MindBase.ObservationWaitWake.DecisionAct, outcome.Wake);
        Assert.Equal(1, entries);
    }

    /// <summary>
    /// A wait result is payload-free — only its wake reason, elapsed duration, and current game time; an immediate
    /// verdict renders as a chance to act, not a command.
    /// </summary>
    [Fact]
    public async Task Wait_ImmediateVerdict_ComposesPayloadFreeReasonElapsedAndGameTime()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.Mind.SetSceneDecisionClientForTesting(new ScriptedWaitDecisionClient(WaitVerdict.ActNow));
        fixture.Mind.ObserveForTest(new TypedObservation("test.alpha", 1f));

        object? result = await fixture.InvokeWaitAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        string message = Assert.IsType<string>(result);
        Assert.Equal(
            "Wait ended: circumstances may call for you to act. Elapsed game time: 0.0 seconds. "
                + "Current game time: 100.0s.",
            message);
    }

    /// <summary>
    /// Wait never renders observation text: the decision model sees the evidence, the result never does
    /// (AI-002 TR-10).
    /// </summary>
    [Fact]
    public async Task Wait_ImmediateVerdict_RemainsPayloadFree()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.Mind.SetSceneDecisionClientForTesting(new ScriptedWaitDecisionClient(WaitVerdict.ActNow));
        fixture.Mind.ObserveForTest(new TypedObservation("test.before", 0.5f));
        fixture.Mind.ObserveForTest(new ObservedVisualDescription("char:coat", "A weathered red coat."));
        fixture.Mind.ObserveForTest(new TypedObservation("test.after", 0.5f));

        string message = Assert.IsType<string>(
            await fixture.InvokeWaitAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("Wait ended:", message, StringComparison.Ordinal);
        Assert.DoesNotContain("weathered red coat", message, StringComparison.Ordinal);
        Assert.DoesNotContain("test.before", message, StringComparison.Ordinal);
        Assert.DoesNotContain("test.after", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One request carries both wait-gate choice questions; a deferred verdict keeps the wait parked while its
    /// deadline has not expired, and an immediate verdict on a later evaluation completes it with the elapsed
    /// game time.
    /// </summary>
    [Fact]
    public async Task Wait_DeferredThenImmediateVerdicts_ParkThenCompleteWithElapsedGameTime()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        ScriptedWaitDecisionClient client = new(WaitVerdict.NoReason);
        fixture.Mind.SetSceneDecisionClientForTesting(client);
        fixture.Mind.SetWaitTimingForTesting(ScriptedWaitVerdicts.NoDeadlines);
        fixture.Mind.WaitEvaluationIntervalSeconds = 0.05f;

        Task<object?> waitTask = fixture.InvokeWaitAsync(CancellationToken.None).AsTask();
        await WaitForWaitEvaluationsAsync(client, minimumEvaluations: 2);
        Assert.False(waitTask.IsCompleted);
        Assert.Equal(
            [DecisionWaitPolicy.ResponseQuestionName, DecisionWaitPolicy.InitiativeQuestionName],
            client.Requests[0].Questions.Select(static question => question.Name));
        Assert.All(client.Requests[0].Questions, question => Assert.IsType<ChoiceQuestion>(question));

        client.Verdict = WaitVerdict.ActNow;
        fixture.Clock.NowSeconds = 102.5d;
        object? result = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

        string message = Assert.IsType<string>(result);
        Assert.Equal(
            "Wait ended: circumstances may call for you to act. Elapsed game time: 2.5 seconds. "
                + "Current game time: 102.5s.",
            message);
    }

    /// <summary>
    /// A deferred verdict's armed deadline starts the agent when it expires, even though every later verdict
    /// keeps deferring: the deadline is never extended by repeated deferrals.
    /// </summary>
    [Fact]
    public async Task Wait_DeferredVerdict_DeadlineExpiryCompletesThePause()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        ScriptedWaitDecisionClient client = new(WaitVerdict.MayAct);
        fixture.Mind.SetSceneDecisionClientForTesting(client);
        fixture.Mind.SetWaitTimingForTesting(new DecisionWaitTimingSettings(30d, 0.3d, 30d, 0d));
        fixture.Mind.WaitEvaluationIntervalSeconds = 0.05f;

        object? result = await fixture.InvokeWaitAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.StartsWith(
            "Wait ended: a pause gives you an opening to act.",
            Assert.IsType<string>(result),
            StringComparison.Ordinal);
        // Several deferrals were served while the first armed deadline ran down unchanged.
        Assert.True(client.Requests.Count >= 2, $"Expected repeated deferrals; served {client.Requests.Count}.");
    }

    /// <summary>
    /// An explicit quiet time seeds the initial deadline: with the gate deferring to a later deadline, the
    /// requested seconds elapse first and the wait completes through the timer wake reason.
    /// </summary>
    [Fact]
    public async Task Wait_WithSeconds_TimerElapseCompletesTheQuietTime()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.Mind.SetSceneDecisionClientForTesting(new ScriptedWaitDecisionClient(WaitVerdict.NoReason));
        fixture.Mind.WaitEvaluationIntervalSeconds = 0.05f;

        object? result = await fixture.InvokeWaitAsync(seconds: 0.3f, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        string message = Assert.IsType<string>(result);
        Assert.StartsWith("Wait ended: the chosen quiet time elapsed.", message, StringComparison.Ordinal);
        Assert.EndsWith("Current game time: 100.0s.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sooner deferred-verdict deadline replaces a longer explicit quiet time: the wait ends at the decision
    /// deadline, never later than requested.
    /// </summary>
    [Fact]
    public async Task Wait_WithSeconds_SoonerDecisionDeadlineReplacesTheQuietTime()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.Mind.SetSceneDecisionClientForTesting(new ScriptedWaitDecisionClient(WaitVerdict.MayAct));
        fixture.Mind.SetWaitTimingForTesting(new DecisionWaitTimingSettings(30d, 0.2d, 30d, 0d));
        fixture.Mind.WaitEvaluationIntervalSeconds = 0.05f;

        object? result = await fixture.InvokeWaitAsync(seconds: 30f, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.StartsWith(
            "Wait ended: a pause gives you an opening to act.",
            Assert.IsType<string>(result),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Deliberate silence can be ended early by circumstances: while a long quiet-time deadline runs, an
    /// immediate verdict wakes the wait before it elapses.
    /// </summary>
    [Fact]
    public async Task Wait_WithSeconds_ImmediateVerdictEndsTheQuietTimeEarly()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.Mind.SetSceneDecisionClientForTesting(new ScriptedWaitDecisionClient(WaitVerdict.ActNow));
        fixture.Mind.WaitEvaluationIntervalSeconds = 0.05f;

        object? result = await fixture.InvokeWaitAsync(seconds: 30f, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        string message = Assert.IsType<string>(result);
        Assert.StartsWith("Wait ended: circumstances may call for you to act.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decision failures keep the wait waiting with backoff — the tool call survives a failing service and
    /// completes only on a later immediate verdict.
    /// </summary>
    [Fact]
    public async Task Wait_FailureVerdict_KeepsWaitingUntilImmediateVerdict()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        ScriptedWaitDecisionClient client = new(WaitVerdict.ActNow, decisionFailure: DecisionFailure.Timeout);
        fixture.Mind.SetSceneDecisionClientForTesting(client);
        // The failures' no-reason deadline must not expire inside the observation window.
        fixture.Mind.SetWaitTimingForTesting(ScriptedWaitVerdicts.NoDeadlines);
        fixture.Mind.WaitEvaluationIntervalSeconds = 0.05f;

        Task<object?> waitTask = fixture.InvokeWaitAsync(CancellationToken.None).AsTask();
        await WaitForWaitEvaluationsAsync(client, minimumEvaluations: 2);
        Assert.False(waitTask.IsCompleted);

        client.DecisionFailure = null;
        object? result = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(
            "Wait ended: circumstances may call for you to act.",
            Assert.IsType<string>(result),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A parked wait never stalls on a failing decision service: with every evaluation failing and no explicit
    /// quiet time, the failures arm the no-reason timeout and its expiry completes the wait as a pause.
    /// </summary>
    [Fact]
    public async Task Wait_EveryEvaluationFailing_NoReasonDeadlineCompletesThePause()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        ScriptedWaitDecisionClient client = new(WaitVerdict.ActNow, decisionFailure: DecisionFailure.Transport);
        fixture.Mind.SetSceneDecisionClientForTesting(client);
        fixture.Mind.SetWaitTimingForTesting(new DecisionWaitTimingSettings(3600d, 3600d, 0.3d, 0d));
        fixture.Mind.WaitEvaluationIntervalSeconds = 0.05f;

        object? result = await fixture.InvokeWaitAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.StartsWith(
            "Wait ended: a pause gives you an opening to act.",
            Assert.IsType<string>(result),
            StringComparison.Ordinal);
        Assert.NotEmpty(client.Requests);
    }

    /// <summary>
    /// An armed deadline still fires while the decision service hangs: the in-flight evaluation is abandoned and
    /// the explicit quiet time completes on time instead of waiting for the request's own timeout.
    /// </summary>
    [Fact]
    public async Task Wait_WithSeconds_DeadlineFiresWhileAnEvaluationHangs()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        ScriptedWaitDecisionClient client = new(WaitVerdict.ActNow)
        {
            Hang = true
        };
        fixture.Mind.SetSceneDecisionClientForTesting(client);
        fixture.Mind.WaitEvaluationIntervalSeconds = 0.05f;

        object? result = await fixture.InvokeWaitAsync(seconds: 0.5f, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.StartsWith(
            "Wait ended: the chosen quiet time elapsed.",
            Assert.IsType<string>(result),
            StringComparison.Ordinal);
        _ = Assert.Single(client.Requests);
        client.Hang = false;
        object? next = await fixture.InvokeWaitAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("Wait ended: circumstances may call for you to act.", Assert.IsType<string>(next), StringComparison.Ordinal);
        Assert.True(client.HungRequestCancelled, "The abandoned evaluation must observe cancellation.");
        Assert.Equal(2, client.Requests.Count);
    }

    /// <summary>
    /// A scene entrant or departure alone brings the next wait evaluation forward (TQ-B4): with the cadence far
    /// outside the observation window and no speech, a departure triggers a membership-labelled evaluation within
    /// the window, an unchanged roster then stays quiet (no wake, no spin), and an entrant triggers the next one.
    /// </summary>
    [Fact]
    public async Task Wait_MembershipChangeAlone_BringsTheNextEvaluationForward()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        ScriptedWaitDecisionClient client = new(WaitVerdict.NoReason);
        fixture.Mind.SetSceneDecisionClientForTesting(client);
        fixture.Mind.SetWaitTimingForTesting(ScriptedWaitVerdicts.NoDeadlines);
        // The cadence boundary lies far outside every observation window below: only a nudge can evaluate.
        fixture.Mind.WaitEvaluationIntervalSeconds = MembershipTestCadenceSeconds;
        using CancellationTokenSource cancellation = new();
        IReadOnlyList<ICharacter> fullRoster = fixture.Membership;
        Task<object?> waitTask = fixture.InvokeWaitAsync(cancellation.Token).AsTask();

        try
        {
            await WaitForActiveWaitAsync(fixture);
            await WaitForQuietWindowAsync();
            Assert.Empty(client.Requests);

            fixture.Membership = [.. fullRoster.Take(fullRoster.Count - 1)];
            await WaitForWaitEvaluationsAsync(client, minimumEvaluations: 1, _membershipObservationWindow);
            Assert.DoesNotContain("twin_second", ((JsonElement)client.Requests[0].Evidence).GetRawText());

            await WaitForQuietWindowAsync();
            _ = Assert.Single(client.Requests);
            Assert.False(waitTask.IsCompleted);

            fixture.Membership = fullRoster;
            await WaitForWaitEvaluationsAsync(client, minimumEvaluations: 2, _membershipObservationWindow);
            Assert.Contains("twin_second", ((JsonElement)client.Requests[1].Evidence).GetRawText());
            Assert.False(waitTask.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            _ = await Record.ExceptionAsync(() => waitTask);
        }
    }

    /// <summary>
    /// A membership change brings the next evaluation forward even while a failure backoff is in effect
    /// (TQ-B4): after a failed membership-triggered evaluation doubles the backoff past the cadence, an entrant
    /// still evaluates within the observation window, and an unchanged roster does not retry early.
    /// </summary>
    [Fact]
    public async Task Wait_MembershipChangeDuringFailureBackoff_StillBringsTheEvaluationForward()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        ScriptedWaitDecisionClient client = new(WaitVerdict.NoReason, decisionFailure: DecisionFailure.Timeout);
        fixture.Mind.SetSceneDecisionClientForTesting(client);
        fixture.Mind.SetWaitTimingForTesting(ScriptedWaitVerdicts.NoDeadlines);
        fixture.Mind.WaitEvaluationIntervalSeconds = MembershipTestCadenceSeconds;
        using CancellationTokenSource cancellation = new();
        IReadOnlyList<ICharacter> fullRoster = fixture.Membership;
        Task<object?> waitTask = fixture.InvokeWaitAsync(cancellation.Token).AsTask();

        try
        {
            await WaitForActiveWaitAsync(fixture);
            fixture.Membership = [.. fullRoster.Take(fullRoster.Count - 1)];
            await WaitForWaitEvaluationsAsync(client, minimumEvaluations: 1, _membershipObservationWindow);

            // The failure doubled the backoff beyond the cadence; an unchanged roster must not retry early.
            await WaitForQuietWindowAsync();
            _ = Assert.Single(client.Requests);

            fixture.Membership = fullRoster;
            await WaitForWaitEvaluationsAsync(client, minimumEvaluations: 2, _membershipObservationWindow);
            Assert.Contains("twin_second", ((JsonElement)client.Requests[1].Evidence).GetRawText());
            Assert.False(waitTask.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            _ = await Record.ExceptionAsync(() => waitTask);
        }
    }

    private const float MembershipTestCadenceSeconds = 120f;
    private static readonly TimeSpan _membershipObservationWindow = TimeSpan.FromSeconds(5);

    private static async Task WaitForActiveWaitAsync(ToolFixture fixture)
    {
        for (int attempt = 0; attempt < 600 && !fixture.Mind.HasActiveObservationWait; attempt++)
        {
            await ToolFixture.WaitForFramesAsync(1);
        }

        Assert.True(fixture.Mind.HasActiveObservationWait);
    }

    /// <summary>A one-second window with an unchanged roster: several membership checks, no cadence boundary.</summary>
    private static Task WaitForQuietWindowAsync() => Task.Delay(TimeSpan.FromSeconds(1));

    /// <summary>Waits up to the observation window for the scripted wait client's evaluation count.</summary>
    private static async Task WaitForWaitEvaluationsAsync(
        ScriptedWaitDecisionClient client,
        int minimumEvaluations,
        TimeSpan window)
    {
        DateTime deadline = DateTime.UtcNow + window;
        while (client.Requests.Count < minimumEvaluations && DateTime.UtcNow < deadline)
        {
            await ToolFixture.WaitForFramesAsync(1);
        }

        Assert.True(
            client.Requests.Count >= minimumEvaluations,
            $"No membership-driven evaluation within {window.TotalSeconds:F0}s; served {client.Requests.Count}.");
    }

    /// <summary>Pumps frames until the scripted wait client has served the requested evaluation count.</summary>
    private static async Task WaitForWaitEvaluationsAsync(
        ScriptedWaitDecisionClient client,
        int minimumEvaluations)
    {
        for (int attempt = 0; attempt < 600 && client.Requests.Count < minimumEvaluations; attempt++)
        {
            await ToolFixture.WaitForFramesAsync(2);
        }

        Assert.True(
            client.Requests.Count >= minimumEvaluations,
            $"The wait decision poll did not evaluate within the pump window; served {client.Requests.Count}.");
    }

    /// <summary>
    /// Scripted wait decision client: answers both wait-gate questions with a reconfigurable verdict, a typed
    /// failure, or a hang until cancelled, capturing every dispatched request.
    /// </summary>
    private sealed class ScriptedWaitDecisionClient(
        WaitVerdict verdict,
        DecisionFailure? decisionFailure = null) : IDecisionClient
    {
        public List<DecisionRequest> Requests { get; } = [];

        public WaitVerdict Verdict
        {
            get; set;
        } = verdict;

        public DecisionFailure? DecisionFailure
        {
            get; set;
        } = decisionFailure;

        public bool Hang
        {
            get; set;
        }

        public bool HungRequestCancelled
        {
            get; private set;
        }

        public async Task<DecisionResult> EvaluateAsync(
            DecisionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Hang)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    HungRequestCancelled = true;
                    throw;
                }
            }

            return DecisionFailure is { } failure
                ? throw new DecisionException(failure, "Scripted wait failure.")
                : ScriptedWaitVerdicts.Answer(request, Verdict);
        }
    }

    private sealed record TypedObservation(string Key, float Importance) : AgentObservation
    {
        public override string TypeKey => Key;

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    /// <summary>
    /// Assembles the in-tree Mind, voiced scene membership, session tool functions, and a controllable game
    /// clock shared by the tool tests.
    /// </summary>
    private sealed class ToolFixture : IAsyncDisposable
    {
        private readonly SpeechTool _speechTool;
        private readonly WaitTool _waitTool = new();
        private AIFunction? _speakFunction;
        private AIFunction? _waitFunction;
        public ToolFixture(
            bool addAiVoice = false,
            FakeGameClock? clock = null,
            SpeechTool? speechTool = null,
            IVoice? ownerVoiceOverride = null)
        {
            _speechTool = speechTool ?? new SpeechTool();
            Clock = clock ?? new FakeGameClock { NowSeconds = 100d };
            OwnerVoice = new ControllableVoice("owner-voice");
            SpeakerVoice = new WindowedVoice("speaker-voice");
            TwinVoice = new WindowedVoice("twin-voice");
            Speaker = new VoiceCharacter("speaker", SpeakerVoice);
            TwinA = new VoiceCharacter("twin_first", TwinVoice);
            TwinB = new VoiceCharacter("twin_second", TwinVoice);
            HandOffVoice = addAiVoice ? new HandOffAIVoice() : null;
            Owner = new VoiceCharacter(
                "owner",
                ownerVoiceOverride ?? (HandOffVoice is null ? OwnerVoice : HandOffVoice));
            Membership = [Owner, Speaker, TwinA, TwinB];
            Mind = new TestMind(Owner)
            {
                AttentionDecayPerSecond = 0f,
            };
            Mind.SetSceneContextLoaderForTesting(() => new TestSceneContext(Membership));
            Mind.AttendForTest(Speaker.FullId);
        }

        public FakeGameClock Clock
        {
            get;
        }

        public ControllableVoice OwnerVoice
        {
            get;
        }

        public WindowedVoice SpeakerVoice
        {
            get;
        }

        public WindowedVoice TwinVoice
        {
            get;
        }

        public HandOffAIVoice? HandOffVoice
        {
            get;
        }

        public VoiceCharacter Owner
        {
            get;
        }

        public IReadOnlyList<ICharacter> Membership
        {
            get;
            set;
        }

        private VoiceCharacter Speaker
        {
            get;
        }

        private VoiceCharacter TwinA
        {
            get;
        }

        private VoiceCharacter TwinB
        {
            get;
        }

        public TestMind Mind
        {
            get;
        }

        public async Task ReadyAsync()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(Mind);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            ScenarioContext context = new(Owner, new TestSceneContext(Membership));
            IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
            AgentToolSession sessionServices = new(context, Mind, Clock);
            _speakFunction = _speechTool.CreateFunction(context, Mind, dispatcher, sessionServices);
            _waitFunction = _waitTool.CreateFunction(context, Mind, dispatcher, sessionServices);
        }

        public ValueTask<object?> InvokeSpeakAsync(string speech, CancellationToken cancellationToken)
            => InvokeAsync(_speakFunction!, new Dictionary<string, object?> { ["speech"] = speech }, cancellationToken);

        /// <summary>
        /// Invokes the speak tool with an explicit recipients argument: null omits the argument entirely — the
        /// model-left-unspecified case — while an empty array passes an explicit empty recipients value.
        /// </summary>
        public ValueTask<object?> InvokeSpeakAsync(
            string speech,
            IReadOnlyList<string>? recipients,
            CancellationToken cancellationToken)
        {
            Dictionary<string, object?> arguments = new()
            {
                ["speech"] = speech
            };
            if (recipients is not null)
            {
                arguments["recipients"] = recipients;
            }

            return InvokeAsync(_speakFunction!, arguments, cancellationToken);
        }

        public ValueTask<object?> InvokeWaitAsync(CancellationToken cancellationToken)
            => InvokeAsync(_waitFunction!, new Dictionary<string, object?>(), cancellationToken);

        /// <summary>
        /// Invokes the wait tool with an explicit <c>seconds</c> argument: the deliberate quiet-time duration
        /// whose timer races the decision gate (§11).
        /// </summary>
        public ValueTask<object?> InvokeWaitAsync(float seconds, CancellationToken cancellationToken)
            => InvokeAsync(_waitFunction!, new Dictionary<string, object?> { ["seconds"] = seconds }, cancellationToken);

        /// <summary>Invokes the wait tool without a tree fixture, for the missing-clock failure contract.</summary>
        public ValueTask<object?> InvokeWaitReadylessAsync(CancellationToken cancellationToken)
        {
            ScenarioContext context = new(Owner, new TestSceneContext(Membership));
            IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
            AgentToolSession sessionServices = new(context, Mind, Clock: null);
            AIFunction waitFunction = _waitTool.CreateFunction(context, Mind, dispatcher, sessionServices);
            return InvokeAsync(waitFunction, new Dictionary<string, object?>(), cancellationToken);
        }

        public static async Task WaitForFramesAsync(int frameCount)
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            await TestUtils.WaitForFramesAsync(sceneTree, frameCount);
        }

        public async ValueTask DisposeAsync()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            Mind.QueueFree();
            HandOffVoice?.Free();
            _speechTool.Free();
            _waitTool.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }

        private static ValueTask<object?> InvokeAsync(
            AIFunction function,
            IDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
            => function.InvokeAsync(new AIFunctionArguments(arguments), cancellationToken);

    }

    private sealed partial class TestMind(VoiceCharacter owner) : MindBase
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public void AttendForTest(string fullId)
            => ReinforceAttention(fullId, 1f, AttentionSettings.Create(1f, 0f, 0.05f, 0.25f));

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>
    /// Voice whose cancellable submission the test controls: submissions are recorded, the hand-off completes
    /// only on demand, and caller cancellation is observed.
    /// </summary>
    private sealed class ControllableVoice(string id) : IVoice
    {
        private readonly TaskCompletionSource _handOff = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; set; } = id;

        public string Type => "voice";

        public string FullId => $"voice:{Id}";

        public bool IsSpeaking
        {
            get; private set;
        }

        public List<string> Submissions { get; } = [];

        public List<IReadOnlyList<string>?> SubmissionRecipientSets { get; } = [];

        public TaskCompletionSource SubmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved
        {
            get; private set;
        }

        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;

        public Vector3 Origin => Vector3.Zero;

        public void BeginSpeakingWindow()
        {
            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
        }

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
        {
            Submissions.Add(speech);
            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
            _ = SubmissionStarted.TrySetResult();
            try
            {
                await _handOff.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                IsSpeaking = false;
                SpeechEnded?.Invoke(this);
                throw;
            }
        }

        public ValueTask SpeakCancellableAsync(
            string speech,
            IReadOnlyList<string>? recipients,
            CancellationToken cancellationToken)
        {
            SubmissionRecipientSets.Add(recipients is { Count: > 0 } ? [.. recipients] : null);
            return SpeakCancellableAsync(speech, cancellationToken);
        }

        public void CompleteHandOff() => _ = _handOff.TrySetResult();
    }

    /// <summary>Windowed voice whose speaking state and events are driven explicitly by the test.</summary>
    private sealed class WindowedVoice(string id) : IVoice
    {
        public string Id { get; set; } = id;

        public string Type => "voice";

        public string FullId => $"voice:{Id}";

        public bool IsSpeaking
        {
            get;
            private set;
        }

        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;

        public Vector3 Origin => Vector3.Zero;

        public void BeginSpeech()
        {
            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
        }

        public void EndSpeech()
        {
            IsSpeaking = false;
            SpeechEnded?.Invoke(this);
        }

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Plain <see cref="IVoice" /> double that additionally implements the admission capability (SPCH-005 TR-37):
    /// it records the admission transactions the speak tool resolves onto it, models an admitted submission that
    /// completes at a demanded playback hand-off, and can exercise the refusal path by attempting the received
    /// transaction — which refuses while the runner owns no pending arbitrated phase.
    /// </summary>
    private sealed class AdmissionCapableVoice(string id) : IVoice, IAdmissionCapableVoice
    {
        private readonly TaskCompletionSource _handOff = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; set; } = id;

        public string Type => "voice";

        public string FullId => $"voice:{Id}";

        public bool IsSpeaking
        {
            get;
            private set;
        }

        public List<string> AdmittedSubmissions { get; } = [];

        public List<IReadOnlyList<string>?> AdmittedRecipientSets { get; } = [];

        public List<SpeechAdmissionTransaction> ReceivedTransactions { get; } = [];

        public List<string> OrdinarySubmissions { get; } = [];

        public TaskCompletionSource SubmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the double attempts the received transaction, modelling a cue-first refusal.</summary>
        public bool AttemptTransaction
        {
            get;
            set;
        }

        public bool TransactionAdmitted
        {
            get;
            private set;
        }

        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;

        public Vector3 Origin => Vector3.Zero;

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
        {
            OrdinarySubmissions.Add(speech);
            _ = SubmissionStarted.TrySetResult();
            await _handOff.Task.WaitAsync(cancellationToken);
        }

        async ValueTask<bool> IAdmissionCapableVoice.SpeakCancellableAdmittedAsync(
            string speech,
            IReadOnlyList<string>? recipients,
            CancellationToken cancellationToken,
            SpeechAdmissionTransaction admission)
        {
            AdmittedSubmissions.Add(speech);
            AdmittedRecipientSets.Add(recipients is { Count: > 0 } ? [.. recipients] : null);
            ReceivedTransactions.Add(admission);
            if (AttemptTransaction)
            {
                TransactionAdmitted = admission.TryAdmit(() => { });
                if (!TransactionAdmitted)
                {
                    return false;
                }
            }

            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
            _ = SubmissionStarted.TrySetResult();
            await _handOff.Task.WaitAsync(cancellationToken);
            return true;
        }

        public void CompleteHandOff()
        {
            IsSpeaking = false;
            SpeechEnded?.Invoke(this);
            _ = _handOff.TrySetResult();
        }
    }

    /// <summary>Chat client proving the tool-level admission broker's runner is never run against a provider.</summary>
    private sealed class UnstartedSessionClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "The tool-level admission broker runner must never contact a provider.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = await GetResponseAsync(messages, options, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// <see cref="AIVoice" /> double whose hand-off the test completes on demand, so the shared cut capability
    /// can be observed after cancellation.
    /// </summary>
    private sealed partial class HandOffAIVoice : AIVoice
    {
        private readonly TaskCompletionSource _handOffCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HandOffAIVoice()
        {
            Id = "handoff-voice";
            SpeechEnded += OnSpeechEnded;
        }

        public TaskCompletionSource HandOffStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SpeechEndedCount
        {
            get; private set;
        }

        public override ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
            => SpeakCancellableAsync(speech, null, cancellationToken);

        public override ValueTask SpeakCancellableAsync(
            string speech,
            IReadOnlyList<string>? recipients,
            CancellationToken cancellationToken)
        {
            // Every cancellable entry point funnels through this one test-controlled hand-off boundary.
            OpenSpeakingWindow();
            _ = HandOffStarted.TrySetResult();
            return new ValueTask(_handOffCompletion.Task);
        }

        public void CompleteHandOff() => _ = _handOffCompletion.TrySetResult();

        private void OnSpeechEnded(IVoice voice) => SpeechEndedCount++;
    }

    private sealed class VoiceCharacter(string id, IVoice voice) : ICharacter
    {
        public string Id { get; set; } = id;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [voice];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed class FakeGameClock : IGameClock
    {
        public double NowSeconds
        {
            get;
            set;
        }
    }

    private sealed record TestSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException();
    }
}
