using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using AlleyCat.XR;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.Perception;

/// <summary>
/// Runtime contracts for observer-side speech-addressee inference through <see cref="SpeechPerception"/> with
/// staged facing geometry: single, multiple, and empty cones, unrecognised speakers, speaker self-exclusion, and
/// the head/camera orientation source order (AI-006 TR-4/11/12).
/// </summary>
[Headless]
public sealed class SpeechAddresseeInferenceIntegrationTests
{
    /// <summary>A speaker facing the observer records the observer as its addressee and applies the to-me policy.</summary>
    [Fact]
    public async Task SpeechPerception_FacingObserver_RecordsAddresseeAndToMePolicy()
    {
        await using FacingFixture fixture = new();
        fixture.Speaker.Position = Vector3.Zero;
        fixture.Observer.Position = new Vector3(0f, 0f, -2f);
        fixture.Bystander.Position = new Vector3(0f, 0f, 2f);
        await fixture.PerceiveAsync();

        ObservedSpeech speech = fixture.SingleObservedSpeech();
        Assert.Equal("char:speaker", speech.ActorId);
        Assert.Equal(["char:observer"], speech.Addressees);
        Assert.Equal(SpeechAddresseeState.AddressedToMe, speech.GetAddresseeState(fixture.ObserverContext));
        Assert.Equal(2f, speech.CalculateImportance(fixture.ObserverContext));
        Assert.True(speech.RequiresFreshTurn(fixture.ObserverContext));
        AttentionEffect effect = Assert.Single(speech.GetAttentionEffects(fixture.ObserverContext));
        Assert.Equal("char:speaker", effect.SubjectFullId);
        Assert.Equal(0.75f, effect.Contribution);
        Assert.Equal("Heard `char:speaker` say to me: hello there", speech.Render(fixture.Observer));
    }

    /// <summary>A speaker facing a different character leaves the observer an over-hearing bystander.</summary>
    [Fact]
    public async Task SpeechPerception_FacingAnotherCharacter_RecordsOverheardPolicy()
    {
        await using FacingFixture fixture = new();
        fixture.Speaker.Position = Vector3.Zero;
        fixture.Bystander.Position = new Vector3(0f, 0f, -2f);
        fixture.Observer.Position = new Vector3(0f, 0f, 2f);
        await fixture.PerceiveAsync();

        ObservedSpeech speech = fixture.SingleObservedSpeech();
        Assert.Equal(["char:bystander"], speech.Addressees);
        Assert.Equal(SpeechAddresseeState.AddressedToAnother, speech.GetAddresseeState(fixture.ObserverContext));
        Assert.Equal(0.25f, speech.CalculateImportance(fixture.ObserverContext));
        Assert.False(speech.RequiresFreshTurn(fixture.ObserverContext));
        AttentionEffect[] effects = [.. speech.GetAttentionEffects(fixture.ObserverContext)];
        Assert.Equal(2, effects.Length);
        Assert.Equal("char:speaker", effects[0].SubjectFullId);
        Assert.Equal(0.5f, effects[0].Contribution);
        Assert.Equal("char:bystander", effects[1].SubjectFullId);
        Assert.Equal(0.25f, effects[1].Contribution);
        Assert.Equal("Heard `char:speaker` say to `char:bystander`: hello there", speech.Render(fixture.Observer));
    }

    /// <summary>A speaker facing away from every character leaves the addressee undecided.</summary>
    [Fact]
    public async Task SpeechPerception_FacingAwayFromEveryone_LeavesAddresseeUndecided()
    {
        await using FacingFixture fixture = new();
        fixture.Speaker.Position = Vector3.Zero;
        fixture.Observer.Position = new Vector3(0f, 0f, 3f);
        fixture.Bystander.Position = new Vector3(2f, 0f, 3f);
        await fixture.PerceiveAsync();

        ObservedSpeech speech = fixture.SingleObservedSpeech();
        Assert.Empty(speech.Addressees);
        Assert.Equal(SpeechAddresseeState.Undecided, speech.GetAddresseeState(fixture.ObserverContext));
        Assert.Equal(1f, speech.CalculateImportance(fixture.ObserverContext));
        Assert.False(speech.RequiresFreshTurn(fixture.ObserverContext));
        AttentionEffect effect = Assert.Single(speech.GetAttentionEffects(fixture.ObserverContext));
        Assert.Equal("char:speaker", effect.SubjectFullId);
        Assert.Equal(0.5f, effect.Contribution);
        // Undecided wording makes no addressee claim.
        Assert.Equal("Heard `char:speaker` say: hello there", speech.Render(fixture.Observer));
    }

