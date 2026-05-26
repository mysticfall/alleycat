using AlleyCat.Core;

namespace AlleyCat.Body.Voice;

/// <summary>
/// Trait for objects that expose a composed voice capability.
/// </summary>
public interface IHasVoice : IComponentHolder
{
    /// <summary>
    /// Attempts to resolve exactly one voice component from this holder.
    /// </summary>
    bool TryGetVoice(out IVoice? voice) => this.TryGetComponent(out voice);

    /// <summary>
    /// Resolves the single voice component from this holder, or throws when unavailable.
    /// </summary>
    IVoice RequireVoice() => this.RequireComponent<IVoice>();
}
