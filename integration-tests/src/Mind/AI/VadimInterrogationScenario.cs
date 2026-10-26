using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Mind.AI.Watch;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Speech.Voice;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Fixed facts of the Vadim interrogation baseline scenario (plan step 3): the representative reproduction of the
/// reported no-reply symptom, scripted for deterministic timing while mirroring the baseline scene.
/// </summary>
/// <remarks>
/// <para>
/// Baseline (old pipeline, <c>qwen3.8-27b</c>, turns 1–6): Vadim opens the interrogation by asking for the player's
/// full name, the scripted player line <c>My name is Ellie Onyx</c> from <c>char:ally</c> is delivered at ~43s
/// game time, and the model never replies — turns 3–5 answer with <c>wait</c> despite the delivered line, and its
/// one engaged response (turn 6) is discarded as invalid. This scenario reproduces the preconditions under the
/// new pipeline: the production prompt stack, authored interrogation scenario, and tool inventory, with the player line
/// injected as a heard-speech observation at a controlled, event-driven point.
/// </para>
/// <para>
/// Deviation from baseline pacing (approved compression): the baseline delivered the line at ~43s of game time
/// while a wait was active; the scenario injects it deterministically after the first provider turn settles, and
/// the fresh-turn scheduler — not wall-clock pacing — produces the delivery request. The observation window is
/// the parent contract's: from the request that makes the line model-visible through the next two requests.
/// </para>
/// </remarks>
internal static class VadimInterrogationBaseline
{
    /// <summary>Stable scenario name recorded in traces and used for artefact file names.</summary>
    public const string ScenarioName = "vadim-interrogation-baseline";

    /// <summary>The owning NPC's full ID, matching the baseline character.</summary>
    public const string OwnerFullId = "char:vadim";

    /// <summary>The player character's full ID; her identity is unknown to Vadim until spoken.</summary>
    public const string PlayerFullId = "char:ally";

    /// <summary>The scripted player line whose non-reply is the parent symptom.</summary>
    public const string PlayerLine = "My name is Ellie Onyx";

    /// <summary>
    /// The rendered observation line the synthetic scene-update exchange delivers for the player line, without
    /// the trailing game-time suffix. The renderer appends the game time directly, so the spoken sentence
    /// carries no trailing period here.
    /// </summary>
    public const string PlayerLineDeliveryPrefix = "Heard `char:ally` say to me: My name is Ellie Onyx";

    /// <summary>Number of requests after the delivery request that complete the observation window.</summary>
    public const int WindowFollowUpRequests = 2;

    /// <summary>Bounds for the deterministic smoke run: scripted turns plus recovery and teardown slack.</summary>
    public static LiveLLMScenarioBounds SmokeBounds => new(
        MaxTargetRequests: 8,
        Timeout: TimeSpan.FromSeconds(120));

    /// <summary>
    /// Bounds for the live run: the baseline's ~6 turns plus invalid-response recovery retries and the request
    /// the scenario's node exit cancels mid-flight.
    /// </summary>
    public static LiveLLMScenarioBounds LiveBounds => new(
        MaxTargetRequests: 12,
        Timeout: TimeSpan.FromMinutes(8));
}

/// <summary>One completed provider request as the scenario observed it: the exact message list sent and the raw
/// response that settled it, before any runner validation.</summary>
internal sealed record VadimInterrogationRequest(
    int Index,
    IReadOnlyList<ChatMessage> Messages,
    ChatResponse Response,
    int SettledTurnsAtRequestStart);

/// <summary>
/// Evidence summary of one scenario execution: the completed requests in arrival order, the zero-based index of
/// the delivery request, and the transcript recorder's turn files — the inputs the parent contract's acceptance
/// criteria judge over.
/// </summary>
internal sealed record VadimInterrogationResult(
    IReadOnlyList<VadimInterrogationRequest> CompletedRequests,
    int DeliveryRequestIndex,
    IReadOnlyList<string> TurnFiles)
{
    /// <summary>Whether the full observation window completed: delivery plus the configured follow-ups.</summary>
    public bool WindowCompleted
        => DeliveryRequestIndex >= 0
            && CompletedRequests.Count
                >= DeliveryRequestIndex + 1 + VadimInterrogationBaseline.WindowFollowUpRequests;
}