    /// <summary>A cone containing several characters yields a multi-addressee set in scene order.</summary>
    [Fact]
    public async Task SpeechPerception_ConeContainsMultipleCharacters_RecordsMultiAddresseeSet()
    {
        await using FacingFixture fixture = new();
        fixture.Speaker.Position = Vector3.Zero;
        fixture.Observer.Position = new Vector3(-1f, 0f, -2f);
        fixture.Bystander.Position = new Vector3(1f, 0f, -2f);
        await fixture.PerceiveAsync();

        ObservedSpeech speech = fixture.SingleObservedSpeech();
        Assert.Equal(["char:observer", "char:bystander"], speech.Addressees);
        Assert.True(speech.RequiresFreshTurn(fixture.ObserverContext));
        Assert.Equal(
            "Heard `char:speaker` say to `char:observer` and `char:bystander`: hello there",
            speech.Render(fixture.Rear));
    }

    /// <summary>
    /// The speaker's gaze look target — the AI-007 machinery's published output — supplies the facing before body
    /// forward, and inference reads it without assigning or clearing it (AI-006 TR-10/12).
    /// </summary>
    [Fact]
    public async Task SpeechPerception_GazeLookTargetBeatsBodyForward()
    {
        await using FacingFixture fixture = new(withSpeakerVision: true);
        fixture.Speaker.Position = Vector3.Zero;
        // The body faces +Z (away from both characters) while the gaze look target sits ahead at -Z.
        fixture.Speaker.GlobalTransform = new Transform3D(
            Basis.FromEuler(new Vector3(0f, Mathf.Pi, 0f)),
            Vector3.Zero);
        fixture.Observer.Position = new Vector3(0f, 0f, -2f);
        fixture.Bystander.Position = new Vector3(0f, 0f, 2f);
        StaticVisualCue gazeTarget = new()
        {
            Position = new Vector3(0f, 0f, -2.5f),
        };
        fixture.Root.AddChild(gazeTarget);
        fixture.SpeakerVision!.LookTarget = gazeTarget;
        await fixture.PerceiveAsync();

        ObservedSpeech speech = fixture.SingleObservedSpeech();
        Assert.Equal(["char:observer"], speech.Addressees);
        // The inference read orientation only: the assigned look target is untouched.
        Assert.Same(gazeTarget, fixture.SpeakerVision.LookTarget);
    }

    /// <summary>
    /// A player speaker's XR camera supplies the facing before the gaze and body-forward fallbacks (AI-006 TR-12).
    /// </summary>
    [Fact]
    public async Task SpeechPerception_PlayerCameraBeatsBodyForward()
    {
        await using FacingFixture fixture = new(playerSpeaker: true);
        fixture.Speaker.Position = Vector3.Zero;
        // The player's body faces +Z while the staged head/camera looks ahead at -Z.
        fixture.Speaker.GlobalTransform = new Transform3D(
            Basis.FromEuler(new Vector3(0f, Mathf.Pi, 0f)),
            Vector3.Zero);
        fixture.Observer.Position = new Vector3(0f, 0f, -2f);
        fixture.Bystander.Position = new Vector3(0f, 0f, 2f);
        Camera3D cameraNode = new();
        fixture.Root.AddChild(cameraNode);
        fixture.Perception.PlayerCameraResolverForTest = () => new StubXRCamera(cameraNode);
        await fixture.PerceiveAsync();

        ObservedSpeech speech = fixture.SingleObservedSpeech();
        Assert.Equal(["char:observer"], speech.Addressees);
    }

