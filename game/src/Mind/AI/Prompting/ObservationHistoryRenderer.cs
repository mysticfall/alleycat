using AlleyCat.Character;
using AlleyCat.Mind.Observation;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Renders ordered observation records through their type-owned canonical text for the per-request event-history
/// message.
/// </summary>
internal sealed class ObservationHistoryRenderer(ICharacter character)
{
    /// <summary>Prefix marking a timeline line as newly presented since the last confirmed response (AI-002 TR-3).</summary>
    private const string NewlyPresentedPrefix = "[new] ";

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

    /// <summary>
    /// Renders the complete accepted timeline for the per-request event-history section: every event beyond the
    /// confirmed watermark carries the newly-presented prefix on its complete rendered line, including any
    /// timestamp suffix (AI-002 TR-3).
    /// </summary>
    public ValueTask<string> RenderAsync(IReadOnlyList<AcceptedObservationEntry> timeline, long confirmedSequenceWatermark)
        => RenderProjectedAsync(ContinuationProjection.Project(timeline, confirmedSequenceWatermark: confirmedSequenceWatermark));

    /// <summary>Renders pre-projected model events through their type-owned canonical text.</summary>
    internal ValueTask<string> RenderProjectedAsync(IReadOnlyList<ContinuationProjection.Event> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(character);
        return ValueTask.FromResult(events.Count == 0
            ? string.Empty
            : string.Join(
                '\n',
                events.Select(RenderLine)));

        string RenderLine(ContinuationProjection.Event @event)
        {
            AgentObservation observation = @event.Observation ?? throw new ArgumentException(
                "Events contain a null observation.", nameof(events));
            return @event.IsNewlyPresented
                ? NewlyPresentedPrefix + observation.Render(character)
                : observation.Render(character);
        }
    }
}
