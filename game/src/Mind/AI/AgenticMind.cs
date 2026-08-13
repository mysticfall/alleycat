using System.Collections.ObjectModel;
using System.Diagnostics;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Core.Threading;
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
/// NPC mind that reconstructs every agent turn from its complete subjective timeline.
/// </summary>
[GlobalClass]
public partial class AgenticMind : MindBase
{
    private static readonly IReadOnlyDictionary<string, object?> _emptyRenderContext =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    private Func<AIDiagnosticsSettings> _diagnosticsSettingsLoader = AIDiagnosticsSettings.LoadOrDefault;
    private Func<ISceneContext> _sceneContextLoader = static ()
        => Game.Instance.GetRequiredService<ISceneContextProvider>().GetCurrent();
    private ContextWorker[] _contextWorkers = [];
    private IReadOnlyDictionary<string, object?> _latestRenderContext = _emptyRenderContext;

    internal CancellationToken LifetimeCancellationToken => NodeLifetimeCancellationToken;

    /// <summary>Occurs after an observation has been committed to this Mind's timeline.</summary>
    public event Action<AgentObservation>? ObservationCommitted;

    /// <summary>Occurs after foreground processing has genuinely completed successfully.</summary>
    public event Action? ForegroundTurnSucceeded;

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        _contextWorkers = [.. GetChildren().OfType<ContextWorker>()];
        foreach (ContextWorker worker in _contextWorkers)
        {
            worker.Attach(this);
        }
    }

    /// <summary>
    /// Editor-authored system prompt stack compiled and rendered afresh for every turn.
    /// </summary>
    [ExportGroup("Prompt")]
    [Export]
    public PromptStack? SystemInstruction
    {
        get;
        set;
    }

    /// <summary>
    /// Backend factory used to create a fresh chat client for each turn.
    /// </summary>
    [ExportGroup("Backend")]
    [Export]
    public ClientProvider? ClientProvider { get; set; } = new OpenAIClientProvider();

    /// <summary>
    /// Allows the provider to return several action calls in one response.
    /// </summary>
    [Export]
    public bool AllowMultipleToolCalls
    {
        get; set;
    }

    /// <summary>
    /// Editor-authored action tools resolved for each turn.
    /// </summary>
    [ExportGroup("Tools")]
    [Export]
    public Godot.Collections.Array<AgentTool> Tools { get; set; } = [];

    /// <inheritdoc />
    protected override async Task ProcessObservationsAsync(
        IReadOnlyList<AgentObservation> observations,
        IReadOnlyList<AgentObservation> timelineSnapshot,
        CancellationToken cancellationToken)
        => await RunAgentTurnAsync(timelineSnapshot, cancellationToken);

    /// <inheritdoc />
    protected override async Task<bool> ProcessForegroundObservationsAsync(
        IReadOnlyList<AgentObservation> observations,
        IReadOnlyList<AgentObservation> timelineSnapshot,
        CancellationToken cancellationToken)
    {
        if (observations.Count == 0)
        {
            return true;
        }

        try
        {
            await ProcessObservationsAsync(observations, timelineSnapshot, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogOptionalResponseFailure(ex);
            return false;
        }
    }

    /// <summary>Runs the foreground tool-only turn after its observation batch has been claimed.</summary>
    protected virtual async Task RunAgentTurnAsync(
        IReadOnlyList<AgentObservation> timeline,
        CancellationToken cancellationToken)
    {
        ClientProvider clientProvider = ClientProvider
            ?? throw new InvalidOperationException("AgenticMind requires a configured ClientProvider.");
        PromptStack systemInstruction = SystemInstruction
            ?? throw new InvalidOperationException("AgenticMind requires a configured SystemInstruction prompt stack.");

        ISceneContext scene = _sceneContextLoader();
        ICharacter character = ResolveOwningCharacter();
        PromptSectionBuildContext buildContext = new(Game.Instance, scene, character);
        ITemplate template = await systemInstruction.CompileAsync(buildContext, cancellationToken);
        IReadOnlyDictionary<string, object?> renderContext = CreateRenderContext(
            character,
            scene,
            timeline,
            _contextWorkers,
            GetContextEligibleAttentionIDs());
        string instructions = RenderAndPublishSystemInstruction(template, renderContext);
        IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
        AgentToolContext toolContext = new(character, scene);
        List<AITool> turnTools = CreateTurnTools(toolContext, dispatcher);

        IChatClient chatClient = AIChatClientDiagnostics.Decorate(
            clientProvider.CreateChatClient(),
            _diagnosticsSettingsLoader(),
            GameLoggerResolver.ResolveFactoryRequired);
        ILogger<AgenticMind> logger = GameLoggerResolver.ResolveRequired<AgenticMind>();
        Stopwatch runStopwatch = AIPipelineDebugLog.StartTimer();
        try
        {
            await ToolOnlyTurnRunner.RunAsync(
                chatClient,
                instructions,
                clientProvider.CreateRunMessages(),
                turnTools,
                AllowMultipleToolCalls,
                logger,
                cancellationToken);
        }
        finally
        {
            if (AIPipelineDebugLog.IsEnabled)
            {
                AIPipelineDebugLog.Latency("LLM turn returned in", runStopwatch, $"{timeline.Count} observation(s)");
            }
        }
    }

    private List<AITool> CreateTurnTools(AgentToolContext context, IMainThreadDispatcher dispatcher)
    {
        List<AITool> tools = new(Tools.Count);
        foreach (AgentTool? tool in Tools)
        {
            if (tool is not null)
            {
                tools.Add(tool.CreateFunction(context, this, dispatcher));
            }
        }

        return tools;
    }

    internal static IReadOnlyDictionary<string, object?> CreateRenderContext(
        ICharacter character,
        ISceneContext scene,
        IReadOnlyList<AgentObservation>? observations = null,
        IReadOnlyList<string>? attentionEligibleFullIDs = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(scene);

        if (attentionEligibleFullIDs is null)
        {
            foreach (ICharacter subject in scene.Characters)
            {
                ValidateSceneCharacterIdentity(subject);
            }
        }

        IReadOnlyList<string> eligibleIDs = attentionEligibleFullIDs
            ?? [.. scene.Characters.Select(static subject => subject.FullId)];
        return CreateRenderContext(character, scene, observations, [], eligibleIDs);
    }

    /// <summary>Constructs the complete foreground render context for a claimed timeline snapshot.</summary>
    protected IReadOnlyDictionary<string, object?> CreateRenderContext(IReadOnlyList<AgentObservation> timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ISceneContext scene = Game.Instance.GetRequiredService<ISceneContextProvider>().GetCurrent();
        return CreateRenderContext(
            ResolveOwningCharacter(),
            scene,
            timeline,
            _contextWorkers,
            GetContextEligibleAttentionIDs());
    }

    private static IReadOnlyDictionary<string, object?> CreateRenderContext(
        ICharacter character,
        ISceneContext scene,
        IReadOnlyList<AgentObservation>? observations,
        IReadOnlyList<ContextWorker> workers,
        IReadOnlyList<string> attentionEligibleFullIDs)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(workers);

        ValidateSceneCharacterIdentity(character);
        if (!ReferenceEquals(scene.Find(character.FullId), character))
        {
            throw new InvalidOperationException(
                $"AgenticMind owning character '{character.FullId}' is absent from the current scene context.");
        }

        var included = new SortedDictionary<string, IContextual>(StringComparer.Ordinal)
        {
            [character.FullId] = character,
        };
        foreach (string fullID in attentionEligibleFullIDs)
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

        Dictionary<string, object?> context = new(StringComparer.Ordinal)
        {
            ["character"] = owningCharacterContext,
            ["characters"] = new ReadOnlyDictionary<string, object?>(characterContexts),
            [EventHistoryPromptSection.ObservationsContextKey] = observations ?? [],
        };
        foreach (ContextWorker worker in workers)
        {
            foreach (KeyValuePair<string, object?> entry in worker.GetProjection())
            {
                if (!context.TryAdd(entry.Key, entry.Value))
                {
                    throw new InvalidOperationException(
                        $"Context worker has duplicate context key '{entry.Key}'. Worker projection keys must be unique in authored order.");
                }
            }
        }

        return new ReadOnlyDictionary<string, object?>(context);
    }

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

    /// <inheritdoc />
    protected override void OnForegroundTurnSettled()
    {
        base.OnForegroundTurnSettled();
        ForegroundTurnSucceeded?.Invoke();
    }

    private static void LogOptionalResponseFailure(Exception exception)
    {
        if (GameLoggerResolver.TryResolve(out ILogger<AgenticMind>? logger) && logger is not null)
        {
            logger.LogError(exception, "AgenticMind response failed.");
        }
    }

    internal void SetDiagnosticsSettingsLoaderForTesting(Func<AIDiagnosticsSettings> diagnosticsSettingsLoader)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsSettingsLoader);
        _diagnosticsSettingsLoader = diagnosticsSettingsLoader;
    }

    internal void SetSceneContextLoaderForTesting(Func<ISceneContext> sceneContextLoader)
    {
        ArgumentNullException.ThrowIfNull(sceneContextLoader);
        _sceneContextLoader = sceneContextLoader;
    }

}
