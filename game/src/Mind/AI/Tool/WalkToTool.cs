using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// The <c>walk_to</c> coordinate navigation tool (AI-015 TR-1–TR-3, TR-7): starts walking to absolute world X/Z
/// metres read off the minimap grid, optionally facing a compass bearing on arrival at a named pace. Composed by
/// AgenticMind only for navigation-capable subjects, bound to the subject's navigation through the session's
/// <see cref="CoordinateNavigationController" />; an authored instance without that binding fails clearly at use.
/// </summary>
[Tool]
[GlobalClass]
public sealed partial class WalkToTool : AgentTool
{
    /// <summary>Normative model-facing name (AI-015 TR-2).</summary>
    internal const string ProductionToolName = "walk_to";

    /// <summary>Normative model-facing description (AI-015 TR-3).</summary>
    internal const string ProductionToolDescription =
        "Start walking to a spot on the ground. Give the destination as absolute world coordinates read from the "
        + "minimap grid labels: `x` in metres (increases to the right on the map) and `z` in metres (increases "
        + "downwards on the map). Optional `heading` is the direction to face once you arrive, as a compass bearing "
        + "in degrees: 0 = up on the map (−Z), 90 = right (+X), 180 = down (+Z), 270 = left (−X), measured "
        + "clockwise; any finite value is normalised into [0, 360). Without `heading` you keep facing the way you "
        + "were walking when you arrive. Optional `pace` is `stroll`, `walk` (default) or `hurry`. Returns at once "
        + "when the walk has started (or with a refusal if the spot cannot be reached); you are told later when you "
        + "arrive or if the walk fails. A new `walk_to` replaces any walk already in progress. To face something you "
        + "can only judge from your destination, such as a person seen from where you will stand, leave `heading` "
        + "out and call `turn_to` after you arrive.";

    private readonly CoordinateNavigationToolFunctions? _functions;

    /// <summary>Creates the tool with its model-facing metadata for editor authoring.</summary>
    public WalkToTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = ProductionToolDescription;
    }

    /// <summary>Creates the tool bound to the session's navigation controller at AgenticMind composition.</summary>
    internal WalkToTool(CoordinateNavigationController controller) : this()
    {
        ArgumentNullException.ThrowIfNull(controller);
        _functions = new CoordinateNavigationToolFunctions(controller);
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate()
        => (_functions ?? throw new InvalidOperationException(
            "walk_to is composed by AgenticMind for navigation-capable subjects and cannot be authored unbound.")).WalkTo;
}
