using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Attention;
using AlleyCat.Scene;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for Mind's attended-speaker-finished cue: attention membership, voice-to-owner
/// attribution, and the wait/speak wake rules of AI-001 TR-34 and AI-002 TR-25/33.
/// </summary>
[Headless]
public sealed partial class MindAttendedSpeakerIntegrationTests
{
    /// <summary>
    /// Attended-speaker membership follows attention-snapshot retention: only a currently attended other
    /// character's speaking window blocks; the owning character's own voice, unattended speakers, ambiguous
    /// composition, and blank voice identities never do.
    /// </summary>
    [Fact]
    public async Task IsAttendedSpeakerSpeaking_FollowsAttentionSnapshotMembership()
    {
        SpeechScene scene = new();
        TestMind mind = scene.CreateMind();
        mind.AttendForTest(scene.Speaker.FullId);
        await mind.WaitForReadyAsync();
        try
        {
            Assert.False(mind.IsAttendedSpeakerSpeakingForTest());

            scene.SpeakerVoice.BeginSpeech();
            Assert.True(mind.IsAttendedSpeakerSpeakingForTest());

            scene.OwnVoice.BeginSpeech();
            scene.BystanderVoice.BeginSpeech();
            scene.TwinVoice.BeginSpeech();
            scene.BlankVoice.BeginSpeech();
            Assert.True(mind.IsAttendedSpeakerSpeakingForTest());

            scene.SpeakerVoice.EndSpeech();
            Assert.False(mind.IsAttendedSpeakerSpeakingForTest());
        }
        finally
        {
            await mind.FreeAsync();
        }
    }

    /// <summary>
    /// The idle wait blocks while an attended speaker's window is open and unblocks on the
    /// attended-speaker-finished cue (AI-002 TR-25).
    /// </summary>
    [Fact]
    public async Task WaitUntilAttendedSpeakerIdle_BlocksUntilAttendedSpeakerFinishes()
    {
        SpeechScene scene = new();
        TestMind mind = scene.CreateMind();
        mind.AttendForTest(scene.Speaker.FullId);
        await mind.WaitForReadyAsync();
        try
        {
            scene.SpeakerVoice.BeginSpeech();
            Task idleTask = mind.WaitUntilAttendedSpeakerIdleForTest(CancellationToken.None);
            await Task.Delay(100);
            Assert.False(idleTask.IsCompleted, "The idle wait must block while the attended speaker speaks.");

            scene.SpeakerVoice.EndSpeech();
            await idleTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await mind.FreeAsync();
        }
    }

