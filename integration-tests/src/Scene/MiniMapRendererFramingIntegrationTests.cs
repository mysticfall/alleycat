using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// SCN-002 TA-3 runtime coverage: the bounding-box/centre-union/margin/minimum-extent viewport rules, uniform scale
/// with aspect fit, distance-filter boundaries, single-pass capture boundaries, and the adaptive world-zero-anchored
/// grid at more than one scale (TR-10/TR-11/TR-14).
/// </summary>
public sealed class MiniMapRendererFramingIntegrationTests
{
    private static MiniMapMarker BoxAt(string name, Vector3 position, string colourHex)
    {
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker(name, 0, new MiniMapRectangle
        {
            Name = "Box",
            Width = 2,
            Depth = 2,
            FillColour = new Color(colourHex),
        });
        marker.Position = position;
        return marker;
    }

    /// <summary>
    /// The automatic viewport frames the bounding box of the included markers unioned with the requested centre,
    /// expanded by the margin on every side (TR-10).
    /// </summary>
    [Fact]
    public async Task AutoScale_FramesIncludedMarkersUnionCentreWithMargin()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapAutoScaleRoot"
        };
        root.AddChild(BoxAt("West", new Vector3(0, 0, 0), "287fa0"));
        root.AddChild(BoxAt("East", new Vector3(10, 0, 4), "d84156"));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, MarginMetres: 2, Width: 512, Height: 512));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            // Two 2 m boxes at (0,0) and (10,4): content (-1,-1)-(11,5), grown by 2 on every side.
            Assert.True(result.Viewport.Position.X <= -3f + 0.01f && result.Viewport.Position.Y <= -3f + 0.01f,
                $"Viewport {result.Viewport} should start at or before the margin-grown bounds.");
            Assert.True(result.Viewport.End.X >= 13f - 0.01f && result.Viewport.End.Y >= 7f - 0.01f,
                $"Viewport {result.Viewport} should end at or after the margin-grown bounds.");

            // The requested centre is unioned in even when no marker covers it.
            MiniMapRenderResult framedAroundCentre = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(new Vector2(20, 0), new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, MarginMetres: 2, Width: 512, Height: 512));
            Assert.True(framedAroundCentre.Viewport.HasPoint(new Vector2(20, 0)),
                $"Viewport {framedAroundCentre.Viewport} must include the requested centre.");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A single small marker set is framed by the documented fixed minimum world extent guard, never a degenerate
    /// zoom (TR-9/TR-10).
    /// </summary>
    [Fact]
    public async Task SingleMarker_IsFramedByMinimumWorldExtentGuard()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapMinExtentRoot"
        };
        root.AddChild(BoxAt("Solo", new Vector3(0, 0, 0), "287fa0"));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: 512, Height: 512));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.True(result.Viewport.Size.X >= MiniMapRenderer.MinimumWorldExtentMetres - 0.01f,
                $"Viewport width {result.Viewport.Size.X} must respect the minimum world extent.");
            Assert.True(result.Viewport.Size.Y >= MiniMapRenderer.MinimumWorldExtentMetres - 0.01f,
                $"Viewport height {result.Viewport.Size.Y} must respect the minimum world extent.");
            Assert.True(result.Viewport.HasPoint(new Vector2(1, 1)),
                $"Viewport {result.Viewport} should still contain the marker footprint.");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A marker is included if and only if the X/Z-plane distance from the centre to its world origin is at most the
    /// inclusion distance — the exact boundary is included, beyond it is excluded (TR-10).
    /// </summary>
    [Fact]
    public async Task DistanceFilter_IncludesBoundaryAndExcludesBeyond()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapDistanceRoot"
        };
        root.AddChild(BoxAt("Near", new Vector3(0, 0, 0), "287fa0"));
        root.AddChild(BoxAt("Edge", new Vector3(5, 0, 0), "64adce"));
        root.AddChild(BoxAt("Far", new Vector3(6, 0, 0), "d84156"));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: 5f, Width: 512, Height: 512));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            Assert.Contains("#287fa0", svg, StringComparison.Ordinal);
            Assert.Contains("#64adce", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("#d84156", svg, StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The viewport is widened along exactly one axis to the image aspect ratio under one uniform pixels-per-metre
    /// scale — never distortion (TR-10).
    /// </summary>
    [Fact]
    public async Task Viewport_MatchesImageAspectRatioUnderUniformScale()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapAspectRoot"
        };
        root.AddChild(BoxAt("West", new Vector3(0, 0, 0), "287fa0"));
        root.AddChild(BoxAt("East", new Vector3(10, 0, 2), "d84156"));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, MarginMetres: 1, Width: 800, Height: 400));

            Assert.Equal(800, MiniMapTestHarness.Image(result).GetWidth());
            Assert.Equal(400, MiniMapTestHarness.Image(result).GetHeight());
            Assert.Equal(800f / 400f, result.Viewport.Size.X / result.Viewport.Size.Y, 3);
            Assert.Equal(800f / result.Viewport.Size.X, result.Scale, 2);
            Assert.Equal(400f / result.Viewport.Size.Y, result.Scale, 2);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The adaptive grid step is the smallest 1, 2, 5 × 10^n step meeting the minimum pixel spacing, anchored at
    /// world zero with negative coordinates labelled, at two distinct scales (TR-14).
    /// </summary>
    [Fact]
    public async Task AdaptiveGrid_AnchorsAtWorldZeroWithMetreLabelsAtTwoScales()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapGridRoot"
        };
        root.AddChild(BoxAt("Origin", new Vector3(-3, 0, -3), "287fa0"));
        root.AddChild(BoxAt("Far", new Vector3(40, 0, 30), "d84156"));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);

            MiniMapRenderSettings close = new(MaximumDistance: 8f, Width: 512, Height: 512);
            MiniMapRenderSettings wide = new(MaximumDistance: float.PositiveInfinity, Width: 128, Height: 128);

            MiniMapRenderResult closeResult = renderer.Render(new Vector2(-3, -3), close);
            MiniMapRenderResult wideResult = renderer.Render(new Vector2(20, 15), wide);
            Assert.Equal(MiniMapRenderStatus.Ready, closeResult.Status);
            Assert.Equal(MiniMapRenderStatus.Ready, wideResult.Status);
            Assert.DoesNotContain("#d84156", MiniMapTestHarness.Svg(closeResult), StringComparison.Ordinal);
            Assert.True(closeResult.GridStep < wideResult.GridStep, $"Close step {closeResult.GridStep} should be finer than wide step {wideResult.GridStep}.");

            foreach (MiniMapRenderResult result in new[] { closeResult, wideResult })
            {
                // The step must be a 1, 2, 5 x 10^n member, meet the minimum spacing, and be the smallest such step.
                double exponent = Math.Pow(10, Math.Floor(Math.Log10(result.GridStep)));
                double mantissa = result.GridStep / exponent;
                Assert.True(mantissa is (>= 0.99 and <= 1.01) or (>= 1.99 and <= 2.01) or (>= 4.99 and <= 5.01),
                    $"Grid step {result.GridStep} must be a 1,2,5 x 10^n metre step.");
                Assert.True(result.GridStep * result.Scale >= 64f, $"Step {result.GridStep} at {result.Scale} px/m must meet the 64 px minimum spacing.");
                Assert.True(result.GridStep / 5f * result.Scale < 64f, "The next smaller 1,2,5 step must violate the minimum spacing.");

                (int vertical, int horizontal) = MiniMapTestHarness.GridLineCounts(MiniMapTestHarness.Svg(result));
                long expectedVertical = (long)Math.Floor(result.Viewport.End.X / result.GridStep) - (long)Math.Ceiling(result.Viewport.Position.X / result.GridStep) + 1;
                long expectedHorizontal = (long)Math.Floor(result.Viewport.End.Y / result.GridStep) - (long)Math.Ceiling(result.Viewport.Position.Y / result.GridStep) + 1;
                Assert.Equal(expectedVertical, vertical);
                Assert.Equal(expectedHorizontal, horizontal);
            }

            // Negative multiples carry absolute metre labels through the glyph pipeline (aria-label mirror).
            Assert.Contains("aria-label=\"-3\"", MiniMapTestHarness.Svg(closeResult), StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The axis convention is fixed: +X maps right and +Z maps down, and the map never rotates (UR-4/TR-14).
    /// </summary>
    [Fact]
    public async Task AxisConvention_PlusXRightAndPlusZDownWithoutRotation()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapAxesRoot"
        };
        root.AddChild(BoxAt("PlusX", new Vector3(5, 0, 0), "287fa0"));
        root.AddChild(BoxAt("PlusZ", new Vector3(0, 0, 5), "d84156"));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: 512, Height: 512));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            IReadOnlyList<(string Fill, Vector2[] Points)> polygons = MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(result));
            Vector2 plusX = MiniMapTestHarness.Centroid(polygons.Single(polygon => polygon.Fill == "#287fa0").Points);
            Vector2 plusZ = MiniMapTestHarness.Centroid(polygons.Single(polygon => polygon.Fill == "#d84156").Points);
            Vector2 expectedPlusX = MiniMapTestHarness.ExpectedPixel(result, new Vector2(5, 0));
            Vector2 expectedPlusZ = MiniMapTestHarness.ExpectedPixel(result, new Vector2(0, 5));

            // +X renders right of both the image centre and the +Z marker; +Z renders below +X (+Z is down).
            Assert.True(MathF.Abs(plusX.X - expectedPlusX.X) < 0.5f && MathF.Abs(plusX.Y - expectedPlusX.Y) < 0.5f,
                $"Marker at world +X must render at its contract pixel {expectedPlusX}, got {plusX}.");
            Assert.True(MathF.Abs(plusZ.X - expectedPlusZ.X) < 0.5f && MathF.Abs(plusZ.Y - expectedPlusZ.Y) < 0.5f,
                $"Marker at world +Z must render at its contract pixel {expectedPlusZ}, got {plusZ}.");
            Assert.True(plusX.X > 256f && plusX.X > plusZ.X, $"+X must map right, got {plusX} vs {plusZ}.");
            Assert.True(plusZ.Y > plusX.Y, $"+Z must map down, got {plusZ} vs {plusX}.");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The explicit-viewport entry renders exactly the requested world rectangle — the editor preview's pan/zoom
    /// driver input (TR-17).
    /// </summary>
    [Fact]
    public async Task ExplicitViewportRequest_RendersRequestedWorldRectangle()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapExplicitViewportRoot"
        };
        root.AddChild(BoxAt("Inside", new Vector3(2, 0, 2), "287fa0"));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            Rect2 requested = new(new Vector2(1, 1), new Vector2(6, 4));
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(new MiniMapViewportRequest(requested.GetCenter(), requested, new MiniMapRenderSettings(Width: 600, Height: 400)));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Equal(requested.Position.X, result.Viewport.Position.X, 2);
            Assert.Equal(requested.Position.Y, result.Viewport.Position.Y, 2);
            Assert.Equal(requested.Size.X, result.Viewport.Size.X, 2);
            Assert.Equal(requested.Size.Y, result.Viewport.Size.Y, 2);
            Assert.Equal(600f / requested.Size.X, result.Scale, 2);
            Assert.Contains("#287fa0", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);

            MiniMapViewportRequest invalid = new(Vector2.Zero, new Rect2(Vector2.Zero, new Vector2(-1, 4)));
            MiniMapRenderResult failure = MiniMapTestHarness.CreateScopedRenderer(root).Render(invalid);
            Assert.Equal(MiniMapRenderStatus.Failed, failure.Status);
            Assert.Null(failure.Image);
            Assert.Null(failure.Svg);
            Assert.Contains("InvalidViewport", Assert.Single(failure.Diagnostics).Code, StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A marker with an empty primitive collection is legal, renders no fragments, and contributes no extent; a
    /// queued-for-deletion marker is excluded from the next single-pass capture (TR-2/TR-11).
    /// </summary>
    [Fact]
    public async Task EmptyMarker_RendersNothingAndQueuedMarker_IsExcludedNextCapture()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapEmptyRoot"
        };
        MiniMapMarker empty = MiniMapTestHarness.CreateMarker("Empty");
        MiniMapMarker doomed = BoxAt("Doomed", new Vector3(3, 0, 3), "d84156");
        root.AddChild(empty);
        root.AddChild(doomed);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            MiniMapRenderSettings settings = new(MaximumDistance: float.PositiveInfinity, Width: 256, Height: 256);

            MiniMapRenderResult withDoomed = renderer.Render(Vector2.Zero, settings);
            Assert.Equal(MiniMapRenderStatus.Ready, withDoomed.Status);
            _ = Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(withDoomed)));

            doomed.QueueFree();
            await WaitForFramesAsync(sceneTree, 3);

            MiniMapRenderResult afterFree = renderer.Render(Vector2.Zero, settings);
            Assert.Equal(MiniMapRenderStatus.Ready, afterFree.Status);
            Assert.Empty(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(afterFree)));
            Assert.Equal(MiniMapRenderer.MinimumWorldExtentMetres, afterFree.Viewport.Size.X, 1);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }
}
