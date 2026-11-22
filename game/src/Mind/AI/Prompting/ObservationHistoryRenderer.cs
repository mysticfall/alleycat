using AlleyCat.Character;
using AlleyCat.Mind.Observation;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Renders ordered observation records through their type-owned canonical text: the observation lines of the
/// runtime-authored synthetic scene-update exchange's tool result (AI-002 TR-18, AI-003 TR-8).
/// </summary>
internal sealed class ObservationHistoryRenderer(ICharacter character)
{
    /// <summary>Renders ordered observation records through their type-owned canonical text.</summary>
    /// <param name="observations">Observation records in timeline order.</param>
    /// <param name="nowSeconds">The composition moment's game time, so every event time states how long ago it
    /// happened relative to when the model reads it.</param>
    /// <returns>The rendered observation text for the supplied records.</returns>
    public ValueTask<string> RenderAsync(IReadOnlyList<AgentObservation> observations, double? nowSeconds = null)
        => RenderAsync(observations, observations, nowSeconds);

    /// <summary>Renders accepted entries while preserving their private transport for projection only.</summary>
    public ValueTask<string> RenderAsync(
        IReadOnlyList<AcceptedObservationEntry> observations,
        double? nowSeconds = null)
        => RenderAsync(observations, observations, nowSeconds);

    /// <summary>
    /// Renders a selected window using the full timeline to expand any selected grouped speech into its complete,
    /// currently-known model-facing utterance.
    /// </summary>
    public ValueTask<string> RenderAsync(
        IReadOnlyList<AgentObservation> observations,
        IReadOnlyList<AgentObservation> timeline,
        double? nowSeconds = null)
        => RenderProjectedAsync(ContinuationProjection.Project(timeline, observations), nowSeconds);

    /// <summary>Renders accepted entries while retaining their private transport for projection only.</summary>
    public ValueTask<string> RenderAsync(
        IReadOnlyList<AcceptedObservationEntry> observations,
        IReadOnlyList<AcceptedObservationEntry> timeline,
        double? nowSeconds = null)
        => RenderProjectedAsync(ContinuationProjection.Project(timeline, observations), nowSeconds);

    /// <summary>Renders pre-projected model events through their type-owned canonical text.</summary>
    internal ValueTask<string> RenderProjectedAsync(
        IReadOnlyList<ContinuationProjection.Event> events,
        double? nowSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(character);
        return ValueTask.FromResult(events.Count == 0
            ? string.Empty
            : string.Join('\n', events.Select(RenderLine)));

        string RenderLine(ContinuationProjection.Event @event)
        {
            AgentObservation observation = @event.Observation ?? throw new ArgumentException(
                "Events contain a null observation.", nameof(events));
            return observation.Render(character, nowSeconds);
        }
    }
}
