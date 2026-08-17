using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using Godot;

namespace AlleyCat.Scene;

/// <summary>
/// Immutable scene context membership snapshot.
/// </summary>
public sealed record SceneContext : ISceneContext
{
    private readonly ICharacter[] _characters;
    private readonly ReadOnlyCollection<ICharacter> _charactersView;
    private readonly IReadOnlyDictionary<string, IIdentifiable[]> _identifiablesByType;
    [SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Getter throws on player-less snapshots.")]
    private readonly ICharacter? _player;

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

        _player = ResolvePlayer(_characters);
        _charactersView = Array.AsReadOnly(_characters);
        Content = content ?? ContentContext.Default;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ICharacter> Characters => _charactersView;

    /// <inheritdoc />
    public ICharacter Player => _player
        ?? throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

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

    private static ICharacter? ResolvePlayer(ICharacter[] characters)
    {
        ICharacter? player = null;
        foreach (ICharacter character in characters)
        {
            if (character is not Node node || !node.IsInGroup(ISceneContext.PlayerGroupName))
            {
                continue;
            }

            if (player is not null)
            {
                throw new InvalidOperationException(
                    $"Scene context characters '{player.FullId}' and '{character.FullId}' are both in the '{ISceneContext.PlayerGroupName}' group. Scene authoring requires exactly one player character.");
            }

            player = character;
        }

        return player;
    }

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
