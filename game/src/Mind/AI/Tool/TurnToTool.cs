using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// The <c>turn_to</c> coordinate navigation tool (AI-015 TR-1–TR-3, TR-8): turns on the spot to face a compass
/// bearing without walking. Composed by AgenticMind only for navigation-capable subjects, bound to the subject's
/// navigation through the session's <see cref="CoordinateNavigationController" />.
/// </summary>
[Tool]
[GlobalClass]
public sealed partial class TurnToTool : AgentTool
{
    /// <summary>Normative model-facing name (AI-015 TR-2).</summary>
    internal const string ProductionToolName = "turn_to";

    /// <summary>Normative model-facing description (AI-015 TR-3).</summary>
    internal const string ProductionToolDescription =
        "Turn on the spot to face a direction, without walking. `heading` is a compass bearing in degrees: 0 = up "
        + "on the map (−Z), 90 = right (+X), 180 = down (+Z), 270 = left (−X), measured clockwise; any finite value "
        + "is normalised into [0, 360). Returns at once when the turn has started.";

    private readonly CoordinateNavigationToolFunctions? _functions;

    /// <summary>Creates the tool with its model-facing metadata for editor authoring.</summary>
    public TurnToTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = ProductionToolDescription;
    }

    /// <summary>Creates the tool bound to the session's navigation controller at AgenticMind composition.</summary>
    internal TurnToTool(CoordinateNavigationController controller) : this()
    {
        ArgumentNullException.ThrowIfNull(controller);
        _functions = new CoordinateNavigationToolFunctions(controller);
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate()
        => (_functions ?? throw new InvalidOperationException(
            "turn_to is composed by AgenticMind for navigation-capable subjects and cannot be authored unbound.")).TurnTo;
}
