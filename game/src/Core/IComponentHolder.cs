namespace AlleyCat.Core;

/// <summary>
/// Represents an object that owns or aggregates components so callers can query its composed capabilities through a
/// stable contract. Holders define the authoritative component set for an entity or scene object without exposing
/// storage details.
/// </summary>
public interface IComponentHolder
{
    /// <summary>
    /// Gets the holder-defined component collection in deterministic iteration order.
    /// </summary>
    IReadOnlyList<IComponent> Components
    {
        get;
    }
}