    /// <summary>A speaker without gaze or camera sources falls back to body forward.</summary>
    [Fact]
    public async Task SpeechPerception_NoGazeOrCamera_FallsBackFromBodyForward()
    {
        await using FacingFixture fixture = new();
        fixture.Speaker.Position = Vector3.Zero;
        fixture.Speaker.GlobalTransform = new Transform3D(
            Basis.FromEuler(new Vector3(0f, Mathf.Pi, 0f)),
            Vector3.Zero);
        fixture.Observer.Position = new Vector3(0f, 0f, 2f);
        fixture.Bystander.Position = new Vector3(0f, 0f, -2f);
        fixture.Rear.Position = new Vector3(0f, 0f, -2f);
        await fixture.PerceiveAsync();

        ObservedSpeech speech = fixture.SingleObservedSpeech();
        Assert.Equal(["char:observer"], speech.Addressees);
    }

    /// <summary>Unrecognised speech is always undecided because facing cannot be resolved without a character node.</summary>
    [Fact]
    public async Task SpeechPerception_UnknownSpeaker_IsAlwaysUndecided()
    {
        await using FacingFixture fixture = new();
        fixture.Speaker.Position = Vector3.Zero;
        fixture.Observer.Position = new Vector3(0f, 0f, -2f);
        await fixture.PerceiveAsync(voiceId: "unknown-voice");

        ObservedSpeech speech = fixture.SingleObservedSpeech();
        Assert.Null(speech.ActorId);
        Assert.Empty(speech.Addressees);
        Assert.Equal(SpeechAddresseeState.Undecided, speech.GetAddresseeState(fixture.ObserverContext));
        Assert.False(speech.RequiresFreshTurn(fixture.ObserverContext));
        Assert.Empty(speech.GetAttentionEffects(fixture.ObserverContext));
        Assert.Equal("Heard an unknown speaker say: hello there", speech.Render(fixture.Observer));
    }

    /// <summary>The speaker itself is never an addressee, while a co-located character stays inside the cone.</summary>
    [Fact]
    public async Task SpeechPerception_ExcludesSpeakerButKeepsCoLocatedCandidates()
    {
        await using FacingFixture fixture = new();
        fixture.Speaker.Position = Vector3.Zero;
        fixture.Rear.Position = Vector3.Zero;
        fixture.Observer.Position = new Vector3(0f, 0f, -2f);
        await fixture.PerceiveAsync();

        ObservedSpeech speech = fixture.SingleObservedSpeech();
        Assert.Equal(["char:observer", "char:rear"], speech.Addressees);
        Assert.DoesNotContain("char:speaker", speech.Addressees);
    }

    /// <summary>The exported cone-angle tunable is validated before inference runs.</summary>
    [Fact]
    public async Task SpeechPerception_InvalidConeAngle_FailsClearly()
    {
        await using FacingFixture fixture = new();
        fixture.Perception.FacingConeAngleDegrees = 0f;

        _ = await Assert.ThrowsAsync<ArgumentException>(() => fixture.PerceiveAsync().AsTask());
    }

    /// <summary>
    /// Staged facing geometry for one observer-owned <see cref="SpeechPerception"/>: an in-tree speaker, observer,
    /// bystander, and rear character with switchable speaker vision and player-speaker wiring. The bystander and
    /// rear characters default behind a forward-facing speaker at the origin.
    /// </summary>
    private sealed class FacingFixture : IAsyncDisposable
    {
        private readonly SceneTree _tree;

        private readonly List<ObservedSpeech> _emissions = [];

        private readonly TestSceneContext _scene;

