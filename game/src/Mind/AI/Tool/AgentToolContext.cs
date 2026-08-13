using AlleyCat.Character;
using AlleyCat.Scene;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Trusted, immutable context captured for one foreground agent turn.
/// </summary>
public sealed class AgentToolContext(ICharacter character, ISceneContext sceneContext)
{
    /// <summary>Gets the exact character that owns the active turn.</summary>
    public ICharacter Character { get; } = character ?? throw new ArgumentNullException(nameof(character));

    /// <summary>Gets the exact scene snapshot captured at turn start.</summary>
    public ISceneContext SceneContext { get; } = sceneContext ?? throw new ArgumentNullException(nameof(sceneContext));
}
