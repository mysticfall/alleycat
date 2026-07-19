using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Scene;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Minimal owning-character context for focused prompt integration tests.
/// </summary>
internal sealed class PromptOwnerCharacter(string id = "test-character") : ICharacter
{
    public string Id { get; set; } = id;

    public IReadOnlyList<IComponent> Components { get; } = [];

    public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, ICharacter? observer)
        => new Dictionary<string, object?>();
}
