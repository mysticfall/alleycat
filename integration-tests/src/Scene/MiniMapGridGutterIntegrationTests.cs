using System.Globalization;
using System.Text.RegularExpressions;
using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// Grid-label gutter coverage: grid number labels sit in reserved edge bands that no marker ink reaches, no two grid
/// labels overlap (the shared corner included), labels still name the world coordinate of their grid line, and the
/// output dimensions are unchanged. The fixture reproduces the reported agent-navigation symptom: a saturated
/// rectangle at the left image edge under the Z-label column, and the top-left corner where the X and Z label rows
/// met.
/// </summary>
/// <remarks>
/// Oracles stay independent of the renderer's layout internals: grid label boxes come from the SVG glyph groups
/// (pen origin plus the bundled font's ink bounds), the gutter bands are derived from those boxes, marker ink is
/// detected in the rasterised image by colour saturation (every grid colour is a low-saturation grey and every
/// fixture marker is saturated), and world positions come from a reference marker's rendered polygon.
/// </remarks>
public sealed partial class MiniMapGridGutterIntegrationTests
{
    private const string GridTextFill = "#33424e";
    private const int GridTextSize = 16;

    /// <summary>
    /// The agent-navigation-shaped portrait render (512×1024, AI margin, 40 px minimum spacing) and the default 64 px
    /// spacing both keep every saturated marker pixel out of the grid-label bands, overlap no grid labels, and keep
    /// the image dimensions.
    /// </summary>
    [Fact]
    public async Task PortraitRender_GridLabelsSitInMarkerFreeGuttersWithoutOverlap()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = CreateFixture("MinimapGutterRoot");
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            foreach (float spacing in new[] { 40f, 64f })
            {
                MiniMapRenderResult result = renderer.Render(new Vector2(1f, 1.5f), new MiniMapRenderSettings(
                    MaximumDistance: float.PositiveInfinity,
                    Width: 512,
                    Height: 1024,
                    MarginMetres: 0.25f,
                    GridMinimumSpacingPixels: spacing));
                AssertGutterContract(result, 512, 1024, $"spacing {spacing}");
            }
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// An explicit viewport that cuts through markers (the editor preview's pan) still paints no marker ink into
    /// the grid-label bands: marker geometry beyond the plot area is clipped, not drawn under the labels.
    /// </summary>
    [Fact]
    public async Task ExplicitViewportCuttingMarkers_PaintsNoMarkerInkInGutters()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = CreateFixture("MinimapGutterExplicitRoot");
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            // The Rug spans x -3.3…-1.7 and z -1.6…-0.4; the viewport's top-left corner cuts through it.
            Rect2 viewport = new(new Vector2(-2.5f, -1f), new Vector2(6f, 6f));
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(new MiniMapViewportRequest(
                viewport.GetCenter(),
                viewport,
                new MiniMapRenderSettings(MaximumDistance: float.PositiveInfinity, Width: 600, Height: 600)));
            AssertGutterContract(result, 600, 600, "explicit viewport");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Grid labels name the world coordinate of their grid line: the X label "2" is centred horizontally, and the Z
    /// label "3" vertically, on a reference square authored at world (2, 3).
    /// </summary>
    [Fact]
    public async Task GridLabels_MatchWorldCoordinatesOfReferenceMarker()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = CreateFixture("MinimapGutterCoordinateRoot");
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);

        try
        {
            MiniMapRenderResult result = MiniMapTestHarness.CreateScopedRenderer(root).Render(new Vector2(1f, 1.5f), new MiniMapRenderSettings(
                MaximumDistance: float.PositiveInfinity,
                Width: 512,
                Height: 1024,
                MarginMetres: 0.25f,
                GridMinimumSpacingPixels: 40f));
            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Equal(1f, result.GridStep);
            string svg = MiniMapTestHarness.Svg(result);
            Vector2 reference = MiniMapTestHarness.Centroid(
                MiniMapTestHarness.Polygons(svg).Single(polygon => polygon.Fill == "#00ff00").Points);
            IReadOnlyList<(string Text, Rect2 Box)> labels = GridLabels(svg);
            Rect2 xTwo = labels.Single(label => label.Text == "2" && IsTopRow(label.Box)).Box;
            Rect2 zThree = labels.Single(label => label.Text == "3" && IsLeftColumn(label.Box)).Box;
            Assert.True(MathF.Abs(xTwo.GetCenter().X - reference.X) <= 1.5f,
                $"X label '2' centre {xTwo.GetCenter().X} must sit on the world x = 2 line at {reference.X}.");
            Assert.True(MathF.Abs(zThree.GetCenter().Y - reference.Y) <= 2.5f,
                $"Z label '3' centre {zThree.GetCenter().Y} must sit on the world z = 3 line at {reference.Y}.");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    private static void AssertGutterContract(MiniMapRenderResult result, int width, int height, string scenario)
    {
        Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
        Image image = MiniMapTestHarness.Image(result);
        Assert.Equal(width, image.GetWidth());
        Assert.Equal(height, image.GetHeight());

        string svg = MiniMapTestHarness.Svg(result);
        IReadOnlyList<(string Text, Rect2 Box)> labels = GridLabels(svg);
        Assert.NotEmpty(labels);
        foreach ((string text, Rect2 box) in labels)
        {
            Assert.True(box.Position.X >= 0 && box.Position.Y >= 0 && box.End.X <= width && box.End.Y <= height,
                $"{scenario}: grid label '{text}' box {Describe(box)} must lie inside the image.");
        }

        for (int i = 0; i < labels.Count; i++)
        {
            for (int j = i + 1; j < labels.Count; j++)
            {
                Assert.False(labels[i].Box.Intersects(labels[j].Box),
                    $"{scenario}: grid labels '{labels[i].Text}' {Describe(labels[i].Box)} and '{labels[j].Text}' "
                    + $"{Describe(labels[j].Box)} overlap.");
            }
        }

        // The label bands: X labels form the top row, Z labels the left column (each label box is classified by
        // which edge it hugs). Every pixel of a band must be free of saturated marker ink.
        float topBand = labels.Where(label => IsTopRow(label.Box))
            .Select(label => label.Box.End.Y).DefaultIfEmpty(0).Max();
        float leftBand = labels.Where(label => IsLeftColumn(label.Box))
            .Select(label => label.Box.End.X).DefaultIfEmpty(0).Max();
        Assert.True(topBand > 0 && leftBand > 0, $"{scenario}: both the X-label row and the Z-label column must render.");
        AssertNoSaturatedInk(image, new Rect2(0, 0, width, topBand), $"{scenario}: top label band");
        AssertNoSaturatedInk(image, new Rect2(0, 0, leftBand, height), $"{scenario}: left label band");
    }

    private static bool IsTopRow(Rect2 box) => box.Position.Y < 1.5f * GridTextSize && box.Position.X > 1.5f * GridTextSize;

    private static bool IsLeftColumn(Rect2 box) => box.Position.X < 1.5f * GridTextSize && box.Position.Y > 1.5f * GridTextSize;

    private static void AssertNoSaturatedInk(Image image, Rect2 band, string description)
    {
        for (int y = (int)band.Position.Y; y < (int)Math.Ceiling(band.End.Y); y++)
        {
            for (int x = (int)band.Position.X; x < (int)Math.Ceiling(band.End.X); x++)
            {
                Color pixel = image.GetPixel(x, y);
                float saturation = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                Assert.True(saturation < 0.2f,
                    $"{description} {Describe(band)} contains marker ink at ({x}, {y}): {pixel.ToHtml(false)}.");
            }
        }
    }

    private static IReadOnlyList<(string Text, Rect2 Box)> GridLabels(string svg)
    {
        List<(string, Rect2)> labels = [];
        foreach (Match match in GridLabelRegex().Matches(svg))
        {
            string text = match.Groups["text"].Value;
            Rect2 ink = MiniMapGlyphs.InkBounds(text, GridTextSize);
            Vector2 pen = new(
                float.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                float.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture));
            labels.Add((text, new Rect2(ink.Position + pen, ink.Size)));
        }

        return labels;
    }

    private static Node CreateFixture(string name)
    {
        Node root = new()
        {
            Name = name
        };
        root.AddChild(Box("Rug", new Vector3(-2.5f, 0, -1f), 1.6f, 1.2f, "#e01020", "Rug"));
        root.AddChild(Box("Crate", new Vector3(4.5f, 0, -2f), 1f, 1f, "#ff8800", "Crate"));
        root.AddChild(Box("Bench", new Vector3(-2.5f, 0, 4.5f), 1.6f, 0.5f, "#a05000", "Bench"));
        root.AddChild(Box("Wall", new Vector3(1f, 0, -1.2f), 3f, 0.3f, "#0000ff", null));
        root.AddChild(Box("Reference", new Vector3(2f, 0, 3f), 0.3f, 0.3f, "#00ff00", null));
        root.AddChild(Box("Companion", new Vector3(-1f, 0, -3.5f), 0.4f, 0.4f, "#1030e0", "Companion"));
        root.AddChild(Box("Subject", new Vector3(1f, 0, 1.5f), 0.4f, 0.4f, "#ffd000", "Subject"));
        return root;
    }

    private static MiniMapMarker Box(string name, Vector3 position, float width, float depth, string colour, string? label)
    {
        List<MiniMapPrimitive> primitives =
        [
            new MiniMapRectangle
            {
                Name = "Shape",
                Width = width,
                Depth = depth,
                FillColour = new Color(colour),
            },
        ];
        if (label is not null)
        {
            primitives.Add(new MiniMapLabel
            {
                Name = "Caption",
                Flavour = MiniMapLabelFlavour.Configured,
                Text = label,
                TextSize = 18,
                FillColour = new Color(colour).Darkened(0.4f),
                Position = new Vector3(0, 0, -0.9f),
            });
        }

        MiniMapMarker marker = MiniMapTestHarness.CreateMarker(name, 1, [.. primitives]);
        marker.Position = position;
        return marker;
    }

    private static string Describe(Rect2 box) => string.Create(
        CultureInfo.InvariantCulture,
        $"[{box.Position.X:0.#}, {box.Position.Y:0.#}]–[{box.End.X:0.#}, {box.End.Y:0.#}]");

    [GeneratedRegex(
        @"<g fill=""" + GridTextFill + @""" aria-label=""(?<text>[^""]*)""><path transform=""translate\((?<x>-?[0-9.]+) (?<y>-?[0-9.]+)\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex GridLabelRegex();
}
