using System.Collections.ObjectModel;
using System.Diagnostics;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Core.Threading;
using AlleyCat.Core.Time;
using AlleyCat.Diagnostics;
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

    private Func<AIDiagnosticsSettings> _diagnosticsSettingsLoader = AIDiagnosticsSettings.LoadOrDefault;
    private IReadOnlyDictionary<string, object?> _latestRenderContext = _emptyRenderContext;
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
    /// Editor-authored event-history resource feeding the on-demand observation-history renderer for wait results,
    /// history results, and interruption injections, or null when the default fallback-only contract applies.
    /// </summary>
    [Export]
    public EventHistory? EventHistory
    {
        get;
        set;
    }

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
    /// never crashes the scene, and failures are logged like any contained response failure (AI-002 TR-1/2).
    /// </summary>
    private void StartSession()
    {
        if (IsNodeLifetimeEnded || _sessionStarted)
        {
            return;
        }

        _sessionStarted = true;
        _ = RunSessionUntilNodeExitAsync();
    }

    private async Task RunSessionUntilNodeExitAsync()
    {
        CancellationToken lifetimeToken = NodeLifetimeCancellationToken;
        NotableObservationsSignalled += HandleNotableObservationsSignalled;
        try
        {
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            Stopwatch runStopwatch = AIPipelineDebugLog.StartTimer();
            try
            {
                AgentSession session = await PrepareSessionAsync(sessionCancellation.Token);
                await ExecuteSessionAsync(session, sessionCancellation.Token);
            }
            finally
            {
                if (AIPipelineDebugLog.IsEnabled)
                {
                    AIPipelineDebugLog.Latency("Agent session ended after", runStopwatch);
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
            NotableObservationsSignalled -= HandleNotableObservationsSignalled;
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
        Scenario? scenario = ScenarioManager?.GetCurrentScenario(coreContext);
        ScenarioContext sessionContext = new(character, scene, scenario);
        IReadOnlyDictionary<string, object?> renderContext = AddScenarioAndSeal(coreContext, scenario);

        PromptSectionBuildContext buildContext = new(Game.Instance, scene, character);
        ITemplate template = await systemInstruction.CompileAsync(buildContext, cancellationToken);
        string instructions = RenderAndPublishSystemInstruction(template, renderContext);

        IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
        IGameClock clock = GameClock;
        var historyRenderer = ObservationHistoryRenderer.Create(
            EventHistory,
            Game.Instance.GetRequiredService<ITemplateCompiler>(),
            ResolveCharacterContext(renderContext));
        _activeHistoryRenderer = historyRenderer;
        List<AITool> tools = CreateSessionTools(sessionContext, dispatcher, historyRenderer, clock);

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
            diagnosticsSettings.EnableReasoningLogging);
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
            session.EnableReasoningLogging);
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
    /// Bridges Mind's notable-observation signal into a session interruption (AI-001 TR-6, AI-002 TR-41): the
    /// pending notable window is taken atomically and appended as an injected user message.
    /// </summary>
    private void HandleNotableObservationsSignalled()
    {
        AgentSessionRunner? runner = _activeRunner;
        if (runner is null || IsNodeLifetimeEnded)
        {
            return;
        }

        IReadOnlyList<AgentObservation>? notable = TryTakePendingNotableWindow();
        if (notable is not { Count: > 0 })
        {
            return;
        }

        string rendered = RenderNotableSummary(notable);
        runner.SignalInterruption(rendered);
    }

    /// <summary>Renders one injected notable-observation summary through the event-history contract.</summary>
    private string RenderNotableSummary(IReadOnlyList<AgentObservation> notable)
    {
        return _activeHistoryRenderer is { } renderer
            ? $"Important scene events require your attention:\n{renderer.Render(notable)}"
            : $"Important scene events require your attention: {notable.Count} notable observation(s).";
    }

    private static IReadOnlyDictionary<string, object?> ResolveCharacterContext(
        IReadOnlyDictionary<string, object?> renderContext)
        => renderContext["character"] is IReadOnlyDictionary<string, object?> characterContext
            ? characterContext
            : throw new InvalidOperationException(
                "The session render context is missing the owning character context dictionary.");

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
    /// template context, and the session flow seals it with the <c>scenario</c> key afterwards. Observations are
    /// never placed in the dictionary (AI-001 TR-25): they reach the model exclusively through AI-002 tool results
    /// and interruption injections.
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

        var included = new SortedDictionary<string, IContextual>(StringComparer.Ordinal)
        {
            [character.FullId] = character,
        };
        foreach (string fullID in attentionEligibleFullIDs ?? [.. scene.Characters.Select(static subject => subject.FullId)])
        {
            IdentityValidator.ValidateFullId(fullID, nameof(attentionEligibleFullIDs));
            if (scene.Find(fullID) is not IContextual contextual)
            {
                continue;
            }

            ValidateIncludedContextualIdentity(contextual, fullID);
            if (!included.TryAdd(fullID, contextual) && !ReferenceEquals(included[fullID], contextual))
            {
                throw new InvalidOperationException($"Foreground context contains duplicate exact FullId '{fullID}'.");
            }
        }

        Dictionary<string, object?> characterContexts = new(StringComparer.Ordinal);
        IReadOnlyDictionary<string, object?>? owningCharacterContext = null;
        foreach (KeyValuePair<string, IContextual> entry in included)
        {
            IReadOnlyDictionary<string, object?> subjectContext = entry.Value.GetContext(scene, observer: character);
            characterContexts.Add(entry.Key, subjectContext);
            if (ReferenceEquals(entry.Value, character))
            {
                owningCharacterContext = subjectContext;
            }
        }

        // The player context is mandatory and unconditional: reuse the attention-eligible dictionary when present,
        // otherwise compute it separately. 'characters' stays attention-gated and may omit the player.
        ICharacter player = scene.Player;
        if (!characterContexts.TryGetValue(player.FullId, out object? playerContext))
        {
            playerContext = player.GetContext(scene, observer: character);
        }

        Dictionary<string, object?> context = new(StringComparer.Ordinal)
        {
            ["character"] = owningCharacterContext,
            ["characters"] = new ReadOnlyDictionary<string, object?>(characterContexts),
            ["player"] = playerContext,
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

    private static void ValidateIncludedContextualIdentity(IContextual contextual, string expectedFullID)
    {
        if (contextual is not IIdentifiable identifiable)
        {
            throw new InvalidOperationException(
                $"Foreground contextual subject for '{expectedFullID}' must retain an identifiable canonical identity.");
        }

        try
        {
            IdentityValidator.Validate(identifiable, "character");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Foreground contextual subject has invalid identity '{identifiable.FullId}'.",
                exception);
        }

        if (!string.Equals(identifiable.FullId, expectedFullID, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Foreground contextual subject resolved for '{expectedFullID}' reported mismatched identity '{identifiable.FullId}'.");
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

    internal static string RenderSystemInstruction(
        ITemplate template,
        IReadOnlyDictionary<string, object?> context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        return template.Render(context);
    }

    internal IReadOnlyDictionary<string, object?> GetLatestRenderContext()
        => Volatile.Read(ref _latestRenderContext);

    internal string RenderAndPublishSystemInstruction(
        ITemplate template,
        IReadOnlyDictionary<string, object?> context)
    {
        string instructions = RenderSystemInstruction(template, context);
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

    internal void SetDiagnosticsSettingsLoaderForTesting(Func<AIDiagnosticsSettings> diagnosticsSettingsLoader)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsSettingsLoader);
        _diagnosticsSettingsLoader = diagnosticsSettingsLoader;
    }

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
        bool EnableReasoningLogging);
}
