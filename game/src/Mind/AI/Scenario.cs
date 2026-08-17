namespace AlleyCat.Mind.AI;

/// <summary>
/// Character-bound narrative and interaction context: the objectives and additional context framing one character's
/// current interactions.
/// </summary>
/// <remarks>
/// Instances are created at runtime by <see cref="IScenarioManager"/> implementations and are never
/// Godot-serialised. <paramref name="Description"/> is plain authored text and is never evaluated as a template.
/// </remarks>
/// <param name="Description">Plain authored scenario narrative.</param>
public sealed record Scenario(string Description)
{
    /// <summary>Gets the plain authored scenario narrative.</summary>
    public string Description { get; init; } = Description ?? throw new ArgumentNullException(nameof(Description));
}
