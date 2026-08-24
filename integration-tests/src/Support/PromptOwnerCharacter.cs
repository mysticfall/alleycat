using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Vision;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Minimal owning-character fixture for focused prompt integration tests.
/// </summary>
internal sealed class PromptOwnerCharacter(string id = "test_character") : ICharacter
{
    public string Id { get; set; } = id;

    public IReadOnlyList<IComponent> Components { get; } = [];

    public IReadOnlyList<VisualCue> VisualCues { get; } = [];
}
