using AlleyCat.Character;
using AlleyCat.Scene;

namespace AlleyCat.Context;

/// <summary>
/// Describes a subject that can provide contextual information for the current scene.
/// </summary>
public interface IContextual
{
    /// <summary>
    /// Gets contextual information for this subject within the supplied scene and optional observer.
    /// </summary>
    /// <param name="scene">Current scene membership snapshot.</param>
    /// <param name="observer">Optional observing character.</param>
    /// <returns>Context entries keyed by stable field name.</returns>
    IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, ICharacter? observer);
}
