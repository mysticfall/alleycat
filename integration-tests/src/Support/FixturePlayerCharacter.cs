using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Minimal node-based player character for focused scene and render-context integration tests.
/// </summary>
/// <remarks>
/// Joins <see cref="ISceneContext.PlayerGroupName" /> on construction so scene snapshots resolve it as the player.
/// Tests additionally add it to the Actors group and scene tree when the fixture captures membership through the
/// live scene-context provider.
/// </remarks>
internal sealed partial class FixturePlayerCharacter : Node, ICharacter
{
    public FixturePlayerCharacter()
    {
        AddToGroup(ISceneContext.PlayerGroupName);
    }

    public string Id { get; set; } = "fixture_player";

    public string FullId => $"char:{Id}";

    public IReadOnlyList<IComponent> Components { get; } = [];

    public IReadOnlyList<VisualCue> VisualCues { get; } = [];

    public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
}
