using System.Collections.ObjectModel;
using System.Diagnostics;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Core.Threading;
using AlleyCat.Core.Time;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.AI.Watch;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI;

/// <summary>
/// NPC mind that sustains one long-running agent session over its complete subjective timeline.
/// </summary>
[GlobalClass]
public partial class AgenticMind : MindBase
{
    private const string ScenarioContextKey = "scenario";

    /// <summary>
    /// Bootstrap input message the session owner sends with the first request for both chat-client kinds
    /// (AI-002 TR-2).
    /// </summary>
    internal const string SessionBootstrapInput = "Begin. Participate in the scene using the available tools.";

    private static readonly IReadOnlyDictionary<string, object?> _emptyRenderContext =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    private readonly SpeechTurnContinuationCoordinator _speechContinuations;
    private Func<AIDiagnosticsSettings> _diagnosticsSettingsLoader = AIDiagnosticsSettings.LoadOrDefault;
    private IReadOnlyDictionary<string, object?> _latestRenderContext = _emptyRenderContext;
    private volatile AgentSessionRunner? _activeRunner;
    private WatchRegistry? _boundWatchRegistry;
    private volatile bool _sessionStarted;

    /// <summary>Occurs after an observation has been committed to this Mind's timeline.</summary>
    public event Action<AgentObservation>? ObservationCommitted;

    /// <summary>
    /// Creates the mind with its session-scoped speech-turn and continuation correlation coordinator
    /// (AI-002 TR-13): the coordinator owns every concrete speech observation read for this node's single
    /// session.
    /// </summary>
    public AgenticMind()
    {
        _speechContinuations = new SpeechTurnContinuationCoordinator(this);
    }

    /// <summary>The runner of the currently executing session, or null outside session execution.</summary>
    internal AgentSessionRunner? ActiveRunner => _activeRunner;

    /// <summary>
    /// Editor-authored system prompt stack compiled and rendered exactly once per session.
    /// </summary>
    [ExportGroup("Prompt")]
    [Export]
    public PromptStack? SystemInstruction
    {
        get;
        set;
    }

    /// <summary>
    /// Editor-authored current-scene-status stack compiled and validated at session start, then rendered from a fresh
    /// typed snapshot at each later logical request boundary.
    /// </summary>
    [Export]
    public PromptStack CurrentSceneStatus { get; set; } = new();

    /// <summary>
    /// Backend factory used to create the session's chat client.
    /// </summary>
    [ExportGroup("Backend")]
    [Export]
    public ClientProvider? ClientProvider { get; set; } = new OpenAIClientProvider();

    /// <summary>
    /// Editor-authored manager resolving the scenario once at session start, or null when the feature is unused.
    /// </summary>
    /// <remarks>
    /// An unconfigured manager behaves exactly like a manager returning null.
    /// </remarks>
    [ExportGroup("Scenario")]
    [Export]
    public ScenarioManager? ScenarioManager
    {
        get; set;
    }

    /// <summary>
    /// Allows the provider to return several action calls in one response.
    /// </summary>
    [Export]
    public bool AllowMultipleToolCalls
    {
        get; set;
    }

    /// <summary>
    /// Consecutive malformed model responses permitted before the session ends through its contained failure path.
    /// </summary>
    [ExportGroup("Recovery")]
    [Export(PropertyHint.Range, "1,100,1")]
    public int InvalidResponseRecoveryBudget { get; set; } = InvalidResponseRecoveryPolicy.DefaultConsecutiveFailureBudget;

    /// <summary>
    /// Delay in seconds before each fresh request after a malformed model response. The final value is reused when
    /// the configured recovery budget exceeds the number of delays.
    /// </summary>
    [Export]
    public float[] InvalidResponseRecoveryBackoffSeconds { get; set; } = [1f, 2f, 4f];

