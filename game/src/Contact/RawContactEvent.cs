namespace AlleyCat.Contact;

/// <summary>
/// The raw contact lifecycle phases (INTR-005 TR3). Events for a given <see cref="ContactId" /> are ordered:
/// exactly one <see cref="Begin" />, zero or more <see cref="Update" />, then exactly one <see cref="End" />.
/// </summary>
public enum RawContactPhase
{
    /// <summary>The contact pair was first observed; the sample is the initial measurement set.</summary>
    Begin,

    /// <summary>The contact pair persists; the sample is the current measurement set.</summary>
    Update,

    /// <summary>The contact pair is gone; the sample is the final measurement set.</summary>
    End,
}

/// <summary>
/// One raw contact lifecycle event (INTR-005 TR3). The raw stream is monitor-internal by default; validation
/// instrumentation observes these events to verify contact identity stability, lifecycle ordering, and every
/// carried measurement (INTR-005 TR4, TR18a).
/// </summary>
/// <param name="Phase">Lifecycle phase of this event.</param>
/// <param name="Contact">Stable identity of the contact pair for the whole lifecycle.</param>
/// <param name="Source">The contacting counterpart party — whose hand, foot, or object caused the contact.</param>
/// <param name="Receiver">The receiving body-part or surface party.</param>
/// <param name="Sample">The objective measurement set carried by this event.</param>
public sealed record RawContactEvent(
    RawContactPhase Phase,
    ContactId Contact,
    ContactParty Source,
    ContactParty Receiver,
    RawContactSample Sample);
