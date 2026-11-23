using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Standalone minimap composition and rasterisation service (SCN-002 TR-9), resolved through CORE-004 dependency
/// injection. Discovers <see cref="MiniMapMarker" />s by live scene-tree group query at render time — never through
/// <c>ISceneContext</c> or character APIs — and gives characters no special handling (TR-4). Capture runs on the
/// Godot main thread in a single pass into immutable snapshots (TR-11); composition then paints background, the
/// adaptive world grid with metre labels, and the markers in ascending draw-order ordinal tie-broken by node path
/// (TR-12), producing byte-identical SVG for identical state and settings (TR-13).
/// </summary>
/// <param name="treeOwner">Node used to resolve the live scene tree.</param>
/// <param name="discoveryScope">
/// Optional subtree scope for discovery: when set, only markers inside it are discovered. The DI-registered instance
/// leaves this unset; the editor preview passes the edited scene root.
/// </param>
public sealed class MiniMapRenderer(Node treeOwner, Node? discoveryScope = null)
{
    /// <summary>
    /// Fixed minimum world extent guard in metres (TR-9/TR-10): the spec pins this at exactly 2 metres — a
    /// degenerate-zoom floor for genuinely sparse scenes (a single or collinear marker set) so the map never
    /// extreme-zooms onto a lone marker, and the extent a zero-marker render frames around the requested
    /// centre. Separating included geometry from the image edge is the per-request margin's job, not the
    /// guard's. Documented fixed guard — deliberately not a per-request setting.
    /// </summary>
    public const float MinimumWorldExtentMetres = 2f;

    /// <summary>
    /// TR-20 label displacement step in image pixels (documented tunable service constant — deliberately not a
    /// per-request setting, TR-9 is unchanged): one typical label row (about 24 px) plus a gap, so the first stack
    /// step alone separates a co-located pair, and because offsets live in pixel space they hold at any zoom.
    /// </summary>
    public const float LabelDisplacementStepPixels = 32f;

    /// <summary>
    /// TR-20 label displacement depth (documented tunable service constant — deliberately not a per-request
    /// setting): up to five labels stacked or fanned from one anchor, beyond the co-located pairs and triples real
    /// scenes produce, bounding the pass at 21 candidate positions per label.
    /// </summary>
    public const int LabelDisplacementDepth = 4;

    /// <summary>Documented output size bounds in pixels; requests outside them fail loudly with a diagnostic.</summary>
    public const int MaximumImageDimension = 16384;

    private const string BackgroundFill = "#f5f7fa";
    private const string GridStroke = "#c3cdd6";
    private const string GridZeroStroke = "#a8b4bd";
    private const string GridTextFill = "#33424e";
    private const int MaximumGridLinesPerAxis = 4096;

    /// <summary>
    /// Clear space in pixels around grid labels inside their gutters: between a label's ink and the image edge, and
    /// between the ink and the plot area.
    /// </summary>
    private const float GutterPaddingPixels = 6f;

    /// <summary>
    /// The gutters may take at most this fraction of either image dimension; a smaller image renders without
    /// gutters or grid labels (grid lines remain), reported by the informational <c>GridLabelsOmitted</c> notice.
    /// </summary>
    private const float MaximumGutterFraction = 0.25f;

    /// <summary>Maximum gutter-fitting passes; the left gutter only ever grows, so the passes are bounded.</summary>
    private const int MaximumGutterPasses = 4;

    /// <summary>Fixed id of the composed document's one internal clip path: the plot area.</summary>
    internal const string PlotClipID = "minimap-plot";

    private static readonly float[] _gridStepMultipliers = [1f, 2f, 5f];

    /// <summary>Documented service defaults; tuning values may change, their structure may not (TR-9).</summary>
    public MiniMapRendererDefaults Defaults { get; init; } = new();

    /// <summary>
    /// Editor-tool-context discriminator (TR-7): decides whether an unresolved identity label reports the runtime
    /// <c>MissingIdentity</c> error or the informational <c>IdentityResolvesAtRuntime</c> preview notice. Defaults to
    /// <see cref="Engine.IsEditorHint()" />; the production editor preview needs no override because it runs under the
    /// editor. Internal seam overridable by the integration tests, which cannot launch the Godot editor.
    /// </summary>
    internal Func<bool> EditorToolContextDetector { get; init; } = static () => Engine.IsEditorHint();

