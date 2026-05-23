namespace AlleyCat.Common;

/// <summary>
/// Represents an object that exposes reusable string metadata tags for discovery, filtering, or contextual grouping.
/// </summary>
public interface ITagged
{
    /// <summary>
    /// Gets the object's metadata tags as a read-only set so consumers can perform membership checks without relying on order.
    /// </summary>
    IReadOnlySet<string> Tags
    {
        get;
    }
}
