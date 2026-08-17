using Godot;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Scenario manager that returns one authored fixed description on every turn.
/// </summary>
[GlobalClass]
public partial class FixedScenarioManager : ScenarioManager
{
    /// <summary>
    /// Authored scenario narrative supplied verbatim as the description of every returned scenario.
    /// </summary>
    [Export(PropertyHint.MultilineText)]
    public string Description { get; set; } = string.Empty;

    /// <inheritdoc />
    public override Scenario? GetCurrentScenario(ScenarioContext previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        return string.IsNullOrWhiteSpace(Description)
            ? throw new InvalidOperationException($"{nameof(FixedScenarioManager)} requires a non-empty authored description.")
            : new Scenario(Description);
    }
}
