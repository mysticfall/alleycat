using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;

namespace AlleyCat.Scene;

/// <summary>
/// Exposes the current scene membership snapshot for character-aware gameplay systems.
/// </summary>
public interface ISceneContext
{
    /// <summary>
    /// Global Godot group identifying the player character node.
    /// </summary>
    const string PlayerGroupName = "Player";

    /// <summary>
    /// Gets the unordered character membership captured when this context was created.
    /// </summary>
    IReadOnlyCollection<ICharacter> Characters
    {
        get;
    }

    /// <summary>
    /// Gets the player character of the captured scene membership.
    /// </summary>
    /// <remarks>
    /// Scene authoring guarantees the player is present in production scene snapshots; player-less contexts are
    /// valid only in narrow test fixtures and fail on access.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The captured membership contains no player character.</exception>
    ICharacter Player
    {
        get;
    }

    /// <summary>
    /// Gets the active content context sourced from CORE content resolution.
    /// </summary>
    ContentContext Content
    {
        get;
    }

    /// <summary>
    /// Finds an identifiable object in the captured scene membership by its canonical full identity.
    /// </summary>
    /// <param name="fullId">Canonical identity in <c>Type:Id</c> form.</param>
    /// <returns>The live identifiable object, or <see langword="null"/> when it is absent or its type is unmapped.</returns>
    /// <exception cref="ArgumentException"><paramref name="fullId"/> is not a canonical identity.</exception>
    IIdentifiable? Find(string fullId);

    /// <summary>
    /// Resolves an identifiable object in the captured scene membership by its canonical full identity.
    /// </summary>
    /// <param name="fullId">Canonical identity in <c>Type:Id</c> form.</param>
    /// <returns>The live identifiable object.</returns>
    /// <exception cref="ArgumentException"><paramref name="fullId"/> is not a canonical identity.</exception>
    /// <exception cref="InvalidOperationException">The identity is absent or its type is unmapped.</exception>
    IIdentifiable Resolve(string fullId);
}
