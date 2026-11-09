using System.Globalization;
using System.Text;
using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// Render-only framing measurement sweep for the AI-013 follow-up question: minimap captures of the holding cell
/// leave much blank space around the walls structure instead of zooming onto it. The fixture is identical to
/// <see cref="MiniMapProductionNpcIdentityIntegrationTests" /> — the real session scene with the production Mali NPC
/// spawned under <c>Actors</c> — and the sweep renders it once per <see cref="MiniMapRenderSettings.MarginMetres" />
/// and image-size variant (default inclusion distance and the AI-013 subject override kept exactly as the user sees
/// them), persisting one SVG/PNG pair per variant plus a computed occupancy summary under
/// <c>game/temp/minimap-bugfix/framing/</c>. Assertions cover plumbing only: the fixture installs, the walls marker is
/// discovered at the S1-measured bounds, every render succeeds and persists, and the computed TR-10 arithmetic model
/// reproduces the engine's reported viewports. No occupancy value is asserted — the tuning decision stays with the
/// user.
/// </summary>
public sealed class MiniMapFramingSweepIntegrationTests
{
    private const string ProductionScenePath = "res://content/the_veridian_spc/characters/mali/mali_npc.tscn";
    private const string SessionScenePath = "res://content/the_veridian_spc/start.tscn";
    private const string ArtefactDirectory = "res://temp/minimap-bugfix/framing";
    private const string WallsMarkerPathSuffix = "Interior/MiniMapMarkers";

    // S1-measured walls bbox of the Kennel marker; anchors this sweep to the exact structure the blank-space report
    // describes. Tolerance covers live-pose sampling jitter only.
    private const float WallsMinXReference = -1.96f;
    private const float WallsMaxXReference = 1.97f;
    private const float WallsMinZReference = -6.13f;
    private const float WallsMaxZReference = 1.37f;
    private const float WallsReferenceToleranceMetres = 0.05f;

    private const float ModelAgreementToleranceMetres = 0.01f;

    private static readonly float[] _sweepMargins = [2f, 1f, 0.5f, 0.25f];
    private static readonly (int Width, int Height)[] _sweepSizes = [(1024, 1024), (768, 1024), (512, 1024)];
    private static readonly float[] _hypotheticalMinimumExtents = [8f, 4f, 2f];
    private static readonly float[] _hypotheticalMargins = [0.5f, 1f];

    /// <summary>
    /// Instances the production session exactly as the sibling identity diagnostics do, then renders it twelve times
    /// — every <see cref="_sweepMargins" /> × <see cref="_sweepSizes" /> combination — through the production
    /// <see cref="MiniMapRenderer" />, writing <c>margin&lt;m&gt;_&lt;w&gt;x&lt;h&gt;</c> SVG/PNG pairs and the
    /// <c>occupancy.md</c> computed summary.
    /// </summary>
    [Fact]
    public async Task RealSessionScene_FramingSweep_RendersMarginAndAspectVariants()
    {
        SceneTree sceneTree = GetSceneTree();
        int groupBaseline = sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count;

        PackedScene sessionScene = GD.Load<PackedScene>(SessionScenePath);
        Assert.NotNull(sessionScene);
        Node session = sessionScene.Instantiate();
        Assert.NotNull(session);
        PackedScene productionScene = GD.Load<PackedScene>(ProductionScenePath);
        Assert.NotNull(productionScene);
        Node npc = productionScene.Instantiate();
        Assert.NotNull(npc);
        Node actorsContainer = session.GetNodeOrNull("Actors");
        Assert.NotNull(actorsContainer);
        actorsContainer.AddChild(npc);

        Node fixtureRoot = new()
        {
            Name = "MinimapFramingSweepRoot"
        };
        fixtureRoot.AddChild(session);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, fixtureRoot);

