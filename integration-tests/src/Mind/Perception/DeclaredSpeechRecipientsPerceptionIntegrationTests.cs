using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.Vision;
using Godot;
using Xunit;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Perception;

/// <summary>
/// Perception coverage for explicitly declared speech recipients: declared recipients reach every observer
/// unfiltered with observer-relative addressee states, omitted recipients stay undecided, and player speech
/// keeps blank recipients while its trusted provenance claims a fresh turn.
/// </summary>
public sealed class DeclaredSpeechRecipientsPerceptionIntegrationTests
{
    /// <summary>
    /// One speech publication with declared recipients reaches every hearing mind unfiltered: the named addressee
    /// derives addressed-to-me with fresh-turn urgency, while a bystander derives addressed-to-another with no
    /// delivery signal at all.
    /// </summary>
    [Fact]
    public async Task DeclaredRecipients_ReachEveryObserverWithObserverRelativeStates()
    {
        await using PerceptionFixture fixture = await PerceptionFixture.CreateAsync();

        List<MindBase.ObservationDeliverySignal> listenerSignals = [];
        fixture.ListenerMind.ObservationDeliverySignalled += listenerSignals.Add;
        List<MindBase.ObservationDeliverySignal> bystanderSignals = [];
        fixture.BystanderMind.ObservationDeliverySignalled += bystanderSignals.Add;

        fixture.SpeakerVoice.PublishCompletedSpeech("Addressed words.", [fixture.Listener.FullId]);
        await fixture.DrainAsync();

        ObservedSpeech listenerSpeech = PerceptionFixture.SingleSpeech(fixture.ListenerMind);
        Assert.Equal("char:speaker", listenerSpeech.ActorId);
        Assert.Equal([fixture.Listener.FullId], listenerSpeech.Addressees);
        Assert.Equal(SpeechAddresseeState.AddressedToMe, listenerSpeech.GetAddresseeState(fixture.ListenerContext));
        Assert.Equal(2f, listenerSpeech.CalculateImportance(fixture.ListenerContext));
        Assert.True(listenerSpeech.RequiresFreshTurn(fixture.ListenerContext));
        Assert.StartsWith(
            "Heard `char:speaker` say to me: Addressed words.",
            listenerSpeech.Render(fixture.Listener),
            StringComparison.Ordinal);

        // The bystander still heard the same publication — recipients never restrict hearing reach — but only as
        // overheard context that never crosses the delivery threshold.
        ObservedSpeech bystanderSpeech = PerceptionFixture.SingleSpeech(fixture.BystanderMind);
        Assert.Equal("Addressed words.", bystanderSpeech.Content);
        Assert.Equal([fixture.Listener.FullId], bystanderSpeech.Addressees);
        Assert.Equal(
            SpeechAddresseeState.AddressedToAnother,
            bystanderSpeech.GetAddresseeState(fixture.BystanderContext));
        Assert.False(bystanderSpeech.RequiresFreshTurn(fixture.BystanderContext));
        Assert.StartsWith(
            $"Heard `char:speaker` say to `{fixture.Listener.FullId}`: Addressed words.",
            bystanderSpeech.Render(fixture.Bystander),
            StringComparison.Ordinal);

        Assert.Contains(listenerSignals, signal => signal.Urgency == MindBase.ObservationDeliveryUrgency.Fresh);
        Assert.DoesNotContain(bystanderSignals, signal => signal.Urgency == MindBase.ObservationDeliveryUrgency.Fresh);
        Assert.Empty(bystanderSignals);
    }

    /// <summary>
    /// Speech published without declared recipients stays undecided for every observer: no fresh-turn urgency is
    /// claimed, so no delivery signal fires (fresh-only signalling).
    /// </summary>
    [Fact]
    public async Task OmittedRecipients_StayUndecidedForEveryObserver()
    {
        await using PerceptionFixture fixture = await PerceptionFixture.CreateAsync();

        List<MindBase.ObservationDeliverySignal> listenerSignals = [];
        fixture.ListenerMind.ObservationDeliverySignalled += listenerSignals.Add;

        fixture.SpeakerVoice.PublishCompletedSpeech("Undecided words.");
        await fixture.DrainAsync();

        ObservedSpeech listenerSpeech = PerceptionFixture.SingleSpeech(fixture.ListenerMind);
        Assert.Empty(listenerSpeech.Addressees);
        Assert.Equal(SpeechAddresseeState.Undecided, listenerSpeech.GetAddresseeState(fixture.ListenerContext));
        Assert.False(listenerSpeech.RequiresFreshTurn(fixture.ListenerContext));
        Assert.StartsWith(
            "Heard `char:speaker` say: Undecided words.",
            listenerSpeech.Render(fixture.Listener),
            StringComparison.Ordinal);

        Assert.Empty(listenerSignals);
    }

