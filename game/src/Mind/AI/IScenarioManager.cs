namespace AlleyCat.Mind.AI;

/// <summary>
/// Resolves the scenario framing one character's current interactions for each foreground turn.
/// </summary>
public interface IScenarioManager
{
    /// <summary>
    /// Resolves the current scenario for a foreground turn.
    /// </summary>
    /// <param name="previous">
    /// The previous turn's trusted binding, lazily created with a null scenario when no previous context exists.
    /// </param>
    /// <returns>The scenario for the current turn, or null when no scenario is available.</returns>
    Scenario? GetCurrentScenario(ScenarioContext previous);
}
