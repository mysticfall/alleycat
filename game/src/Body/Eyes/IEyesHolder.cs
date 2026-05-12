using AlleyCat.Core;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// Trait for objects that expose a composed eyes capability.
/// </summary>
public interface IEyesHolder : IComponentHolder
{
    /// <summary>
    /// Attempts to resolve exactly one eyes component from this holder.
    /// </summary>
    bool TryGetEyes(out IEyes? eyes) => this.TryGetComponent(out eyes);

    /// <summary>
    /// Resolves the single eyes component from this holder, or throws when unavailable.
    /// </summary>
    IEyes RequireEyes() => this.RequireComponent<IEyes>();
}
