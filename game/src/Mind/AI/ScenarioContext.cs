using AlleyCat.Character;
using AlleyCat.Scene;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Trusted, immutable turn binding captured for one foreground agent turn.
/// </summary>
public sealed class ScenarioContext(ICharacter character, ISceneContext sceneContext, Scenario? scenario = null)
{
    /// <summary>Gets the exact character that owns the active turn.</summary>
    public ICharacter Character { get; } = character ?? throw new ArgumentNullException(nameof(character));

    /// <summary>Gets the exact scene snapshot captured at turn start.</summary>
    public ISceneContext SceneContext { get; } = sceneContext ?? throw new ArgumentNullException(nameof(sceneContext));

    /// <summary>Gets the scenario resolved for the active turn, or null when none is available.</summary>
    public Scenario? Scenario { get; } = scenario;
}