/// <summary>
/// Drives the Vadim interrogation scenario inside a <see cref="LiveLLMScenarioContext" />: it wires the production
/// mind with the baseline's authored interrogation context, forwards every provider roundtrip through the executor's traced target
/// client, injects the scripted player line after the first turn settles, and ends the session once the
/// observation window completes.
/// </summary>
/// <remarks>
/// The scenario owns no clients and contacts no network: the executor's borrowed target client — live or the
/// deterministic scripted fake — is the only transport, so the smoke run exercises the identical plumbing the
/// live run uses. Turn-level evidence (accepted/discarded outcomes with reasons, stripped-text records, full
/// request transcripts) is captured by the real AI-011 transcript recorder, whose files the scenario collects.
/// </remarks>
internal sealed partial class VadimInterrogationScenario
{
    private const string PromptStackPath = "res://assets/characters/prompts/generic_npc_prompt_stack.tres";
    private const string CurrentScenePromptPath = "res://assets/characters/prompts/current_scene.tres";
    private const string OwnerId = "vadim";
    private const string PlayerId = "ally";

    private readonly string _transcriptRoot;
    private readonly TimeSpan _timeout;

    public VadimInterrogationScenario(string transcriptRoot, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptRoot);
        _transcriptRoot = transcriptRoot;
        _timeout = timeout ?? VadimInterrogationBaseline.LiveBounds.Timeout;
    }

    /// <summary>Runs the scenario to the end of its observation window and returns the captured evidence.</summary>
    /// <param name="context">The executor scenario context whose target client receives every provider roundtrip.</param>
    /// <param name="cancellationToken">Token cancelling the whole execution.</param>
    /// <returns>The scenario's evidence summary.</returns>
    public async Task<VadimInterrogationResult> RunAsync(
        LiveLLMScenarioContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        DateTime deadline = DateTime.UtcNow + _timeout;

        SceneTree sceneTree = TestUtils.GetSceneTree();
        InterrogationSessionClientProvider clientProvider = new()
        {
            Context = context,
            SettledTurnCount = () => ListTurnFiles().Count,
        };
        ScenarioCharacter owner = new(OwnerId, new ImmediateVoice());
        // The scene context resolves the player through the player group, so Ally is the fixture node player
        // carrying the baseline identity.
        FixturePlayerCharacter player = new()
        {
            Id = PlayerId
        };
        InterrogationMind mind = new(owner)
        {
            SystemInstruction = Assert.IsType<PromptStack>(
                ResourceLoader.Load(PromptStackPath),
                exactMatch: false),
            CurrentSceneStatus = Assert.IsType<PromptStack>(
                ResourceLoader.Load(CurrentScenePromptPath),
                exactMatch: false),
            ClientProvider = clientProvider,
            ScenarioManager = new FixedScenarioManager
            {
                DescriptionPath = "res://lore/wiki/scenarios/interrogation.md",
            },
            ObservationImportanceThreshold = 1f,
            // Keep the player visible for the whole window without decay-sensitive defaults, and recover from any
            // invalid response immediately so the window completes deterministically.
            AttentionDecayPerSecond = 0f,
            InvalidResponseRecoveryBackoffSeconds = [0f],
        };
        WatchRegistry watchRegistry = new()
        {
            Conditions = [new ProximityWatchTool()],
        };
        mind.AddChild(watchRegistry);
        mind.AddChild(new AttendedCharacterSceneStatusProjector
        {
            ProjectorID = AttendedCharacterSceneStatusProjector.ProjectorIDValue,
        });
        mind.AddChild(new WatchSceneStatusProjector
        {
            ProjectorID = WatchSceneStatusProjector.ProjectorIDValue,
            Registry = watchRegistry,
        });
        mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(
            EnableRequestResponseLogging: false,
            EnableReasoningLogging: true,
            EnableSessionTranscriptLogging: true));
        mind.SetSceneContextLoaderForTesting(
            () => new SceneContext([owner, player], ContentContext.Default));
        // The interrogator's attention sits on the subject from the start, so the scripted speech clears the
        // observation importance threshold the way the baseline's attended delivery did (SharedGuidanceDelivery's
        // admission pattern).
        mind.ReinforceAttentionForTest(VadimInterrogationBaseline.PlayerFullId);

        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(_transcriptRoot);
        try
        {
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
            try
            {
                await WaitUntilAsync(
                    sceneTree,
                    () => clientProvider.Snapshot().Count >= 1,
                    "the opening turn to complete",
                    deadline,
                    cancellationToken);
                await WaitUntilAsync(
                    sceneTree,
                    () => clientProvider.StartedRequestCount >= 2,
                    "the follow-up request to start",
                    deadline,
                    cancellationToken);

                // Inject the scripted player line while that follow-up request is in flight: the fresh
                // heard-speech observation invalidates it, and its replacement carries the synthetic
                // scene-update exchange delivering the line — the deterministic compression of the baseline's
                // ~43s arrival into an interaction boundary (AI-002 TR-5/TR-18). This synthetic observation models
                // a reply addressed to Vadim; undecided speech must not interrupt held reasoning (AI-001 TR-14).
                mind.ObserveForTest(new ObservedSpeech(
                    VadimInterrogationBaseline.PlayerFullId,
                    VadimInterrogationBaseline.PlayerLine,
                    [VadimInterrogationBaseline.OwnerFullId]));

                await WaitUntilAsync(
                    sceneTree,
                    () => FindDeliveryRequestIndex(clientProvider) >= 0,
                    "the player line to become model-visible",
                    deadline,
                    cancellationToken);
                await WaitUntilAsync(
                    sceneTree,
                    () => WindowComplete(clientProvider),
                    "the observation window to complete",
                    deadline,
                    cancellationToken);
                // Let the closing turn's tool batch settle before ending the session: its transcript page
                // appearing proves the cycle resolved, so the page records its settled outcome rather than an
                // interruption.
                int settledCyclesBeforeClosing = clientProvider.Snapshot()[^1].SettledTurnsAtRequestStart;
                await WaitUntilAsync(
                    sceneTree,
                    () => ListTurnFiles().Count > settledCyclesBeforeClosing,
                    "the closing turn's transcript page to appear",
                    deadline,
                    cancellationToken);

                // The window is complete: end the session by node exit. The loop may already have issued one
                // more request; its mid-flight cancellation is the contained quiet exit, never a scenario
                // failure.
                (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
                await TestUtils.WaitForFramesAsync(sceneTree, 4);
            }
            finally
            {
                if (mind.IsInsideTree())
                {
                    (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
                }

                mind.QueueFree();
            }
        }
        finally
        {
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
        }

        return new VadimInterrogationResult(
            clientProvider.Snapshot(),
            FindDeliveryRequestIndex(clientProvider),
            ListTurnFiles());
    }

    /// <summary>
    /// Finds the zero-based index of the first completed request whose messages carry the synthetic
    /// scene-update exchange delivering the scripted player line: the request in which the line becomes
    /// model-visible.
    /// </summary>
    internal static int FindDeliveryRequestIndex(InterrogationSessionClientProvider provider)
    {
        IReadOnlyList<VadimInterrogationRequest> requests = provider.Snapshot();
        for (int index = 0; index < requests.Count; index++)
        {
            if (DeliversPlayerLine(requests[index].Messages))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether a request's messages carry a runtime-authored <c>scene_update</c> exchange whose result delivers
    /// the scripted player line (AI-002 TR-18).
    /// </summary>
    internal static bool DeliversPlayerLine(IReadOnlyList<ChatMessage> messages)
    {
        HashSet<string> deliveryCallIds =
        [
            .. messages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>()
                .Where(static call => call.Name == SyntheticSceneUpdateExchange.DeliveryToolName)
                .Select(static call => call.CallId),
        ];
        return messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>()
            .Where(result => deliveryCallIds.Contains(result.CallId))
            .Any(result => result.Result?.ToString()?.Contains(
                VadimInterrogationBaseline.PlayerLineDeliveryPrefix,
                StringComparison.Ordinal) == true);
    }

    private static bool WindowComplete(InterrogationSessionClientProvider provider)
    {
        int deliveryIndex = FindDeliveryRequestIndex(provider);
        return deliveryIndex >= 0
            && provider.Snapshot().Count
                >= deliveryIndex + 1 + VadimInterrogationBaseline.WindowFollowUpRequests;
    }

    private IReadOnlyList<string> ListTurnFiles()
    {
        // The recorder nests turn pages under a sortable run directory, then the character directory.
        return Directory.Exists(_transcriptRoot)
            ? [.. Directory
                .GetFiles(_transcriptRoot, "turn-*.md", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)]
            : [];
    }

    private static async Task WaitUntilAsync(
        SceneTree sceneTree,
        Func<bool> predicate,
        string description,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"The interrogation scenario timed out waiting for {description}.");
            }

            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>Mind fixture exposing the observation injection the scripted player line needs.</summary>
    private sealed partial class InterrogationMind(ICharacter owner) : AgenticMind
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public void ReinforceAttentionForTest(string fullId)
            => ReinforceAttention(fullId, 1f, AttentionSettings.Create(1f, 0f, 0.05f, 0.25f));

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>
    /// Session client provider that forwards every provider roundtrip through the executor's traced target client
    /// (AI-002 TR-2): live runs borrow the live target client, and the deterministic smoke borrows the scripted
    /// fake — identical plumbing, no network in the smoke.
    /// </summary>
    internal sealed partial class InterrogationSessionClientProvider : ClientProvider
    {
        private InterrogationTracingClient? _client;

        public Func<int> SettledTurnCount { get; init; } = static () => 0;

        public LiveLLMScenarioContext? Context
        {
            get;
            set;
        }

        /// <summary>
        /// Snapshot of the completed requests in arrival order; safe to poll from the main thread while provider
        /// continuations append.
        /// </summary>
        public IReadOnlyList<VadimInterrogationRequest> Snapshot()
            => _client?.Snapshot() ?? [];

        /// <summary>Number of requests that have started, including any still awaiting their response.</summary>
        public int StartedRequestCount => _client?.StartedCount ?? 0;

        public override IChatClient CreateChatClient()
        {
            if (_client is null)
            {
                LiveLLMScenarioContext context = Context
                    ?? throw new InvalidOperationException(
                        "The interrogation session client provider needs its scenario context before the session starts.");
                _client = new InterrogationTracingClient(context, SettledTurnCount);
            }

            return _client;
        }

        private sealed class InterrogationTracingClient(
            LiveLLMScenarioContext context,
            Func<int> settledTurnCount) : IChatClient
        {
            private readonly List<VadimInterrogationRequest> _requests = [];
            private readonly Lock _gate = new();
            private int _startedCount;

            public IReadOnlyList<VadimInterrogationRequest> Snapshot()
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }

            public int StartedCount
            {
                get
                {
                    lock (_gate)
                    {
                        return _startedCount;
                    }
                }
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<ChatMessage> request = [.. messages];
                int settledTurnsAtRequestStart = settledTurnCount();
                lock (_gate)
                {
                    _startedCount++;
                }

                ChatResponse response = await context.SendTargetAsync(request, options, cancellationToken);
                lock (_gate)
                {
                    _requests.Add(new VadimInterrogationRequest(
                        _requests.Count, request, response, settledTurnsAtRequestStart));
                }

                return response;
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
                => throw new NotSupportedException("The interrogation scenario never uses streaming requests.");

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Plain scene character fixture with an optional voice for the owner.</summary>
    private sealed class ScenarioCharacter(string id, IVoice? voice = null) : ICharacter
    {
        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = voice is null ? [] : [voice];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    /// <summary>Voice double whose cancellable submission completes immediately at playback hand-off.</summary>
    private sealed class ImmediateVoice : IVoice
    {
        public string Id { get; set; } = "vadim-scenario-voice";

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

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
        {
            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
            IsSpeaking = false;
            SpeechEnded?.Invoke(this);
            return ValueTask.CompletedTask;
        }
    }
}