        try
        {
            int installFrames = await WaitForInstalledMarkersAsync(sceneTree, fixtureRoot);
            IReadOnlyList<MiniMapMarker> markers = DiscoverFixtureMarkers(fixtureRoot);
            Assert.True(markers.Count >= 2,
                $"Expected at least the player and NPC actor markers after {installFrames} frames; found {markers.Count}.");

            // AI-013 subject restyling exactly as the sibling identity diagnostics compose it.
            Dictionary<string, MiniMapMarkerOverride> markerOverrides = new()
            {
                ["char:mali"] = new(new Color("ffd700"), "*You*"),
            };
            MiniMapCaptureContext measurementContext = new(ScopeRoot: fixtureRoot, EditorToolContext: false);

            Vector2 centre = npc is Node3D npcRoot
                ? new Vector2(npcRoot.GlobalPosition.X, npcRoot.GlobalPosition.Z)
                : throw new InvalidOperationException("NPC root must be a Node3D for live centre sampling.");

            List<MiniMapMarkerSnapshot> snapshots =
                [.. markers.Select(marker => marker.CaptureSnapshot(measurementContext))];
            MiniMapMarkerSnapshot wallsSnapshot = Assert.Single(
                snapshots, snapshot => snapshot.NodePath.EndsWith(WallsMarkerPathSuffix, StringComparison.Ordinal));

            Rect2 wallsBounds = GeometryBounds([wallsSnapshot], centre: null);
            AssertBoundsNear(wallsBounds);

            Rect2 contentBounds = GeometryBounds(snapshots, centre);

            string artefactDirectory = ProjectSettings.GlobalizePath(ArtefactDirectory);
            _ = Directory.CreateDirectory(artefactDirectory);
            MiniMapRenderer renderer = new(fixtureRoot, fixtureRoot);

            List<string> variantRows = [];
            float maxModelDelta = 0f;
            foreach (float margin in _sweepMargins)
            {
                foreach ((int width, int height) in _sweepSizes)
                {
                    MiniMapRenderSettings settings =
                        new(Width: width, Height: height, MarginMetres: margin, MarkerOverrides: markerOverrides);
                    MiniMapRenderResult result = renderer.Render(centre, settings);
                    Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
                    Assert.True(result.Scale > 0f, $"Variant margin={DescribeMargin(margin)} {width}x{height} reported a non-positive scale.");
                    Assert.True(result.Viewport.Size.X > 0f && result.Viewport.Size.Y > 0f,
                        $"Variant margin={DescribeMargin(margin)} {width}x{height} reported a degenerate viewport.");

                    string stem = $"margin{DescribeMargin(margin)}_{width}x{height}";
                    await File.WriteAllTextAsync(Path.Combine(artefactDirectory, $"{stem}.svg"), MiniMapTestHarness.Svg(result));
                    Error pngError = MiniMapTestHarness.Image(result).SavePng(Path.Combine(artefactDirectory, $"{stem}.png"));
                    Assert.Equal(Error.Ok, pngError);

                    float widthCover = wallsBounds.Size.X / result.Viewport.Size.X;
                    float heightCover = wallsBounds.Size.Y / result.Viewport.Size.Y;
                    float modelDelta = ModelViewportDelta(
                        contentBounds.Size, margin, MiniMapRenderer.MinimumWorldExtentMetres, (float)width / height, result.Viewport.Size);
                    maxModelDelta = MathF.Max(maxModelDelta, modelDelta);

                    variantRows.Add(
                        $"| {DescribeMargin(margin)} | {width}×{height} "
                        + $"| ({DescribeMetres(result.Viewport.Position.X)}, {DescribeMetres(result.Viewport.Position.Y)}) "
                        + $"| {DescribeMetres(result.Viewport.Size.X)} × {DescribeMetres(result.Viewport.Size.Y)} "
                        + $"| {DescribeScale(result.Scale)} | {DescribePercent(widthCover)} | {DescribePercent(heightCover)} "
                        + $"| {DescribePixels(wallsBounds.Size.X * result.Scale)} × {DescribePixels(wallsBounds.Size.Y * result.Scale)} |");
                    GD.Print($"[minimap-framing] {stem}: viewport={DescribeMetres(result.Viewport.Position.X)},"
                        + $"{DescribeMetres(result.Viewport.Position.Y)},{DescribeMetres(result.Viewport.Size.X)},{DescribeMetres(result.Viewport.Size.Y)}"
                        + $" scale={DescribeScale(result.Scale)} walls_cover_w={DescribePercent(widthCover)}"
                        + $" walls_cover_h={DescribePercent(heightCover)} model_delta={modelDelta.ToString("0.####", CultureInfo.InvariantCulture)}");
                }
            }

            // The hypothetical guard table is arithmetic only; this check keeps that arithmetic honest against the engine.
            Assert.True(maxModelDelta <= ModelAgreementToleranceMetres,
                $"TR-10 arithmetic model must reproduce every engine viewport within {DescribeMetres(ModelAgreementToleranceMetres)} m;"
                + $" worst delta was {maxModelDelta.ToString("0.####", CultureInfo.InvariantCulture)} m.");

            List<string> hypotheticalRows = [];
            foreach (float minimumExtent in _hypotheticalMinimumExtents)
            {
                foreach (float margin in _hypotheticalMargins)
                {
                    foreach ((int width, int height) in _sweepSizes)
                    {
                        float aspect = (float)width / height;
                        Vector2 size = HypotheticalViewportSize(contentBounds.Size, margin, minimumExtent, aspect);
                        hypotheticalRows.Add(
                            $"| {DescribeMetres(minimumExtent)} | {DescribeMargin(margin)} | {DescribeAspect(width, height)} "
                            + $"| {DescribeMetres(size.X)} × {DescribeMetres(size.Y)} "
                            + $"| {DescribePercent(wallsBounds.Size.X / size.X)} | {DescribePercent(wallsBounds.Size.Y / size.Y)} |");
                    }
                }
            }

            string report = ComposeOccupancyReport(
                installFrames, markers.Count, centre, wallsBounds, contentBounds, maxModelDelta, variantRows, hypotheticalRows);
            await File.WriteAllTextAsync(Path.Combine(artefactDirectory, "occupancy.md"), report);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, fixtureRoot);
            Assert.Equal(groupBaseline, sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count);
        }
    }

