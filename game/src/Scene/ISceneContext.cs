using AlleyCat.Character;

namespace AlleyCat.Scene;

/// <summary>
/// Exposes the current scene membership snapshot for character-aware gameplay systems.
/// </summary>
public interface ISceneContext
{
    /// <summary>
    /// Gets the unordered character membership captured when this context was created.
    /// </summary>
    IReadOnlyCollection<ICharacter> Characters
    {
        get;
    }
}
