namespace AlleyCat.Core;

/// <summary>
/// Represents an object that owns or aggregates components so callers can query its composed capabilities through a
/// stable contract. Holders define the authoritative component set for an entity or scene object without exposing
/// storage details.
/// </summary>
public interface IComponentHolder : IServiceProvider
{
    /// <summary>
    /// Gets the holder-defined component collection in deterministic iteration order.
    /// </summary>
    IReadOnlyList<IComponent> Components
    {
        get;
    }

    /// <summary>
    /// Resolves exactly one component assignable to <paramref name="serviceType"/>.
    /// </summary>
    /// <param name="serviceType">The requested component or capability type.</param>
    /// <returns>The single matching component, or null when no component matches.</returns>
    object? IServiceProvider.GetService(Type serviceType) => ComponentResolution.GetService(this, serviceType);
}
