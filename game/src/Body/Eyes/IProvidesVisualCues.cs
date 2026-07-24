namespace AlleyCat.Body.Eyes;

/// <summary>
/// Provides authored visual cues in deterministic order.
/// </summary>
public interface IProvidesVisualCues
{
    /// <summary>
    /// Gets the provider's authored visual cues.
    /// </summary>
    IReadOnlyList<VisualCue> VisualCues
    {
        get;
    }
}
