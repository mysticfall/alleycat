using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// SCN-002 TA-9 runtime coverage: per-request marker style overrides (TR-19) end to end — colour and label-text
/// restyling of exactly the matching markers, inert no-ops, byte-identity without overrides, the informational
/// <c>UnusedOverride</c> contract (TR-16), shared-<c>FullId</c> styling, loud null-key/entry rejection, unchanged
/// extent/inclusion/order/framing/grid, and application from every render entry that accepts settings.
/// </summary>
public sealed class MiniMapOverrideIntegrationTests
{
    private const string Gold = "#d4af37";

    private static readonly Color _gold = new("d4af37");

    private static string Describe(MiniMapRenderResult result) => result.Diagnostics.Length == 0
        ? $"{result.Status} (no diagnostics)"
        : $"{result.Status}: {string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code} [{diagnostic.Reference}] {diagnostic.Message}"))}";

    /// <summary>Unbounded-inclusion 512×512 settings carrying the given overrides.</summary>
    private static MiniMapRenderSettings Overrides(params (string Key, MiniMapMarkerOverride Override)[] entries) => new(
        MaximumDistance: float.PositiveInfinity,
        Width: 512,
        Height: 512,
        MarkerOverrides: entries.ToDictionary(entry => entry.Key, entry => entry.Override));

    private static int CountOccurrences(string svg, string fragment)
    {
        int count = 0;
        for (int index = svg.IndexOf(fragment, StringComparison.Ordinal); index >= 0;
             index = svg.IndexOf(fragment, index + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// A colour-only override replaces the fill of every primitive of exactly the matching marker — shapes and label
    /// glyphs alike — while every other marker keeps its authored fills (TR-19).
    /// </summary>
    [Fact]
    public async Task ColourOnlyOverride_RestylesExactlyTheMatchingMarkersFills()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapColourOverrideRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") },
            new MiniMapLabel { Name = "Caption", Text = "Alice", TextSize = 16, FillColour = new Color("17202a"), Position = new Vector3(0, 0, -1.3f) })));
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("bob", "Bob", MiniMapTestHarness.CreateMarker("BobMarker", 0,
            new MiniMapCircle { Name = "Pool", Radius = 0.8f, FillColour = new Color("64adce") }),
            new Vector3(6, 0, 0)));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, Overrides(("char:alice", new MiniMapMarkerOverride(Colour: _gold))));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            IReadOnlyList<(string Fill, Vector2[] Points)> polygons = MiniMapTestHarness.Polygons(svg);
            Assert.Equal(2, polygons.Count);
            Assert.Contains(polygons, polygon => polygon.Fill == Gold);
            Assert.Contains(polygons, polygon => polygon.Fill == "#64adce");
            Assert.DoesNotContain(polygons, polygon => polygon.Fill == "#287fa0");

            // The override recolours the label glyphs too: the authored text keeps rendering under the override fill.
            Assert.Contains($"fill=\"{Gold}\" aria-label=\"Alice\"", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("#17202a", svg, StringComparison.Ordinal);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A label-text override replaces the rendered text of every label primitive of the matching marker regardless of
    /// flavour — configured and identity labels alike — while fills keep their authored values (TR-19).
    /// </summary>
    [Fact]
    public async Task LabelTextOverride_ReplacesBothLabelFlavours()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapLabelOverrideRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapLabel { Name = "Nickname", Text = "Stand-in", TextSize = 16, FillColour = new Color("17202a"), Position = new Vector3(0, 0, -1.3f) },
            new MiniMapLabel { Name = "Identity", Flavour = MiniMapLabelFlavour.Identity, TextSize = 16, FillColour = new Color("17202a"), Position = new Vector3(0, 0, 1.3f) },
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") })));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, Overrides(("char:alice", new MiniMapMarkerOverride(LabelText: "You"))));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            Assert.Equal(2, CountOccurrences(svg, "aria-label=\"You\""));
            Assert.DoesNotContain("aria-label=\"char:alice\"", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("aria-label=\"Stand-in\"", svg, StringComparison.Ordinal);

            // Label-only override leaves authored fills in place, shapes and glyphs alike.
            Assert.Contains($"fill=\"#17202a\" aria-label=\"You\"", svg, StringComparison.Ordinal);
            Assert.Contains("#287fa0", svg, StringComparison.Ordinal);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Requests supplying no overrides — the feature absent — render byte-identically to a request carrying an empty
    /// override collection (TR-13/TR-19 determinism: empty collection means none).
    /// </summary>
    [Fact]
    public async Task RequestsWithoutOverrides_RenderByteIdenticallyToEmptyOverrideCollection()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapNoOverrideIdentityRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") },
            new MiniMapLabel { Name = "Identity", Flavour = MiniMapLabelFlavour.Identity, TextSize = 16, Position = new Vector3(0, 0, -1.3f) })));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            MiniMapRenderSettings absent = new(MaximumDistance: float.PositiveInfinity, Width: 512, Height: 512);
            MiniMapRenderSettings empty = new(
                MaximumDistance: float.PositiveInfinity,
                Width: 512,
                Height: 512,
                MarkerOverrides: new Dictionary<string, MiniMapMarkerOverride>());

            MiniMapRenderResult plain = renderer.Render(Vector2.Zero, absent);
            MiniMapRenderResult emptyCollection = renderer.Render(Vector2.Zero, empty);

            Assert.Equal(MiniMapRenderStatus.Ready, plain.Status);
            Assert.Equal(MiniMapRenderStatus.Ready, emptyCollection.Status);
            Assert.Equal(MiniMapTestHarness.Svg(plain), MiniMapTestHarness.Svg(emptyCollection));
            Assert.Empty(plain.Diagnostics);
            Assert.Empty(emptyCollection.Diagnostics);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// An override entry with both <c>Colour</c> and <c>LabelText</c> unset is a legal inert no-op: the matching
    /// marker renders exactly as authored and the matched key is not reported unused (TR-19).
    /// </summary>
    [Fact]
    public async Task BothUnsetOverride_IsInertNoOp()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapInertOverrideRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") },
            new MiniMapLabel { Name = "Identity", Flavour = MiniMapLabelFlavour.Identity, TextSize = 16, Position = new Vector3(0, 0, -1.3f) })));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            MiniMapRenderResult plain = renderer.Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: 512, Height: 512));
            MiniMapRenderResult inert = renderer.Render(Vector2.Zero, Overrides(("char:alice", new MiniMapMarkerOverride())));

            Assert.Equal(MiniMapRenderStatus.Ready, inert.Status);
            Assert.Equal(MiniMapTestHarness.Svg(plain), MiniMapTestHarness.Svg(inert));
            Assert.Empty(inert.Diagnostics);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// An override key matching no discovered marker is reported as the informational <c>UnusedOverride</c>
    /// diagnostic naming the key, and the render continues Ready with the other overrides applied (TR-16/TR-19).
    /// </summary>
    [Fact]
    public async Task UnusedOverrideKey_ReportsInformationalDiagnosticAndStillRenders()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapUnusedOverrideRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") })));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, Overrides(
                    ("char:alice", new MiniMapMarkerOverride(Colour: _gold)),
                    ("char:ghost", new MiniMapMarkerOverride(Colour: _gold))));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Contains(Gold, MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            MiniMapDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("UnusedOverride", diagnostic.Code);
            Assert.Equal("char:ghost", diagnostic.Reference, StringComparer.Ordinal);
            Assert.Contains("'char:ghost'", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("no discovered marker", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A key matching a distance-excluded marker is not unused — group membership is considered before the distance
    /// filter — and overrides never change inclusion: the excluded marker stays absent (TR-16/TR-19).
    /// </summary>
    [Fact]
    public async Task OverrideKeyMatchingDistanceExcludedMarker_IsNotUnusedAndStaysExcluded()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapOutOfRangeOverrideRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") })));
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("bob", "Bob", MiniMapTestHarness.CreateMarker("BobMarker", 0,
            new MiniMapCircle { Name = "Pool", Radius = 0.8f, FillColour = new Color("64adce") }),
            new Vector3(50, 0, 0)));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, new MiniMapRenderSettings(
                MaximumDistance: 5f,
                Width: 512,
                Height: 512,
                MarkerOverrides: new Dictionary<string, MiniMapMarkerOverride> { ["char:bob"] = new(Colour: _gold) }));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            Assert.Contains("#287fa0", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("#64adce", svg, StringComparison.Ordinal);
            Assert.DoesNotContain(Gold, svg, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "UnusedOverride");
            Assert.True(result.Diagnostics.Length == 0, $"Expected no diagnostics, got: {Describe(result)}.");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Markers sharing one resolved <c>FullId</c> are all styled by its override, while other identities keep their
    /// authored fills (TR-19 matching: every matching marker in the render is styled).
    /// </summary>
    [Fact]
    public async Task SharedFullIdMarkers_AreAllStyled()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapSharedFullIdRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alpha", MiniMapTestHarness.CreateMarker("Marker", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("d84156") })));
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Beta", MiniMapTestHarness.CreateMarker("Marker", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("8a2be2") }),
            new Vector3(4, 0, 0)));
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("carol", "Carol", MiniMapTestHarness.CreateMarker("Marker", 0,
            new MiniMapCircle { Name = "Pool", Radius = 0.8f, FillColour = new Color("64adce") }),
            new Vector3(8, 0, 0)));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, Overrides(("char:alice", new MiniMapMarkerOverride(Colour: _gold))));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            IReadOnlyList<(string Fill, Vector2[] Points)> polygons = MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(result));
            Assert.Equal(3, polygons.Count);
            Assert.Equal(2, polygons.Count(polygon => polygon.Fill == Gold));
            Assert.DoesNotContain(polygons, polygon => polygon.Fill is "#d84156" or "#8a2be2");
            Assert.Contains(polygons, polygon => polygon.Fill == "#64adce");
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A null key or a null override entry fails the render loudly with a settings diagnostic — no SVG, no image —
    /// consistent with the other TR-9 settings validation (TR-16/TR-19).
    /// </summary>
    [Fact]
    public async Task NullOverrideKeyOrEntry_FailsLoudly()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapNullOverrideRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") })));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            MiniMapRenderSettings nullEntry = new(
                MaximumDistance: float.PositiveInfinity,
                Width: 512,
                Height: 512,
                MarkerOverrides: new Dictionary<string, MiniMapMarkerOverride> { ["char:alice"] = null! });
            MiniMapRenderSettings nullKey = new(
                MaximumDistance: float.PositiveInfinity,
                Width: 512,
                Height: 512,
                MarkerOverrides: new NullKeyOverrideDictionary());

            MiniMapRenderResult nullEntryResult = renderer.Render(Vector2.Zero, nullEntry);
            Assert.Equal(MiniMapRenderStatus.Failed, nullEntryResult.Status);
            Assert.Null(nullEntryResult.Svg);
            Assert.Null(nullEntryResult.Image);
            MiniMapDiagnostic entryDiagnostic = Assert.Single(nullEntryResult.Diagnostics);
            Assert.Equal("InvalidSetting", entryDiagnostic.Code);
            Assert.Equal("settings", entryDiagnostic.Reference, StringComparer.Ordinal);
            Assert.Contains("char:alice", entryDiagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("null entry", entryDiagnostic.Message, StringComparison.Ordinal);

            MiniMapRenderResult nullKeyResult = renderer.Render(Vector2.Zero, nullKey);
            Assert.Equal(MiniMapRenderStatus.Failed, nullKeyResult.Status);
            Assert.Null(nullKeyResult.Svg);
            Assert.Null(nullKeyResult.Image);
            MiniMapDiagnostic keyDiagnostic = Assert.Single(nullKeyResult.Diagnostics);
            Assert.Equal("InvalidSetting", keyDiagnostic.Code);
            Assert.Contains("null key", keyDiagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Overrides never alter geometry, footprint extent, inclusion, draw order, framing or the grid: the overridden
    /// render differs from the plain render only in the matching marker's fills, polygon for polygon in paint order
    /// (TR-19 extent-and-ordering).
    /// </summary>
    [Fact]
    public async Task Overrides_NeverChangeExtentInclusionDrawOrderFramingOrGrid()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapOverrideStabilityRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateMarker("Walls", 0, new MiniMapRectangle
        {
            Name = "Box",
            Width = 4,
            Depth = 1,
            FillColour = new Color("e4a52c"),
            Position = new Vector3(0, 0, 4),
        }));
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 5,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") },
            new MiniMapLabel { Name = "Identity", Flavour = MiniMapLabelFlavour.Identity, TextSize = 16, Position = new Vector3(0, 0, -1.3f) })));
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("bob", "Bob", MiniMapTestHarness.CreateMarker("BobMarker", 10,
            new MiniMapCircle { Name = "Pool", Radius = 0.8f, FillColour = new Color("64adce") }),
            new Vector3(6, 0, 0)));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            MiniMapRenderResult plain = renderer.Render(Vector2.Zero, new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: 512, Height: 512));
            MiniMapRenderResult overridden = renderer.Render(Vector2.Zero, Overrides(("char:alice", new MiniMapMarkerOverride(Colour: _gold))));

            Assert.Equal(MiniMapRenderStatus.Ready, plain.Status);
            Assert.Equal(MiniMapRenderStatus.Ready, overridden.Status);

            // Framing and grid are untouched: same viewport, uniform scale, adaptive grid step and line counts.
            Assert.Equal(plain.Viewport, overridden.Viewport);
            Assert.Equal(plain.Scale, overridden.Scale);
            Assert.Equal(plain.GridStep, overridden.GridStep);
            Assert.Equal(MiniMapTestHarness.GridLineCounts(MiniMapTestHarness.Svg(plain)), MiniMapTestHarness.GridLineCounts(MiniMapTestHarness.Svg(overridden)));

            // Geometry and paint order are untouched: identical polygon sequence with only the matching marker's fill changed.
            IReadOnlyList<(string Fill, Vector2[] Points)> plainPolygons = MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(plain));
            IReadOnlyList<(string Fill, Vector2[] Points)> overriddenPolygons = MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(overridden));
            Assert.Equal(plainPolygons.Count, overriddenPolygons.Count);
            int restyled = 0;
            for (int index = 0; index < plainPolygons.Count; index++)
            {
                Assert.Equal(plainPolygons[index].Points, overriddenPolygons[index].Points);
                if (plainPolygons[index].Fill != overriddenPolygons[index].Fill)
                {
                    restyled++;
                    Assert.Equal("#287fa0", plainPolygons[index].Fill);
                    Assert.Equal(Gold, overriddenPolygons[index].Fill);
                }
            }

            Assert.Equal(1, restyled);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Overrides apply to every render entry that accepts settings — the automatic centre-based, explicit-viewport
    /// and coverage renders (TR-19 matching); the editor preview supplies none and is unchanged (TR-17).
    /// </summary>
    [Fact]
    public async Task Overrides_ApplyToEveryRenderEntryAcceptingSettings()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapOverrideEntriesRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") })));
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("bob", "Bob", MiniMapTestHarness.CreateMarker("BobMarker", 0,
            new MiniMapCircle { Name = "Pool", Radius = 0.8f, FillColour = new Color("64adce") }),
            new Vector3(6, 0, 0)));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);

            MiniMapRenderResult coverage = renderer.RenderCoverage(Overrides(("char:alice", new MiniMapMarkerOverride(Colour: _gold))));
            Assert.Equal(MiniMapRenderStatus.Ready, coverage.Status);
            Assert.Contains(Gold, MiniMapTestHarness.Svg(coverage), StringComparison.Ordinal);
            Assert.DoesNotContain("#287fa0", MiniMapTestHarness.Svg(coverage), StringComparison.Ordinal);
            Assert.Contains("#64adce", MiniMapTestHarness.Svg(coverage), StringComparison.Ordinal);
            Assert.Empty(coverage.Diagnostics);

            MiniMapViewportRequest request = new(
                Vector2.Zero,
                new Rect2(new Vector2(-6, -6), new Vector2(12, 12)),
                Overrides(("char:alice", new MiniMapMarkerOverride(Colour: _gold))));
            MiniMapRenderResult viewport = renderer.Render(request);
            Assert.Equal(MiniMapRenderStatus.Ready, viewport.Status);
            Assert.Contains(Gold, MiniMapTestHarness.Svg(viewport), StringComparison.Ordinal);
            Assert.DoesNotContain("#287fa0", MiniMapTestHarness.Svg(viewport), StringComparison.Ordinal);
            Assert.Contains("#64adce", MiniMapTestHarness.Svg(viewport), StringComparison.Ordinal);
            Assert.Empty(viewport.Diagnostics);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Glyph diagnostics apply to the overridden text: an override replacing label text with glyphs the bundled Inter
    /// font does not cover reports <c>UnsupportedGlyph</c> naming the marker and omits the glyphs while the render
    /// continues Ready (TR-15/TR-19).
    /// </summary>
    [Fact]
    public async Task OverriddenLabelText_GlyphDiagnosticsApplyToTheOverriddenText()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapOverrideGlyphRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("alice", "Alice", MiniMapTestHarness.CreateMarker("AliceMarker", 0,
            new MiniMapLabel { Name = "Caption", Text = "Alice", TextSize = 16, Position = new Vector3(0, 0, -1.3f) },
            new MiniMapRectangle { Name = "Box", Width = 2, Depth = 2, FillColour = new Color("287fa0") })));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root)
                .Render(Vector2.Zero, Overrides(("char:alice", new MiniMapMarkerOverride(LabelText: "민수"))));

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Contains("#287fa0", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            Assert.DoesNotContain("민수", MiniMapTestHarness.Svg(result), StringComparison.Ordinal);
            MiniMapDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("UnsupportedGlyph", diagnostic.Code);
            Assert.Equal("Alice/AliceMarker", diagnostic.Reference, StringComparer.Ordinal);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Settings-shaped dictionary carrying a null key — unbuildable with <see cref="Dictionary{TKey, TValue}" /> — so
    /// the null-key validation contract (TR-16/TR-19) is exercisable end to end through the public settings record.
    /// </summary>
    private sealed class NullKeyOverrideDictionary : IReadOnlyDictionary<string, MiniMapMarkerOverride>
    {
        public int Count => 1;

        public IEnumerable<string> Keys => [null!];

        public IEnumerable<MiniMapMarkerOverride> Values => [new MiniMapMarkerOverride()];

        public MiniMapMarkerOverride this[string key] => new();

        public bool ContainsKey(string key) => key is null;

        public bool TryGetValue(string key, out MiniMapMarkerOverride value)
        {
            value = new MiniMapMarkerOverride();
            return key is null;
        }

        public IEnumerator<KeyValuePair<string, MiniMapMarkerOverride>> GetEnumerator()
        {
            yield return new(null!, new MiniMapMarkerOverride());
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
