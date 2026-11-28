using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AlleyCat.IntegrationTests.Motion;
using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// Render-only actor-marker size comparison for the AI-013 follow-up (plan step S3): the shared actor marker
/// scene's icons and labels are too large, so adjacent characters' labels overlap on minimap captures. The fixture
/// matches <see cref="MiniMapFramingSweepIntegrationTests" /> exactly — the real session scene with the production
/// Mali NPC spawned under <c>Actors</c> — and renders once per run with the AI-013 subject settings plus the S5
/// margin decision: <see cref="MiniMapRenderSettings.MarginMetres" /> 0.25 m, default 1024×1024 image, default
/// 100 m inclusion, gold <c>*You*</c> override for <c>char:mali</c>, centred on Mali's live position. The output
/// stem is derived from the marker's authored circle radius, so a run against the pre-reduction scene persists
/// <c>before.svg|png|md</c> and a run against the reduced scene <c>after.svg|png|md</c> under
/// <c>game/temp/minimap-bugfix/sizes/</c>. Assertions cover plumbing only — the fixture installs, the actor marker
/// reports the expected primitive kinds, and the render succeeds and persists both actor labels; the size judgement
/// stays with the user.
/// </summary>
public sealed partial class MiniMapMarkerSizeComparisonIntegrationTests : IDisposable
{
    // Nothing here depends on generated motion: keep every MotionBricks node in this fixture native-free.
    private readonly NativeFreeMotion _nativeFreeMotion = NativeFreeMotion.Install(GetSceneTree());

    /// <inheritdoc />
    public void Dispose() => _nativeFreeMotion.Dispose();

    private const string ProductionScenePath = "res://content/the_veridian_spc/characters/mali/mali_npc.tscn";
    private const string SessionScenePath = "res://content/the_veridian_spc/start.tscn";
    private const string ArtefactDirectory = "res://temp/minimap-bugfix/sizes";

    private const float MarginMetres = 0.25f;

    // The authored circle radius splits the pre-reduction scene (0.3 m) from the reduced one (0.2 m); the stem only
    // names which authored state produced the artefacts, never judges the sizes.
    private const float AuthoredRadiusSplit = 0.25f;