    /// <summary>
    /// Player speech carries blank recipients — undecided — yet its trusted ingestion-side provenance claims a
    /// fresh turn for every hearing NPC (the documented temporary experimental rule).
    /// </summary>
    [Fact]
    public async Task PlayerSpeech_KeepsBlankRecipientsAndClaimsAFreshTurn()
    {
        await using PerceptionFixture fixture = await PerceptionFixture.CreateAsync();

        List<MindBase.ObservationDeliverySignal> listenerSignals = [];
        fixture.ListenerMind.ObservationDeliverySignalled += listenerSignals.Add;

        fixture.PlayerVoice.PublishCompletedSpeech("Player words.");
        await fixture.DrainAsync();

        ObservedSpeech playerSpeech = PerceptionFixture.SingleSpeech(fixture.ListenerMind);
        Assert.Equal(fixture.Player.FullId, playerSpeech.ActorId);
        Assert.Empty(playerSpeech.Addressees);
        Assert.Equal(SpeechAddresseeState.Undecided, playerSpeech.GetAddresseeState(fixture.ListenerContext));
        Assert.StartsWith(
            $"Heard `{fixture.Player.FullId}` say: Player words.",
            playerSpeech.Render(fixture.Listener),
            StringComparison.Ordinal);

        // Freshness rides the private transport provenance, not the undecided addressee state.
        Assert.Contains(listenerSignals, signal => signal.Urgency == MindBase.ObservationDeliveryUrgency.Fresh);
        Assert.Contains(
            fixture.ListenerMind.GetAcceptedObservationLogSnapshot(),
            entry => entry.Payload is ObservedSpeech speech
                && speech.Content == "Player words."
                && entry.Scheduling.RequiresFreshTurn);
    }

