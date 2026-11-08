using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Editor-only bridge that lets the spatial minimap addon compose through the production
/// <see cref="MiniMapRenderer" /> and element pipeline (SCN-002 TR-17): only the viewport driver inputs differ, so
/// the editor and the runtime render the same fragments for the same scene state. The editor has no DI game
/// singleton, so this bridge constructs a renderer scoped to the edited scene root and reuses it while that root is
/// open. It starts no gameplay, moves no nodes and writes no assets; the preview panel owns any image lifetime.
/// </summary>
[Tool]
public sealed partial class MiniMapPreviewDriver : RefCounted
{
    private Node? _rendererRoot;
    private MiniMapRenderer _renderer = null!;

    /// <summary>
    /// Renders the whole authored coverage of the edited scene: every marker in scope, inclusion distance effectively
    /// unbounded.
    /// </summary>
    /// <param name="sceneRoot">Edited scene root; discovery is scoped to its subtree.</param>
    /// <param name="width">Output width in pixels.</param>
    /// <param name="height">Output height in pixels.</param>
    /// <returns>Dictionary with <c>status</c>, <c>diagnostics</c>, <c>viewport</c>, <c>scale</c>, <c>grid_step</c> and, on success, <c>texture</c> and <c>svg</c>.</returns>
    public Godot.Collections.Dictionary RenderCoverage(Node sceneRoot, int width, int height)
        => Describe(RendererFor(sceneRoot).RenderCoverage(new MiniMapRenderSettings(Width: width, Height: height)));

    /// <summary>
    /// Renders an explicit world viewport (the preview's pan and zoom driver inputs) with unbounded inclusion so
    /// panning never drops authored markers.
    /// </summary>
    /// <param name="sceneRoot">Edited scene root; discovery is scoped to its subtree.</param>
    /// <param name="worldViewport">World X/Z rectangle to map to the image.</param>
    /// <param name="width">Output width in pixels.</param>
    /// <param name="height">Output height in pixels.</param>
    /// <returns>Dictionary with <c>status</c>, <c>diagnostics</c>, <c>viewport</c>, <c>scale</c>, <c>grid_step</c> and, on success, <c>texture</c> and <c>svg</c>.</returns>
    public Godot.Collections.Dictionary RenderViewport(Node sceneRoot, Rect2 worldViewport, int width, int height)
    {
        MiniMapViewportRequest request = new(
            worldViewport.GetCenter(),
            worldViewport,
            new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: width, Height: height));
        return Describe(RendererFor(sceneRoot).Render(request));
    }

    private MiniMapRenderer RendererFor(Node sceneRoot)
    {
        if (_rendererRoot != sceneRoot)
        {
            _renderer = new MiniMapRenderer(sceneRoot, sceneRoot);
            _rendererRoot = sceneRoot;
        }

        return _renderer;
    }

    private static Godot.Collections.Dictionary Describe(MiniMapRenderResult result)
    {
        string diagnostics = string.Join("\n", result.Diagnostics.Select(item => $"{item.Code} [{item.Reference}]: {item.Message}"));
        var description = new Godot.Collections.Dictionary
        {
            ["status"] = result.Status.ToString(),
            ["diagnostics"] = diagnostics,
            ["viewport"] = result.Viewport,
            ["scale"] = result.Scale,
            ["grid_step"] = result.GridStep,
        };

        if (result is { Status: MiniMapRenderStatus.Ready, Svg: { } svg, Image: { } image })
        {
            description["texture"] = ImageTexture.CreateFromImage(image);
            description["svg"] = svg;
        }

        return description;
    }
}
