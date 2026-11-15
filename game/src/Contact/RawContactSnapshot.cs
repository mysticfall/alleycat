namespace AlleyCat.Contact;

/// <summary>
/// Recorded raw stream state for one active contact — the validation instrumentation surface of the raw layer
/// (INTR-005 TR4), sufficient to verify contact identity stability, lifecycle ordering, and the latest carried
/// measurements.
/// </summary>
/// <param name="Contact">Stable identity of the contact pair.</param>
/// <param name="Source">The contacting counterpart party.</param>
/// <param name="Receiver">The receiving body-part or surface party.</param>
/// <param name="LastSample">The most recent measurement set observed for this contact.</param>
public sealed record RawContactSnapshot(
    ContactId Contact,
    ContactParty Source,
    ContactParty Receiver,
    RawContactSample LastSample);
