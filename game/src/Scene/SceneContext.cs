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
    private readonly IReadOnlyDictionary<string, IIdentifiable[]> _identifiablesByType;

    /// <summary>
    /// Initializes a new scene context with a fixed membership snapshot.
    /// </summary>
    /// <param name="characters">Characters currently participating in the scene.</param>
    /// <param name="content">Active content context for the scene.</param>
    public SceneContext(IEnumerable<ICharacter> characters, ContentContext? content = null)
        : this(CreateCharacterMembership(characters), content)
    {
    }

    internal SceneContext(IReadOnlyDictionary<string, IIdentifiable[]> identifiablesByType, ContentContext? content = null)
    {
        ArgumentNullException.ThrowIfNull(identifiablesByType);

        _identifiablesByType = CopyMembership(identifiablesByType);
        _characters = _identifiablesByType.TryGetValue("char", out IIdentifiable[]? identifiables)
            ? [.. identifiables.Cast<ICharacter>()]
            : [];
        Dictionary<string, int> characterIDIndexes = new(StringComparer.Ordinal);
        for (int index = 0; index < _characters.Length; index++)
        {
            ICharacter character = _characters[index];
            try
            {
                IdentityValidator.Validate(character, nameof(identifiablesByType));
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    $"Scene context character at snapshot index {index} ('{character.GetType().FullName}') has invalid identity '{character.FullId}': {exception.Message}",
                    nameof(identifiablesByType),
                    exception);
            }

            if (!characterIDIndexes.TryAdd(character.FullId, index))
            {
                throw new ArgumentException(
                    $"Scene context characters at snapshot indexes {characterIDIndexes[character.FullId]} and {index} share duplicate exact character identity '{character.FullId}'. Character identities must be unique using ordinal, case-sensitive comparison.",
                    nameof(identifiablesByType));
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

    /// <inheritdoc />
    public IIdentifiable? Find(string fullId)
    {
        IdentityValidator.ValidateFullId(fullId, nameof(fullId));

        int typeSeparator = fullId.IndexOf(':', StringComparison.Ordinal);
        string type = fullId[..typeSeparator];
        if (!_identifiablesByType.TryGetValue(type, out IIdentifiable[]? identifiables))
        {
            return null;
        }

        foreach (IIdentifiable identifiable in identifiables)
        {
            if (string.Equals(identifiable.FullId, fullId, StringComparison.Ordinal))
            {
                return identifiable;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IIdentifiable Resolve(string fullId)
        => Find(fullId)
            ?? throw new InvalidOperationException($"Current scene does not contain identifiable object '{fullId}'.");

    private static IReadOnlyDictionary<string, IIdentifiable[]> CreateCharacterMembership(IEnumerable<ICharacter> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        return new Dictionary<string, IIdentifiable[]>(StringComparer.Ordinal)
        {
            ["char"] = [.. characters],
        };
    }

    private static IReadOnlyDictionary<string, IIdentifiable[]> CopyMembership(
        IReadOnlyDictionary<string, IIdentifiable[]> identifiablesByType)
    {
        var membership = new Dictionary<string, IIdentifiable[]>(identifiablesByType.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, IIdentifiable[]> entry in identifiablesByType)
        {
            ArgumentNullException.ThrowIfNull(entry.Value);
            membership.Add(entry.Key, [.. entry.Value]);
        }

        return membership.TryGetValue("char", out IIdentifiable[]? characters) && characters.Any(static identifiable => identifiable is not ICharacter)
            ? throw new ArgumentException("Character scene membership must contain only ICharacter objects.", nameof(identifiablesByType))
            : (IReadOnlyDictionary<string, IIdentifiable[]>)new ReadOnlyDictionary<string, IIdentifiable[]>(membership);
    }
}
