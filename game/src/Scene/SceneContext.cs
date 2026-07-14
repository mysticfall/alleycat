using System.Collections.ObjectModel;
using AlleyCat.Character;
using AlleyCat.Core.Content;

namespace AlleyCat.Scene;

/// <summary>
/// Immutable scene context membership snapshot.
/// </summary>
public sealed record SceneContext : ISceneContext
{
    private readonly ICharacter[] _characters;
    private readonly ReadOnlyCollection<ICharacter> _charactersView;

    /// <summary>
    /// Initializes a new scene context with a fixed membership snapshot.
    /// </summary>
    /// <param name="characters">Characters currently participating in the scene.</param>
    /// <param name="content">Active content context for the scene.</param>
    public SceneContext(IEnumerable<ICharacter> characters, ContentContext? content = null)
    {
        ArgumentNullException.ThrowIfNull(characters);

        _characters = [.. characters];
        _charactersView = Array.AsReadOnly(_characters);
        Content = content ?? ContentContext.Default;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ICharacter> Characters => _charactersView;

    /// <inheritdoc />
    public ContentContext Content
    {
        get;
    }
}
