namespace AlleyCat.Mind.Observation;

/// <summary>
/// Observer-relative addressee state that <see cref="ObservedSpeech"/> derives from its addressee set and actor
/// identity (AI-001 TR-14).
/// </summary>
public enum SpeechAddresseeState
{
    /// <summary>
    /// The observing character spoke itself. This state dominates every other signal and keeps self speech
    /// unchanged: importance zero, never a fresh turn, no attention effect.
    /// </summary>
    Self,

    /// <summary>The observer is in the addressee set: the speech was addressed to the observing character.</summary>
    AddressedToMe,

    /// <summary>The addressee set is non-empty but does not contain the observer: the speech was overheard.</summary>
    AddressedToAnother,

    /// <summary>
    /// The addressee is undecided: the set is empty — including every unknown-speaker case, because facing cannot
    /// be resolved without a character node.
    /// </summary>
    Undecided,
}