    /// <summary>
    /// The idle wait never blocks on the owning character's own voice, an unattended speaker, or an
    /// unattributable voice.
    /// </summary>
    [Fact]
    public async Task WaitUntilAttendedSpeakerIdle_WithOwnUnattendedOrUnattributableVoices_ReturnsImmediately()
    {
        SpeechScene scene = new();
        TestMind mind = scene.CreateMind();
        mind.AttendForTest(scene.Speaker.FullId);
        await mind.WaitForReadyAsync();
        try
        {
            scene.OwnVoice.BeginSpeech();
            scene.BystanderVoice.BeginSpeech();
            scene.TwinVoice.BeginSpeech();
            scene.BlankVoice.BeginSpeech();

            await mind.WaitUntilAttendedSpeakerIdleForTest(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await mind.FreeAsync();
        }
    }

    /// <summary>
    /// An attended speaker finishing wakes an active wait early with the cue flag and no promoted sub-threshold
    /// observations (AI-001 TR-34, AI-002 TR-33).
    /// </summary>
    [Fact]
    public async Task Wait_WhenAttendedSpeakerFinishes_WakesEarlyWithCueFlagAndNothingNotable()
    {
        SpeechScene scene = new();
        TestMind mind = scene.CreateMind();
        mind.AttendForTest(scene.Speaker.FullId);
        await mind.WaitForReadyAsync();
        try
        {
            scene.SpeakerVoice.BeginSpeech();
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            scene.SpeakerVoice.EndSpeech();

            MindBase.WaitOutcome outcome = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(outcome.AttendedSpeakerFinished);
            Assert.Empty(outcome.Notable);
        }
        finally
        {
            await mind.FreeAsync();
        }
    }

    /// <summary>
    /// Speech-end of an unattended speaker, an unattributable voice, or the owning character's own voice never
    /// wakes the wait.
    /// </summary>
    [Fact]
    public async Task Wait_WhenUnattendedUnattributableOrOwnVoiceFinishes_NeverWakes()
    {
        SpeechScene scene = new();
        TestMind mind = scene.CreateMind();
        mind.AttendForTest(scene.Speaker.FullId);
        await mind.WaitForReadyAsync();
        try
        {
            FakeVoice[] voices = [scene.OwnVoice, scene.BystanderVoice, scene.TwinVoice, scene.BlankVoice];
            foreach (FakeVoice voice in voices)
            {
                Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.3), CancellationToken.None);
                await Task.Delay(50);
                voice.BeginSpeech();
                voice.EndSpeech();
                MindBase.WaitOutcome outcome = await waitTask;
                Assert.False(outcome.AttendedSpeakerFinished);
                Assert.Empty(outcome.Notable);
            }
        }
        finally
        {
            await mind.FreeAsync();
        }
    }

    /// <summary>
    /// Builds the voiced scene fixture shared by the attended-speaker tests.
    /// </summary>
    private sealed class SpeechScene
    {
        private readonly TestMind _mind;

        public SpeechScene()
        {
            OwnVoice = new FakeVoice("own-voice");
            SpeakerVoice = new FakeVoice("speaker-voice");
            BystanderVoice = new FakeVoice("bystander-voice");
            TwinVoice = new FakeVoice("twin-voice");
            BlankVoice = new FakeVoice("");
            Owner = new VoiceCharacter("owner", OwnVoice);
            Speaker = new VoiceCharacter("speaker", SpeakerVoice);
            Bystander = new VoiceCharacter("bystander", BystanderVoice);
            TwinA = new VoiceCharacter("twin-a", TwinVoice);
            TwinB = new VoiceCharacter("twin-b", TwinVoice);
            BlankHolder = new VoiceCharacter("blank-holder", BlankVoice);
            _mind = new TestMind(Owner)
            {
                AttentionDecayPerSecond = 0f,
            };
            _mind.SetSceneContextLoaderForTesting(() => new TestSceneContext(
                [Owner, Speaker, Bystander, TwinA, TwinB, BlankHolder]));
        }

        public FakeVoice OwnVoice
        {
            get;
        }

        public FakeVoice SpeakerVoice
        {
            get;
        }

        public FakeVoice BystanderVoice
        {
            get;
        }

        public FakeVoice TwinVoice
        {
            get;
        }

        public FakeVoice BlankVoice
        {
            get;
        }

        public VoiceCharacter Owner
        {
            get;
        }

        public VoiceCharacter Speaker
        {
            get;
        }

        private VoiceCharacter Bystander
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

        private VoiceCharacter BlankHolder
        {
            get;
        }

        public TestMind CreateMind() => _mind;
    }

    private sealed partial class TestMind(VoiceCharacter owner) : MindBase
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public void AttendForTest(string fullId)
            => ReinforceAttention(fullId, 1f, AttentionSettings.Create(1f, 0f, 0.05f, 0.25f));

        public bool IsAttendedSpeakerSpeakingForTest() => IsAttendedSpeakerSpeaking();

        public Task WaitUntilAttendedSpeakerIdleForTest(CancellationToken cancellationToken)
            => WaitUntilAttendedSpeakerIdleAsync(cancellationToken);

        public Task<WaitOutcome> WaitForNotableForTestAsync(TimeSpan maxWait, CancellationToken cancellationToken)
            => WaitForNotableObservationsAsync(maxWait, cancellationToken);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        public async Task WaitForReadyAsync()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(this);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }

        public async Task FreeAsync()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>
    /// Plain controllable voice: speaking-window state and events are driven explicitly by the test.
    /// </summary>
    private sealed class FakeVoice(string id) : IVoice
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

    private sealed class VoiceCharacter(string id, IVoice voice) : ICharacter
    {
        public string Id { get; set; } = id;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [voice];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
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
