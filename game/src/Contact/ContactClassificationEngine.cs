namespace AlleyCat.Contact;

/// <summary>
/// The classification engine the monitor hosts (INTR-005 TR5, TR6): one
/// <see cref="ContactClassificationStateMachine" /> per active raw contact, routing raw lifecycle events to
/// their machine and surfacing classification transitions only — never per-tick output, and never anything on
/// contact End.
/// </summary>
/// <remarks>
/// The engine is pure C# so classification stays unit-testable below the Godot boundary; the monitor feeds it
/// the raw stream's events and combines its transitions with receiver payload slots to deliver
/// <see cref="ContactEvent" /> instances.
/// </remarks>
public sealed class ContactClassificationEngine
{
    private readonly ContactClassificationPolicy _policy;

    private readonly Dictionary<ContactId, ContactClassificationStateMachine> _machines = [];

    /// <summary>Creates an engine bound to one validated classification policy.</summary>
    /// <param name="policy">Validated classification tunables.</param>
    public ContactClassificationEngine(ContactClassificationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <summary>Number of contacts with a live state machine — one machine per active raw contact.</summary>
    public int ActiveContactCount => _machines.Count;

    /// <summary>
    /// Routes one raw lifecycle event to its contact's machine and returns the classification transition the
    /// event produced, or <see langword="null" /> when the class holds. Begin returns the entry transition;
    /// End releases the machine and returns <see langword="null" />.
    /// </summary>
    /// <param name="raw">The raw stream event to process.</param>
    /// <returns>The emitted transition, or <see langword="null" /> when no class changed.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the raw lifecycle is misrouted — a Begin for a live contact, or an Update or End for an
    /// unknown contact.
    /// </exception>
    public ContactClassificationTransition? Process(RawContactEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        switch (raw.Phase)
        {
            case RawContactPhase.Begin:
                if (_machines.ContainsKey(raw.Contact))
                {
                    throw new InvalidOperationException(
                        $"A state machine already exists for {raw.Contact}; a contact begins exactly once.");
                }

                ContactClassificationStateMachine machine =
                    new(_policy, raw.Contact, raw.Source, raw.Receiver);
                _machines.Add(raw.Contact, machine);
                return machine.Begin(raw.Sample);

            case RawContactPhase.Update:
                return ResolveMachine(raw.Contact).Update(raw.Sample);

            case RawContactPhase.End:
                ResolveMachine(raw.Contact).End();
                _ = _machines.Remove(raw.Contact);
                return null;

            default:
                throw new ArgumentOutOfRangeException(nameof(raw), raw.Phase, "Unknown raw contact phase.");
        }
    }

    private ContactClassificationStateMachine ResolveMachine(ContactId contact) =>
        _machines.TryGetValue(contact, out ContactClassificationStateMachine? machine)
            ? machine
            : throw new InvalidOperationException(
                $"No active state machine exists for {contact}; the raw lifecycle must deliver Begin before " +
                "Update or End.");
}
