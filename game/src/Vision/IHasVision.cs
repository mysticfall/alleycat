using AlleyCat.Core;

namespace AlleyCat.Vision;

/// <summary>
/// Trait for objects that expose a composed eyes capability.
/// </summary>
public interface IHasVision : IComponentHolder
{
    /// <summary>
    /// Attempts to resolve exactly one eyes component from this holder.
    /// </summary>
    bool TryGetVision(out IVision? vision) => this.TryGetComponent(out vision);

    /// <summary>
    /// Resolves the single eyes component from this holder, or throws when unavailable.
    /// </summary>
    IVision RequireVision() => this.RequireComponent<IVision>();
}