    /// <summary>
    /// Instances the production session exactly as the sibling framing diagnostics do, then renders it once through
    /// the production <see cref="MiniMapRenderer" /> with the AI-013-equivalent subject settings and the S5 margin,
    /// writing the comparison artefact set whose stem reflects the currently authored actor-marker sizes.
    /// </summary>
    [Fact]
    public async Task RealSessionScene_ActorMarkerSizes_RenderComparisonArtefacts()
    {
        StringBuilder report = new();
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
            Name = "MinimapMarkerSizeComparisonRoot"
        };
        fixtureRoot.AddChild(session);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, fixtureRoot);

        try
        {
            int installFrames = await WaitForInstalledMarkersAsync(sceneTree, fixtureRoot);
            IReadOnlyList<MiniMapMarker> markers = DiscoverFixtureMarkers(fixtureRoot);
            Assert.True(markers.Count >= 2,
                $"Expected at least the player and NPC actor markers after {installFrames} frames; found {markers.Count}.");

            MiniMapMarker npcMarker = Assert.Single(markers, npc.IsAncestorOf);
            MiniMapCircle? circle = null;
            MiniMapTriangle? triangle = null;
            MiniMapLabel? label = null;
            foreach (MiniMapPrimitive primitive in npcMarker.Primitives)
            {
                switch (primitive)
                {
                    case MiniMapCircle matchedCircle:
                        circle = matchedCircle;
                        break;
                    case MiniMapTriangle matchedTriangle:
                        triangle = matchedTriangle;
                        break;
                    case MiniMapLabel matchedLabel:
                        label = matchedLabel;
                        break;
                    default:
                        break;
                }
            }

            Assert.NotNull(circle);
            Assert.NotNull(triangle);
            Assert.NotNull(label);

            // The comparison is only meaningful with at least two actor markers in frame: the subject's gold label
            // and a neighbour's identity label are the pair whose pre-reduction overlap the user reported.
            List<MiniMapMarker> actorMarkers = [.. markers.Where(marker => marker.Primitives.Any(static p => p is MiniMapCircle))];
            Assert.True(actorMarkers.Count >= 2,
                $"Expected at least the player and NPC actor markers carrying circle icons; found {actorMarkers.Count}.");

            string stem = circle!.Radius > AuthoredRadiusSplit ? "before" : "after";

            // AI-013 subject restyling exactly as the sibling diagnostics compose it.
            Dictionary<string, MiniMapMarkerOverride> markerOverrides = new()
            {
                ["char:mali"] = new(new Color("ffd700"), "*You*"),
            };

            Vector2 centre = npc is Node3D npcRoot
                ? new Vector2(npcRoot.GlobalPosition.X, npcRoot.GlobalPosition.Z)
                : throw new InvalidOperationException("NPC root must be a Node3D for live centre sampling.");

            MiniMapRenderer renderer = new(fixtureRoot, fixtureRoot);
            MiniMapRenderResult result = renderer.Render(
                centre, new MiniMapRenderSettings(MarginMetres: MarginMetres, MarkerOverrides: markerOverrides));
            string svg = MiniMapTestHarness.Svg(result);
            Image image = MiniMapTestHarness.Image(result);
            MatchCollection ariaLabels = AriaLabelRegex().Matches(svg);

            AppendLine(report, $"stem={stem}");
            AppendLine(report, $"scene={SessionScenePath} + spawned {ProductionScenePath} under '{actorsContainer.GetPath()}'");
            AppendLine(report, $"install_wait_frames={installFrames.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"fixture_markers={markers.Count.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"actor_markers={actorMarkers.Count.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report,
                $"authored_npc_marker: circle_radius={DescribeMetres(circle!.Radius)}"
                + $" triangle_width={DescribeMetres(triangle!.Width)} triangle_length={DescribeMetres(triangle!.Length)}"
                + $" label_text_size={label!.TextSize.ToString(CultureInfo.InvariantCulture)}px"
                + $" label_local_z={DescribeMetres(label.Position.Z)}");
            foreach (MiniMapMarker actorMarker in actorMarkers)
            {
                AppendLine(report,
                    $"actor_marker: path='{actorMarker.GetPath()}'"
                    + $" origin=({DescribeMetres(actorMarker.GlobalPosition.X)},"
                    + $" {DescribeMetres(actorMarker.GlobalPosition.Z)})");
            }

            AppendLine(report,
                $"render: status={result.Status} viewport={MiniMapTestHarness.FragmentNumber(result.Viewport.Position.X)},"
                + $"{MiniMapTestHarness.FragmentNumber(result.Viewport.Position.Y)},"
                + $"{MiniMapTestHarness.FragmentNumber(result.Viewport.Size.X)},"
                + $"{MiniMapTestHarness.FragmentNumber(result.Viewport.Size.Y)}"
                + $" scale={MiniMapTestHarness.FragmentNumber(result.Scale)}"
                + $" margin={MarginMetres.ToString(CultureInfo.InvariantCulture)}m"
                + $" centre=({MiniMapTestHarness.FragmentNumber(centre.X)},{MiniMapTestHarness.FragmentNumber(centre.Y)})");
            AppendLine(report, "render_diagnostics:");
            foreach (MiniMapDiagnostic diagnostic in result.Diagnostics)
            {
                AppendLine(report, $"  - [{diagnostic.Code}] {diagnostic.Reference}: {diagnostic.Message}");
            }

            AppendLine(report, $"svg_aria_label_count={ariaLabels.Count.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, "svg_aria_labels:");
            foreach (Match match in ariaLabels)
            {
                AppendLine(report, $"  - '{match.Groups[1].Value}'");
            }

            string artefactDirectory = ProjectSettings.GlobalizePath(ArtefactDirectory);
            _ = Directory.CreateDirectory(artefactDirectory);
            await File.WriteAllTextAsync(Path.Combine(artefactDirectory, $"{stem}.md"), report.ToString());
            await File.WriteAllTextAsync(Path.Combine(artefactDirectory, $"{stem}.svg"), svg);
            Error pngError = image.SavePng(Path.Combine(artefactDirectory, $"{stem}.png"));

            // Plumbing assertions: a successful 1024×1024 render whose SVG carries both actor labels — the subject's
            // gold *You* label and the neighbour identity labels — or the artefacts cannot show the reported
            // overlap symptom at all.
            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.True(result.Scale > 0f, $"The comparison render reported a non-positive scale ({result.Scale}).");
            Assert.Equal(1024, image.GetWidth());
            Assert.Equal(1024, image.GetHeight());
            Assert.True(ariaLabels.Count >= 2,
                $"Expected at least two rendered labels in the comparison SVG; found {ariaLabels.Count}.");
            Assert.Contains("aria-label=\"*You*\"", svg);
            Assert.Equal(Error.Ok, pngError);
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

    private static void AppendLine(StringBuilder builder, string line)
    {
        _ = builder.AppendLine(line);
        GD.Print($"[minimap-sizes] {line}");
    }

    private static string DescribeMetres(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"aria-label=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AriaLabelRegex();
}
