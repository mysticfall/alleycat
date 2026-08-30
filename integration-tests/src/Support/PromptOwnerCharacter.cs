using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Minimal owning-character fixture for focused prompt integration tests.
/// </summary>
internal sealed class PromptOwnerCharacter(string id = "test_character") : ICharacter
{
    public string Id { get; set; } = id;

    public IReadOnlyList<IComponent> Components { get; } = [];

    public IReadOnlyList<VisualCue> VisualCues { get; } = [];

    public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
}
