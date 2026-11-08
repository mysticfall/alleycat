using AlleyCat.Scene.MiniMap;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Read-only zero-argument visual-context tool that renders a top-down minimap of the calling NPC's surroundings,
/// always centred on the subject's live world position with the subject's own marker highlighted in gold and
/// labelled <c>*You*</c> (AI-013 TR-2/TR-3). The subject binds at AgenticMind composition through the trusted
/// session context; the model supplies no identity, position, distance, or framing input, and the renderer stays
/// character-agnostic (SCN-002 TR-4). Failures are contained error results (AI-013 TR-5).
/// </summary>
[Tool]
[GlobalClass]
public sealed partial class MinimapTool : AgentTool
{
    /// <summary>Model-facing name of the production minimap tool (AI-013 TR-2).</summary>
    internal const string ProductionToolName = "minimap";

    /// <summary>The shipped model-facing description (AI-013 TR-6): zero-argument, self-bound, read-only, ephemeral.</summary>
    internal const string ProductionToolDescription =
        "Render a top-down minimap of the area around you, always centred on your own current position, with your "
        + "own marker highlighted in gold and labelled `*You*`. Takes no arguments: the map always centres on you, "
        + "reaches as far as the service default, and its framing auto-scales to what is within that distance — you "
        + "cannot choose where it is centred or how far it reaches, because you cannot know what matters before "
        + "reading the map. Returns the rendered map image, plus a short textual description of its centre and "
        + "capture time. Reading the map observes nothing and changes nothing. A failed render returns an error "
        + "result instead of an image. The image is ephemeral: after your next scheduling turn it is replaced in "
        + "your context by a short textual record of the capture, so request a fresh minimap whenever you need "
        + "current visuals.";

    private readonly MiniMapRenderer? _renderer;
    private readonly ILogger<MinimapTool>? _logger;

    /// <summary>
    /// Creates a minimap tool with the default model-facing metadata for editor authoring. Composition binds the
    /// renderer through the internal constructor; an authored instance without it fails clearly at use.
    /// </summary>
    public MinimapTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = ProductionToolDescription;
    }

    /// <summary>
    /// Creates a minimap tool with the SCN-002 renderer service bound at the AgenticMind composition boundary
    /// (AI-013 TR-2/TR-3). A null renderer — the service unavailable — keeps the tool invocable and turns every
    /// call into a contained error result (AI-013 TR-5). The optional logger receives complete internal failure
    /// detail that must never reach the model.
    /// </summary>
    internal MinimapTool(MiniMapRenderer? renderer, ILogger<MinimapTool>? logger = null) : this()
    {
        _renderer = renderer;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => RenderMinimap;

    private ValueTask<AgentToolResult> RenderMinimap(
        ScenarioContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_renderer is null)
        {
            return ValueTask.FromResult(new AgentToolResult(
                "Minimap unavailable: the minimap renderer service is not available, so no image was returned. "
                + "Nothing about the scene or your session changed; a later call may succeed."));
        }

        VisualContextCaptureOutcome outcome = VisualContextCapture.TryRenderMinimap(
            _renderer,
            context.Character,
            Session?.Clock);
        return ValueTask.FromResult(
            outcome.Image is { } image
                ? new AgentToolResult($"Minimap delivered: {image.DescribeCapture()}.", image: image)
                : ComposeFailedResult(outcome.Failure));
    }

    private AgentToolResult ComposeFailedResult(string? failure)
    {
        _logger?.LogWarning(
            "The minimap tool failed to render visual context for subject '{SubjectId}': {Failure}.",
            Session?.Context.Character.FullId,
            failure ?? "the render failed");
        return new AgentToolResult(
            $"Minimap unavailable: the render failed — {failure ?? "the render failed"}. No image was returned. "
            + "Nothing about the scene or your session changed; a later call may succeed.");
    }
}