    /// <summary>
    /// Declared recipients from an unrecognised speaker are ignored: without a recognised speaker identity the
    /// observation stays unknown and undecided.
    /// </summary>
    [Fact]
    public async Task UnknownSpeaker_DeclaredRecipientsAreIgnored()
    {
        await using PerceptionFixture fixture = await PerceptionFixture.CreateAsync();

        fixture.StrangerVoice.PublishCompletedSpeech("Unknown words.", [fixture.Listener.FullId]);
        await fixture.DrainAsync();

        ObservedSpeech unknownSpeech = PerceptionFixture.SingleSpeech(fixture.ListenerMind);
        Assert.Null(unknownSpeech.ActorId);
        Assert.Empty(unknownSpeech.Addressees);
        Assert.Equal(SpeechAddresseeState.Undecided, unknownSpeech.GetAddresseeState(fixture.ListenerContext));
        Assert.StartsWith(
            "Heard an unknown speaker say: Unknown words.",
            unknownSpeech.Render(fixture.Listener),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Two hearing minds — a listener and a bystander — plus a recognised NPC speaker, the scene player, and an
    /// unrecognised stranger voice, all wired through real hearing, speech perception, and Mind ingestion.
    /// </summary>
    private sealed class PerceptionFixture : IAsyncDisposable
    {
        private PerceptionFixture(
            SceneTree sceneTree,
            Node root,
            TestMind listenerMind,
            TestMind bystanderMind,
            VoicedCharacter listener,
            VoicedCharacter bystander,
            VoicedPlayerCharacter player,
            PublishableVoice speakerVoice,
            PublishableVoice playerVoice,
            PublishableVoice strangerVoice)
        {
            _sceneTree = sceneTree;
            _root = root;
            ListenerMind = listenerMind;
            BystanderMind = bystanderMind;
            Listener = listener;
            Bystander = bystander;
            Player = player;
            SpeakerVoice = speakerVoice;
            PlayerVoice = playerVoice;
            StrangerVoice = strangerVoice;
        }

        private readonly SceneTree _sceneTree;
        private readonly Node _root;

        public TestMind ListenerMind
        {
            get;
        }

        public TestMind BystanderMind
        {
            get;
        }

        public VoicedCharacter Listener
        {
            get;
        }

        public VoicedCharacter Bystander
        {
            get;
        }

        public VoicedPlayerCharacter Player
        {
            get;
        }

        public PublishableVoice SpeakerVoice
        {
            get;
        }

        public PublishableVoice PlayerVoice
        {
            get;
        }

        public PublishableVoice StrangerVoice
        {
            get;
        }

        public ObservationContext ListenerContext => new(Listener);

        public ObservationContext BystanderContext => new(Bystander);

        public static async Task<PerceptionFixture> CreateAsync()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            PublishableVoice listenerVoice = new()
            {
                Id = "listener-voice"
            };
            PublishableVoice bystanderVoice = new()
            {
                Id = "bystander-voice"
            };
            PublishableVoice speakerVoice = new()
            {
                Id = "speaker-voice"
            };
            PublishableVoice playerVoice = new()
            {
                Id = "player-voice"
            };
            PublishableVoice strangerVoice = new()
            {
                Id = "stranger-voice"
            };
            Hearing listenerHearing = new();
            Hearing bystanderHearing = new();
            VoicedCharacter listener = new("listener", listenerVoice, listenerHearing);
            VoicedCharacter bystander = new("bystander", bystanderVoice, bystanderHearing);
            VoicedCharacter speaker = new("speaker", speakerVoice);
            VoicedPlayerCharacter player = new(playerVoice);

            TestMind listenerMind = CreateMind(listener, [listener, bystander, speaker, player]);
            TestMind bystanderMind = CreateMind(bystander, [listener, bystander, speaker, player]);

            Node root = new();
            root.AddChild(listenerHearing);
            root.AddChild(bystanderHearing);
            root.AddChild(speakerVoice);
            root.AddChild(playerVoice);
            root.AddChild(strangerVoice);
            root.AddChild(listenerMind);
            root.AddChild(bystanderMind);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(root);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            return new PerceptionFixture(
                sceneTree,
                root,
                listenerMind,
                bystanderMind,
                listener,
                bystander,
                player,
                speakerVoice,
                playerVoice,
                strangerVoice);
        }

        public static ObservedSpeech SingleSpeech(TestMind mind)
            => Assert.Single(mind.GetObservationTimelineSnapshot().OfType<ObservedSpeech>());

        public async Task DrainAsync()
        {
            await ListenerMind.DrainPerceptionsForTestingAsync();
            await BystanderMind.DrainPerceptionsForTestingAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _root.QueueFree();
            await TestUtils.WaitForFramesAsync(_sceneTree, 2);
        }

        private static TestMind CreateMind(VoicedCharacter owner, IReadOnlyList<ICharacter> roster)
        {
            TestMind mind = new(owner)
            {
                AttentionDecayPerSecond = 0f,
            };
            mind.SetSceneContextLoaderForTesting(() => new SceneContext(roster));
            mind.AddChild(new SpeechPerception());
            return mind;
        }
    }

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>Scene character whose components supply its voice and hearing.</summary>
    private sealed class VoicedCharacter(string id, params IComponent[] components) : ICharacter
    {
        public string Id { get; set; } = id;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    /// <summary>Node-based scene player carrying a voice so perception resolves its trusted provenance.</summary>
    private sealed partial class VoicedPlayerCharacter : Node, ICharacter
    {
        public VoicedPlayerCharacter(PublishableVoice voice)
        {
            // Group membership joins at construction — matching the shared player fixture — so scene snapshots
            // resolve the player even while the node is outside the tree.
            AddToGroup(ISceneContext.PlayerGroupName);
            Components = [voice];
        }

        public VoicedPlayerCharacter() : this(new PublishableVoice())
        {
        }

        public string Id { get; set; } = "fixture_player";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components
        {
            get;
        }

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    /// <summary>Voice double exposing the recipient-carrying speech publication for deterministic drives.</summary>
    private sealed partial class PublishableVoice : Voice
    {
        public void PublishCompletedSpeech(string speech, IReadOnlyList<string>? declaredRecipients = null)
            => PublishSpeech(speech, null, declaredRecipients);
    }
}
