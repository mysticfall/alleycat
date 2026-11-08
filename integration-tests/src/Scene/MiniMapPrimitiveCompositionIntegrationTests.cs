using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// SCN-002 TA-2 runtime coverage: fragment self-containment, primitive geometry and projection (rectangle, circle
/// under non-uniform scale, triangle apex under yaw, labels), authored-offset labels, and TR-8 transform behaviour
/// including ignored Y translation.
/// </summary>
public sealed class MiniMapPrimitiveCompositionIntegrationTests
{
    private static readonly MiniMapRenderSettings _smallSquare = new(MaximumDistance: float.PositiveInfinity, Width: 512, Height: 512);

    /// <summary>
    /// A marker composing every primitive type renders one filled fragment per shape plus glyph-path text, and the
    /// SVG stays self-contained: no <c>&lt;text&gt;</c>, no external references (TR-5/TR-6/TR-15).
    /// </summary>
    [Fact]
    public async Task EveryPrimitive_RendersSelfContainedGeometryWithAuthoredColours()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapAllPrimitivesRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Legend", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 1, FillColour = new Color("287fa0") },
            new MiniMapCircle { Name = "Pool", Radius = 1, FillColour = new Color("64adce"), Position = new Vector3(4, 0, 0) },
            new MiniMapTriangle { Name = "Heading", Width = 2, Length = 2, FillColour = new Color("e4a52c"), Position = new Vector3(0, 0, 4) },
            new MiniMapLabel { Name = "Caption", Text = "Crate", TextSize = 16, FillColour = new Color("17202a"), Position = new Vector3(0, 0, -1.5f) });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            foreach (string fill in new[] { "#287fa0", "#64adce", "#e4a52c" })
            {
                Assert.Contains($"fill=\"{fill}\"", svg, StringComparison.Ordinal);
            }

            IReadOnlyList<(string Fill, Vector2[] Points)> polygons = MiniMapTestHarness.Polygons(svg);
            Assert.Equal(3, polygons.Count);

            Assert.Contains("aria-label=\"Crate\"", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<text", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("href", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("url(#", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", svg, StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Configured label text containing XML-special characters is escaped in its SVG attribute context — the output
    /// stays well-formed with no unescaped raw specials, so identical state keeps producing byte-identical SVG
    /// (TR-13/TA-4).
    /// </summary>
    [Fact]
    public async Task ConfiguredLabel_WithXmlSpecialCharacters_RendersEscapedAttributeText()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapEscapingRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Specials", 0,
            new MiniMapLabel { Name = "Caption", Text = "A&B<C>\"D", TextSize = 16, FillColour = new Color("17202a") },
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            Assert.Contains("aria-label=\"A&amp;B&lt;C&gt;&quot;D\"", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("A&B", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<C", svg, StringComparison.Ordinal);
            Assert.DoesNotContain(">D", svg, StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A centred rectangle projects to the four contract pixels under the render's declared viewport and scale
    /// (TR-6/TR-10 mapping: +X right, +Z down).
    /// </summary>
    [Fact]
    public async Task Rectangle_ProjectsAtContractPixelCoordinates()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapRectanglePixelsRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Framed", 0, new MiniMapRectangle
        {
            Name = "Box",
            Width = 2,
            Depth = 1,
            FillColour = new Color("287fa0"),
        });

        marker.Position = new Vector3(3, 0, 0);
        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);
            (string Fill, Vector2[] Points) = Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(result)));

            Assert.Equal("#287fa0", Fill);
            Vector2[] expected =
            [
                MiniMapTestHarness.ExpectedPixel(result, new Vector2(2, -0.5f)),
                MiniMapTestHarness.ExpectedPixel(result, new Vector2(4, -0.5f)),
                MiniMapTestHarness.ExpectedPixel(result, new Vector2(4, 0.5f)),
                MiniMapTestHarness.ExpectedPixel(result, new Vector2(2, 0.5f)),
            ];
            Assert.Equal(expected, Points);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Under non-uniform positive scale a circle projects as an ellipse: the parsed footprint spans twice the
    /// radius along the doubled axis (TR-6).
    /// </summary>
    [Fact]
    public async Task Circle_UnderNonUniformScale_ProjectsAsEllipse()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapEllipseRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Stretched", 0, new MiniMapCircle
        {
            Name = "Pool",
            Radius = 1,
            FillColour = new Color("64adce"),
        });

        marker.Scale = new Vector3(2, 1, 1);
        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);
            (string Fill, Vector2[] Points) = Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(result)));

            Assert.Equal("#64adce", Fill);
            (Vector2 minimum, Vector2 maximum) = MiniMapTestHarness.Bounds(Points);
            float width = maximum.X - minimum.X;
            float height = maximum.Y - minimum.Y;

            Assert.InRange(width / height, 1.96f, 2.04f);
            Assert.InRange(height / result.Scale, 1.96f, 2.04f);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The triangle apex points along local −Z (map up) and rotates with the marker's yaw while the map itself stays
    /// unrotated (UR-4/TR-6).
    /// </summary>
    [Fact]
    public async Task Triangle_ApexFollowsMarkerYaw()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapTriangleYawRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Turning", 0, new MiniMapTriangle
        {
            Name = "Heading",
            Width = 2,
            Length = 2,
            FillColour = new Color("e4a52c"),
        });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);

            MiniMapRenderResult upright = renderer.Render(Vector2.Zero, _smallSquare);
            Vector2[] uprightPoints = Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(upright))).Points;
            Assert.Equal(
                [
                    MiniMapTestHarness.ExpectedPixel(upright, new Vector2(-1, 1)),
                    MiniMapTestHarness.ExpectedPixel(upright, new Vector2(1, 1)),
                    MiniMapTestHarness.ExpectedPixel(upright, new Vector2(0, -1)),
                ],
                uprightPoints);

            marker.RotationDegrees = new Vector3(0, 180, 0);
            await WaitForFramesAsync(sceneTree, 2);

            MiniMapRenderResult reversed = renderer.Render(Vector2.Zero, _smallSquare);
            Vector2[] reversedPoints = Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(reversed))).Points;
            Assert.Equal(
                [
                    MiniMapTestHarness.ExpectedPixel(reversed, new Vector2(1, -1)),
                    MiniMapTestHarness.ExpectedPixel(reversed, new Vector2(-1, -1)),
                    MiniMapTestHarness.ExpectedPixel(reversed, new Vector2(0, 1)),
                ],
                reversedPoints);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// An identity label displays the canonical <c>FullId</c> of the nearest identifiable ancestor of the marker,
    /// walking through intermediate nodes, with no per-character configuration (UR-5/TR-7).
    /// </summary>
    [Fact]
    public async Task IdentityLabel_ResolvesFullIdFromNearestIdentifiableAncestor()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapIdentityRoot"
        };
        CharacterHub character = new()
        {
            Name = "Nomad",
            Id = "nomad"
        };
        Node3D rig = new()
        {
            Name = "Rig"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Tagged", 0, new MiniMapLabel
        {
            Name = "Identity",
            Flavour = MiniMapLabelFlavour.Identity,
            TextSize = 16,
        });

        rig.AddChild(marker);
        character.AddChild(rig);
        root.AddChild(character);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Contains("aria-label=\"char:nomad\"", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A label's authored world anchor counts as footprint extent: a label-only marker stretches the viewport beyond
    /// the minimum world extent guard (TR-10 amendment).
    /// </summary>
    [Fact]
    public async Task LabelAnchor_ContributesFootprintExtent()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapLabelExtentRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("OnlyLabel", 0, new MiniMapLabel
        {
            Name = "Caption",
            Text = "Outpost",
            TextSize = 16,
            Position = new Vector3(10, 0, 0),
        });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.True(result.Viewport.End.X >= 12f - 0.01f, $"Viewport {result.Viewport} should reach the anchored label plus margin.");
            Assert.True(result.Viewport.Size.X > MiniMapRenderer.MinimumWorldExtentMetres + 0.5f,
                $"Viewport width {result.Viewport.Size.X} should exceed the {MiniMapRenderer.MinimumWorldExtentMetres} m guard.");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Translation in Y is ignored by the X/Z projection: moving a marker vertically changes nothing in the output
    /// (TR-8), and the repeated request remains byte-identical (TR-13).
    /// </summary>
    [Fact]
    public async Task TranslationInY_IsIgnoredByProjection()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapYIgnoredRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Lifted", 0, new MiniMapRectangle
        {
            Name = "Box",
            Width = 2,
            Depth = 2,
            FillColour = new Color("287fa0"),
        });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            string grounded = MiniMapTestHarness.Svg(renderer.Render(Vector2.Zero, _smallSquare));

            marker.Position = new Vector3(0, 37, 0);
            await WaitForFramesAsync(sceneTree, 2);

            string lifted = MiniMapTestHarness.Svg(renderer.Render(Vector2.Zero, _smallSquare));
            Assert.Equal(grounded, lifted);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Negative (mirroring) scale projects mirrored geometry rather than being rejected (TR-8).
    /// </summary>
    [Fact]
    public async Task NegativeScale_ProjectsMirroredGeometry()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapMirrorRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Mirrored", 0, new MiniMapRectangle
        {
            Name = "Box",
            Width = 3,
            Depth = 1,
            FillColour = new Color("287fa0"),
        });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            (Vector2 plainMinimum, Vector2 plainMaximum) = MiniMapTestHarness.Bounds(
                Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(renderer.Render(Vector2.Zero, _smallSquare)))).Points);

            marker.Scale = new Vector3(-1, 1, 1);
            await WaitForFramesAsync(sceneTree, 2);

            MiniMapRenderResult mirrored = renderer.Render(Vector2.Zero, _smallSquare);
            (Vector2 mirroredMinimum, Vector2 mirroredMaximum) = MiniMapTestHarness.Bounds(
                Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(mirrored))).Points);

            Assert.Equal(MiniMapRenderStatus.Ready, mirrored.Status);
            Assert.InRange(plainMaximum.X - plainMinimum.X - (mirroredMaximum.X - mirroredMinimum.X), -0.5f, 0.5f);
            Assert.InRange(plainMaximum.Y - plainMinimum.Y - (mirroredMaximum.Y - mirroredMinimum.Y), -0.5f, 0.5f);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Tilt is projected, not rejected: a pitched marker still renders, with its depth axis foreshortened by the tilt
    /// cosine (TR-8/TR-16 transient-degenerate distinction: this pose stays above zero area).
    /// </summary>
    [Fact]
    public async Task Tilt_ProjectsRatherThanRejects()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapTiltRoot"
        };
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker("Pitched", 0, new MiniMapRectangle
        {
            Name = "Box",
            Width = 2,
            Depth = 1,
            FillColour = new Color("287fa0"),
        });

        root.AddChild(marker);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            (Vector2 uprightMinimum, Vector2 uprightMaximum) = MiniMapTestHarness.Bounds(
                Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(renderer.Render(Vector2.Zero, _smallSquare)))).Points);

            marker.RotationDegrees = new Vector3(30, 0, 0);
            await WaitForFramesAsync(sceneTree, 2);

            MiniMapRenderResult tilted = renderer.Render(Vector2.Zero, _smallSquare);
            (Vector2 tiltedMinimum, Vector2 tiltedMaximum) = MiniMapTestHarness.Bounds(
                Assert.Single(MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(tilted))).Points);

            Assert.Equal(MiniMapRenderStatus.Ready, tilted.Status);
            float uprightDepth = uprightMaximum.Y - uprightMinimum.Y;
            float tiltedDepth = tiltedMaximum.Y - tiltedMinimum.Y;
            Assert.InRange(tiltedDepth / uprightDepth, 0.84f, 0.89f);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }
}
