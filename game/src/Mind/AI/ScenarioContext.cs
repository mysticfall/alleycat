using AlleyCat.Character;
using AlleyCat.Scene;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Trusted, immutable session binding captured once at session start and retained unchanged for the complete
/// session (AI-008 TR-5/7/8).
/// </summary>
public sealed class ScenarioContext(ICharacter character, ISceneContext sceneContext, Scenario? scenario = null)
{
    /// <summary>Gets the exact character that owns the session.</summary>
    public ICharacter Character { get; } = character ?? throw new ArgumentNullException(nameof(character));

    /// <summary>
    /// Gets the exact scene snapshot captured once at session start; the same snapshot is shared by the prompt
    /// render and every tool invocation of the session.
    /// </summary>
    public ISceneContext SceneContext { get; } = sceneContext ?? throw new ArgumentNullException(nameof(sceneContext));

    /// <summary>Gets the scenario resolved once at session start, or null when none is available.</summary>
    public Scenario? Scenario { get; } = scenario;
}
