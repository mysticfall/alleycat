using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Scene;
using AlleyCat.Vision;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Minimal owning-character context for focused prompt integration tests.
/// </summary>
internal sealed class PromptOwnerCharacter(string id = "test_character") : ICharacter
{
    public string Id { get; set; } = id;

    public IReadOnlyList<IComponent> Components { get; } = [];

    public IReadOnlyList<VisualCue> VisualCues { get; } = [];

    public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
        => new Dictionary<string, object?>();
}
