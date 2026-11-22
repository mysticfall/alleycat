using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Parent-symptom regression for explicit speech recipients: an in-flight NPC session cycle must be invalidated
/// only by non-self speech that actually claims the listener — declared recipients that include her, or any
/// accepted player speech — while non-self NPC speech whose declared recipients exclude her must leave the
/// in-flight cycle untouched.
/// </summary>
[Headless]
public sealed class SpeechRecipientFreshTurnIntegrationTests
{
    /// <summary>
    /// Non-self NPC speech whose declared recipients exclude the listener never invalidates her in-flight cycle:
    /// the observation commits as overheard context, the held generation keeps running, and no replacement
    /// request is issued.
    /// </summary>
    [Fact]
    public async Task InFlightCycle_SpeechAddressedToAnother_DoesNotInvalidateTheCycle()
    {
        SessionFixture fixture = await SessionFixture.CreateAsync();

        try
        {
            await fixture.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.SpeakerVoice.PublishCompletedSpeech(
                "Overheard words.",
                declaredRecipients: [fixture.Player.FullId]);

            await fixture.DrainAndSettleAsync();
            List<ObservedSpeech> committed = fixture.CommittedSpeech();

            // The listener perceived the speech — recipients never restrict hearing reach — but only as
            // overheard context, so the in-flight cycle survives untouched.
            ObservedSpeech overheard = Assert.Single(committed);
            Assert.Equal("Overheard words.", overheard.Content);
            Assert.Equal([fixture.Player.FullId], overheard.Addressees);
            Assert.Equal(
                SpeechAddresseeState.AddressedToAnother,
                overheard.GetAddresseeState(new ObservationContext(fixture.Owner)));
            _ = Assert.Single(fixture.ClientProvider.Requests);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Non-self NPC speech whose declared recipients include the listener invalidates her in-flight cycle with a
    /// replacement request, exactly like any addressed-to-me speech.
    /// </summary>
    [Fact]
    public async Task InFlightCycle_SpeechAddressedToListener_InvalidatesTheCycle()
    {
        SessionFixture fixture = await SessionFixture.CreateAsync();

        try
        {
            await fixture.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.SpeakerVoice.PublishCompletedSpeech(
                "Addressed words.",
                declaredRecipients: [fixture.Owner.FullId]);

            await fixture.WaitUntilAsync(() => fixture.ClientProvider.Requests.Count >= 2);

            // The stale generation was invalidated and replaced by exactly one replacement request.
            Assert.Equal(2, fixture.ClientProvider.Requests.Count);
            Assert.Contains(fixture.CommittedSpeech(), speech => speech.Content == "Addressed words.");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Accepted non-self player speech always invalidates the listener's in-flight cycle — the documented
    /// temporary experimental rule — even though the player declares no recipients and the observation stays
    /// undecided.
    /// </summary>
    [Fact]
    public async Task InFlightCycle_PlayerSpeech_AlwaysInvalidatesTheCycle()
    {
        SessionFixture fixture = await SessionFixture.CreateAsync();

        try
        {
            await fixture.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.PlayerVoice.PublishCompletedSpeech("Player words.", declaredRecipients: null);

            await fixture.WaitUntilAsync(() => fixture.ClientProvider.Requests.Count >= 2);

            Assert.Equal(2, fixture.ClientProvider.Requests.Count);
            // The player-authored observation carries blank recipients — undecided — yet still claimed the turn.
            ObservedSpeech playerSpeech = Assert.Single(
                fixture.CommittedSpeech(),
                speech => speech.Content == "Player words.");
            Assert.Empty(playerSpeech.Addressees);
            Assert.Equal(
                SpeechAddresseeState.Undecided,
                playerSpeech.GetAddresseeState(new ObservationContext(fixture.Owner)));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Assembles one live session fixture: an owner NPC mind with hearing and speech perception mid-cycle on a
    /// held generation, a recognised NPC speaker, and the scene player — all wired through the real
    /// hearing-perception-ingestion pipeline.
    /// </summary>
    private sealed class SessionFixture : IAsyncDisposable
    {
        private readonly SceneTree _sceneTree;
        private readonly Node _root;
        private readonly TestAgenticMind _mind;

        private SessionFixture(
            SceneTree sceneTree,
            Node root,
            TestAgenticMind mind,
            VoicedCharacter owner,
            VoicedPlayerCharacter player,
            VoicedCharacter speaker,
            PublishableVoice speakerVoice,
            PublishableVoice playerVoice,
            ScriptedSessionClientProvider clientProvider,
            TaskCompletionSource firstRequestStarted)
        {
            _sceneTree = sceneTree;
            _root = root;
            _mind = mind;
            Owner = owner;
            Player = player;
            Speaker = speaker;
            SpeakerVoice = speakerVoice;
            PlayerVoice = playerVoice;
            ClientProvider = clientProvider;
            FirstRequestStarted = firstRequestStarted;
        }

        public VoicedCharacter Owner
        {
            get;
        }

        public VoicedPlayerCharacter Player
        {
            get;
        }

        public VoicedCharacter Speaker
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

        public ScriptedSessionClientProvider ClientProvider
        {
            get;
        }

        public TaskCompletionSource FirstRequestStarted
        {
            get;
        }

        public static async Task<SessionFixture> CreateAsync()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptedSessionClientProvider clientProvider = new();
            clientProvider.EnqueueHold(firstRequestStarted);
            clientProvider.EnqueueHoldForever();

            PublishableVoice ownerVoice = new()
            {
                Id = "owner-voice"
            };
            PublishableVoice speakerVoice = new()
            {
                Id = "speaker-voice"
            };
            PublishableVoice playerVoice = new()
            {
                Id = "player-voice"
            };
            Hearing ownerHearing = new();
            VoicedCharacter owner = new("listener", ownerVoice, ownerHearing);
            VoicedPlayerCharacter player = new(playerVoice);
            VoicedCharacter speaker = new("speaker", speakerVoice);

            TestAgenticMind mind = new(owner)
            {
                SystemInstruction = new PromptStack
                {
                    Sections = [new TextPromptSection { Text = "static", Name = "Static" }],
                },
                ClientProvider = clientProvider,
            };
            mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player, speaker]));
            mind.SetVisualContextModeLoaderForTesting(() => VisualContextMode.Tools);
            mind.AddChild(new SpeechPerception());

            Node root = new();
            root.AddChild(ownerHearing);
            root.AddChild(ownerVoice);
            root.AddChild(speakerVoice);
            root.AddChild(playerVoice);
            root.AddChild(mind);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(root);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            return new SessionFixture(
                sceneTree,
                root,
                mind,
                owner,
                player,
                speaker,
                speakerVoice,
                playerVoice,
                clientProvider,
                firstRequestStarted);
        }

        public List<ObservedSpeech> CommittedSpeech()
            => [.. _mind.GetObservationTimelineSnapshot().OfType<ObservedSpeech>()];

        /// <summary>Drains queued perception work and lets a replacement request, if any, be issued.</summary>
        public async Task DrainAndSettleAsync()
        {
            await _mind.DrainPerceptionsForTestingAsync();
            await TestUtils.WaitForFramesAsync(_sceneTree, 6);
        }

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            for (int frame = 0; frame < 300; frame++)
            {
                if (predicate())
                {
                    return;
                }

                await TestUtils.WaitForNextFrameAsync(_sceneTree);
            }

            Assert.True(predicate(), "Condition was not met within the frame budget.");
        }

        public async ValueTask DisposeAsync()
        {
            (_sceneTree.CurrentScene ?? _sceneTree.Root).RemoveChild(_root);
            _mind.Free();
            ClientProvider.Free();
            Player.Free();
            await TestUtils.WaitForFramesAsync(_sceneTree, 2);
            _root.QueueFree();
            await TestUtils.WaitForFramesAsync(_sceneTree, 2);
        }
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>Scene character whose components supply its voice and, for the owner, its hearing.</summary>
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

    private sealed partial class ScriptedSessionClientProvider : ClientProvider
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new();

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public void EnqueueHold(TaskCompletionSource started)
            => _steps.Enqueue(async cancellationToken =>
            {
                _ = started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ChatResponse();
            });

        public void EnqueueHoldForever()
            => _steps.Enqueue(async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ChatResponse();
            });

        public override IChatClient CreateChatClient(string subjectFullId) => new ScriptedClient(this);

        private sealed class ScriptedClient(ScriptedSessionClientProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _ = options;
                owner.Requests.Add([.. messages]);
                cancellationToken.ThrowIfCancellationRequested();
                return owner._steps.Count == 0
                    ? throw new InvalidOperationException("The scripted session client received an unexpected request.")
                    : owner._steps.Dequeue()(cancellationToken);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
                foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
                {
                    yield return update;
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