    /// <summary>
    /// Renders the area around <paramref name="centre" /> with the automatic viewport (SCN-002 UR-3/TR-10): markers
    /// within the inclusion distance of the centre are included, and the viewport is the bounding box of their
    /// projected footprints unioned with the centre, expanded by the margin, floored at the minimum world extent and
    /// widened along one axis to the image aspect ratio under one uniform scale.
    /// </summary>
    /// <param name="centre">Requested world X/Z centre in metres.</param>
    /// <param name="settings">Optional per-request settings overriding the service defaults, including TR-19 marker
    /// style overrides.</param>
    /// <returns>Ready result with SVG and image, or a loud failure carrying diagnostics.</returns>
    public MiniMapRenderResult Render(Vector2 centre, MiniMapRenderSettings? settings = null) => Guarded(() =>
    {
        ResolvedSettings resolved = ResolveSettings(settings);
        List<MiniMapDiagnostic> diagnostics = [];
        ImmutableArray<MiniMapMarkerSnapshot> markers = CaptureMarkers(centre, resolved, diagnostics);
        if (markers.IsEmpty)
        {
            diagnostics.Add(new MiniMapDiagnostic("ZeroMarkers", string.Empty,
                $"No markers are within {DescribeDistance(resolved.MaximumDistance)} of centre "
                + $"{DescribePoint(centre)}; rendered the background and grid over the default extent."));
        }

        Rect2 content = ContentBounds(markers, centre);
        return Compose(markers, plotSize => ComputeViewport(content, resolved, plotSize), resolved, diagnostics);
    });

