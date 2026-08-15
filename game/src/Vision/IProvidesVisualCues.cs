namespace AlleyCat.Vision;

/// <summary>
/// Provides authoritative, provider-owned visual cues in deterministic order.
/// </summary>
public interface IProvidesVisualCues
{
    /// <summary>
    /// Gets the provider's published visual cues. The published topology remains immutable until the provider explicitly refreshes it.
    /// </summary>
    IReadOnlyList<VisualCue> VisualCues
    {
        get;
    }
}
