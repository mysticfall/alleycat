using AlleyCat.Core;

namespace AlleyCat.Speech;

/// <summary>Trait for objects that expose a composed hearing capability.</summary>
public interface IHasHearing : IComponentHolder
{
    /// <summary>Attempts to resolve exactly one hearing component from this holder.</summary>
    bool TryGetHearing(out IHearing? hearing) => this.TryGetComponent(out hearing);

    /// <summary>Resolves the single hearing component from this holder, or throws when unavailable.</summary>
    IHearing RequireHearing() => this.RequireComponent<IHearing>();
}
