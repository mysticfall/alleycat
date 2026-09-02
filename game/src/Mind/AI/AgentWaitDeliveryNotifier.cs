namespace AlleyCat.Mind.AI;

/// <summary>
/// Session-scoped notification sink for observation windows delivered through a wait result (AI-002 TR-41/57): the
/// wait result is the sole delivery channel for its window, so the session runtime learns the delivery here to
/// correlate pending keyed speech holds and settle them without a duplicate injected replacement.
/// </summary>
internal sealed class AgentWaitDeliveryNotifier(Action<IReadOnlyList<Observation.Observation>> notify)
{
    /// <summary>Reports one wait's delivered observation window in FIFO ingestion order.</summary>
    internal void NotifyDelivered(IReadOnlyList<Observation.Observation> delivered) => notify(delivered);
}