    /// <summary>
    /// AutoInstallOnReady installs deferred on tree entry; poll process frames until the fixture contributes at
    /// least one marker, then let physics-driven installers settle before sampling.
    /// </summary>
    private static async Task<int> WaitForInstalledMarkersAsync(SceneTree sceneTree, Node fixtureRoot)
    {
        int installFrames = 0;
        for (; installFrames < 300; installFrames++)
        {
            if (DiscoverFixtureMarkers(fixtureRoot).Count > 0)
            {
                break;
            }

            await WaitForFramesAsync(sceneTree, 1);
        }

        await WaitForPhysicsFramesAsync(sceneTree, 10);
        await WaitForFramesAsync(sceneTree, 2);
        return installFrames;
    }

    private static IReadOnlyList<MiniMapMarker> DiscoverFixtureMarkers(Node fixtureRoot)
    {
        List<MiniMapMarker> markers = [];
        foreach (Node node in fixtureRoot.GetTree().GetNodesInGroup(MiniMapMarker.GroupName))
        {
            if (node is MiniMapMarker marker && GodotObject.IsInstanceValid(marker) && fixtureRoot.IsAncestorOf(marker))
            {
                markers.Add(marker);
            }
        }

        return markers;
    }

    /// <summary>
    /// World X/Z bounds of the TR-10 content definition over the given snapshots: every shape polygon point and label
    /// anchor, plus the render centre when one is supplied. This is a measured input for the occupancy arithmetic,
    /// not a prediction of the framing pipeline.
    /// </summary>
    private static Rect2 GeometryBounds(IEnumerable<MiniMapMarkerSnapshot> snapshots, Vector2? centre)
    {
        Vector2 minimum = Vector2.Zero;
        Vector2 maximum = Vector2.Zero;
        bool any = false;
        void Include(Vector2 point)
        {
            if (!any)
            {
                minimum = point;
                maximum = point;
                any = true;
                return;
            }

            minimum = minimum.Min(point);
            maximum = maximum.Max(point);
        }

        if (centre is { } requested)
        {
            Include(requested);
        }

        foreach (MiniMapMarkerSnapshot snapshot in snapshots)
        {
            foreach (IMiniMapElement element in snapshot.Primitives)
            {
                switch (element)
                {
                    case MiniMapShapeSnapshot shape:
                        foreach (Vector2 point in shape.Polygon)
                        {
                            Include(point);
                        }

                        break;
                    case MiniMapLabelSnapshot label:
                        Include(label.Anchor);
                        break;
                    default:
                        break;
                }
            }
        }

        Assert.True(any, "Expected at least one geometry point or the centre to bound.");
        return new Rect2(minimum, maximum - minimum);
    }

