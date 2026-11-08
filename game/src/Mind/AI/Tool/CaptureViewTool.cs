using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Read-only zero-argument visual-context tool that captures one fresh still image of what the calling NPC
/// currently sees through its own eyes (AI-013 TR-2/TR-4). The subject binds at AgenticMind composition through
/// the trusted session context: the rig resolves through <see cref="Vision.IHasVision" /> from the
/// subject's own components, and the single <see cref="Vision.IVision.CaptureImage" /> call honours
/// VISION-001's bounded, side-effect-free one-shot contract. Failures are contained error results (AI-013 TR-5).
/// </summary>
[Tool]
[GlobalClass]
public sealed partial class CaptureViewTool : AgentTool
{
    /// <summary>Model-facing name of the production view-capture tool (AI-013 TR-2).</summary>
    internal const string ProductionToolName = "capture_view";

    /// <summary>The shipped model-facing description (AI-013 TR-6): zero-argument, self-bound, read-only, ephemeral.</summary>
    internal const string ProductionToolDescription =
        "Capture one fresh still image of what you currently see through your own eyes, from your own current "
        + "position and gaze. Takes no arguments: the view always comes from your own vision rig, and every call "
        + "captures a new frame — nothing is cached between calls. Returns the captured image with its capture "
        + "time. Capturing observes nothing, publishes no percepts, and changes nothing about you or the scene. A "
        + "character with no vision rig, or a failed capture, returns an error result instead of an image. The "
        + "image is ephemeral: after your next scheduling turn it is replaced in your context by a short textual "
        + "record of the capture, so capture a fresh view whenever you need current visuals.";

    private readonly ILogger<CaptureViewTool>? _logger;

    /// <summary>
    /// Creates a view-capture tool with the default model-facing metadata for editor authoring.
    /// </summary>
    public CaptureViewTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = ProductionToolDescription;
    }

    /// <summary>
    /// Creates a view-capture tool bound at the AgenticMind composition boundary (AI-013 TR-2/TR-4). The optional
    /// logger receives complete internal failure detail that must never reach the model.
    /// </summary>
    internal CaptureViewTool(ILogger<CaptureViewTool>? logger = null) : this()
    {
        _logger = logger;
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => CaptureView;

    private async ValueTask<AgentToolResult> CaptureView(
        ScenarioContext context,
        CancellationToken cancellationToken = default)
    {
        VisualContextCaptureOutcome outcome = await VisualContextCapture.TryCaptureViewAsync(
            context.Character,
            Session?.Clock,
            cancellationToken);
        return outcome.Image is { } image
            ? new AgentToolResult($"View delivered: {image.DescribeCapture()}.", image: image)
            : ComposeFailedResult(outcome.Failure);
    }

    private AgentToolResult ComposeFailedResult(string? failure)
    {
        _logger?.LogWarning(
            "The capture_view tool failed to capture visual context for subject '{SubjectId}': {Failure}.",
            Session?.Context.Character.FullId,
            failure ?? "the capture failed");
        return new AgentToolResult(
            $"View capture unavailable: {failure ?? "the capture failed"}. No image was returned. Nothing about "
            + "the scene or your session changed; a later call may succeed.");
    }
}
