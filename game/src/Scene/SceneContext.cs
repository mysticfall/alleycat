using System.Collections.ObjectModel;
using AlleyCat.Character;
using AlleyCat.Core;
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
        Dictionary<string, int> characterIDIndexes = new(StringComparer.Ordinal);
        for (int index = 0; index < _characters.Length; index++)
        {
            ICharacter character = _characters[index];
            try
            {
                IdentityValidator.Validate(character, nameof(characters));
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    $"Scene context character at snapshot index {index} ('{character.GetType().FullName}') has invalid identity '{character.FullId}': {exception.Message}",
                    nameof(characters),
                    exception);
            }

            if (!characterIDIndexes.TryAdd(character.FullId, index))
            {
                throw new ArgumentException(
                    $"Scene context characters at snapshot indexes {characterIDIndexes[character.FullId]} and {index} share duplicate exact character identity '{character.FullId}'. Character identities must be unique using ordinal, case-sensitive comparison.",
                    nameof(characters));
            }
        }

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
