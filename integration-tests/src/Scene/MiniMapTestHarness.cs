using System.Globalization;
using System.Text.RegularExpressions;
using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// Construction and inspection helpers shared by the minimap integration suites. Every fixture builds a hermetic
/// scene root and renders through a renderer scoped to it, so leaked session state cannot change a verdict.
/// </summary>
public static partial class MiniMapTestHarness
{
    /// <summary>
    /// Creates a plain <see cref="AlleyCat.Character.Character" /> node hosting one marker, so the marker's identity
    /// labels and TR-19 overrides resolve to <c>char:&lt;id&gt;</c> through ordinary ancestry — no character-side
    /// changes (UR-2/TR-7).
    /// </summary>
    /// <param name="id">Character local id; the hosted marker's resolved <c>FullId</c> becomes <c>char:id</c>.</param>
    /// <param name="name">Character node name; deliberately independent of <paramref name="id" /> so shared-FullId
    /// hosts get distinct node paths.</param>
    /// <param name="marker">Marker parented under the character.</param>
    /// <param name="position">Character world position.</param>
    /// <returns>The hosting character, not yet inside the tree.</returns>
    public static AlleyCat.Character.Character CreateCharacterHost(string id, string name, MiniMapMarker marker, Vector3 position = default)
    {
        AlleyCat.Character.Character host = new()
        {
            Name = name,
            Id = id,
            Position = position
        };
        host.AddChild(marker);
        return host;
    }

    /// <summary>Adds <paramref name="root" /> to the session tree deferred and waits for markers to run <c>_Ready</c>.</summary>
    /// <param name="sceneTree">Active scene tree.</param>
    /// <param name="root">Fixture root to add.</param>
    /// <returns>Completion task.</returns>
    public static async Task AddToTreeAsync(SceneTree sceneTree, Node root)
    {
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        await Support.TestUtils.WaitForFramesAsync(sceneTree, 3);
    }

    /// <summary>Frees <paramref name="root" /> and lets the deletion flush so later renders cannot see it.</summary>
    /// <param name="sceneTree">Active scene tree.</param>
    /// <param name="root">Fixture root to free.</param>
    /// <returns>Completion task.</returns>
    public static async Task RemoveFromTreeAsync(SceneTree sceneTree, Node root)
    {
        root.QueueFree();
        await Support.TestUtils.WaitForFramesAsync(sceneTree, 3);
    }

    /// <summary>Creates a marker with ordered primitive children; primitives parent under the marker at identity.</summary>
    /// <param name="name">Marker node name.</param>
    /// <param name="drawOrder">Authored draw-order ordinal.</param>
    /// <param name="primitives">Primitives in authored collection order.</param>
    /// <returns>The marker, not yet inside the tree.</returns>
    public static MiniMapMarker CreateMarker(string name, int drawOrder = 0, params MiniMapPrimitive[] primitives)
    {
        MiniMapMarker marker = new()
        {
            Name = name,
            DrawOrder = drawOrder,
            Primitives = primitives
        };
        foreach (MiniMapPrimitive primitive in primitives)
        {
            marker.AddChild(primitive);
        }

        return marker;
    }

    /// <summary>The successful render's SVG text, asserting success ownership of the value.</summary>
    /// <param name="result">Render result.</param>
    /// <returns>Non-null SVG text.</returns>
    public static string Svg(MiniMapRenderResult result)
    {
        string? svg = result.Svg;
        Assert.NotNull(svg);
        return svg;
    }

    /// <summary>The successful render's rasterised image, asserting success ownership of the value.</summary>
    /// <param name="result">Render result.</param>
    /// <returns>Non-null image.</returns>
    public static Image Image(MiniMapRenderResult result)
    {
        Image? image = result.Image;
        Assert.NotNull(image);
        return image;
    }

    /// <summary>Creates a scoped renderer exactly like the editor preview driver: discovery limited to the fixture root.</summary>
    /// <param name="root">Fixture root.</param>
    /// <returns>Scoped renderer.</returns>
    public static MiniMapRenderer CreateScopedRenderer(Node root) => new(root, root);

