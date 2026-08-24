namespace AlleyCat.Mind.AI;

/// <summary>
/// Resolves the scenario framing one character's current interactions once per agent session.
/// </summary>
public interface IScenarioManager
{
    /// <summary>
    /// Resolves the current scenario for a session.
    /// </summary>
    /// <param name="coreContext">
    /// The session's core render context: every reserved key except <c>scenario</c>, including the owning character
    /// and player context dictionaries the scenario body may template against.
    /// </param>
    /// <returns>A task resolving to the scenario for the session, or null when no scenario is available.</returns>
    ValueTask<Scenario?> GetCurrentScenario(IReadOnlyDictionary<string, object?> coreContext);
}
