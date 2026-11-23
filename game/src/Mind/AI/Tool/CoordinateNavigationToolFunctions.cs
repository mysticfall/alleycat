using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// The model-facing delegates of <c>walk_to</c>, <c>turn_to</c> and <c>stop</c> (AI-015 TR-2–TR-3), bound to one
/// session's <see cref="CoordinateNavigationController" />. The parameters are the only model inputs: the subject
/// binds at composition (AI-002 trusted binding), so no identity parameter exists, and no target-name parameter is
/// offered (reserved for the future name-based tool). Results are short texts with no observation and no image.
/// </summary>
internal sealed class CoordinateNavigationToolFunctions(CoordinateNavigationController controller)
{
    /// <summary>Normative <c>walk_to</c> <c>x</c> parameter text (AI-015 TR-3).</summary>
    internal const string WalkToXDescription = "Destination world X in metres, as labelled on the minimap grid.";

    /// <summary>Normative <c>walk_to</c> <c>z</c> parameter text (AI-015 TR-3).</summary>
    internal const string WalkToZDescription = "Destination world Z in metres, as labelled on the minimap grid.";

    /// <summary>Normative <c>walk_to</c> <c>heading</c> parameter text (AI-015 TR-3).</summary>
    internal const string WalkToHeadingDescription =
        "Optional compass bearing in degrees to face on arrival. It is the direction to face after arriving, judged "
        + "from the destination point on the map, not from where you stand now.";

    /// <summary>Normative <c>walk_to</c> <c>pace</c> parameter text (AI-015 TR-3).</summary>
    internal const string WalkToPaceDescription = "Optional walking pace.";

    /// <summary>Normative <c>turn_to</c> <c>heading</c> parameter text (AI-015 TR-3).</summary>
    internal const string TurnToHeadingDescription = "Compass bearing in degrees to face.";

    /// <summary>Starts walking to a ground spot; see <see cref="WalkToTool.ProductionToolDescription" />.</summary>
    public ValueTask<AgentToolResult> WalkTo(
        [Description(WalkToXDescription)] double x,
        [Description(WalkToZDescription)] double z,
        [Description(WalkToHeadingDescription)] double? heading = null,
        [Description(WalkToPaceDescription)]
        [AllowedValues(NavigationPace.Stroll, NavigationPace.Walk, NavigationPace.Hurry)]
        string? pace = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Complete(controller.WalkTo(x, z, heading, pace));
    }

    /// <summary>Starts a stationary turn; see <see cref="TurnToTool.ProductionToolDescription" />.</summary>
    public ValueTask<AgentToolResult> TurnTo(
        [Description(TurnToHeadingDescription)] double heading,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Complete(controller.TurnTo(heading));
    }

    /// <summary>Stops the current move; see <see cref="StopTool.ProductionToolDescription" />.</summary>
    public ValueTask<AgentToolResult> Stop(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Complete(controller.Stop());
    }

    private static ValueTask<AgentToolResult> Complete(CoordinateNavigationResponse response)
        => ValueTask.FromResult(new AgentToolResult(response.Message));
}
