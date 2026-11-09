using System.Globalization;
using System.Text.RegularExpressions;
using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// SCN-002 TA-5/TA-10 runtime coverage for the amended TR-15 fallback chain and the TR-20 label collision rule:
/// Hangul identity labels render natively in Hangul glyphs, mixed-script labels resolve per rune with Latin
/// metrics unchanged, co-located colliding pairs render both labels displaced apart, clear labels stay at their
/// authored anchors, extreme crowding degrades to bounded omission with the informational <c>LabelOverlaps</c>
/// diagnostic, an override-emphasised label is never omitted, and displacement changes no framing, geometry,
/// grid or determinism.
/// </summary>
public sealed partial class MiniMapLabelCollisionAndFallbackIntegrationTests
{
    private static readonly MiniMapRenderSettings _smallSquare = new(MaximumDistance: float.PositiveInfinity, Width: 512, Height: 512);

    /// <summary>
    /// A Korean <c>FullId</c> identity label renders natively in Hangul through the bundled fallback chain — the
    /// AI-013 dissent symptom class — with no <c>UnsupportedGlyph</c> and no <c>MissingIdentity</c> (TR-15/UR-5).
    /// </summary>
    [Fact]
    public async Task HangulIdentityLabel_RendersNativelyInHangulGlyphs()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapHangulIdentityRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateCharacterHost("민수", "Minsu", MiniMapTestHarness.CreateMarker("MinsuMarker", 0,
            new MiniMapLabel { Name = "Identity", Flavour = MiniMapLabelFlavour.Identity, TextSize = 18, FillColour = new Color("17202a") },
            new MiniMapCircle { Name = "Spot", Radius = 0.7f, FillColour = new Color("5523a0") })));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            Assert.Contains("aria-label=\"char:민수\"", svg, StringComparison.Ordinal);
            // Every rune of the canonical FullId renders as its own glyph outline path — five Latin runes and two
            // Hangul — never transliterated, never <text>.
            Assert.Equal(7, CountLabelPaths(svg, "char:민수"));
            Assert.DoesNotContain("<text", svg, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code is "UnsupportedGlyph" or "MissingIdentity");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A mixed-script label resolves per rune: every rune renders a glyph path, and the Latin rune's outline is
    /// byte-identical to the same rune in a Latin-only render — Inter stays first, so Latin metrics are
    /// unchanged — while Latin measurement goldens captured before the fallback chain still hold (TR-15/TA-5).
    /// </summary>
    [Fact]
    public async Task MixedScriptLabel_ResolvesPerRuneWithLatinMetricsUnchanged()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapMixedScriptRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateMarker("Mixed", 0,
            new MiniMapLabel { Name = "Caption", Text = "A강", TextSize = 18, FillColour = new Color("17202a") },
            new MiniMapRectangle { Name = "Box", Width = 1, Depth = 1, FillColour = new Color("287fa0") }));
        Node3D latinRoot = new()
        {
            Name = "MinimapLatinOnlyRoot",
            Position = new Vector3(5, 0, 0)
        };
        latinRoot.AddChild(MiniMapTestHarness.CreateMarker("Latin", 0,
            new MiniMapLabel { Name = "Caption", Text = "A", TextSize = 18, FillColour = new Color("17202a") },
            new MiniMapRectangle { Name = "Box", Width = 1, Depth = 1, FillColour = new Color("287fa0") }));
        root.AddChild(latinRoot);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            Assert.Contains("aria-label=\"A강\"", svg, StringComparison.Ordinal);
            Assert.Equal(2, CountLabelPaths(svg, "A강"));
            Assert.Contains("aria-label=\"A\"", svg, StringComparison.Ordinal);

            // Inter-first: the 'A' outline in the mixed label is the identical path data the Latin-only label renders.
            Assert.Equal(
                FirstLabelPathData(svg, "A"),
                FirstLabelPathData(svg, "A강"));

            // Goldens captured from the pre-chain pipeline (game/temp/minimap-bugfix/probe/latin_goldens.txt):
            // Latin advances and heights must be exactly unchanged with the fallback font bundled.
            Assert.Equal(46.46875f, MiniMapGlyphs.Measure("Crate", 18).X, 5);
            Assert.Equal(18f, MiniMapGlyphs.Measure("Crate", 18).Y, 5);
            Assert.Equal(33.734375f, MiniMapGlyphs.Measure("You", 18).X, 5);
            Assert.Equal(36.421875f, MiniMapGlyphs.Measure("0123", 16).X, 5);
            Assert.Equal(83.5f, MiniMapGlyphs.Measure("char:ayana", 16).X, 5);
            Assert.Equal(27.421875f, MiniMapGlyphs.Measure("-24", 16).X, 5);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A co-located pair whose label boxes intersect at the base position renders both labels, the second
    /// displaced one fixed stack step up — never a merged unreadable blob (TR-20/UA-7).
    /// </summary>
    [Fact]
    public async Task CollidingPair_BothLabelsRenderDisplacedApart()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapCollidingPairRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateMarker("First", 0, LabelledSpot("Left", new Color("17202a"))));
        root.AddChild(MiniMapTestHarness.CreateMarker("Second", 0, LabelledSpot("Right", new Color("6a1b9a"))));
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            Assert.Contains("aria-label=\"Left\"", svg, StringComparison.Ordinal);
            Assert.Contains("aria-label=\"Right\"", svg, StringComparison.Ordinal);
            Assert.Empty(result.Diagnostics);

            // The two markers are exactly co-located, so both labels attempt the same base position: the first
            // keeps it and the second takes the first stack step, one S above (TR-20 defaults, S = 32 px). The
            // horizontal pen difference is exactly the two labels' centring halves — each stays centred on the
            // shared anchor, so displacement added no horizontal drift.
            (float firstX, float firstY) = LabelPen(svg, "Left");
            (float secondX, float secondY) = LabelPen(svg, "Right");
            Assert.Equal((MiniMapGlyphs.Measure("Right", 18).X - MiniMapGlyphs.Measure("Left", 18).X) / 2, firstX - secondX, 4);
            Assert.Equal(MiniMapRenderer.LabelDisplacementStepPixels, firstY - secondY, 4);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// At spacing clear of each other, labels sit at their authored anchors: the rendered baseline equals the
    /// anchor's projected pixel plus the pipeline's baseline offset (TR-20 base position; TR-7 authored offset).
    /// </summary>
    [Fact]
    public async Task ClearPair_LabelsRemainAtAuthoredAnchors()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapClearPairRoot"
        };
        root.AddChild(MiniMapTestHarness.CreateMarker("First", 0, LabelledSpot("Left", new Color("17202a"))));
        MiniMapMarker second = MiniMapTestHarness.CreateMarker("Second", 0, LabelledSpot("Right", new Color("6a1b9a")));
        second.Position = new Vector3(30, 0, 0);
        root.AddChild(second);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            Assert.Empty(result.Diagnostics);

            // The baseline offset is advance-independent, so it pins the rendered label to its authored anchor's
            // projected pixel without re-deriving the pipeline's centring arithmetic.
            Vector2 anchor = MiniMapTestHarness.ExpectedPixel(result, new Vector2(30, -1.3f));
            (_, float rightY) = LabelPen(svg, "Right");
            Assert.Equal(anchor.Y + (18 * 0.36f), rightY, 4);
            (_, float leftY) = LabelPen(svg, "Left");
            Assert.Equal(MiniMapTestHarness.ExpectedPixel(result, new Vector2(0, -1.3f)).Y + (18 * 0.36f), leftY, 4);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// TR-20 shape clearance at the renderer boundary: a label authored to sit under a neighbouring marker's
    /// circle is displaced up the candidate stack until its ink box clears that circle's projected shape box,
    /// while a label anchored over its own marker's rectangle keeps the authored anchor — the own-marker
    /// exemption — and a spacing-clear control pair stays at its anchors (TR-20/UA-7).
    /// </summary>
    [Fact]
    public async Task LabelUnderNeighbouringShape_DisplacesClearOfIt()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapShapeClearanceRoot"
        };
        // Co-located pair: the planter's circle is other-marker shape ink; the milestone's label anchor sits at
        // the shared origin — the circle's centre.
        root.AddChild(MiniMapTestHarness.CreateMarker("Planter", 0,
            new MiniMapCircle { Name = "Bed", Radius = 0.8f, FillColour = new Color("4a854f") }));
        root.AddChild(MiniMapTestHarness.CreateMarker("Milestone", 1, new MiniMapLabel
        {
            Name = "Caption",
            Text = "Milestone",
            TextSize = 18,
            FillColour = new Color("17202a"),
        }));
        // Own-marker control: the crate's label anchor sits over its own rectangle, which never blocks it.
        MiniMapMarker crate = MiniMapTestHarness.CreateMarker("Crate", 2,
            new MiniMapRectangle { Name = "Box", Width = 1.2f, Depth = 1.2f, FillColour = new Color("287fa0") },
            new MiniMapLabel { Name = "Caption", Text = "Crate", TextSize = 18, FillColour = new Color("17202a") });
        crate.Position = new Vector3(4, 0, 0);
        root.AddChild(crate);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);
            Assert.Empty(result.Diagnostics);

            // Engine-observed boxes: the planter circle's pixel AABB from its polygon, and each label's ink box
            // from its SVG pen plus the glyph pipeline's ink bounds.
            (Vector2 bedMin, Vector2 bedMax) = MiniMapTestHarness.Bounds(
                MiniMapTestHarness.Polygons(svg).Single(polygon => polygon.Fill == "#4a854f").Points);
            Rect2 bedBox = new(bedMin, bedMax - bedMin);
            Rect2 milestoneInk = LabelInkBox(svg, "Milestone");
            Rect2 crateInk = LabelInkBox(svg, "Crate");

            // The milestone label rendered somewhere clear of the neighbouring circle's shape box.
            Assert.False(milestoneInk.Intersects(bedBox),
                $"Milestone ink {milestoneInk} must clear the planter circle box {bedBox} (TR-20 shape clearance).");

            // It genuinely displaced: its pen sits above the authored anchor's base pen by whole stack steps.
            (float _, float milestoneY) = LabelPen(svg, "Milestone");
            float basePenY = MiniMapTestHarness.ExpectedPixel(result, Vector2.Zero).Y + (18 * 0.36f);
            float displacement = basePenY - milestoneY;
            Assert.True(displacement > 1f, $"Milestone pen Y {milestoneY} must sit above the authored base pen Y {basePenY}.");
            float remainder = displacement % MiniMapRenderer.LabelDisplacementStepPixels;
            Assert.True(remainder is < 0.01f or > 31.99f,
                $"The displacement {displacement} must be a whole number of stack steps.");

            // Own-marker exemption: the crate label keeps its authored anchor over its own rectangle, whose box
            // its ink genuinely intersects.
            (Vector2 boxMin, Vector2 boxMax) = MiniMapTestHarness.Bounds(
                MiniMapTestHarness.Polygons(svg).Single(polygon => polygon.Fill == "#287fa0").Points);
            Rect2 crateBox = new(boxMin, boxMax - boxMin);
            Assert.True(crateInk.Intersects(crateBox),
                "The control must genuinely anchor the crate label over its own marker's shape.");
            (float _, float crateY) = LabelPen(svg, "Crate");
            Assert.Equal(MiniMapTestHarness.ExpectedPixel(result, new Vector2(4, 0)).Y + (18 * 0.36f), crateY, 4);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// The axis-aligned pixel ink box of one rendered label: its glyph group's pen translate plus the glyph
    /// pipeline's ink bounds — the same box the TR-20 placement pass tested (SVG-level evidence).
    /// </summary>
    private static Rect2 LabelInkBox(string svg, string label)
    {
        (float penX, float penY) = LabelPen(svg, label);
        Rect2 ink = MiniMapGlyphs.InkBounds(label, 18);
        return new Rect2(new Vector2(penX, penY) + ink.Position, ink.Size);
    }

    /// <summary>
    /// Extreme crowding beyond the bounded candidate list degrades gracefully: the render stays Ready, the
    /// lowest-priority (latest-ordered) labels are omitted with the informational <c>LabelOverlaps</c> diagnostic
    /// naming each label and its marker by scope-relative node path, and the earliest-ordered label keeps the
    /// base position (TR-20/TA-10).
    /// </summary>
    [Fact]
    public async Task ExtremeCrowding_OmitsLowestPriorityLabelsWithInformationalDiagnostic()
    {
        const int crowdSize = 30;
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapCrowdRoot"
        };
        for (int index = 1; index <= crowdSize; index++)
        {
            root.AddChild(MiniMapTestHarness.CreateMarker($"Crowd{index:00}", index, LabelledSpot("P", new Color("17202a"))));
        }

        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);

            // The earliest-ordered label owns the base position; the latest-ordered one is omitted.
            Assert.Contains("aria-label=\"P\"", svg, StringComparison.Ordinal);
            (float _, float baseY) = LabelPen(svg, "P", 0);
            Vector2 anchor = MiniMapTestHarness.ExpectedPixel(result, new Vector2(0, -1.3f));
            Assert.Equal(anchor.Y + (18 * 0.36f), baseY, 4);

            // The pass bounds at 21 candidate positions (TR-20 defaults), and narrow single-rune labels pack all
            // 21, so the tail of the crowd omits with the informational LabelOverlaps diagnostic — each naming
            // its label and marker by scope-relative node path.
            MiniMapDiagnostic[] overlaps = [.. result.Diagnostics.Where(diagnostic => diagnostic.Code == "LabelOverlaps")];
            Assert.Equal(
                [.. Enumerable.Range(22, crowdSize - 21).Select(index => $"Crowd{index:00}")],
                [.. overlaps.Select(diagnostic => diagnostic.Reference).Order(StringComparer.Ordinal)]);
            Assert.All(overlaps, diagnostic =>
            {
                Assert.Contains($"{diagnostic.Reference}/Caption", diagnostic.Message, StringComparison.Ordinal);
                Assert.False(diagnostic.Reference.StartsWith('/'), "References must stay scope-relative.");
            });
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code is "UnsupportedGlyph" or "MissingIdentity");

            // One pass, pure function of the snapshot: a repeated render is byte-identical (TR-20 determinism).
            Assert.Equal(svg, MiniMapTestHarness.Svg(MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, _smallSquare)));
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// A label emphasised through a TR-19 marker override is never omitted: under the same extreme crowding that
    /// omits its plain neighbours, the overridden marker's label renders at its authored anchor (TR-20/UA-7).
    /// </summary>
    [Fact]
    public async Task OverriddenLabel_IsNeverOmittedAndPlacesAtItsAnchorUnderCrowding()
    {
        const int crowdSize = 30;
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapCrowdOverrideRoot"
        };
        for (int index = 1; index <= crowdSize; index++)
        {
            root.AddChild(MiniMapTestHarness.CreateCharacterHost(
                $"crowd{index:00}",
                $"Crowd{index:00}",
                MiniMapTestHarness.CreateMarker("CrowdMarker", index, new MiniMapLabel
                {
                    Name = "Identity",
                    Flavour = MiniMapLabelFlavour.Identity,
                    TextSize = 16,
                    FillColour = new Color("17202a"),
                    Position = new Vector3(0, 0, -1.3f),
                })));
        }

        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            // The latest-draw-order member is the first the plain rule would omit; the subject override flips its fate.
            MiniMapRenderSettings settings = new(
                MaximumDistance: float.PositiveInfinity,
                Width: 512,
                Height: 512,
                MarkerOverrides: new Dictionary<string, MiniMapMarkerOverride> { ["char:crowd30"] = new(Colour: new Color("ffd700")) });
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(Vector2.Zero, settings);

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            string svg = MiniMapTestHarness.Svg(result);

            // Exactly one gold overridden label set renders — the crowd renders "char:crowdNN" labels, and the
            // overridden one is present despite its late order, at its authored anchor.
            Assert.Contains("fill=\"#ffd700\" aria-label=\"char:crowd30\"", svg, StringComparison.Ordinal);
            (float _, float subjectY) = LabelPen(svg, "char:crowd30");
            Vector2 anchor = MiniMapTestHarness.ExpectedPixel(result, new Vector2(0, -1.3f));
            Assert.Equal(anchor.Y + (16 * 0.36f), subjectY, 4);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Reference.EndsWith("Crowd30", StringComparison.Ordinal));
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Displacement changes no framing, geometry, paint order, inclusion or grid: a colliding pair's render
    /// matches the same scene with empty label texts — identical viewport, scale, grid step, grid labels and
    /// polygon-for-polygon geometry — while only the label glyphs differ; grid labels keep TR-14's positions
    /// under crowding (TR-20 boundaries).
    /// </summary>
    [Fact]
    public async Task Displacement_PreservesFramingGeometryGridAndDeterminism()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "MinimapDisplacementStabilityRoot"
        };
        MiniMapMarker first = MiniMapTestHarness.CreateMarker("First", 0,
            LabelledSpot("Left", new Color("17202a")),
            new MiniMapRectangle { Name = "Box", Width = 1.2f, Depth = 1.2f, FillColour = new Color("287fa0") });
        MiniMapMarker second = MiniMapTestHarness.CreateMarker("Second", 0,
            LabelledSpot("Right", new Color("6a1b9a")),
            new MiniMapCircle { Name = "Pool", Radius = 0.6f, FillColour = new Color("64adce") });
        root.AddChild(first);
        root.AddChild(second);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            MiniMapRenderResult labelled = renderer.Render(Vector2.Zero, _smallSquare);
            Assert.Equal(MiniMapRenderStatus.Ready, labelled.Status);

            // Empty label text: labels render no glyphs and own no box, so no displacement runs at all. The anchors
            // still count as footprint extent, so framing must be identical.
            first.GetNode<MiniMapLabel>("Caption").Text = string.Empty;
            second.GetNode<MiniMapLabel>("Caption").Text = string.Empty;
            await WaitForFramesAsync(sceneTree, 2);
            MiniMapRenderResult silent = renderer.Render(Vector2.Zero, _smallSquare);
            Assert.Equal(MiniMapRenderStatus.Ready, silent.Status);

            Assert.Equal(labelled.Viewport, silent.Viewport);
            Assert.Equal(labelled.Scale, silent.Scale);
            Assert.Equal(labelled.GridStep, silent.GridStep);
            IReadOnlyList<(string Fill, Vector2[] Points)> labelledPolygons = MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(labelled));
            IReadOnlyList<(string Fill, Vector2[] Points)> silentPolygons = MiniMapTestHarness.Polygons(MiniMapTestHarness.Svg(silent));
            Assert.Equal(labelledPolygons.Count, silentPolygons.Count);
            for (int index = 0; index < labelledPolygons.Count; index++)
            {
                Assert.Equal(labelledPolygons[index].Fill, silentPolygons[index].Fill);
                Assert.Equal(labelledPolygons[index].Points, silentPolygons[index].Points);
            }

            // Grid labels keep TR-14's adaptive positions under marker-label displacement: the numeric glyph groups
            // are identical between the displaced and the glyph-free render.
            Assert.Equal(GridLabelPens(MiniMapTestHarness.Svg(silent)), GridLabelPens(MiniMapTestHarness.Svg(labelled)));

            // Repeated renders of one snapshot stay byte-identical with displacement active.
            first.GetNode<MiniMapLabel>("Caption").Text = "Left";
            second.GetNode<MiniMapLabel>("Caption").Text = "Right";
            await WaitForFramesAsync(sceneTree, 2);
            Assert.Equal(MiniMapTestHarness.Svg(labelled), MiniMapTestHarness.Svg(renderer.Render(Vector2.Zero, _smallSquare)));
            Assert.Empty(renderer.Render(Vector2.Zero, _smallSquare).Diagnostics);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    private static MiniMapLabel LabelledSpot(string text, Color fill) => new()
    {
        Name = "Caption",
        Text = text,
        TextSize = 18,
        FillColour = fill,
        Position = new Vector3(0, 0, -1.3f),
    };

    private static int CountLabelPaths(string svg, string label)
    {
        foreach (Match group in LabelGroupRegex().Matches(svg))
        {
            if (group.Groups["label"].Value == label)
            {
                return group.Groups["paths"].Value.Split("<path ", StringSplitOptions.RemoveEmptyEntries).Length;
            }
        }

        throw new InvalidOperationException($"No glyph group with aria-label '{label}'.");
    }

    private static string FirstLabelPathData(string svg, string label)
    {
        foreach (Match group in LabelGroupRegex().Matches(svg))
        {
            if (group.Groups["label"].Value == label)
            {
                return PathDataRegex().Match(group.Groups["paths"].Value).Groups["data"].Value;
            }
        }

        throw new InvalidOperationException($"No glyph group with aria-label '{label}'.");
    }

    private static (float X, float Y) LabelPen(string svg, string label, int occurrence = 0)
    {
        int seen = 0;
        foreach (Match group in LabelGroupRegex().Matches(svg))
        {
            if (group.Groups["label"].Value != label || seen++ < occurrence)
            {
                continue;
            }

            Match pen = TranslateRegex().Match(group.Groups["paths"].Value);
            return (float.Parse(pen.Groups["x"].Value, CultureInfo.InvariantCulture), float.Parse(pen.Groups["y"].Value, CultureInfo.InvariantCulture));
        }

        throw new InvalidOperationException($"No glyph group with aria-label '{label}'.");
    }

    private static IReadOnlyList<string> GridLabelPens(string svg) =>
    [
        .. LabelGroupRegex().Matches(svg)
            .Where(match => IsNumericLabel(match.Groups["label"].Value))
            .Select(match => match.Groups["label"].Value + "@"
                + TranslateRegex().Match(match.Groups["paths"].Value).Groups["x"].Value + ","
                + TranslateRegex().Match(match.Groups["paths"].Value).Groups["y"].Value)
            .Order(StringComparer.Ordinal),
    ];

    private static bool IsNumericLabel(string label) =>
        label.Length > 0 && (label.All(char.IsDigit) || (label.StartsWith('-') && label[1..].All(char.IsDigit)));

    [GeneratedRegex(@"<g fill=""#[0-9a-fA-F]{6}"" aria-label=""(?<label>[^""]*)"">(?<paths>.*?)</g>", RegexOptions.CultureInvariant)]
    private static partial Regex LabelGroupRegex();

    [GeneratedRegex(@"<path transform=""translate\((?<x>-?[0-9.]+) (?<y>-?[0-9.]+)\)""", RegexOptions.CultureInvariant)]
    private static partial Regex TranslateRegex();

    [GeneratedRegex(@"d=""(?<data>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex PathDataRegex();
}
