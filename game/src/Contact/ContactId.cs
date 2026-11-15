namespace AlleyCat.Contact;

/// <summary>
/// Stable identity of one raw contact pair — one sweeper's coalesced query shapes against one receiver body
/// (INTR-005 TR3, TR18b) — for the whole Begin/Update/End lifecycle. A pair observed again after its End is a
/// new contact and receives a new id; the identity never changes within a lifecycle.
/// </summary>
public readonly record struct ContactId(long Value)
{
    /// <summary>Returns a diagnostic rendering of the id.</summary>
    public override string ToString() => $"Contact {Value}";
}
