namespace AlleyCat.Control;

/// <summary>
/// Supported locomotion turn modes.
/// </summary>
public enum TurnMode
{
    /// <summary>
    /// Applies discrete snap turns.
    /// </summary>
    Snap = 0,

    /// <summary>
    /// Applies continuous smooth turning.
    /// </summary>
    Smooth = 1,
}