    /// <summary>
    /// Creates a scoped renderer with the editor-tool-context seam forced on (TR-7/TA-6): diagnostics behave exactly as
    /// inside the Godot editor without launching it, because the harness cannot.
    /// </summary>
    /// <param name="root">Fixture root acting as the edited scene root.</param>
    /// <returns>Scoped renderer that captures as an editor tool.</returns>
    public static MiniMapRenderer CreateEditorPreviewRenderer(Node root)
        => new(root, root)
        {
            EditorToolContextDetector = static () => true
        };

    /// <summary>Expected image pixel for a world X/Z position under a render's declared viewport and scale.</summary>
    /// <param name="result">Render whose frame metadata is used.</param>
    /// <param name="world">World X/Z position.</param>
    /// <returns>Expected pixel position.</returns>
    public static Vector2 ExpectedPixel(MiniMapRenderResult result, Vector2 world) => (world - result.Viewport.Position) * result.Scale;

    /// <summary>Formats a coordinate the way fragments format numbers, for substring assertions.</summary>
    /// <param name="value">Coordinate value.</param>
    /// <returns>Invariant <c>G9</c> text.</returns>
    public static string FragmentNumber(float value) => (value == 0 ? 0 : value).ToString("G9", CultureInfo.InvariantCulture);

    /// <summary>All filled shape fragments in the SVG: fill colour plus pixel-space outline.</summary>
    /// <param name="svg">Rendered SVG text.</param>
    /// <returns>Parsed polygon fragments in paint order.</returns>
    public static IReadOnlyList<(string Fill, Vector2[] Points)> Polygons(string svg)
        => [.. PolygonRegex().Matches(svg).Select(match => (match.Groups[2].Value, ParsePoints(match.Groups[1].Value)))];

    /// <summary>Axis-aligned bounds of parsed pixel points.</summary>
    /// <param name="points">Pixel-space polygon points.</param>
    /// <returns>Minimum, maximum corner pair.</returns>
    public static (Vector2 Minimum, Vector2 Maximum) Bounds(Vector2[] points)
    {
        Vector2 minimum = points.Aggregate(Vector2.Inf, (current, point) => current.Min(point));
        Vector2 maximum = points.Aggregate(-Vector2.Inf, (current, point) => current.Max(point));
        return (minimum, maximum);
    }

    /// <summary>Arithmetic mean of parsed pixel points.</summary>
    /// <param name="points">Pixel-space polygon points.</param>
    /// <returns>Centroid position.</returns>
    public static Vector2 Centroid(Vector2[] points)
        => points.Aggregate(Vector2.Zero, (sum, point) => sum + point) / points.Length;

    /// <summary>Counts drawn grid lines per axis: vertical strokes on X multiples, horizontal strokes on Z multiples.</summary>
    /// <param name="svg">Rendered SVG text.</param>
    /// <returns>Vertical and horizontal grid-line counts.</returns>
    public static (int Vertical, int Horizontal) GridLineCounts(string svg) => (VerticalGridRegex().Matches(svg).Count, HorizontalGridRegex().Matches(svg).Count);

    private static Vector2[] ParsePoints(string points)
        => [.. points.Split(' ').Select(pair => pair.Split(',')).Select(parts => new Vector2(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture)))];

    [GeneratedRegex(@"<polygon points=""([^""]+)"" fill=""(#[0-9a-fA-F]{6})""/>", RegexOptions.CultureInvariant)]
    private static partial Regex PolygonRegex();

    [GeneratedRegex(@"<path d=""M[^""]*V[0-9.]+"" stroke=""#[0-9a-fA-F]{6}""/>", RegexOptions.CultureInvariant)]
    private static partial Regex VerticalGridRegex();

    [GeneratedRegex(@"<path d=""M0 [^""]*H[0-9.]+"" stroke=""#[0-9a-fA-F]{6}""/>", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalGridRegex();
}
