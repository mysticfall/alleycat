using AlleyCat.Core;
using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.Character;

/// <summary>
/// Character root for the player role and its independently composed locomotion capability.
/// </summary>
[GlobalClass]
public partial class PlayerCharacter : Character, IPlayer
{
    /// <inheritdoc />
    protected override void ComposeComponents(List<IComponent> components)
    {
        components.Add(RequireComponentReference(Eyes, nameof(Eyes)));
        components.Add(RequireComponentReference(Locomotion, nameof(Locomotion)));
        if (Navigation is not null)
        {
            components.Add(Navigation);
        }

        components.Add(RequireComponentReference(Voice, nameof(Voice)));
        components.Add(RequireHandReference(LeftHand, LimbSide.Left, nameof(LeftHand)));
        components.Add(RequireHandReference(RightHand, LimbSide.Right, nameof(RightHand)));
    }
}
