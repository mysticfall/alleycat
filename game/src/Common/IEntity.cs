namespace AlleyCat.Common;

/// <summary>
/// Represents a persistent object with a stable identity that can be stored, referenced, or restored across sessions.
/// </summary>
public interface IEntity
{
    /// <summary>
    /// Gets or sets the stable identifier for this persistent object.
    /// </summary>
    string Id
    {
        get; set;
    }
}