        public FacingFixture(bool withSpeakerVision = false, bool playerSpeaker = false)
        {
            _tree = TestUtils.GetSceneTree();
            Root = new Node3D();
            (_tree.CurrentScene ?? _tree.Root).AddChild(Root);

            Speaker = CreateCharacter("speaker", "speaker-voice", playerSpeaker, withSpeakerVision);
            Observer = CreateCharacter("observer", "observer-voice", playerSpeaker: false, withVision: false);
            Bystander = CreateCharacter("bystander", "bystander-voice", playerSpeaker: false, withVision: false);
            Rear = CreateCharacter("rear", "rear-voice", playerSpeaker: false, withVision: false);
            Bystander.Position = new Vector3(0f, 0f, 2f);
            Rear.Position = new Vector3(0f, 0f, 2f);

            _scene = new TestSceneContext([Speaker, Observer, Bystander, Rear]);
            ObserverContext = new ObservationContext(Observer);

            Perception = new SpeechPerception();
            Perception.Observed += observation => _emissions.Add(Assert.IsType<ObservedSpeech>(observation));
        }

        public Node3D Root
        {
            get;
        }

        public SpeechPerception Perception
        {
            get;
        }

        public FakeVision? SpeakerVision
        {
            get; private set;
        }

        public FacingCharacter Speaker
        {
            get;
        }

        public FacingCharacter Observer
        {
            get;
        }

        public FacingCharacter Bystander
        {
            get;
        }

        public FacingCharacter Rear
        {
            get;
        }

        public ObservationContext ObserverContext
        {
            get;
        }

        public ValueTask PerceiveAsync(string voiceId = "speaker-voice")
        {
            _emissions.Clear();
            return Perception.PerceiveAsync(
                new SpeechPercept("hello there", voiceId),
                new PerceptionContext(Observer, _scene),
                CancellationToken.None);
        }

        public ObservedSpeech SingleObservedSpeech() => Assert.Single(_emissions);

        private FacingCharacter CreateCharacter(string id, string voiceId, bool playerSpeaker, bool withVision)
        {
            TestVoice voice = new()
            {
                Id = voiceId
            };
            FakeVision? vision = withVision ? new FakeVision() : null;
            FacingCharacter character = new(id, vision is null ? [voice] : [voice, vision]);
            if (withVision)
            {
                SpeakerVision = vision;
            }

            if (playerSpeaker)
            {
                character.AddToGroup(ISceneContext.PlayerGroupName);
            }

            Root.AddChild(character);
            return character;
        }

        public async ValueTask DisposeAsync()
        {
            Root.QueueFree();
            await TestUtils.WaitForFramesAsync(_tree, 2);
        }
    }

    /// <summary>Minimal in-tree character whose world transform stages the facing geometry.</summary>
    private sealed partial class FacingCharacter(string id, IReadOnlyList<IComponent> components) : Node3D, ICharacter
    {
        public string Id { get; set; } = id;

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }

    /// <summary>Minimal vision component exposing an assignable look target without any presentation behaviour.</summary>
    private sealed class FakeVision : IVision
    {
        public VisualCue? LookTarget
        {
            get;
            set;
        }

        public IReadOnlyList<Type> PerceptTypes { get; } = [];

#pragma warning disable CS0067
        public event Action<IPercept>? Perceived;
#pragma warning restore CS0067

        public void SetLookTarget(VisualCue? target) => LookTarget = target;

        public void ClearLookTarget() => LookTarget = null;
    }

    /// <summary>XR camera seam wrapping one staged camera node.</summary>
    private sealed class StubXRCamera(Camera3D cameraNode) : IXRCamera
    {
        public Camera3D CameraNode => cameraNode;
    }

    private sealed class TestVoice : IVoice
    {
        public string Id { get; set; } = string.Empty;

        public Vector3 Origin => Vector3.Zero;

        public bool IsSpeaking => false;

#pragma warning disable CS0067
        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;
#pragma warning restore CS0067

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
            => SpeakAsync(speech, cancellationToken);
    }

    private sealed class TestSceneContext(IReadOnlyCollection<ICharacter> characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public IReadOnlyCollection<ICharacter> Characters => characters;

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId) => null;

        public IIdentifiable Resolve(string fullId) => throw new InvalidOperationException();
    }
}
