using System.Collections.ObjectModel;
using System.Diagnostics;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Core.Threading;
using AlleyCat.Core.Time;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
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
    /// (AI-002 TR-7).
    /// </summary>
    internal const string SessionBootstrapInput = "Begin. Participate in the scene using the available tools.";

    private static readonly IReadOnlyDictionary<string, object?> _emptyRenderContext =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    private readonly Lock _observationDeliveryChainLock = new();
    private Func<AIDiagnosticsSettings> _diagnosticsSettingsLoader = AIDiagnosticsSettings.LoadOrDefault;
    private IReadOnlyDictionary<string, object?> _latestRenderContext = _emptyRenderContext;
    private Task _observationDeliveryChain = Task.CompletedTask;
    private volatile AgentSessionRunner? _activeRunner;
    private volatile ObservationHistoryRenderer? _activeHistoryRenderer;
    private volatile bool _sessionStarted;

    /// <summary>Occurs after an observation has been committed to this Mind's timeline.</summary>
    public event Action<AgentObservation>? ObservationCommitted;

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
    /// Godot resource path to the authored standalone event-history file feeding the on-demand observation-history
    /// renderer for wait results, history results, and interruption injections (AI-003 TR-12), or empty when the
    /// default fallback-only contract applies.
    /// </summary>
    [Export(PropertyHint.File, "*.md")]
    public string EventHistoryPath { get; set; } = string.Empty;

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
    /// never crashes the scene, and failures are logged like any contained response failure (AI-002 TR-1/2). A
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
            // Expected node-lifetime end of the session (AI-002 TR-44): quiet, never a backend failure.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !lifetimeToken.IsCancellationRequested)
        {
            LogSessionFailure(ex);
        }
        finally
        {
            ObservationDeliverySignalled -= HandleObservationDeliverySignalled;
        }
    }

    /// <summary>
    /// Prepares the session start sequence (AI-002 TR-5/6, AI-008 TR-7): assembles the render context on demand,
    /// resolves the scenario once with the freshly built core context, seals and renders the PromptStack exactly
    /// once, and creates the session's tools and chat client.
    /// </summary>
    internal async Task<AgentSession> PrepareSessionAsync(CancellationToken cancellationToken)
    {
        ClientProvider clientProvider = ClientProvider
            ?? throw new InvalidOperationException("AgenticMind requires a configured ClientProvider.");
        PromptStack systemInstruction = SystemInstruction
            ?? throw new InvalidOperationException("AgenticMind requires a configured SystemInstruction prompt stack.");

        cancellationToken.ThrowIfCancellationRequested();
        ISceneContext scene = GetCurrentSceneContext();
        ICharacter character = ResolveOwningCharacter();

        // Phase 1: core render context — every reserved key except 'scenario', including the unconditional player
        // context (AI-001 TR-25). Observations never enter the dictionary: they reach the model exclusively
        // through tool results and interruption injections.
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
        string instructions = await RenderAndPublishSystemInstruction(template, renderContext);

        IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
        IGameClock clock = GameClock;
        ObservationHistoryRenderer historyRenderer = CreateSessionHistoryRenderer(renderContext);
        _activeHistoryRenderer = historyRenderer;
        List<AITool> tools = CreateSessionTools(sessionContext, dispatcher, historyRenderer, clock);
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
            invalidResponseRecoveryPolicy);
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
            invalidResponseRecoveryPolicy: session.InvalidResponseRecoveryPolicy);
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
    /// Bridges Mind's urgency-aware delivery signal into the session runtime (AI-001 TR-44, AI-002 TR-39/40/41).
    /// Ordinary unowned windows are claimed, rendered, and queued as boundary injections without cancelling
    /// anything; fresh unowned windows record their invalidation immediately — cancelling the stale generation or
    /// batch first — and then queue their payload behind the runner's rendering barrier; wait-owned fresh windows
    /// skip injection entirely, because the wait result is the sole delivery channel, while still recording the
    /// stale latch without cancelling the wait itself so it completes naturally with its delivery and the batch's
    /// remaining calls are skipped. Windows are claimed, rendered, and queued serially in signal order so pending
    /// payloads coalesce in FIFO order, and a window that fails to render is abandoned back to Mind so its
    /// observations are never silently lost.
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
            // Before the session's runner exists — or after it ended — nothing can be invalidated or injected:
            // Mind retains the unclaimed window (AI-001 TR-44) for the next wait or delivery claim.
            return;
        }

        if (signal.WaitOwned)
        {
            if (signal.Urgency is ObservationDeliveryUrgency.Fresh)
            {
                // The woken wait delivers the window through its own result (AI-002 TR-41): no injected
                // duplication, and the wait — the active phase — is never cancelled, because it must complete
                // naturally with its delivery. The stale latch still invalidates the surrounding batch: its
                // remaining calls are skipped before the forced next request proceeds behind the wait's natural
                // result.
                runner.InvalidateForFreshTurn(expectFreshInjection: false, cancelActivePhase: false);
            }

            return;
        }

        bool fresh = signal.Urgency is ObservationDeliveryUrgency.Fresh;
        if (fresh)
        {
            // Cancel the stale generation or batch immediately (AI-002 TR-40); the rendering barrier keeps the
            // replacement request behind the fresh payload queued by the delivery below.
            runner.InvalidateForFreshTurn(expectFreshInjection: true);
        }

        EnqueueObservationDelivery(runner, fresh);
    }

    /// <summary>
    /// Serialises observation deliveries end-to-end in signal order: claims, renders, and payload queueing run as
    /// one chain so concurrently signalled windows coalesce in FIFO order (AI-002 TR-39/40).
    /// </summary>
    private void EnqueueObservationDelivery(AgentSessionRunner runner, bool spawnedByFreshSignal)
    {
        lock (_observationDeliveryChainLock)
        {
            _observationDeliveryChain = _observationDeliveryChain.ContinueWith(
                static (_, state) => ((Func<Task>)state!)(),
                () => DeliverPendingObservationWindowAsync(runner, spawnedByFreshSignal),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>
    /// Claims, renders, and queues every currently deliverable observation window (AI-001 TR-44, AI-002 TR-39/40):
    /// a fresh claim queues through the runner's rendering barrier while an ordinary claim queues as a plain
    /// boundary injection. A claim that cannot render is abandoned — restoring the window to Mind so scheduling
    /// pressure is never silently lost — and releases any fresh barrier so the replacement request is never blocked
    /// on a lost payload. The delivery never throws; failures surface through contained Error-level logging.
    /// </summary>
    private async Task DeliverPendingObservationWindowAsync(AgentSessionRunner runner, bool spawnedByFreshSignal)
    {
        ObservationDeliveryClaim? claim = null;
        bool payloadQueued = false;
        try
        {
            claim = TryClaimPendingObservationDelivery();
            if (claim is null || claim.Observations.Count == 0)
            {
                // Another delivery or an active wait owns the window: a fresh signal's rendering barrier is
                // released because no payload will arrive through injection.
                if (spawnedByFreshSignal)
                {
                    runner.AbandonFreshInjection();
                }

                return;
            }

            string summary = await RenderNotableSummaryAsync(claim.Observations);
            if (claim.Urgency is ObservationDeliveryUrgency.Fresh)
            {
                runner.QueueFreshInjection(summary);
            }
            else
            {
                runner.QueueInjection(summary);
            }

            payloadQueued = true;
            CompleteObservationDelivery(claim);
        }
        catch (Exception exception)
        {
            bool releaseFreshBarrier = spawnedByFreshSignal;
            if (claim is not null && claim.Observations.Count > 0 && !payloadQueued)
            {
                // Rendering or queueing failed before the payload was queued: restore the claimed window so it
                // stays deliverable (AI-001 TR-44). A payload that already reached the queue cannot be
                // un-delivered, so its window is never restored on top of the queued copy.
                AbandonObservationDelivery(claim);
                releaseFreshBarrier = claim.Urgency is ObservationDeliveryUrgency.Fresh;
            }

            if (releaseFreshBarrier)
            {
                runner.AbandonFreshInjection();
            }

            LogObservationDeliveryFailure(exception);
        }
    }

    private static void LogObservationDeliveryFailure(Exception exception)
    {
        if (GameLoggerResolver.TryResolve(out ILogger<AgenticMind>? logger) && logger is not null)
        {
            logger.LogError(
                exception,
                "Delivering the pending observation window failed; its ownership was restored to Mind.");
        }
    }

    private async Task<string> RenderNotableSummaryAsync(IReadOnlyList<AgentObservation> notable)
    {
        ObservationHistoryRenderer renderer = _activeHistoryRenderer
            ?? throw new InvalidOperationException(
                "AgenticMind has no active observation history renderer; a notable observation summary cannot be "
                + "rendered without an initialised ObservationHistoryRenderer.");

        return $"Important scene events require your attention:\n{await renderer.RenderAsync(notable)}";
    }

    private ObservationHistoryRenderer CreateSessionHistoryRenderer(
        IReadOnlyDictionary<string, object?> renderContext)
        => ObservationHistoryRenderer.Create(
            LoadEventHistoryDocument(),
            Game.Instance.GetRequiredService<ITemplateCompiler>(),
            renderContext["character"] as ICharacter
                ?? throw new InvalidOperationException(
                    "The session render context is missing the owning character."));

    /// <summary>
    /// Loads and parses the configured event-history file once at session start, or returns null when no file is
    /// configured so the default fallback-only contract applies (AI-003 TR-12).
    /// </summary>
    private EventHistoryDocument? LoadEventHistoryDocument()
    {
        if (string.IsNullOrWhiteSpace(EventHistoryPath))
        {
            return null;
        }

        using var file = Godot.FileAccess.Open(EventHistoryPath, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            Error error = Godot.FileAccess.GetOpenError();
            throw new InvalidOperationException(
                $"Could not read the event-history file '{EventHistoryPath}'. Godot FileAccess error: {error}.");
        }

        try
        {
            return EventHistoryDocument.Parse(file.GetAsText());
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Invalid event-history authoring in '{EventHistoryPath}'.", exception);
        }
    }

    private List<AITool> CreateSessionTools(
        ScenarioContext context,
        IMainThreadDispatcher dispatcher,
        ObservationHistoryRenderer historyRenderer,
        IGameClock clock)
    {
        // The production tool inventory is available without scene-authored configuration (AI-002 TR-16); authored
        // entries add extra or test tools alongside it.
        List<AgentTool> tools = [new SpeechTool(), new WaitTool(), new HistoryTool()];
        foreach (AgentTool? extra in Tools)
        {
            if (extra is not null)
            {
                tools.Add(extra);
            }
        }

        AgentToolSession sessionServices = new(context, this, historyRenderer, clock);
        List<AITool> functions = new(tools.Count);
        foreach (AgentTool tool in tools)
        {
            functions.Add(tool.CreateFunction(context, this, dispatcher, sessionServices));
        }

        return functions;
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
    /// are never placed in the dictionary (AI-001 TR-25): they reach the model exclusively through AI-002 tool
    /// results and interruption injections.
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

    /// <inheritdoc />
    protected override void OnObservationIngested(AgentObservation observation)
    {
        base.OnObservationIngested(observation);
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

    /// <summary>Replaces or clears the active observation-history renderer for fault and guard fixtures.</summary>
    internal void SetActiveHistoryRendererForTesting(ObservationHistoryRenderer? historyRenderer)
        => _activeHistoryRenderer = historyRenderer;

    /// <summary>
    /// Prepared session state captured once at session start (AI-002 TR-5/6): the trusted binding, the rendered
    /// system instruction, the session-owner bootstrap input message, the decorated chat client, and the bound
    /// tools.
    /// </summary>
    internal sealed record AgentSession(
        ScenarioContext Context,
        string Instructions,
        IReadOnlyList<ChatMessage> RunMessages,
        IChatClient ChatClient,
        IList<AITool> Tools,
        bool EnableReasoningLogging,
        IInvalidResponseRecoveryPolicy InvalidResponseRecoveryPolicy);
}
