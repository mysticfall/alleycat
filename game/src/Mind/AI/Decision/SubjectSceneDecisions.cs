using AlleyCat.Character;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>Main-thread composition root for the scene gates of one Mind.</summary>
internal sealed class SubjectSceneDecisions
{
    private const int RecentEventCount = 6;
    private const int RecentEventCharacterBudget = 2000;
    private const int MaximumSceneCharacters = 8;
    private readonly Mind _mind;
    private readonly ILogger? _logger;
    private readonly List<Func<SceneDecisionGroup?>> _gates = [];
    private readonly SceneDecisionRunner _runner;
    private Task<string?>? _premise;
    internal Func<ILoreQueryService?> LoreLoader { get; set; } = static () => Game.Instance.GetRequiredService<ILoreQueryService>();
    internal AttentionGazeTargetSelector? Gaze
    {
        get; set;
    }

    internal SubjectSceneDecisions(Mind mind, IDecisionClient client, ILogger? logger)
    {
        _mind = mind;
        _logger = logger;
        _runner = new(client, Capture, _gates);
    }

    internal void Register(Func<SceneDecisionGroup?> prepare)
    {
        _gates.Add(prepare);
        _premise ??= StartPremise();
    }

    internal void Unregister(Func<SceneDecisionGroup?> prepare)
    {
        _ = _gates.Remove(prepare);
        if (_gates.Count == 0)
        {
            _premise = null;
        }
    }

    internal void Pump(CancellationToken lifetime)
    {
        if (_gates.Count > 0 && _premise is { IsCompletedSuccessfully: true })
        {
            _ = _runner.EvaluateAsync(lifetime);
        }
    }


    private Task<string?> StartPremise()
    {
        try
        {
            return _mind is AgenticMind { ScenarioManager: { } manager }
                ? ResolveScenePremiseAsync(manager, AgenticMind.CreateCoreRenderContext(_mind.OwningCharacter,
                    _mind.GetCurrentSceneContext(), _mind.GetContextEligibleAttentionIDs())) : Task.FromResult<string?>(null);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Scene decision premise unavailable.");
            return Task.FromResult<string?>(null);
        }
    }

    private SceneDecisionSnapshot Capture()
    {
        IReadOnlyList<AgentObservation> timeline = _mind.GetObservationTimelineSnapshot();
        GazeOwnAction? action = Gaze?.CurrentOwnAction;
        string ownAction = _mind.HasActiveObservationWait && action?.Kind != GazeOwnActionKind.SpeakingTo
            ? ComposeOwnAction(timeline).Describe() : action?.Describe() ?? "idle";
        return new(_mind.OwningCharacter.FullId, _premise!.Result, ComposeSceneCharacters(_mind.GetCurrentSceneContext()), RenderRecentEvents(timeline),
            ownAction, Gaze?.AppliedCueValue ?? "none", Gaze?.AppliedDwellSeconds ?? 0);
    }

    private async Task<string?> ResolveScenePremiseAsync(
        ScenarioManager manager,
        IReadOnlyDictionary<string, object?> coreContext)
    {
        try
        {
            Scenario? scenario = await manager.GetCurrentScenario(coreContext).ConfigureAwait(false);
            string? description = scenario?.Description.Trim();
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Scene decision premise unavailable.");
            return null;
        }
    }

    private IReadOnlyList<SceneDecisionCharacter> ComposeSceneCharacters(ISceneContext scene)
    {
        ICharacter owner = _mind.OwningCharacter;
        ILoreQueryService? queryService = ResolveLoreQueryService();
        List<SceneDecisionCharacter> roster = [];
        foreach (ICharacter other in scene.Characters
                     .Where(static candidate => candidate is not null)
                     .OrderBy(static candidate => candidate.FullId, StringComparer.Ordinal))
        {
            if (ReferenceEquals(other, owner) || roster.Count >= MaximumSceneCharacters)
            {
                continue;
            }

            string? description = queryService is null
                ? null
                : CharacterLoreDescriptions.ResolveSynchronously(
                    queryService,
                    owner.FullId,
                    scene.Content,
                    other.FullId,
                    _logger);
            roster.Add(new SceneDecisionCharacter(other.FullId, description));
        }

        return roster;
    }

    private ILoreQueryService? ResolveLoreQueryService()
    {
        try
        {
            return LoreLoader();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private IReadOnlyList<string> RenderRecentEvents(IReadOnlyList<AgentObservation> timeline)
    {
        if (timeline.Count == 0)
        {
            return [];
        }

        List<AgentObservation> window = [.. timeline.Skip(Math.Max(0, timeline.Count - RecentEventCount))];
        // The renderer is a deterministic CPU-only canonical-text projection: every overload completes
        // synchronously, so the value is available without yielding off the Godot thread. Event times render
        // relative to this evaluation's composition moment.
        string rendered = new ObservationHistoryRenderer(_mind.OwningCharacter)
            .RenderAsync(window, timeline, _mind.GameClock.NowSeconds)
            .GetAwaiter()
            .GetResult();
        if (string.IsNullOrEmpty(rendered))
        {
            return [];
        }

        List<string> lines = [.. rendered.Split('\n')];
        int totalCharacters = lines.Sum(static line => line.Length);
        while (lines.Count > 1 && totalCharacters > RecentEventCharacterBudget)
        {
            totalCharacters -= lines[0].Length;
            lines.RemoveAt(0);
        }

        return lines;
    }

    private DecisionWaitOwnAction ComposeOwnAction(IReadOnlyList<AgentObservation> timeline)
        => _mind.TryGetAttendedSpeaker(out string? speakerFullId)
            ? new DecisionWaitOwnAction(
                DecisionWaitOwnActionKind.Hearing,
                speakerFullId,
                ResolveAddresseeStatus(speakerFullId, timeline))
            : DecisionWaitOwnAction.WaitingAction;

    /// <summary>
    /// Determines how the attended speaker's latest observed speech is addressed: declared to this character, to
    /// another, or undecided when no recipients were declared or no speech is retained.
    /// </summary>
    private DecisionWaitAddresseeStatus ResolveAddresseeStatus(string speakerFullId, IReadOnlyList<AgentObservation> timeline)
    {
        string ownerFullId = _mind.OwningCharacter.FullId;
        foreach (AgentObservation observation in timeline.Reverse())
        {
            if (observation is ObservedSpeech { ActorId: { } actor } speech
                && string.Equals(actor, speakerFullId, StringComparison.Ordinal))
            {
                return speech.Addressees.Contains(ownerFullId, StringComparer.Ordinal)
                    ? DecisionWaitAddresseeStatus.AddressedToMe
                    : speech.Addressees.Count > 0
                        ? DecisionWaitAddresseeStatus.AddressedToAnother
                        : DecisionWaitAddresseeStatus.Undecided;
            }
        }

        return DecisionWaitAddresseeStatus.Undecided;
    }

}
