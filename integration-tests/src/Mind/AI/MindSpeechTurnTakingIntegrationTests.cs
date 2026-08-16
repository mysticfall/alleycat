using System.Diagnostics;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.LipSync;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Deterministic coverage for the speaking-activity turn-start gate, block-with-wake, speech-start interruption,
/// and interruption cut of already-audible speech.
/// </summary>
[Headless]
public sealed partial class MindSpeechTurnTakingIntegrationTests
{
    /// <summary>
    /// An attended speaker's open speaking window blocks a due turn, and a blocking SpeechEnded starts it immediately.
    /// </summary>
    [Fact]
    public async Task TurnStart_BlockedWhileAttendedSpeakerSpeaks_AndStartsOnSpeechEnded()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestFixture fixture = await CreateFixtureAsync(sceneTree, TurnGate.Completed(), TurnGate.Completed());
        fixture.Mind.ReinforceAttentionForTest(fixture.SpeakerCharacter.FullId, 0.1f);

        try
        {
            fixture.SpeakerVoice.BeginSpeech();
            fixture.Mind.ObserveForTest(new TestObservation(1f, "blocked turn"));
            await TestUtils.WaitForFramesAsync(sceneTree, 10);

            Assert.Empty(fixture.Mind.Batches);
            Assert.True(fixture.Mind.HasPendingForTest);

            fixture.SpeakerVoice.EndSpeech();
            await WaitUntilAsync(sceneTree, () => fixture.Mind.Batches.Count == 1, 120);

            Assert.Equal("blocked turn", Assert.IsType<TestObservation>(Assert.Single(fixture.Mind.Batches[0])).Value);
            Assert.False(fixture.Mind.HasPendingForTest);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// The owning character's own voice gates turn starts unconditionally, without attention membership.
    /// </summary>
    [Fact]
    public async Task TurnStart_OwnSpeakingVoice_GatesUnconditionally()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestFixture fixture = await CreateFixtureAsync(sceneTree, TurnGate.Completed(), TurnGate.Completed());

        try
        {
            fixture.OwnVoice.BeginSpeech();
            fixture.Mind.ObserveForTest(new TestObservation(1f, "own voice gate"));
            await TestUtils.WaitForFramesAsync(sceneTree, 10);

            Assert.Empty(fixture.Mind.Batches);

            fixture.OwnVoice.EndSpeech();
            await WaitUntilAsync(sceneTree, () => fixture.Mind.Batches.Count == 1, 120);
            Assert.Equal("own voice gate", Assert.IsType<TestObservation>(Assert.Single(fixture.Mind.Batches[0])).Value);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Retention-level attention membership gates regardless of weight, while evicted members and unattributable
    /// voices never gate.
    /// </summary>
    [Fact]
    public async Task TurnStart_UnattendedOrUnattributableVoices_NeverGate()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestFixture fixture = await CreateFixtureAsync(sceneTree, TurnGate.Completed(), TurnGate.Completed(), TurnGate.Completed());

        try
        {
            fixture.AttentionMemberVoice.BeginSpeech();
            fixture.Mind.ObserveForTest(new TestObservation(1f, "retention member"));
            await TestUtils.WaitForFramesAsync(sceneTree, 10);
            Assert.Empty(fixture.Mind.Batches);

            // A speaker below the retention threshold has no snapshot entry and therefore never gates.
            fixture.AttentionMemberVoice.EndSpeech();
            fixture.EvictedSpeakerVoice.BeginSpeech();
            await TestUtils.WaitForFramesAsync(sceneTree, 10);
            await WaitUntilAsync(sceneTree, () => fixture.Mind.Batches.Count == 1, 120);
            Assert.Equal("retention member", Assert.IsType<TestObservation>(Assert.Single(fixture.Mind.Batches[0])).Value);

            // A voice composed on no current-scene character is unattributable and never gates.
            fixture.UnattributableVoice.BeginSpeech();
            fixture.Mind.ObserveForTest(new TestObservation(1f, "unattributable speaker"));
            await WaitUntilAsync(sceneTree, () => fixture.Mind.Batches.Count == 2, 120);
            Assert.Equal("unattributable speaker", Assert.IsType<TestObservation>(Assert.Single(fixture.Mind.Batches[1])).Value);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Eligibility reached behind the gate stays pending without loss, and the minimum turn interval still applies
    /// when the gate opens before the interval elapses.
    /// </summary>
    [Fact]
    public async Task BlockedEligibility_StaysPending_AndMinimumIntervalStillAppliesAfterWake()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestFixture fixture = await CreateFixtureAsync(sceneTree, TurnGate.Completed(), TurnGate.Completed());
        fixture.Mind.MinimumTurnIntervalSeconds = 0.25f;
        fixture.Mind.ReinforceAttentionForTest(fixture.SpeakerCharacter.FullId, 0.1f);

        try
        {
            fixture.Mind.ObserveForTest(new TestObservation(1f, "first"));
            await WaitUntilAsync(sceneTree, () => fixture.Mind.Batches.Count == 1, 120);
            double firstCompletion = fixture.Mind.CompletionTimestamps[0];

            fixture.SpeakerVoice.BeginSpeech();
            fixture.Mind.ObserveForTest(new TestObservation(1f, "second"));
            await TestUtils.WaitForFramesAsync(sceneTree, 5);
            _ = Assert.Single(fixture.Mind.Batches);
            Assert.True(fixture.Mind.HasPendingForTest);

            fixture.SpeakerVoice.EndSpeech();
            await WaitUntilAsync(sceneTree, () => fixture.Mind.Batches.Count == 2, 120);

            Assert.True(
                fixture.Mind.StartTimestamps[1] - firstCompletion >= 0.20d,
                $"Blocked turn began only {fixture.Mind.StartTimestamps[1] - firstCompletion:F3}s after completion.");
            Assert.Equal("second", Assert.IsType<TestObservation>(Assert.Single(fixture.Mind.Batches[1])).Value);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// An attended speaker starting speech mid-turn interrupts through the shared machinery and the single
    /// replacement's context includes the new speech, bypassing the minimum interval exactly once.
    /// </summary>
    [Fact]
    public async Task AttendedSpeakerStart_MidTurn_InterruptsAndReplacesWithNewSpeechContext()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.Interruptible();
        var replacement = TurnGate.Completed();
        TestFixture fixture = await CreateFixtureAsync(sceneTree, firstTurn, replacement);
        fixture.Mind.MinimumTurnIntervalSeconds = 5f;
        fixture.Mind.ReinforceAttentionForTest(fixture.SpeakerCharacter.FullId, 0.1f);

        try
        {
            fixture.Mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            fixture.SpeakerVoice.BeginSpeech();
            fixture.Mind.ObserveForTest(new ObservedSpeech(fixture.SpeakerCharacter.FullId, "speaker", "hello there"));
            await firstTurn.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(replacement.Started.Task.IsCompleted);

            firstTurn.ReleaseSettlement();
            await TestUtils.WaitForFramesAsync(sceneTree, 5);
            Assert.False(
                replacement.Started.Task.IsCompleted,
                "The replacement must wait behind the speaking gate until the new speech ends.");

            fixture.SpeakerVoice.EndSpeech();
            await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, fixture.Mind.MaximumConcurrentTurns);
            Assert.Equal(2, fixture.Mind.Batches.Count);
            ObservedSpeech newSpeech = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.Batches[1]));
            Assert.Equal("hello there", newSpeech.Content);
            Assert.Contains(fixture.Mind.TimelineSnapshots[1], observation
                => observation is ObservedSpeech speech && speech.Content == "hello there");
        }
        finally
        {
            firstTurn.ReleaseSettlement();
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Simultaneous attended speech starts coalesce into one cancellation and one replacement containing all events.
    /// </summary>
    [Fact]
    public async Task MultipleSpeakerStarts_CoalesceIntoOneCancellationAndReplacement()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.Interruptible();
        var replacement = TurnGate.Completed();
        TestFixture fixture = await CreateFixtureAsync(sceneTree, firstTurn, replacement);
        fixture.Mind.ReinforceAttentionForTest(fixture.SpeakerCharacter.FullId, 0.1f);
        fixture.Mind.ReinforceAttentionForTest(fixture.SecondSpeakerCharacter.FullId, 0.1f);

        try
        {
            fixture.Mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            fixture.SpeakerVoice.BeginSpeech();
            fixture.SecondSpeakerVoice.BeginSpeech();
            fixture.Mind.ObserveForTest(new ObservedSpeech(fixture.SpeakerCharacter.FullId, "speaker", "first arrival"));
            fixture.Mind.ObserveForTest(new ObservedSpeech(fixture.SecondSpeakerCharacter.FullId, "speaker_two", "second arrival"));
            await firstTurn.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            firstTurn.ReleaseSettlement();

            Assert.False(replacement.Started.Task.IsCompleted, "Both speakers still hold the speaking gate closed.");
            fixture.SpeakerVoice.EndSpeech();
            fixture.SecondSpeakerVoice.EndSpeech();
            await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(1, firstTurn.CancellationCount);
            Assert.Equal(2, fixture.Mind.Batches.Count);
            Assert.Equal(1, fixture.Mind.MaximumConcurrentTurns);
            Assert.Equal(
                ["first arrival", "second arrival"],
                fixture.Mind.Batches[1].Cast<ObservedSpeech>().Select(speech => speech.Content));
        }
        finally
        {
            firstTurn.ReleaseSettlement();
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// The turn's own speech admission never interrupts its own turn, while own-voice activity still gates others.
    /// </summary>
    [Fact]
    public async Task OwnVoiceStart_MidTurn_NeverInterruptsItsOwnTurn()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.NaturallyCompleting();
        var nextTurn = TurnGate.Completed();
        TestFixture fixture = await CreateFixtureAsync(sceneTree, firstTurn, nextTurn);
        fixture.Mind.ReinforceAttentionForTest(fixture.SpeakerCharacter.FullId, 0.1f);

        try
        {
            fixture.Mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            fixture.OwnVoice.BeginSpeech();
            fixture.OwnVoice.EndSpeech();
            await TestUtils.WaitForFramesAsync(sceneTree, 5);

            Assert.False(firstTurn.CancellationObserved.Task.IsCompleted);

            fixture.Mind.ObserveForTest(new TestObservation(1f, "follow-up"));
            firstTurn.CompleteNaturally();
            await nextTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(firstTurn.CancellationObserved.Task.IsCompleted);
        }
        finally
        {
            firstTurn.CompleteNaturally();
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Disabling the voice-activity trigger keeps the attended speaker's speech from interrupting an active turn.
    /// </summary>
    [Fact]
    public async Task SpeakerStart_WhenInterruptionDisabled_DoesNotInterrupt()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.NaturallyCompleting();
        var nextTurn = TurnGate.Completed();
        TestFixture fixture = await CreateFixtureAsync(sceneTree, firstTurn, nextTurn);
        fixture.Mind.SpeechInterruptionEnabled = false;
        fixture.Mind.ReinforceAttentionForTest(fixture.SpeakerCharacter.FullId, 0.1f);

        try
        {
            fixture.Mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            fixture.SpeakerVoice.BeginSpeech();
            fixture.SpeakerVoice.EndSpeech();
            await TestUtils.WaitForFramesAsync(sceneTree, 5);

            Assert.False(firstTurn.CancellationObserved.Task.IsCompleted);
            fixture.Mind.ObserveForTest(new TestObservation(1f, "follow-up"));
            firstTurn.CompleteNaturally();
            await nextTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(firstTurn.CancellationObserved.Task.IsCompleted);
        }
        finally
        {
            firstTurn.CompleteNaturally();
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Interruption cuts already-audible own speech through the lip-sync stop capability, closes the speaking window
    /// without a playback-completed notification, keeps the replacement behind the gate, and leaves the voice able
    /// to speak again.
    /// </summary>
    [Fact]
    public async Task Interruption_CutsAudibleOwnSpeech_ClosesWindow_AndLeavesVoiceReusable()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.Interruptible();
        var replacement = TurnGate.Completed();
        TestFixture fixture = await CreateFixtureAsync(sceneTree, [firstTurn, replacement], includeRealVoice: true);
        SpeakingMind mind = fixture.Mind;
        mind.MinimumTurnIntervalSeconds = 5f;
        mind.ReinforceAttentionForTest(fixture.SpeakerCharacter.FullId, 0.1f);
        AIVoice ownVoice = fixture.OwnAIVoice!;
        CutObservingLipSyncPlayer lipSyncPlayer = fixture.LipSyncPlayer!;
        int endedCount = 0;
        ownVoice.SpeechEnded += _ => endedCount++;

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await WaitUntilAsync(sceneTree, () => lipSyncPlayer.IsAudioPlaying && ownVoice.IsSpeaking, 120);
            Assert.True(ownVoice.IsSpeaking);

            fixture.SpeakerVoice.BeginSpeech();
            mind.ObserveForTest(new ObservedSpeech(fixture.SpeakerCharacter.FullId, "speaker", "excuse me"));
            await firstTurn.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await WaitUntilAsync(sceneTree, () => !ownVoice.IsSpeaking, 120);
            Assert.False(lipSyncPlayer.IsAudioPlaying);
            Assert.Equal(1, endedCount);
            Assert.Equal(0, lipSyncPlayer.PlaybackCompletedCount);

            firstTurn.ReleaseSettlement();
            await TestUtils.WaitForFramesAsync(sceneTree, 5);
            Assert.False(replacement.Started.Task.IsCompleted, "The replacement must wait behind the speaking gate.");

            fixture.SpeakerVoice.EndSpeech();
            await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // The interrupted voice can speak again after the cut.
            using CancellationTokenSource replacementSpeechCancellation = new();
            ValueTask resubmission = ownVoice.SpeakCancellableAsync("speaking again", replacementSpeechCancellation.Token);
            await resubmission.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            Assert.True(ownVoice.IsSpeaking);

            lipSyncPlayer.CompletePlaybackForTesting();
            await WaitUntilAsync(sceneTree, () => !ownVoice.IsSpeaking, 120);
            Assert.True(resubmission.IsCompletedSuccessfully);
            Assert.Equal(1, lipSyncPlayer.PlaybackCompletedCount);
        }
        finally
        {
            firstTurn.ReleaseSettlement();
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// The voice-activity trigger defaults to enabled and remains configurable independently of the disabled-by-default
    /// high-importance observation trigger.
    /// </summary>
    [Fact]
    public void SpeechInterruptionSettings_DefaultEnabledIndependentlyOfHighImportanceInterruption()
    {
        SpeakingMind mind = new();

        Assert.True(mind.SpeechInterruptionEnabled);
        Assert.False(mind.HighImportanceInterruptionEnabled);
        _ = Assert.Single(
            typeof(MindBase).GetProperty(nameof(MindBase.SpeechInterruptionEnabled))!
                .GetCustomAttributes(typeof(ExportAttribute), inherit: true)
                .Cast<ExportAttribute>());

        mind.SpeechInterruptionEnabled = false;
        Assert.False(mind.SpeechInterruptionEnabled);
        Assert.False(mind.HighImportanceInterruptionEnabled);
        mind.Free();
    }

    private static Task<TestFixture> CreateFixtureAsync(
        SceneTree sceneTree,
        params TurnGate[] turns)
        => CreateFixtureAsync(sceneTree, turns, includeRealVoice: false);

    private static async Task<TestFixture> CreateFixtureAsync(
        SceneTree sceneTree,
        TurnGate[] turns,
        bool includeRealVoice)
    {
        SpeakingMind mind = new(turns)
        {
            AttentionMaximum = 1f,
            AttentionDecayPerSecond = 0f,
            AttentionRetentionThreshold = 0.05f,
            AttentionContextThreshold = 0.25f,
            HighImportanceInterruptionEnabled = false,
            ObservationImportanceThreshold = 1f,
        };
        ControllableVoice ownMockVoice = new("self");
        mind.Owner.SetVoice(ownMockVoice);

        ControllableVoice speakerVoice = new("speaker");
        ControllableVoice secondSpeakerVoice = new("speaker_two");
        ControllableVoice attentionMemberVoice = new("member");
        ControllableVoice evictedSpeakerVoice = new("evicted");
        var speakerCharacter = new VoiceCharacter("speaker", speakerVoice);
        var secondSpeakerCharacter = new VoiceCharacter("speaker_two", secondSpeakerVoice);
        var memberCharacter = new VoiceCharacter("member", attentionMemberVoice);
        var evictedCharacter = new VoiceCharacter("evicted", evictedSpeakerVoice);
        MutableSceneContext scene = new([mind.Owner, speakerCharacter, secondSpeakerCharacter, memberCharacter, evictedCharacter]);
        mind.SetSceneContextLoaderForTesting(() => scene);

        AIVoice? ownAIVoice = null;
        SpeechGeneratorStub? speechGenerator = null;
        CutObservingLipSyncPlayer? lipSyncPlayer = null;
        Node3D? voiceRoot = null;
        if (includeRealVoice)
        {
            speechGenerator = new SpeechGeneratorStub();
            lipSyncPlayer = new CutObservingLipSyncPlayer
            {
                AudioPlayer = new AudioStreamPlayer3D(),
                Skeleton = new Skeleton3D(),
            };
            ownAIVoice = new AIVoice
            {
                Id = "self",
                SpeechGenerator = speechGenerator,
                LipSyncPlayer = lipSyncPlayer,
            };
            voiceRoot = new Node3D { Name = "MindVoiceFixture" };
            voiceRoot.AddChild(lipSyncPlayer.AudioPlayer!);
            voiceRoot.AddChild(lipSyncPlayer.Skeleton!);
            voiceRoot.AddChild(speechGenerator);
            voiceRoot.AddChild(lipSyncPlayer);
            voiceRoot.AddChild(ownAIVoice);
            AddTestNode(sceneTree, voiceRoot);
            mind.Owner.SetVoice(ownAIVoice);
            mind.TurnSpeechAsync = async cancellationToken =>
            {
                ValueTask speech = ownAIVoice.SpeakCancellableAsync("hello from the interrupted turn", cancellationToken);
                await speech.AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            };
        }

        mind.ReinforceAttentionForTest(memberCharacter.FullId, 0.1f);
        mind.ReinforceAttentionForTest(evictedCharacter.FullId, 0.01f);

        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        return new TestFixture(
            mind,
            scene,
            ownMockVoice,
            speakerVoice,
            secondSpeakerVoice,
            attentionMemberVoice,
            evictedSpeakerVoice,
            speakerCharacter,
            secondSpeakerCharacter,
            new ControllableVoice("unattributable"),
            ownAIVoice,
            lipSyncPlayer,
            speechGenerator,
            voiceRoot);
    }

    private static void AddTestNode(SceneTree sceneTree, Node node)
    {
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        parent.AddChild(node);
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (predicate())
            {
                return;
            }

            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, TestFixture fixture)
    {
        if (fixture.VoiceRoot is { } voiceRoot)
        {
            voiceRoot.QueueFree();
        }

        fixture.Mind.QueueFree();
        await TestUtils.WaitForFramesAsync(sceneTree, 2);
    }

    private sealed record TestFixture(
        SpeakingMind Mind,
        MutableSceneContext Scene,
        ControllableVoice OwnVoice,
        ControllableVoice SpeakerVoice,
        ControllableVoice SecondSpeakerVoice,
        ControllableVoice AttentionMemberVoice,
        ControllableVoice EvictedSpeakerVoice,
        VoiceCharacter SpeakerCharacter,
        VoiceCharacter SecondSpeakerCharacter,
        ControllableVoice UnattributableVoice,
        AIVoice? OwnAIVoice,
        CutObservingLipSyncPlayer? LipSyncPlayer,
        SpeechGeneratorStub? SpeechGenerator,
        Node3D? VoiceRoot);

    private sealed partial class SpeakingMind(params TurnGate[] turns) : MindBase
    {
        private readonly Queue<TurnGate> _turns = new(turns);
        private int _activeTurns;

        public new VoiceCharacter Owner { get; } = new("self", new ControllableVoice("unused"));

        public Func<CancellationToken, Task>? TurnSpeechAsync
        {
            get; set;
        }

        public List<IReadOnlyList<AgentObservation>> Batches { get; } = [];

        public List<IReadOnlyList<AgentObservation>> TimelineSnapshots { get; } = [];

        public List<double> StartTimestamps { get; } = [];

        public List<double> CompletionTimestamps { get; } = [];

        public int MaximumConcurrentTurns
        {
            get; private set;
        }

        public bool HasPendingForTest => HasPendingObservations;

        public void ObserveForTest(AgentObservation observation) => _ = Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        public void ReinforceAttentionForTest(string fullId, float contribution)
            => ReinforceAttention(
                fullId,
                contribution,
                AttentionSettings.Create(maximum: 1f, decayPerSecond: 0f, retentionThreshold: 0.05f, contextThreshold: 0.25f));

        protected override ICharacter ResolveOwningCharacter() => Owner;

        protected override async Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<AgentObservation> timelineSnapshot,
            CancellationToken cancellationToken)
        {
            TurnGate turn = _turns.Count > 0 ? _turns.Dequeue() : TurnGate.Completed();
            _activeTurns++;
            MaximumConcurrentTurns = Math.Max(MaximumConcurrentTurns, _activeTurns);
            Batches.Add([.. observations]);
            TimelineSnapshots.Add([.. timelineSnapshot]);
            StartTimestamps.Add(GetTimestamp());

            try
            {
                if (TurnSpeechAsync is { } turnSpeech)
                {
                    await turnSpeech(cancellationToken);
                }

                await turn.RunAsync(cancellationToken);
            }
            finally
            {
                CompletionTimestamps.Add(GetTimestamp());
                _activeTurns--;
            }
        }

        private static double GetTimestamp() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }

    private sealed class VoiceCharacter(string id, IVoice voice) : ICharacter
    {
        private readonly List<IComponent> _components = [voice];

        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => $"{Type}:{Id}";

        public IReadOnlyList<IComponent> Components => _components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public void SetVoice(IVoice voice) => _components[0] = voice;

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }

    private sealed class ControllableVoice(string id) : IVoice
    {
        public string Id { get; set; } = id;

        public string Type => "voice";

        public bool IsSpeaking
        {
            get; private set;
        }

        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;

        public Vector3 Origin
        {
            get; set;
        }

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

        public void Speak(string speech) => throw new NotSupportedException("Test voices do not speak.");

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Test voices do not speak.");

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Test voices do not speak.");
    }

    private sealed class MutableSceneContext(List<ICharacter> characters) : ISceneContext
    {
        public IReadOnlyCollection<ICharacter> Characters => characters;

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"No test scene entry exists for '{fullId}'.");
    }

    private sealed partial class SpeechGeneratorStub : SpeechGenerator
    {
        protected override Task<byte[]> GenerateCore(string text, string? instruction = null)
            => Task.FromResult(CreateSilenceWaveBytes(seconds: 2));
    }

    private sealed partial class CutObservingLipSyncPlayer : LipSyncPlayer
    {
        public int PlaybackCompletedCount
        {
            get;
            private set;
        }

        protected override void InitialiseBackend()
        {
        }

        public override void _Ready()
        {
            base._Ready();
            PlaybackCompleted += () => PlaybackCompletedCount++;
        }

        protected override LipSyncInferenceResult RunBackendInference(
            AudioStreamWav speech,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = speech;

            // 90 frames at 30 fps outlast the two-second silent clip, so an actively playing session stays
            // observably alive until it is cut or completed explicitly.
            float[][] frames = new float[90][];
            for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                frames[frameIndex] = [frameIndex % 2 == 0 ? 0.5f : 1f];
            }

            return new LipSyncInferenceResult(frames, ["jawOpen"], 30f);
        }

        protected override void DisposeBackend()
        {
        }
    }

    private static byte[] CreateSilenceWaveBytes(double seconds)
    {
        int sampleCount = (int)(16000d * seconds);
        byte[] data = new byte[sampleCount * 2];
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + data.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed record TestObservation(float Importance, string Value) : AgentObservation
    {
        public override string TypeKey => "test.speech_turn_taking";

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed class TurnGate
    {
        private readonly TurnMode _mode;
        private readonly TaskCompletionSource _completion = CreateCompletion();
        private readonly TaskCompletionSource _settlement = CreateCompletion();

        private TurnGate(TurnMode mode)
        {
            _mode = mode;
        }

        public TaskCompletionSource Started { get; } = CreateCompletion();

        public TaskCompletionSource CancellationObserved { get; } = CreateCompletion();

        public TaskCompletionSource Settled { get; } = CreateCompletion();

        public int CancellationCount
        {
            get;
            private set;
        }

        public static TurnGate Interruptible() => new(TurnMode.Interruptible);

        public static TurnGate NaturallyCompleting() => new(TurnMode.Natural);

        public static TurnGate Completed() => new(TurnMode.Completed);

        public void ReleaseSettlement() => _settlement.TrySetResult();

        public void CompleteNaturally() => _completion.TrySetResult();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _ = Started.TrySetResult();
            try
            {
                switch (_mode)
                {
                    case TurnMode.Interruptible:
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            CancellationCount++;
                            _ = CancellationObserved.TrySetResult();
                            await _settlement.Task;
                            throw;
                        }

                        break;
                    case TurnMode.Natural:
                        using (cancellationToken.Register(() =>
                        {
                            CancellationCount++;
                            _ = CancellationObserved.TrySetResult();
                        }))
                        {
                            await _completion.Task;
                        }

                        break;
                    case TurnMode.Completed:
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown turn mode '{_mode}'.");
                }
            }
            finally
            {
                _ = Settled.TrySetResult();
            }
        }

        private static TaskCompletionSource CreateCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum TurnMode
    {
        Interruptible,
        Natural,
        Completed,
    }
}