    /// <summary>
    /// Editor-authored extra tools bound to the session in addition to the production inventory.
    /// </summary>
    [ExportGroup("Tools")]
    [Export]
    public Godot.Collections.Array<AgentTool> Tools { get; set; } = [];

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        StartSession();
    }

    /// <summary>
    /// Starts the one session for this Mind's node lifetime — fire-and-forget with full containment: the session
    /// never crashes the scene, and failures are logged like any contained response failure (AI-002 TR-1). A
    /// process-wide <c>--no-ai</c> switch instead ends the session before it begins with one Information notice.
    /// </summary>
    private void StartSession()
    {
        if (IsNodeLifetimeEnded || _sessionStarted)
        {
            return;
        }

        // The suppression switch is process-wide and resolved once, so a suppressed mind can never legitimately
        // start a session later in its node lifetime: consuming the one-shot start guard here keeps it ended for
        // good and preserves the guard's simple semantics.
        _sessionStarted = true;

        if (AgentSessionSuppression.IsDisabled)
        {
            if (AgentSessionSuppression.TryBeginSuppressionNotice())
            {
                LogSessionSuppressed();
            }

            return;
        }

        _ = RunSessionUntilNodeExitAsync();
    }

    private async Task RunSessionUntilNodeExitAsync()
    {
        CancellationToken lifetimeToken = NodeLifetimeCancellationToken;
        ObservationDeliverySignalled += HandleObservationDeliverySignalled;
        SpeechSegmentLifecycleNotified += HandleSpeechSegmentLifecycleNotified;
        try
        {
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            Stopwatch runStopwatch = PipelineDebugLog.StartTimer();
            try
            {
                AgentSession session = await PrepareSessionAsync(sessionCancellation.Token);
                await ExecuteSessionAsync(session, sessionCancellation.Token);
            }
            finally
            {
                if (PipelineDebugLog.IsEnabled)
                {
                    PipelineDebugLog.LogOnlyLatency("Agent session ended after", runStopwatch);
                }
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            // Expected node-lifetime end of the session (AI-002 TR-14): quiet, never a backend failure.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !lifetimeToken.IsCancellationRequested)
        {
            LogSessionFailure(ex);
        }
        finally
        {
            ObservationDeliverySignalled -= HandleObservationDeliverySignalled;
            SpeechSegmentLifecycleNotified -= HandleSpeechSegmentLifecycleNotified;
            _speechContinuations.ClearContinuationExpectations();
            EndWatchSession();
        }
    }

    /// <summary>
    /// Prepares the session start sequence (AI-002 TR-2, AI-003 TR-7; AI-008 TR-7): assembles the render context on demand,
    /// resolves the scenario once with the freshly built core context, seals and renders the PromptStack exactly
    /// once, and creates the session's tools and chat client.
    /// </summary>
    internal async Task<AgentSession> PrepareSessionAsync(CancellationToken cancellationToken)
    {
        ClientProvider clientProvider = ClientProvider
            ?? throw new InvalidOperationException("AgenticMind requires a configured ClientProvider.");
        PromptStack systemInstruction = SystemInstruction
            ?? throw new InvalidOperationException("AgenticMind requires a configured SystemInstruction prompt stack.");
        PromptStack currentSceneStatus = CurrentSceneStatus
            ?? throw new InvalidOperationException("AgenticMind requires a configured CurrentSceneStatus prompt stack.");

        cancellationToken.ThrowIfCancellationRequested();
        ISceneContext scene = GetCurrentSceneContext();
        ICharacter character = ResolveOwningCharacter();

        // Phase 1: core render context — every reserved key except 'scenario', including the unconditional player
        // context (AI-003 TR-7). Observations never enter the dictionary: they reach the model exclusively
        // through tool results and per-request event-timeline context.
        Dictionary<string, object?> coreContext = CreateCoreRenderContext(
            character,
            scene,
            GetContextEligibleAttentionIDs());

        // Phase 2: one manager query with the freshly assembled core context, then the scenario key (AI-008 TR-7).
        Scenario? scenario = ScenarioManager is not null
            ? await ScenarioManager.GetCurrentScenario(coreContext)
            : null;
        ScenarioContext sessionContext = new(character, scene, scenario);
        IReadOnlyDictionary<string, object?> renderContext = AddScenarioAndSeal(coreContext, scenario);

        PromptSectionBuildContext buildContext = new(Game.Instance, scene, character);
        ITemplate template = await systemInstruction.CompileAsync(buildContext, cancellationToken);
        var statusProjectors = SceneStatusProjectorRegistry.Discover(this);
        CompiledSceneStatusPrompt compiledCurrentSceneStatus = await currentSceneStatus.CompileSceneStatusAsync(
            buildContext,
            statusProjectors,
            cancellationToken);
        statusProjectors.ValidateProjections(CreateCurrentSceneStatusBuildContext(), currentSceneStatus.Sections ?? []);
        string instructions = await RenderAndPublishSystemInstruction(template, renderContext);

        IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
        IGameClock clock = GameClock;
        ToolAdmissionBroker toolAdmission = new();
        List<AITool> tools = CreateSessionTools(sessionContext, dispatcher, clock, toolAdmission);
        var invalidResponseRecoveryPolicy = new InvalidResponseRecoveryPolicy(
            InvalidResponseRecoveryBudget,
            [.. InvalidResponseRecoveryBackoffSeconds.Select(static seconds => TimeSpan.FromSeconds(seconds))]);

        AIDiagnosticsSettings diagnosticsSettings = _diagnosticsSettingsLoader();
        IChatClient chatClient = AIChatClientDiagnostics.Decorate(
            clientProvider.CreateChatClient(),
            diagnosticsSettings,
            GameLoggerResolver.ResolveFactoryRequired);

        return new AgentSession(
            sessionContext,
            instructions,
            [new ChatMessage(ChatRole.User, SessionBootstrapInput)],
            chatClient,
            tools,
            diagnosticsSettings.EnableReasoningLogging,
            invalidResponseRecoveryPolicy,
            toolAdmission,
            compiledCurrentSceneStatus);
    }

    /// <summary>
    /// Executes the prepared session through the transcript-execution runner until node exit or contained failure.
    /// </summary>
    internal async Task ExecuteSessionAsync(AgentSession session, CancellationToken cancellationToken)
    {
        AgentSessionRunner runner = new(
            session.ChatClient,
            session.Instructions,
            session.RunMessages,
            session.Tools,
            AllowMultipleToolCalls,
            GameLoggerResolver.ResolveRequired<AgenticMind>(),
            session.EnableReasoningLogging,
            invalidResponseRecoveryPolicy: session.InvalidResponseRecoveryPolicy,
            requestContextSource: new MindRequestContextSource(this, session, _speechContinuations));
        session.ToolAdmission.AttachRunner(runner);
        _activeRunner = runner;
        try
        {
            await runner.RunAsync(cancellationToken);
        }
        finally
        {
            _activeRunner = null;
        }
    }

    /// <summary>
    /// Bridges Mind's urgency-aware delivery signal into the session runtime (AI-001 TR-7, AI-002 TR-7–9).
    /// Scheduling is deliberately payload-free: every event remains in Mind's persistent timeline and is rendered
    /// only by the canonical request-context source. Freshness alone invalidates stale work; ordinary pressure waits
    /// for the session's next natural request boundary and clears only after context confirmation.
    /// </summary>
    private void HandleObservationDeliverySignalled(ObservationDeliverySignal signal)
    {
        if (IsNodeLifetimeEnded)
        {
            return;
        }

        AgentSessionRunner? runner = _activeRunner;
        if (runner is null)
        {
            // Before the session's runner exists — or after it ended — nothing can be invalidated. Mind retains
            // its scheduling pressure until a valid provider response confirms a future request context.
            return;
        }

        if (signal.Urgency is ObservationDeliveryUrgency.Fresh)
        {
            // A wait owns its normal completion and therefore is not cancelled. The following replacement request
            // rematerialises the timeline instead of receiving a rendered observation payload.
            IReadOnlySet<SpeechContinuationKey> readyContinuations = _speechContinuations.GetReadyContinuationKeys();
            if (!signal.WaitOwned && readyContinuations.Count > 0)
            {
                runner.InvalidateForFreshTurn(readyContinuations);
            }
            else
            {
                runner.InvalidateForFreshTurn(cancelActivePhase: !signal.WaitOwned);
            }
        }
    }

    private void HandleSpeechSegmentLifecycleNotified(SpeechSegmentLifecycleNotification notification)
    {
        if (IsNodeLifetimeEnded)
        {
            return;
        }

        _speechContinuations.HandleLifecycleNotification(notification);
    }

    private List<AITool> CreateSessionTools(
        ScenarioContext context,
        IMainThreadDispatcher dispatcher,
        IGameClock clock,
        ToolAdmissionBroker toolAdmission)
    {
        // Concrete capabilities bind typed to their concrete tool here at composition (AI-002 TR-13):
        // Speech-admission arbitration reaches only the speak tool; canonical request context owns all observation
        // delivery rather than the common tool session.
        SpeechTool speechTool = new(toolAdmission);
        WaitTool waitTool = new();
        AgentToolSession sessionServices = new(context, this, clock);
        WatchRegistry? watchRegistry = DiscoverWatchRegistry();
        // Composition registers the speak tool's per-function phase policy (AI-002 TR-14): its invocation phase
        // executes under admission arbitration, and the runner consults only this generic descriptor — never a
        // concrete tool, function name, or tool type. The production inventory is available without scene-authored
        // configuration (AI-002 TR-13); authored entries add extra or test tools alongside it.
        List<AITool> functions =
        [
            AgentSessionPhasePolicy.AdmissionArbitration.Bind(
                speechTool.CreateFunction(context, this, dispatcher, sessionServices)),
            waitTool.CreateFunction(context, this, dispatcher, sessionServices),
            new UnwatchTool(watchRegistry).CreateFunction(context, this, dispatcher, sessionServices),
        ];
        if (watchRegistry is not null)
        {
            IReadOnlyList<AITool> watchTools = watchRegistry.BindSessionAndCreateTools(
                context,
                this,
                dispatcher);
            _boundWatchRegistry = watchRegistry;
            functions.AddRange(watchTools);
        }
        foreach (AgentTool? extra in Tools)
        {
            if (extra is not null)
            {
                functions.Add(extra.CreateFunction(context, this, dispatcher, sessionServices));
            }
        }

        return functions;
    }

    private WatchRegistry? DiscoverWatchRegistry()
    {
        WatchRegistry[] registries = [.. GetChildren().OfType<WatchRegistry>()];
        return registries.Length switch
        {
            0 => null,
            1 => registries[0],
            _ => throw new InvalidOperationException("AgenticMind supports exactly one direct-child WatchRegistry."),
        };
    }

    private void EndWatchSession()
    {
        WatchRegistry? registry = Interlocked.Exchange(ref _boundWatchRegistry, null);
        registry?.EndSession();
    }

    /// <inheritdoc />
    protected override IEnumerable<IObservationLifetimePolicy> CreateLifetimePolicies()
        => [.. base.CreateLifetimePolicies(), new NeverExpireObservationLifetimePolicy<ObservedProximityTransition>(eventEligible: true)];

    /// <inheritdoc />
    protected override void OnNodeLifetimeEnding()
    {
        EndWatchSession();
        base.OnNodeLifetimeEnding();
    }

    internal static IReadOnlyDictionary<string, object?> CreateRenderContext(
        ICharacter character,
        ISceneContext scene,
        IReadOnlyList<string>? attentionEligibleFullIDs = null,
        Scenario? scenario = null)
    {
        Dictionary<string, object?> coreContext = CreateCoreRenderContext(
            character,
            scene,
            attentionEligibleFullIDs);
        return AddScenarioAndSeal(coreContext, scenario);
    }

    /// <summary>
    /// Builds the phase-one core render context: every reserved key except <c>scenario</c>.
    /// </summary>
    /// <remarks>
    /// The returned dictionary is intentionally left mutable and scenario-less: scenario managers receive it as their
    /// template context, and the session flow seals it with the <c>scenario</c> key afterwards. Entries hold the raw
    /// <see cref="ICharacter" /> instances, whose template surface the curated member-access policy seals to exactly
    /// <c>FullId</c>, so sensitive character members stay unreachable from templates by construction. Observations
    /// are never placed in the dictionary (AI-002 TR-10): they reach the model exclusively through AI-002 tool
    /// results and per-request event-timeline context.
    /// </remarks>
    internal static Dictionary<string, object?> CreateCoreRenderContext(
        ICharacter character,
        ISceneContext scene,
        IReadOnlyList<string>? attentionEligibleFullIDs)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(scene);

        ValidateSceneCharacterIdentity(character);
        if (!ReferenceEquals(scene.Find(character.FullId), character))
        {
            throw new InvalidOperationException(
                $"AgenticMind owning character '{character.FullId}' is absent from the current scene context.");
        }

        if (attentionEligibleFullIDs is null)
        {
            foreach (ICharacter subject in scene.Characters)
            {
                ValidateSceneCharacterIdentity(subject);
            }
        }

        // Included characters are stored as their resolved instances so the owner keeps its single instance in both
        // context locations, and only genuinely distinct characters sharing an exact FullId trip the duplicate guard.
        SortedDictionary<string, ICharacter> included = new(StringComparer.Ordinal)
        {
            [character.FullId] = character,
        };
        foreach (string fullID in attentionEligibleFullIDs ?? [.. scene.Characters.Select(static subject => subject.FullId)])
        {
            IdentityValidator.ValidateFullId(fullID, nameof(attentionEligibleFullIDs));
            if (scene.Find(fullID) is not ICharacter includedCharacter)
            {
                continue;
            }

            ValidateIncludedCharacterIdentity(includedCharacter, fullID);
            if (!included.TryAdd(fullID, includedCharacter) && !ReferenceEquals(included[fullID], includedCharacter))
            {
                throw new InvalidOperationException($"Foreground context contains duplicate exact FullId '{fullID}'.");
            }
        }

        Dictionary<string, object?> characterEntries = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ICharacter> entry in included)
        {
            characterEntries.Add(entry.Key, entry.Value);
        }

        // The player context is mandatory and unconditional: reuse the attention-included character when present,
        // otherwise use the scene player directly. 'characters' stays attention-gated and may omit the player.
        ICharacter player = scene.Player;
        ICharacter playerCharacter = characterEntries.TryGetValue(player.FullId, out object? includedPlayer)
            ? (ICharacter)includedPlayer!
            : player;

        Dictionary<string, object?> context = new(StringComparer.Ordinal)
        {
            ["character"] = character,
            ["characters"] = new ReadOnlyDictionary<string, object?>(characterEntries),
            ["player"] = playerCharacter,
        };

        return context;
    }

    /// <summary>
    /// Seals the core render context with the session's scenario key and freezes it for publication.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> AddScenarioAndSeal(
        Dictionary<string, object?> coreContext,
        Scenario? scenario)
        => coreContext.TryAdd(ScenarioContextKey, scenario)
            ? new ReadOnlyDictionary<string, object?>(coreContext)
            : throw new InvalidOperationException(
                $"Core render context already contains the reserved '{ScenarioContextKey}' key.");

    private static void ValidateIncludedCharacterIdentity(ICharacter character, string expectedFullID)
    {
        try
        {
            IdentityValidator.Validate(character, "character");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Foreground contextual subject has invalid identity '{character.FullId}'.",
                exception);
        }

        if (!string.Equals(character.FullId, expectedFullID, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Foreground contextual subject resolved for '{expectedFullID}' reported mismatched identity '{character.FullId}'.");
        }
    }

    private static void ValidateSceneCharacterIdentity(ICharacter character)
    {
        string fullId = character.FullId;
        try
        {
            IdentityValidator.Validate(character, nameof(character));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Scene character context has invalid identity '{fullId}'. Context assembly requires matching canonical Type, ID, and FullId values.",
                exception);
        }
    }

    internal static async Task<string> RenderSystemInstruction(
        ITemplate template,
        IReadOnlyDictionary<string, object?> context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        return await template.RenderAsync(context);
    }

    internal IReadOnlyDictionary<string, object?> GetLatestRenderContext()
        => Volatile.Read(ref _latestRenderContext);

    internal async Task<string> RenderAndPublishSystemInstruction(
        ITemplate template,
        IReadOnlyDictionary<string, object?> context)
    {
        string instructions = await RenderSystemInstruction(template, context);
        _ = Interlocked.Exchange(ref _latestRenderContext, context);
        return instructions;
    }

    /// <summary>
    /// Captures fresh request-scoped scene state without touching the session-fixed scenario context or scenario.
    /// </summary>
    internal SceneStatusBuildContext CreateCurrentSceneStatusBuildContext()
    {
        ProcessRetainedObservationExpiry();
        ISceneContext scene = GetCurrentSceneContext();
        ICharacter character = ResolveOwningCharacter();
        AttentionSnapshot attention = GetAttentionSnapshot();
        IReadOnlyList<AcceptedObservationEntry> retainedLog = GetRetainedObservationSnapshot();
        IReadOnlyList<AcceptedObservationEntry> eventTimeline = GetPersistentEventTimelineSnapshot();
        double timestamp = GameClock.NowSeconds;
        return new SceneStatusBuildContext(character, scene, attention, retainedLog, eventTimeline, timestamp);
    }

    /// <summary>
    /// Renders the session-start compiled current-scene-status stack with a fresh typed snapshot. This deliberately
    /// leaves request attachment to the subsequent AgentSessionRunner integration task.
    /// </summary>
    internal Task<string> RenderCurrentSceneStatusAsync(
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.CurrentSceneStatus.RenderAsync(CreateCurrentSceneStatusBuildContext(), cancellationToken);
    }

    /// <summary>
    /// Composes Mind-specific event history and scene status into the generic runner request-context boundary.
    /// The source owns the confirmed event watermark; it never exposes Mind, projectors, observations, or tools to
    /// <see cref="AgentSessionRunner" />.
    /// </summary>
    private sealed class MindRequestContextSource(
        AgenticMind mind,
        AgentSession session,
        SpeechTurnContinuationCoordinator continuations) : IAgentRequestContextSource
    {
        private readonly Lock _watermarkLock = new();
        private long _confirmedEventSequenceID;

        public async ValueTask<AgentRequestContext> MaterialiseAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await continuations.WaitForPresentationEligibilityAsync(cancellationToken);
                IReadOnlyList<AcceptedObservationEntry> timeline = mind.GetPersistentEventTimelineSnapshot();
                long eventWatermark = timeline.Count == 0 ? 0 : timeline[^1].SequenceID;
                if (!continuations.TryPresentThrough(eventWatermark, timeline))
                {
                    // A lifecycle cue was registered between eligibility sampling and timeline capture. It belongs
                    // to the same request boundary, so wait for its committed event before rendering anything.
                    continue;
                }

                IReadOnlyList<AcceptedObservationEntry> accepted = mind.GetAcceptedObservationLogSnapshot();
                long confirmed;
                lock (_watermarkLock)
                {
                    confirmed = _confirmedEventSequenceID;
                }

                List<AcceptedObservationEntry> established = [];
                List<AcceptedObservationEntry> newlyObserved = [];
                foreach (AcceptedObservationEntry entry in timeline)
                {
                    (entry.SequenceID <= confirmed ? established : newlyObserved).Add(entry);
                }

                string establishedText = await RenderTimelineAsync(established, timeline);
                string newText = await RenderTimelineAsync(newlyObserved, timeline);
                string sceneStatus = await mind.RenderCurrentSceneStatusAsync(session, cancellationToken);
                long schedulingWatermark = accepted.Count == 0 ? 0 : accepted[^1].SequenceID;
                var confirmation = new Confirmation(eventWatermark, schedulingWatermark);
                return new AgentRequestContext(
                    [
                        new ChatMessage(ChatRole.User, BuildTimelineMessage(establishedText, newText)),
                        new ChatMessage(ChatRole.User, sceneStatus),
                    ],
                    confirmation);
            }
        }

        public void Confirm(object? confirmation)
        {
            if (confirmation is not Confirmation snapshot)
            {
                return;
            }

            lock (_watermarkLock)
            {
                _confirmedEventSequenceID = Math.Max(_confirmedEventSequenceID, snapshot.EventSequenceID);
            }

            mind.ConfirmProviderRequestContext(snapshot.SchedulingSequenceID);
            continuations.ConfirmThrough(snapshot.EventSequenceID);
        }

        public void Discard(object? confirmation)
        {
            if (confirmation is Confirmation snapshot)
            {
                continuations.DiscardPresentation(snapshot.EventSequenceID);
            }
        }

        private async Task<string> RenderTimelineAsync(
            IReadOnlyList<AcceptedObservationEntry> entries,
            IReadOnlyList<AcceptedObservationEntry> completeTimeline)
            => entries.Count == 0
                ? "(none)"
                : await new ObservationHistoryRenderer(session.Context.Character).RenderAsync(entries, completeTimeline);

        private static string BuildTimelineMessage(string established, string newlyObserved)
            => $"Established Event History:\n{established}\n--- New Since Your Previous Response ---\n{newlyObserved}";

        private readonly record struct Confirmation(long EventSequenceID, long SchedulingSequenceID);
    }

    /// <inheritdoc />
    protected override void OnObservationIngested(AgentObservation observation)
    {
        base.OnObservationIngested(observation);
        _speechContinuations.NotifyObservationAccepted(observation);
        ObservationCommitted?.Invoke(observation);
    }

    private static void LogSessionFailure(Exception exception)
    {
        if (GameLoggerResolver.TryResolve(out ILogger<AgenticMind>? logger) && logger is not null)
        {
            logger.LogError(exception, "AgenticMind agent session failed.");
        }
    }

    private static void LogSessionSuppressed()
    {
        if (GameLoggerResolver.TryResolve(out ILogger<AgenticMind>? logger) && logger is not null)
        {
            logger.LogInformation(
                "Agent sessions are suppressed process-wide by the '{NoAISwitch}' command-line switch; "
                + "no LLM requests will be made.",
                AgentSessionSuppression.NoAISwitch);
        }
    }

    internal void SetDiagnosticsSettingsLoaderForTesting(Func<AIDiagnosticsSettings> diagnosticsSettingsLoader)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsSettingsLoader);
        _diagnosticsSettingsLoader = diagnosticsSettingsLoader;
    }

    /// <summary>
    /// Prepared session state captured once at session start (AI-002 TR-2): the trusted binding, the rendered
    /// system instruction, the session-owner bootstrap input message, the decorated chat client, the bound
    /// tools, and the tool-admission carrier whose runner is attached at execution.
    /// </summary>
    internal sealed record AgentSession(
        ScenarioContext Context,
        string Instructions,
        IReadOnlyList<ChatMessage> RunMessages,
        IChatClient ChatClient,
        IList<AITool> Tools,
        bool EnableReasoningLogging,
        IInvalidResponseRecoveryPolicy InvalidResponseRecoveryPolicy,
        ToolAdmissionBroker ToolAdmission,
        CompiledSceneStatusPrompt CurrentSceneStatus);
}
