using AlleyCat.Core;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Test-only character host with an empty component projection: a real <see cref="CharacterHub" /> for identity and
/// minimap-marker ancestry whose <see cref="CharacterHub.RefreshComponents" /> succeeds without the role-authored eyes,
/// voice and hands, so a mind's session can prepare against it.
/// </summary>
internal sealed partial class ComponentlessCharacterHost : CharacterHub
{
    /// <inheritdoc />
    protected override void ComposeComponents(List<IComponent> components)
    {
    }
}
