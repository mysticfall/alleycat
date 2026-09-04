namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Per-side observation of which hand-pose source a hand unambiguously proposes (XR-002 TR2).
/// </summary>
public enum XRHandSourceObservation
{
    /// <summary>
    /// The side unambiguously proposes the controller source.
    /// </summary>
    Controller = 0,

    /// <summary>
    /// The side unambiguously proposes the optical source.
    /// </summary>
    Optical = 1,

    /// <summary>
    /// The observation is ambiguous or missing; the committed mode is retained.
    /// </summary>
    Ambiguous = 2,
}
