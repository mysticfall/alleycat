using AlleyCat.Context;
using AlleyCat.Scene;
using Godot;

namespace AlleyCat.Character;

/// <summary>
/// Contributes the exact asset-owned character identity to character context.
/// </summary>
[GlobalClass]
public sealed partial class CharacterCardContextSource : ContextSource, IContextSource<ICharacter>
{
    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> GetContext(
        IContextual subject,
        ISceneContext scene,
        ICharacter? observer)
        => GetContext(RequireCompatibleSubject<ICharacter>(subject), scene, observer);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> GetContext(
        ICharacter subject,
        ISceneContext scene,
        ICharacter? observer)
        => new Dictionary<string, object?>
        {
            [nameof(ICharacter.Id)] = subject.Id,
        };
}
