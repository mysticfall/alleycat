using Godot;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Godot-authorable base for scenario managers selectable on an <see cref="AgenticMind"/>.
/// </summary>
/// <remarks>
/// Interface-typed properties cannot be exported to the Godot inspector, so this abstract
/// <see cref="Resource"/> base mirrors the <c>ClientProvider</c> precedent: it carries the
/// <see cref="IScenarioManager"/> contract for inspector authoring while remaining replaceable in tests.
/// </remarks>
[GlobalClass]
public abstract partial class ScenarioManager : Resource, IScenarioManager
{
    /// <inheritdoc />
    public abstract Scenario? GetCurrentScenario(ScenarioContext previous);
}
