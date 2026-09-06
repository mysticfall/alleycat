namespace AlleyCat.Mind.Observation;

/// <summary>
/// Scheduler data evaluated once when an observation is accepted by its owning Mind.
/// </summary>
/// <param name="Importance">Validated non-negative importance used only for scheduling.</param>
/// <param name="RequiresFreshTurn">Whether the accepted observation requests fresh-turn scheduling.</param>
public readonly record struct ObservationSchedulingMetadata(float Importance, bool RequiresFreshTurn);

/// <summary>
/// Immutable snapshot of one accepted observation-log entry.
/// </summary>
/// <remarks>
/// <see cref="IsRetained" /> is the entry's active-retention state at the instant this snapshot was created. A later
/// supersession or expiry produces a new snapshot state; it never mutates this payload, timestamp, sequence ID, or
/// scheduling metadata.
/// </remarks>
/// <param name="SequenceID">Mind-lifetime monotonic sequence ID. IDs are never reused.</param>
/// <param name="ObservedAt">Game-time timestamp captured at acceptance.</param>
/// <param name="Payload">Immutable stamped observation payload.</param>
/// <param name="Scheduling">Scheduling metadata evaluated exactly once at acceptance.</param>
/// <param name="IsRetained">Whether this entry is currently active retained evidence.</param>
public sealed record AcceptedObservationEntry(
    long SequenceID,
    double ObservedAt,
    Observation Payload,
    ObservationSchedulingMetadata Scheduling,
    bool IsRetained)
{
    /// <summary>Ingestion-only transport retained for session routing and never exposed to prompt rendering.</summary>
    internal SpeechObservationTransport? SpeechTransport
    {
        get;
        init;
    }
}

/// <summary>Kind of post-commit active-retention change published by Mind.</summary>
public enum RetainedObservationChangeKind
{
    /// <summary>An accepted entry became active retained evidence.</summary>
    Added,

    /// <summary>An active retained entry was superseded or expired without creating a new observation.</summary>
    Removed,
}

/// <summary>Payload-free of scheduler text, post-commit notification for a retained-log change.</summary>
/// <param name="Kind">Whether evidence entered or left active retention.</param>
/// <param name="Entry">Immutable entry state at the time of the change.</param>
public readonly record struct RetainedObservationChange(RetainedObservationChangeKind Kind, AcceptedObservationEntry Entry);