    /// <summary>
    /// Renders an explicit world viewport through the same production composition — the editor preview's pan and zoom
    /// driver (TR-17). The request's centre still drives distance filtering.
    /// </summary>
    /// <param name="request">Explicit viewport request; its settings may carry TR-19 marker style overrides.</param>
    /// <returns>Ready result with SVG and image, or a loud failure carrying diagnostics.</returns>
    public MiniMapRenderResult Render(MiniMapViewportRequest request) => Guarded(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        ResolvedSettings resolved = ResolveSettings(request.Settings);
        Rect2 viewport = request.WorldViewport;
        if (!viewport.Position.IsFinite() || !viewport.Size.IsFinite() || viewport.Size.X <= 0 || viewport.Size.Y <= 0)
        {
            throw new MiniMapRenderException("InvalidViewport", string.Empty,
                $"The explicit world viewport must be finite and positive; received {DescribeRect(viewport)}.");
        }

        List<MiniMapDiagnostic> diagnostics = [];
        ImmutableArray<MiniMapMarkerSnapshot> markers = CaptureMarkers(request.Centre, resolved, diagnostics);
        return Compose(markers, _ => viewport, resolved, diagnostics);
    });

    /// <summary>
    /// Renders the whole authored coverage: every discoverable marker with the inclusion distance effectively
    /// unbounded, framed by the same margin, minimum-extent and aspect rules (TR-17's opening preview; also useful
    /// for runtime overviews).
    /// </summary>
    /// <param name="settings">Optional per-request settings overriding the service defaults, including TR-19 marker
    /// style overrides.</param>
    /// <returns>Ready result with SVG and image, or a loud failure carrying diagnostics.</returns>
    public MiniMapRenderResult RenderCoverage(MiniMapRenderSettings? settings = null) => Guarded(() =>
    {
        ResolvedSettings resolved = ResolveSettings(settings);
        List<MiniMapDiagnostic> diagnostics = [];
        ImmutableArray<MiniMapMarkerSnapshot> markers = CaptureMarkers(Vector2.Zero, resolved, diagnostics);
        if (markers.IsEmpty)
        {
            diagnostics.Add(new MiniMapDiagnostic("ZeroMarkers", string.Empty,
                "No markers are authored in the discovery scope; rendered the background and grid over the default extent."));
        }

        Rect2 content = ContentBounds(markers, null);
        return Compose(markers, plotSize => ComputeViewport(content, resolved, plotSize), resolved, diagnostics);
    });

    private static MiniMapRenderResult Guarded(Func<MiniMapRenderResult> render)
    {
        try
        {
            return render();
        }
        catch (MiniMapRenderException failure)
        {
            return MiniMapRenderResult.Failed([new MiniMapDiagnostic(failure.Code, failure.Reference, failure.Message)]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return MiniMapRenderResult.Failed([new MiniMapDiagnostic("RenderFailed", string.Empty, exception.Message)]);
        }
    }

    private sealed record ResolvedSettings(
        float MaximumDistance,
        int Width,
        int Height,
        float MarginMetres,
        int GridTextSizePixels,
        float GridMinimumSpacingPixels,
        IReadOnlyDictionary<string, MiniMapMarkerOverride>? MarkerOverrides);

    private ResolvedSettings ResolveSettings(MiniMapRenderSettings? settings)
    {
        MiniMapRendererDefaults defaults = Defaults;
        float maximumDistance = settings?.MaximumDistance ?? defaults.MaximumDistance;
        int width = settings?.Width ?? defaults.Width;
        int height = settings?.Height ?? defaults.Height;
        float marginMetres = settings?.MarginMetres ?? defaults.MarginMetres;
        int gridTextSize = settings?.GridTextSizePixels ?? defaults.GridTextSizePixels;
        float gridMinimumSpacing = settings?.GridMinimumSpacingPixels ?? defaults.GridMinimumSpacingPixels;
        IReadOnlyDictionary<string, MiniMapMarkerOverride>? markerOverrides = settings?.MarkerOverrides;

        if (!(float.IsPositiveInfinity(maximumDistance) || (float.IsFinite(maximumDistance) && maximumDistance > 0)))
        {
            throw new MiniMapRenderException("InvalidSetting", nameof(settings),
                $"MaximumDistance must be positive or float.PositiveInfinity; received {DescribeNumber(maximumDistance)}.");
        }

        if (width is < 16 or > MaximumImageDimension || height is < 16 or > MaximumImageDimension)
        {
            throw new MiniMapRenderException("InvalidSetting", nameof(settings),
                $"Output dimensions must each be 16–{MaximumImageDimension} pixels; received {width}x{height}.");
        }

        if (!(float.IsFinite(marginMetres) && marginMetres >= 0))
        {
            throw new MiniMapRenderException("InvalidSetting", nameof(settings),
                $"MarginMetres must be finite and non-negative; received {DescribeNumber(marginMetres)}.");
        }

        if (gridTextSize is < 4 or > 256)
        {
            throw new MiniMapRenderException("InvalidSetting", nameof(settings),
                $"GridTextSizePixels must be 4–256; received {gridTextSize}.");
        }

        if (!(float.IsFinite(gridMinimumSpacing) && gridMinimumSpacing >= 4))
        {
            throw new MiniMapRenderException("InvalidSetting", nameof(settings),
                $"GridMinimumSpacingPixels must be finite and at least 4; received {DescribeNumber(gridMinimumSpacing)}.");
        }

        ValidateMarkerOverrides(markerOverrides);
        return new ResolvedSettings(maximumDistance, width, height, marginMetres, gridTextSize, gridMinimumSpacing, markerOverrides);
    }

    /// <summary>
    /// TR-19/TR-16 settings validation: a null key or a null override entry fails the render loudly. An empty
    /// collection or <c>null</c> means no overrides and validates trivially.
    /// </summary>
    /// <param name="markerOverrides">Supplied overrides, if any.</param>
    private static void ValidateMarkerOverrides(IReadOnlyDictionary<string, MiniMapMarkerOverride>? markerOverrides)
    {
        if (markerOverrides is not { Count: > 0 })
        {
            return;
        }

        foreach (KeyValuePair<string, MiniMapMarkerOverride> entry in markerOverrides)
        {
            if (entry.Key is null)
            {
                throw new MiniMapRenderException("InvalidSetting", "settings",
                    "MarkerOverrides keys must be exact non-null FullId strings; received a null key.");
            }

            if (entry.Value is null)
            {
                throw new MiniMapRenderException("InvalidSetting", "settings",
                    $"MarkerOverrides entries must be non-null override records; received a null entry for key '{entry.Key}'.");
            }
        }
    }

    private ImmutableArray<MiniMapMarkerSnapshot> CaptureMarkers(
        Vector2 filterCentre,
        ResolvedSettings settings,
        List<MiniMapDiagnostic> diagnostics)
    {
        if (!GodotThread.IsMainThread())
        {
            throw new MiniMapRenderException("OffMainThread", string.Empty,
                "Minimap capture must run on the Godot main thread; marshal the request and retry.");
        }

        SceneTree tree = treeOwner.GetTree()
            ?? throw new MiniMapRenderException("NoSceneTree", string.Empty,
                $"The renderer's tree owner '{treeOwner.Name}' is not inside a scene tree.");

        // Every diagnostic node path of this render is relative to one scope root (TR-16): the discovery scope when
        // set — the edited scene root for the editor preview — else the tree root, so paths never traverse host UI
        // hierarchies. The single context also keeps the TR-12 tie-break on one consistent path form per render.
        MiniMapCaptureContext context = new(discoveryScope ?? tree.Root, EditorToolContextDetector(), settings.MarkerOverrides);

        // TR-19: override keys are matched on group membership considered before the distance filter, so while
        // overrides are supplied every discovered marker's identity resolves — a distance-excluded match still
        // consumes its key. Without overrides the set stays null and this extra resolution pass is skipped entirely,
        // keeping the no-override capture path byte-identical to the pre-feature render (TR-13).
        HashSet<string>? matchedOverrideKeys = settings.MarkerOverrides is { Count: > 0 } ? [] : null;

        var markers = new List<MiniMapMarkerSnapshot>();
        foreach (Node node in tree.GetNodesInGroup(MiniMapMarker.GroupName))
        {
            // Live group query at render time (TR-4/TR-11): freed or queued-for-deletion markers are excluded.
            if (node is not MiniMapMarker marker || !GodotObject.IsInstanceValid(marker) || marker.IsQueuedForDeletion() || !marker.IsInsideTree())
            {
                continue;
            }

            if (discoveryScope is { } scope && marker != scope && !scope.IsAncestorOf(marker))
            {
                continue;
            }

            MiniMapIdentityCapture? identity = null;
            if (matchedOverrideKeys is not null)
            {
                identity = marker.ResolveIdentity(context);
                if (identity.FullId is { } fullId && settings.MarkerOverrides!.ContainsKey(fullId))
                {
                    _ = matchedOverrideKeys.Add(fullId);
                }
            }

            // Inclusion is measured on the X/Z plane from the requested centre to the marker's world origin (TR-10);
            // overrides never change it.
            Vector2 origin = new(marker.GlobalPosition.X, marker.GlobalPosition.Z);
            if ((origin - filterCentre).Length() > settings.MaximumDistance)
            {
                continue;
            }

            markers.Add(identity is null ? marker.CaptureSnapshot(context) : marker.CaptureSnapshot(context, identity));
        }

        if (matchedOverrideKeys is not null)
        {
            // TR-16/TR-19: a key matching no discovered marker is an informational notice, never a failure.
            foreach (string unusedKey in settings.MarkerOverrides!.Keys
                         .Where(key => !matchedOverrideKeys.Contains(key))
                         .Order(StringComparer.Ordinal))
            {
                diagnostics.Add(new MiniMapDiagnostic("UnusedOverride", unusedKey,
                    $"The marker override key '{unusedKey}' matched no discovered marker; the render continues without it."));
            }
        }

        markers.Sort(static (left, right) => left.DrawOrder != right.DrawOrder
            ? left.DrawOrder.CompareTo(right.DrawOrder)
            : string.CompareOrdinal(left.NodePath, right.NodePath));
        return [.. markers];
    }

    private static Rect2 ContentBounds(ImmutableArray<MiniMapMarkerSnapshot> markers, Vector2? centre)
    {
        Vector2 minimum = Vector2.Zero;
        Vector2 maximum = Vector2.Zero;
        bool any = false;
        void Include(Vector2 point)
        {
            minimum = any ? minimum.Min(point) : point;
            maximum = any ? maximum.Max(point) : point;
            any = true;
        }

        if (centre is { } requested)
        {
            Include(requested);
        }

        foreach (MiniMapMarkerSnapshot marker in markers)
        {
            foreach (IMiniMapElement primitive in marker.Primitives)
            {
                switch (primitive)
                {
                    case MiniMapShapeSnapshot shape:
                        foreach (Vector2 point in shape.Polygon)
                        {
                            Include(point);
                        }

                        break;
                    case MiniMapLabelSnapshot label:
                        // Labels contribute their anchor point; markers with no geometry contribute no extent (TR-10).
                        Include(label.Anchor);
                        break;
                    default:
                        break;
                }
            }
        }

        return any ? new Rect2(minimum, maximum - minimum) : new Rect2(-Vector2.One * (MinimumWorldExtentMetres / 2), Vector2.One * MinimumWorldExtentMetres);
    }

    private static Rect2 ComputeViewport(Rect2 content, ResolvedSettings settings, Vector2 plotSize)
    {
        Rect2 grown = content.Grow(settings.MarginMetres);
        Vector2 centre = grown.GetCenter();
        Vector2 size = new(
            MathF.Max(grown.Size.X, MinimumWorldExtentMetres),
            MathF.Max(grown.Size.Y, MinimumWorldExtentMetres));

        // Widen exactly one axis to the plot area's aspect ratio; one uniform scale, never distortion (TR-10).
        float target = plotSize.X / plotSize.Y;
        float ratio = size.X / size.Y;
        size = ratio < target ? new Vector2(size.Y * target, size.Y) : new Vector2(size.X, size.X / target);
        return new Rect2(centre - (size / 2), size);
    }

    /// <summary>
    /// The resolved render layout: the reserved grid-label gutters, the plot area inside them, the world viewport
    /// mapped onto the plot area, its uniform scale and the adaptive grid step.
    /// </summary>
    private sealed record GridLayout(float LeftGutter, float TopGutter, Rect2 PlotArea, Rect2 Viewport, float Scale, float GridStep);

    /// <summary>
    /// Lays the image out as a reserved top gutter for X labels, a reserved left gutter for Z labels, and the plot
    /// area inside them that the world viewport projects onto. The top gutter is one label row plus padding; the
    /// left gutter fits the widest Z label the layout draws, so it is fitted iteratively: each pass projects with
    /// the current width and widens it when a label needs more, never narrowing, for at most
    /// <see cref="MaximumGutterPasses" /> passes. The adaptive step (TR-14) applies to the plot area's scale.
    /// </summary>
    private static GridLayout ResolveLayout(Func<Vector2, Rect2> viewportFor, ResolvedSettings settings)
    {
        float top = settings.GridTextSizePixels + (2 * GutterPaddingPixels);
        float left = MathF.Ceiling(MiniMapGlyphs.InkBounds("-00", settings.GridTextSizePixels).Size.X + (2 * GutterPaddingPixels));
        if (top > settings.Height * MaximumGutterFraction || left > settings.Width * MaximumGutterFraction)
        {
            return LayoutFor(viewportFor, settings, 0, 0);
        }

        GridLayout layout = LayoutFor(viewportFor, settings, left, top);
        for (int pass = 1; pass < MaximumGutterPasses; pass++)
        {
            float needed = MathF.Ceiling(WidestZLabel(layout, settings) + (2 * GutterPaddingPixels));
            if (needed <= left)
            {
                break;
            }

            left = needed;
            if (left > settings.Width * MaximumGutterFraction)
            {
                return LayoutFor(viewportFor, settings, 0, 0);
            }

            layout = LayoutFor(viewportFor, settings, left, top);
        }

        return layout;
    }

    private static GridLayout LayoutFor(Func<Vector2, Rect2> viewportFor, ResolvedSettings settings, float left, float top)
    {
        Rect2 plot = new(left, top, settings.Width - left, settings.Height - top);
        Rect2 viewport = viewportFor(plot.Size);
        float scale = Math.Min(plot.Size.X / viewport.Size.X, plot.Size.Y / viewport.Size.Y);
        return new GridLayout(left, top, plot, viewport, scale, SelectGridStep(scale, settings));
    }

    private static float WidestZLabel(GridLayout layout, ResolvedSettings settings)
    {
        float widest = 0;
        (long first, long last) = GridRange(layout.Viewport.Position.Y, layout.Viewport.End.Y, layout.GridStep);
        for (long z = first; z <= last; z++)
        {
            string text = DescribeNumber((float)(z * (double)layout.GridStep));
            widest = MathF.Max(widest, MiniMapGlyphs.InkBounds(text, settings.GridTextSizePixels).Size.X);
        }

        return widest;
    }

    private static MiniMapRenderResult Compose(
        ImmutableArray<MiniMapMarkerSnapshot> markers,
        Func<Vector2, Rect2> viewportFor,
        ResolvedSettings settings,
        List<MiniMapDiagnostic> diagnostics)
    {
        GridLayout layout = ResolveLayout(viewportFor, settings);
        if (layout.LeftGutter == 0)
        {
            diagnostics.Add(new MiniMapDiagnostic("GridLabelsOmitted", string.Empty,
                $"A {settings.Width}x{settings.Height} image is too small for grid-label gutters; rendered grid lines without labels."));
        }

        Rect2 plot = layout.PlotArea;
        var projection = new MiniMapProjection(
            layout.Viewport, layout.Scale, new Vector2I(settings.Width, settings.Height), plot, diagnostics);

        var svg = new StringBuilder();
        _ = svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{settings.Width}\" height=\"{settings.Height}\" "
            + $"viewBox=\"0 0 {settings.Width} {settings.Height}\">");
        _ = svg.Append($"<rect x=\"0\" y=\"0\" width=\"{settings.Width}\" height=\"{settings.Height}\" fill=\"{BackgroundFill}\"/>");
        AppendGrid(svg, projection, layout, settings);
        // Marker labels place through the deterministic TR-20 pass before composition; grid labels never
        // participate — their spacing is TR-14's adaptive rule alone, and they live in the gutters, which the
        // placement treats as outside its bounds.
        PlaceMarkerLabels(markers, projection);
        // Markers paint in ascending draw order, tie-broken by node path; primitives in collection order (TR-12).
        // Displacement moves only a label's rendered position — painting order is untouched (TR-20). All marker
        // ink is clipped to the plot area through the document's single internal clip path (nested SVG viewports
        // do not clip in the in-process rasteriser), so geometry beyond the world viewport never paints under a
        // grid label. Marker fragments themselves stay reference-free (TR-5).
        _ = svg.Append($"<clipPath id=\"{PlotClipID}\"><rect x=\"{MiniMapGlyphs.Number(plot.Position.X)}\" "
            + $"y=\"{MiniMapGlyphs.Number(plot.Position.Y)}\" width=\"{MiniMapGlyphs.Number(plot.Size.X)}\" "
            + $"height=\"{MiniMapGlyphs.Number(plot.Size.Y)}\"/></clipPath><g clip-path=\"url(#{PlotClipID})\">");
        foreach (MiniMapMarkerSnapshot marker in markers)
        {
            _ = svg.Append(marker.BuildSvgFragment(projection));
        }

        _ = svg.Append("</g></svg>");
        string source = svg.ToString();

        var image = new Image();
        return image.LoadSvgFromString(source) != Error.Ok || image.GetWidth() != settings.Width || image.GetHeight() != settings.Height
            ? throw new MiniMapRenderException("RasterisationFailed", string.Empty,
                "In-process SVG rasterisation failed or returned incorrect dimensions.")
            : new MiniMapRenderResult(MiniMapRenderStatus.Ready, source, image, layout.Viewport, layout.Scale, layout.GridStep, projection.CollectDiagnostics())
            {
                PlotArea = plot,
            };
    }

    /// <summary>
    /// The deterministic single-pass TR-20 label placement, run after the grid and before marker composition:
    /// gathers every marker label that still renders glyphs after identity resolution and glyph conversion — both
    /// flavours and <c>LabelText</c>-overridden text alike — and records each label's placement outcome on the
    /// projection. Labels omitted earlier (<c>MissingIdentity</c>, <c>IdentityResolvesAtRuntime</c>) and empty text
    /// own no box and stay absent; a label whose glyphs the fallback chain rejects is omitted here with the
    /// <c>UnsupportedGlyph</c> diagnostic its fragment later reports. Every included marker's projected shape
    /// primitives feed the placement's shape-ink boxes — the same footprint boxes that computed TR-10's viewport
    /// extent — which a candidate label box must clear unless the shape belongs to the label's own marker. Grid
    /// lines and grid labels never participate (TR-14). Placement never alters the viewport, extent, inclusion or
    /// draw order: extent is still computed from authored anchors (TR-10).
    /// </summary>
    /// <param name="markers">Included marker snapshots in TR-12 paint order.</param>
    /// <param name="projection">Projection carrying the placement outcomes into composition.</param>
    private static void PlaceMarkerLabels(ImmutableArray<MiniMapMarkerSnapshot> markers, MiniMapProjection projection)
    {
        List<MiniMapLabelPlacementRequest> requests = [];
        List<MiniMapShapeInkBox> shapeInkBoxes = [];
        foreach (MiniMapMarkerSnapshot marker in markers)
        {
            for (int index = 0; index < marker.Primitives.Length; index++)
            {
                switch (marker.Primitives[index])
                {
                    case MiniMapShapeSnapshot shape:
                        // One ink box per projected shape primitive (TR-20 shape clearance): the axis-aligned
                        // pixel bounding box of the same polygon points that fed TR-10's content extent.
                        shapeInkBoxes.Add(new MiniMapShapeInkBox(marker.NodePath, BoundsOf(shape.Polygon, projection)));
                        break;
                    case MiniMapLabelSnapshot label when label.Text is { } text && !string.IsNullOrWhiteSpace(text):
                        try
                        {
                            Rect2 baseBox = MiniMapProjection.TextInkBounds(text, projection.WorldToPixel(label.Anchor), label.TextSizePixels);
                            requests.Add(new MiniMapLabelPlacementRequest(
                                label.PrimitivePath, label.MarkerPath, marker.MatchedOverride, marker.DrawOrder, marker.NodePath, index, baseBox));
                        }
                        catch (InvalidOperationException exception)
                        {
                            // Glyph conversion failed (TR-15): the whole label is omitted and inert in placement.
                            projection.SetLabelPlacement(label.PrimitivePath, new MiniMapLabelPlacement.Omitted("UnsupportedGlyph", exception.Message));
                        }

                        break;
                    default:
                        break;
                }
            }
        }

        foreach (KeyValuePair<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> entry in MiniMapLabelPlacer.Place(
                     requests, shapeInkBoxes, projection.PlotArea, LabelDisplacementStepPixels, LabelDisplacementDepth))
        {
            projection.SetLabelPlacement(entry.Key.PrimitivePath, entry.Value);
        }
    }

    private static Rect2 BoundsOf(ImmutableArray<Vector2> polygon, MiniMapProjection projection)
    {
        Vector2 minimum = Vector2.Inf;
        Vector2 maximum = -Vector2.Inf;
        foreach (Vector2 point in polygon)
        {
            Vector2 pixel = projection.WorldToPixel(point);
            minimum = minimum.Min(pixel);
            maximum = maximum.Max(pixel);
        }

        return new Rect2(minimum, maximum - minimum);
    }

    private static float SelectGridStep(float scale, ResolvedSettings settings)
    {
        // Smallest 1, 2, 5 × 10^n metre step whose on-image spacing meets the minimum (TR-14).
        double target = settings.GridMinimumSpacingPixels / scale;
        double power = Math.Pow(10, Math.Floor(Math.Log10(target)));
        for (int attempt = 0; attempt < 80 && double.IsFinite(power); attempt++, power *= 10)
        {
            foreach (float multiplier in _gridStepMultipliers)
            {
                double step = multiplier * power;
                if (step >= target)
                {
                    return (float)step;
                }
            }
        }

        throw new MiniMapRenderException("GridStepUnavailable", string.Empty,
            "No finite 1,2,5×10^n grid step meets the minimum spacing for this viewport.");
    }

    private static (long First, long Last) GridRange(float start, float end, float gridStep)
        => ((long)Math.Ceiling(start / gridStep), (long)Math.Floor(end / gridStep));

    /// <summary>
    /// Draws the world-zero-anchored grid (TR-14): lines span the plot area, and each line's number label sits in
    /// its reserved gutter — X labels centred in the top band, Z labels centred in the left band.
    /// </summary>
    /// <remarks>
    /// Deterministic label rule: an X label draws only when its ink lies fully right of the left gutter and inside
    /// the image, and a Z label only when its ink lies fully below the top gutter and inside the image, so the shared
    /// top-left corner cell is always empty and no X label can meet a Z label. Along each axis, labels are visited in
    /// ascending coordinate order and a label whose ink would overlap the previously drawn one on that axis is
    /// skipped. Lines are never skipped.
    /// </remarks>
    private static void AppendGrid(StringBuilder svg, MiniMapProjection projection, GridLayout layout, ResolvedSettings settings)
    {
        float gridStep = layout.GridStep;
        Rect2 plot = layout.PlotArea;
        bool labelled = layout.LeftGutter > 0;
        int size = settings.GridTextSizePixels;
        (long firstX, long lastX) = GridRange(projection.Viewport.Position.X, projection.Viewport.End.X, gridStep);
        (long firstZ, long lastZ) = GridRange(projection.Viewport.Position.Y, projection.Viewport.End.Y, gridStep);
        if (lastX - firstX > MaximumGridLinesPerAxis || lastZ - firstZ > MaximumGridLinesPerAxis)
        {
            throw new MiniMapRenderException("GridDensityExceeded", string.Empty,
                $"The viewport would draw more than {MaximumGridLinesPerAxis} grid lines per axis at step {DescribeNumber(gridStep)} metres.");
        }

        // Vertically centre the X-label row's ink in the top band, whatever the font's baseline offset.
        float rowOffset = MiniMapProjection.TextInkBounds("-0123456789.", Vector2.Zero, size).GetCenter().Y;
        float rowAnchor = (layout.TopGutter / 2f) - rowOffset;
        Rect2? previous = null;
        for (long x = firstX; x <= lastX; x++)
        {
            float value = (float)(x * (double)gridStep);
            float pixel = projection.WorldToPixel(new Vector2(value, 0)).X;
            string stroke = x == 0 ? GridZeroStroke : GridStroke;
            _ = svg.Append($"<path d=\"M{MiniMapGlyphs.Number(pixel)} {MiniMapGlyphs.Number(plot.Position.Y)}V{settings.Height}\" stroke=\"{stroke}\"/>");
            string text = DescribeNumber(value);
            Vector2 anchor = new(pixel, rowAnchor);
            if (labelled && TryLabelBox(text, anchor, size, out Rect2 box)
                && box.Position.X >= plot.Position.X && box.End.X <= settings.Width
                && (previous is not { } prior || !prior.Intersects(box)))
            {
                _ = svg.Append(projection.WriteText(text, anchor, size, GridTextFill, "grid"));
                previous = box;
            }
        }

        previous = null;
        for (long z = firstZ; z <= lastZ; z++)
        {
            float value = (float)(z * (double)gridStep);
            float pixel = projection.WorldToPixel(new Vector2(0, value)).Y;
            string stroke = z == 0 ? GridZeroStroke : GridStroke;
            _ = svg.Append($"<path d=\"M{MiniMapGlyphs.Number(plot.Position.X)} {MiniMapGlyphs.Number(pixel)}H{settings.Width}\" stroke=\"{stroke}\"/>");
            string text = DescribeNumber(value);
            Vector2 anchor = new(layout.LeftGutter / 2f, pixel);
            if (labelled && TryLabelBox(text, anchor, size, out Rect2 box)
                && box.Position.Y >= plot.Position.Y && box.End.Y <= settings.Height
                && (previous is not { } prior || !prior.Intersects(box)))
            {
                _ = svg.Append(projection.WriteText(text, anchor, size, GridTextFill, "grid"));
                previous = box;
            }
        }
    }

    private static bool TryLabelBox(string text, Vector2 anchor, int size, out Rect2 box)
    {
        try
        {
            box = MiniMapProjection.TextInkBounds(text, anchor, size);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Grid labels are ASCII digits; an unsupported glyph is reported by WriteText's own path if drawn.
            box = default;
            return false;
        }
    }

    private static string DescribeNumber(float value) => (value == 0 ? 0 : value).ToString("G6", CultureInfo.InvariantCulture);

    private static string DescribePoint(Vector2 point) => $"({DescribeNumber(point.X)}, {DescribeNumber(point.Y)})";

    private static string DescribeRect(Rect2 rectangle) =>
        $"{DescribePoint(rectangle.Position)} size {DescribePoint(rectangle.Size)}";

    private static string DescribeDistance(float value) =>
        float.IsPositiveInfinity(value) ? "any distance" : $"{DescribeNumber(value)} metres";
}