    /// <summary>Anchors the measured walls bbox to the S1-reported structure so the sweep cannot silently drift onto a different fixture.</summary>
    private static void AssertBoundsNear(Rect2 wallsBounds)
    {
        AssertNear(wallsBounds.Position.X, WallsMinXReference, "walls min X");
        AssertNear(wallsBounds.Position.X + wallsBounds.Size.X, WallsMaxXReference, "walls max X");
        AssertNear(wallsBounds.Position.Y, WallsMinZReference, "walls min Z");
        AssertNear(wallsBounds.Position.Y + wallsBounds.Size.Y, WallsMaxZReference, "walls max Z");
    }

    private static void AssertNear(float value, float reference, string label) => Assert.True(
        MathF.Abs(value - reference) <= WallsReferenceToleranceMetres,
        $"{label} measured {DescribeMetres(value)} but expected the S1 reference {DescribeMetres(reference)}"
        + $" within {DescribeMetres(WallsReferenceToleranceMetres)} m.");

    /// <summary>
    /// The documented TR-10 viewport rule applied arithmetically: grow the content by the margin on every side, floor
    /// each axis at the given minimum world extent, then widen exactly one axis to the image aspect. Used for the
    /// hypothetical guard table and to cross-check the engine's viewports; never fed back into a render.
    /// </summary>
    private static Vector2 HypotheticalViewportSize(Vector2 contentSize, float marginMetres, float minimumWorldExtentMetres, float aspect)
    {
        Vector2 grown = new(contentSize.X + (2 * marginMetres), contentSize.Y + (2 * marginMetres));
        Vector2 size = new(
            MathF.Max(grown.X, minimumWorldExtentMetres),
            MathF.Max(grown.Y, minimumWorldExtentMetres));
        float ratio = size.X / size.Y;
        return ratio < aspect ? new Vector2(size.Y * aspect, size.Y) : new Vector2(size.X, size.X / aspect);
    }

    private static float ModelViewportDelta(
        Vector2 contentSize, float marginMetres, float minimumWorldExtentMetres, float aspect, Vector2 engineViewportSize)
    {
        Vector2 model = HypotheticalViewportSize(contentSize, marginMetres, minimumWorldExtentMetres, aspect);
        return MathF.Max(MathF.Abs(model.X - engineViewportSize.X), MathF.Abs(model.Y - engineViewportSize.Y));
    }

