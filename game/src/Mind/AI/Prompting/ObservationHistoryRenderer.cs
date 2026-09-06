using AlleyCat.Character;
using AlleyCat.Mind.Observation;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Renders ordered observation records through their type-owned canonical text for on-demand paths such as the
/// AI-002 <c>wait</c> and timeline history tools.
/// </summary>
internal sealed class ObservationHistoryRenderer(ICharacter character)
{
    /// <summary>Renders ordered observation records through their type-owned canonical text.</summary>
    /// <param name="observations">Observation records in timeline order.</param>
    /// <returns>The rendered event-history text for the supplied records.</returns>
    public ValueTask<string> RenderAsync(IReadOnlyList<AgentObservation> observations)
        => RenderAsync(observations, observations);

    /// <summary>Renders accepted entries while preserving their private transport for projection only.</summary>
    public ValueTask<string> RenderAsync(IReadOnlyList<AcceptedObservationEntry> observations)
        => RenderAsync(observations, observations);

    /// <summary>
    /// Renders a selected window using the full timeline to expand any selected grouped speech into its complete,
    /// currently-known model-facing utterance.
    /// </summary>
    public ValueTask<string> RenderAsync(
        IReadOnlyList<AgentObservation> observations,
        IReadOnlyList<AgentObservation> timeline)
        => RenderProjectedAsync(ContinuationProjection.Project(timeline, observations));

    /// <summary>Renders accepted entries while retaining their private transport for projection only.</summary>
    public ValueTask<string> RenderAsync(
        IReadOnlyList<AcceptedObservationEntry> observations,
        IReadOnlyList<AcceptedObservationEntry> timeline)
        => RenderProjectedAsync(ContinuationProjection.Project(timeline, observations));

    /// <summary>Projects a timeline or selected timeline window for model-facing callers that need event counting.</summary>
    internal static IReadOnlyList<ContinuationProjection.Event> Project(
        IReadOnlyList<AgentObservation> timeline,
        IReadOnlyList<AgentObservation>? selected = null)
        => ContinuationProjection.Project(timeline, selected);

    /// <summary>Projects accepted entries with private speech transport retained at the ingestion boundary.</summary>
    internal static IReadOnlyList<ContinuationProjection.Event> Project(
        IReadOnlyList<AcceptedObservationEntry> timeline,
        IReadOnlyList<AcceptedObservationEntry>? selected = null)
        => ContinuationProjection.Project(timeline, selected);

    /// <summary>Renders pre-projected model events through their type-owned canonical text.</summary>
    internal ValueTask<string> RenderProjectedAsync(IReadOnlyList<ContinuationProjection.Event> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(character);
        return ValueTask.FromResult(events.Count == 0
            ? string.Empty
            : string.Join(
                '\n',
                events.Select(@event => (@event.Observation ?? throw new ArgumentException(
                    "Events contain a null observation.", nameof(events))).Render(character))));
    }
}
