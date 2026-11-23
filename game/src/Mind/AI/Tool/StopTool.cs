using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// The <c>stop</c> coordinate navigation tool (AI-015 TR-1–TR-3, TR-9): cancels the current destination so the
/// character stands still where it is. Composed by AgenticMind only for navigation-capable subjects, bound to the
/// subject's navigation through the session's <see cref="CoordinateNavigationController" />.
/// </summary>
[Tool]
[GlobalClass]
public sealed partial class StopTool : AgentTool
{
    /// <summary>Normative model-facing name (AI-015 TR-2).</summary>
    internal const string ProductionToolName = "stop";

    /// <summary>Normative model-facing description (AI-015 TR-3).</summary>
    internal const string ProductionToolDescription =
        "Stop walking: cancels your current destination and you stand still where you are. Returns at once.";

    private readonly CoordinateNavigationToolFunctions? _functions;

    /// <summary>Creates the tool with its model-facing metadata for editor authoring.</summary>
    public StopTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = ProductionToolDescription;
    }

    /// <summary>Creates the tool bound to the session's navigation controller at AgenticMind composition.</summary>
    internal StopTool(CoordinateNavigationController controller) : this()
    {
        ArgumentNullException.ThrowIfNull(controller);
        _functions = new CoordinateNavigationToolFunctions(controller);
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate()
        => (_functions ?? throw new InvalidOperationException(
            "stop is composed by AgenticMind for navigation-capable subjects and cannot be authored unbound.")).Stop;
}