    private static string ComposeOccupancyReport(
        int installFrames,
        int markerCount,
        Vector2 centre,
        Rect2 wallsBounds,
        Rect2 contentBounds,
        float maxModelDelta,
        IReadOnlyList<string> variantRows,
        IReadOnlyList<string> hypotheticalRows)
    {
        StringBuilder report = new();
        _ = report.AppendLine("# Holding-Cell Minimap Framing Sweep (AI-013 Follow-Up)");
        _ = report.AppendLine();
        _ = report.AppendLine("Render-only measurement sweep for the reported symptom: AI-013 minimap captures of the");
        _ = report.AppendLine("holding cell leave much blank space around the walls structure instead of zooming onto it.");
        _ = report.AppendLine("Produced by `MiniMapFramingSweepIntegrationTests.RealSessionScene_FramingSweep_RendersMarginAndAspectVariants`;");
        _ = report.AppendLine("no production code, asset, or specification was changed to produce these numbers. The fixture");
        _ = report.AppendLine("and render inputs match the S1 identity dump exactly: `res://content/the_veridian_spc/start.tscn`");
        _ = report.AppendLine("with `mali_npc.tscn` spawned under `Actors`, the default inclusion distance (100 m), the AI-013");
        _ = report.AppendLine("gold `*You*` subject override for `char:mali`, and the render centred on Mali's live position.");
        _ = report.AppendLine();
        _ = report.AppendLine("## Measured Inputs");
        _ = report.AppendLine();
        _ = report.AppendLine($"- install_wait_frames: {installFrames.ToString(CultureInfo.InvariantCulture)}; fixture markers: {markerCount.ToString(CultureInfo.InvariantCulture)}.");
        _ = report.AppendLine($"- render centre (Mali live): ({DescribeMetres(centre.X)}, {DescribeMetres(centre.Y)}).");
        _ = report.AppendLine($"- walls bbox (Kennel marker `Interior/MiniMapMarkers`): {DescribeBounds(wallsBounds)} — measured from the marker snapshot polygons.");
        _ = report.AppendLine($"- all-marker content bbox incl. label anchors and the render centre: {DescribeBounds(contentBounds)} — measured; this is the TR-10 content union the renderer frames.");
        _ = report.AppendLine();
        _ = report.AppendLine("## Occupancy By Variant");
        _ = report.AppendLine();
        _ = report.AppendLine("Viewport and scale are the engine-observed `MiniMapRenderResult` values; occupancy is computed");
        _ = report.AppendLine("arithmetic — walls extent ÷ viewport extent on the same axis. The SVG/PNG pair for each row sits");
        _ = report.AppendLine("next to this file as `margin<m>_<w>x<h>.svg|.png`.");
        _ = report.AppendLine();
        _ = report.AppendLine("| Margin (m) | Image (px) | Viewport Position (m) | Viewport Size (m) | Scale (px/m) | Walls Width Cover | Walls Height Cover | Walls Size On Image (px) |");
        _ = report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (string row in variantRows)
        {
            _ = report.AppendLine(row);
        }

        _ = report.AppendLine();
        _ = report.AppendLine($"The TR-10 arithmetic model (grow by margin, floor each axis at the fixed"
            + $" {DescribeMetres(MiniMapRenderer.MinimumWorldExtentMetres)} m guard, widen one axis to the image aspect)");
        _ = report.AppendLine($"reproduces every engine viewport above to within"
            + $" {maxModelDelta.ToString("0.####", CultureInfo.InvariantCulture)} m, so the hypothetical table below is trustworthy.");
        _ = report.AppendLine();
        _ = report.AppendLine("## Hypothetical Minimum World Extent (Computed Only)");
        _ = report.AppendLine();
        _ = report.AppendLine($"`MiniMapRenderer.MinimumWorldExtentMetres = {DescribeMetres(MiniMapRenderer.MinimumWorldExtentMetres)}` is the"
            + " spec-pinned fixed service guard (SCN-002 TR-9), not a per-request setting. The table below applies the same TR-10");
        _ = report.AppendLine("arithmetic to the measured content bbox with other hypothetical guard values the renderer does not");
        _ = report.AppendLine("use; altering the guard is a specification decision, not a per-request tuning option.");
        _ = report.AppendLine();
        _ = report.AppendLine("| Guard (m) | Margin (m) | Image Aspect | Hypothetical Viewport Size (m) | Walls Width Cover | Walls Height Cover |");
        _ = report.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (string row in hypotheticalRows)
        {
            _ = report.AppendLine(row);
        }

        return report.ToString();
    }

    private static string DescribeBounds(Rect2 bounds) =>
        $"x[{DescribeMetres(bounds.Position.X)}, {DescribeMetres(bounds.Position.X + bounds.Size.X)}]"
        + $" × z[{DescribeMetres(bounds.Position.Y)}, {DescribeMetres(bounds.Position.Y + bounds.Size.Y)}]"
        + $" ({DescribeMetres(bounds.Size.X)} × {DescribeMetres(bounds.Size.Y)} m)";

    private static string DescribeAspect(int width, int height) => (width, height) switch
    {
        (1024, 1024) => "1:1 (1024×1024)",
        (768, 1024) => "3:4 (768×1024)",
        (512, 1024) => "1:2 (512×1024)",
        _ => $"{width}×{height}",
    };

    private static string DescribeMargin(float margin) => margin.ToString(CultureInfo.InvariantCulture);

    private static string DescribeMetres(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string DescribeScale(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string DescribePixels(float value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static string DescribePercent(float fraction) => fraction.ToString("P1", CultureInfo.InvariantCulture);
}
