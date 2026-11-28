using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Motion;
using AlleyCat.Mind.AI;
using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// Runtime identity diagnostics for the production NPC minimap path (AI-013 symptom: actor identity labels missing
/// from minimap captures). The production fixtures are deliberate: the actor marker reaches the live tree only
/// through <see cref="AlleyCat.Rigging.Installation.RigRoleTemplateSceneInstaller" /> runtime subtree installation
/// (<c>mali_npc.tscn</c> assigns the template to an installer node instead of statically instancing it), so minimal
/// code-built fixtures cannot reproduce the wiring that decides the marker's ancestry. Assertions cover plumbing
/// only — scene instancing, marker discovery and a successful render — never the identity values under diagnosis,
/// which live in the captured artefacts under <c>game/temp/minimap-bugfix/</c>.
/// </summary>
public sealed partial class MiniMapProductionNpcIdentityIntegrationTests : IDisposable
{
    // Nothing here depends on generated motion: keep every MotionBricks node in this fixture native-free.
    private readonly NativeFreeMotion _nativeFreeMotion = NativeFreeMotion.Install(GetSceneTree());

    /// <inheritdoc />
    public void Dispose() => _nativeFreeMotion.Dispose();

    private const string ProductionScenePath = "res://content/the_veridian_spc/characters/mali/mali_npc.tscn";
    private const string SessionScenePath = "res://content/the_veridian_spc/start.tscn";
    private const string DissentSessionScenePath = "res://content/the_price_of_dissent/start.tscn";
    private const string ArtefactDirectory = "res://temp/minimap-bugfix";

    /// <summary>
    /// Instances the production Mali NPC in isolation, lets the runtime installer machinery settle, then dumps every
    /// discovered marker's ancestry, resolved identity and label snapshot text, and renders once through the
    /// production <see cref="MiniMapRenderer" /> with the AI-013-equivalent settings (gold <c>*You*</c> override
    /// keyed by <c>char:mali</c>).
    /// </summary>
    [Fact]
    public async Task ProductionMaliNpc_ActorMarkerIdentity_DiagnosedThroughRuntimeInstallerPath()
    {
        StringBuilder report = new();
        SceneTree sceneTree = GetSceneTree();
        int groupBaseline = sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count;

        // Production fixture: statically instancing the template would bypass the installer path under diagnosis.
        PackedScene productionScene = GD.Load<PackedScene>(ProductionScenePath);
        Assert.NotNull(productionScene);
        Node npc = productionScene.Instantiate();
        Assert.NotNull(npc);
        Node fixtureRoot = new()
        {
            Name = "MinimapProductionNpcDiagnosticsRoot"
        };
        fixtureRoot.AddChild(npc);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, fixtureRoot);

        try
        {
            int installFrames = await WaitForInstalledMarkersAsync(sceneTree, fixtureRoot);
            IReadOnlyList<MiniMapMarker> markers = DiscoverFixtureMarkers(fixtureRoot);
            int groupTotal = sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count;
            Assert.True(markers.Count > 0,
                $"Expected at least one MiniMapMarker installed under the production NPC after {installFrames} frames; "
                + $"group total={groupTotal}, baseline={groupBaseline}.");
            Assert.True(npc is Node3D, $"Production NPC root '{npc.Name}' must be a Node3D for live centre sampling.");

            Dictionary<string, MiniMapMarkerOverride> markerOverrides = ComposeSubjectOverrides();
            MiniMapCaptureContext rawContext = new(ScopeRoot: fixtureRoot, EditorToolContext: false);
            MiniMapCaptureContext overrideContext = new(ScopeRoot: fixtureRoot, EditorToolContext: false, MarkerOverrides: markerOverrides);

            AppendLine(report, $"scene={ProductionScenePath}");
            AppendLine(report, $"install_wait_frames={installFrames.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"group_total_after_install={groupTotal.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"fixture_markers={markers.Count.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"npc_root={DescribeNode(npc)}");
            DumpChildList(report, npc, "npc_direct_children:");

            foreach (MiniMapMarker marker in markers)
            {
                DumpMarker(report, marker, fixtureRoot, rawContext, overrideContext);
            }

            Vector2 centre = ReadLiveCentre(npc);
            _ = await RenderAndPersistAsync(
                report, fixtureRoot, centre, VisualContextCapture.ComposeMinimapSettings("char:mali"), artefactPrefix: "mali_npc");
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, fixtureRoot);
            Assert.Equal(groupBaseline, sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count);
        }
    }

    /// <summary>
    /// Widened diagnostics closer to the live sandbox session: instances <c>start.tscn</c> (holding cell plus the
    /// player character, each carrying its own installer machinery) and spawns the production Mali NPC under the
    /// session's <c>Actors</c> container, then dumps every actor marker's identity twice — through an override-free
    /// context (the identity text non-subject markers show) and through the AI-013 subject override context — and
    /// renders once centred on Mali with the exact AI-013 composed settings (0.25 m margin, 512×1024 portrait,
    /// gold <c>*You*</c>), asserting the S7 collision acceptance: both actor labels legible and
    /// <c>char:ayana</c>'s label ink box clear of the gold subject marker's shape boxes.
    /// </summary>
    [Fact]
    public async Task RealSessionScene_BothActorMarkerIdentities_Diagnosed()
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
            Name = "MinimapRealSessionDiagnosticsRoot"
        };
        fixtureRoot.AddChild(session);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, fixtureRoot);

        try
        {
            int installFrames = await WaitForInstalledMarkersAsync(sceneTree, fixtureRoot);
            IReadOnlyList<MiniMapMarker> markers = DiscoverFixtureMarkers(fixtureRoot);
            int groupTotal = sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count;
            Assert.True(markers.Count >= 2,
                $"Expected at least the player and NPC actor markers after {installFrames} frames; "
                + $"fixture markers={markers.Count}, group total={groupTotal}, baseline={groupBaseline}.");
            Assert.True(npc is Node3D, $"Production NPC root '{npc.Name}' must be a Node3D for live centre sampling.");

            Dictionary<string, MiniMapMarkerOverride> markerOverrides = ComposeSubjectOverrides();
            MiniMapCaptureContext rawContext = new(ScopeRoot: fixtureRoot, EditorToolContext: false);
            MiniMapCaptureContext overrideContext = new(ScopeRoot: fixtureRoot, EditorToolContext: false, MarkerOverrides: markerOverrides);

            AppendLine(report, $"scene={SessionScenePath} + spawned {ProductionScenePath} under '{actorsContainer.GetPath()}'");
            AppendLine(report, $"install_wait_frames={installFrames.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"group_total_after_install={groupTotal.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"fixture_markers={markers.Count.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"session_root={DescribeNode(session)}");
            DumpChildList(report, session.GetNode("Actors"), "actors:");

            foreach (MiniMapMarker marker in markers)
            {
                DumpMarker(report, marker, fixtureRoot, rawContext, overrideContext);
            }

            Vector2 centre = ReadLiveCentre(npc);
            MiniMapRenderResult collision = await RenderAndPersistAsync(
                report, fixtureRoot, centre, VisualContextCapture.ComposeMinimapSettings("char:mali"), artefactPrefix: "s7_collision_after");

            // S7 acceptance over the S2 collision symptom (char:ayana's leading glyphs behind the gold subject
            // circle): both actor labels render, and — TR-20 shape clearance — char:ayana's label ink box
            // intersects no projected shape box of the gold-overridden subject marker. The evidence is SVG-level:
            // the label's glyph-path pen and ink bounds against every gold polygon's pixel AABB.
            string svg = await File.ReadAllTextAsync(Path.Combine(
                ProjectSettings.GlobalizePath(ArtefactDirectory), "s7_collision_after_minimap.svg"));
            Assert.Contains("aria-label=\"char:ayana\"", svg, StringComparison.Ordinal);
            Assert.Contains("fill=\"#ffd700\" aria-label=\"*You*\"", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<text", svg, StringComparison.Ordinal);
            Assert.DoesNotContain(collision.Diagnostics, diagnostic => diagnostic.Code is "MissingIdentity" or "UnsupportedGlyph");
            MiniMapCaptureContext sizeContext = new(ScopeRoot: fixtureRoot, EditorToolContext: false);
            MiniMapMarker ayanaMarker = Assert.Single(markers, marker =>
                marker.ResolveIdentity(sizeContext).FullId == "char:ayana");
            MiniMapLabel ayanaLabel = Assert.Single(ayanaMarker.Primitives.OfType<MiniMapLabel>());
            AssertAyanaLabelClearOfGoldShapes(svg, ayanaLabel.TextSize);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, fixtureRoot);
            Assert.Equal(groupBaseline, sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count);
        }
    }

    /// <summary>
    /// Instances the user's real <c>the_price_of_dissent</c> map (holding cell plus three statically placed characters
    /// whose ids are Korean Hangul), lets the runtime installer machinery settle for the complete marker set, then
    /// renders once through the production renderer centred on the player character with the exact AI-013 composed
    /// settings (0.25 m margin, 512×1024 portrait, gold <c>*You*</c> keyed by <c>char:박소희</c>) and asserts the
    /// AI-013 acceptance outcome: the co-located Hangul identity labels <c>char:강주영</c> and <c>char:이만복</c>
    /// render legibly — present in the SVG as glyph-path groups carrying their aria-label, displaced apart under
    /// crowding rather than merged — with no <c>MissingIdentity</c> and no <c>UnsupportedGlyph</c> for identity
    /// labels, and the gold <c>*You*</c> subject label intact at its authored anchor. The dump, SVG and PNG
    /// artefacts persist the evidence under <c>game/temp/minimap-bugfix/</c> with the <c>s7_dissent_after</c>
    /// prefix, and the viewport assertion pins the engine-observed extent for this scene state.
    /// </summary>
    [Fact]
    public async Task DissentScene_KoreanIdentityLabels_Diagnosed()
    {
        StringBuilder report = new();
        SceneTree sceneTree = GetSceneTree();
        int groupBaseline = sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count;

        PackedScene sessionScene = GD.Load<PackedScene>(DissentSessionScenePath);
        Assert.NotNull(sessionScene);
        Node session = sessionScene.Instantiate();
        Assert.NotNull(session);
        Node? subject = session.GetNodeOrNull("박소희");
        Assert.NotNull(subject);
        Assert.True(subject is Node3D, $"Subject character '{subject.Name}' must be a Node3D for live centre sampling.");

        Node fixtureRoot = new()
        {
            Name = "MinimapDissentDiagnosticsRoot"
        };
        fixtureRoot.AddChild(session);
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, fixtureRoot);

        try
        {
            // Three statically placed characters (one actor marker each) plus the holding cell's wall and prop
            // markers; polling for the full set keeps a lagging installer from silently truncating the evidence.
            const int expectedMarkers = 6;
            int installFrames = await WaitForMarkerSetAsync(sceneTree, fixtureRoot, expectedMarkers);
            IReadOnlyList<MiniMapMarker> markers = DiscoverFixtureMarkers(fixtureRoot);
            int groupTotal = sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count;
            Assert.True(markers.Count >= 4,
                $"Expected the three actor markers plus the holding cell markers after {installFrames} frames; "
                + $"fixture markers={markers.Count}, group total={groupTotal}, baseline={groupBaseline}.");

            Dictionary<string, MiniMapMarkerOverride> markerOverrides = ComposeSubjectOverrides("char:박소희");
            MiniMapCaptureContext rawContext = new(ScopeRoot: fixtureRoot, EditorToolContext: false);
            MiniMapCaptureContext overrideContext = new(ScopeRoot: fixtureRoot, EditorToolContext: false, MarkerOverrides: markerOverrides);

            AppendLine(report, $"scene={DissentSessionScenePath}");
            AppendLine(report, $"install_wait_frames={installFrames.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"group_total_after_install={groupTotal.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"fixture_markers={markers.Count.ToString(CultureInfo.InvariantCulture)}");
            AppendLine(report, $"subject_character={DescribeNode(subject)}");
            DumpChildList(report, session, "session_direct_children:");

            foreach (MiniMapMarker marker in markers)
            {
                DumpMarker(report, marker, fixtureRoot, rawContext, overrideContext);
            }

            Vector2 centre = ReadLiveCentre(subject);
            MiniMapRenderResult dissent = await RenderAndPersistAsync(
                report, fixtureRoot, centre, VisualContextCapture.ComposeMinimapSettings("char:박소희"), artefactPrefix: "s7_dissent_after");

            // AI-013 acceptance over the RED baseline (dissent_identity_dump.txt / dissent_minimap.svg): the two
            // Korean identity labels now reach the SVG as glyph-path groups, zero MissingIdentity, zero
            // UnsupportedGlyph, and the gold *You* subject label intact.
            string svg = await File.ReadAllTextAsync(Path.Combine(
                ProjectSettings.GlobalizePath(ArtefactDirectory), "s7_dissent_after_minimap.svg"));
            Assert.Contains("aria-label=\"char:강주영\"", svg, StringComparison.Ordinal);
            Assert.Contains("aria-label=\"char:이만복\"", svg, StringComparison.Ordinal);
            Assert.Contains("fill=\"#ffd700\" aria-label=\"*You*\"", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<text", svg, StringComparison.Ordinal);
            Assert.DoesNotContain(dissent.Diagnostics, diagnostic => diagnostic.Code == "MissingIdentity");
            Assert.DoesNotContain(dissent.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedGlyph");

            // The pinned 512×1024 portrait frames the cell at the composed 0.25 m margin with the TR-9 2 m guard
            // inert (the content's width dominates): the viewport keeps the plot area's aspect (the image minus the
            // grid-label gutters) and matches the engine-observed width for this scene state (x-span 3.93 m +
            // 2 × 0.25 m = 4.43 m).
            Assert.Equal(512, MiniMapTestHarness.Image(dissent).GetWidth());
            Assert.Equal(1024, MiniMapTestHarness.Image(dissent).GetHeight());
            float plotAspect = dissent.PlotArea.Size.X / dissent.PlotArea.Size.Y;
            Assert.Equal(plotAspect, dissent.Viewport.Size.X / dissent.Viewport.Size.Y, 3);
            Assert.InRange(dissent.Viewport.Size.X, 4.33f, 4.53f);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, fixtureRoot);
            Assert.Equal(groupBaseline, sceneTree.GetNodesInGroup(MiniMapMarker.GroupName).Count);
        }
    }

    private static Dictionary<string, MiniMapMarkerOverride> ComposeSubjectOverrides() => ComposeSubjectOverrides("char:mali");

    private static Dictionary<string, MiniMapMarkerOverride> ComposeSubjectOverrides(string subjectFullId) => new()
    {
        // AI-013 TR-3 fixed subject highlight, exactly as VisualContextCapture.ComposeMinimapSettings composes it.
        [subjectFullId] = new(new Color("ffd700"), "*You*"),
    };

    private static Vector2 ReadLiveCentre(Node npc) => npc is Node3D npcRoot
        ? new Vector2(npcRoot.GlobalPosition.X, npcRoot.GlobalPosition.Z)
        : throw new InvalidOperationException("NPC root must be a Node3D for live centre sampling.");

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

    /// <summary>
    /// Same deferred-install settle as <see cref="WaitForInstalledMarkersAsync" />, but polls until the fixture
    /// contributes the full expected marker count so a lagging per-character installer cannot truncate the dump.
    /// </summary>
    private static async Task<int> WaitForMarkerSetAsync(SceneTree sceneTree, Node fixtureRoot, int expectedMarkers)
    {
        int installFrames = 0;
        for (; installFrames < 600; installFrames++)
        {
            if (DiscoverFixtureMarkers(fixtureRoot).Count >= expectedMarkers)
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

    private static void DumpChildList(StringBuilder report, Node node, string heading)
    {
        _ = report.AppendLine(heading);
        foreach (Node child in node.GetChildren())
        {
            AppendLine(report, $"  - {DescribeNode(child)}");
        }
    }

    /// <summary>
    /// Dumps one marker's ancestry, its identity resolution, and its label snapshot text under both the override-free
    /// context (raw identity text) and the AI-013 subject-override context.
    /// </summary>
    private static void DumpMarker(
        StringBuilder report,
        MiniMapMarker marker,
        Node fixtureRoot,
        MiniMapCaptureContext rawContext,
        MiniMapCaptureContext overrideContext)
    {
        _ = report.AppendLine();
        AppendLine(report,
            $"marker={rawContext.PathFor(marker)} draw_order={marker.DrawOrder.ToString(CultureInfo.InvariantCulture)}");
        _ = report.AppendLine("ancestry (marker parent up to fixture root):");
        for (Node? current = marker.GetParent(); current is not null && current != fixtureRoot; current = current.GetParent())
        {
            string identity = current is IIdentifiable identifiable
                ? $" IIdentifiable Id='{identifiable.Id}' FullId='{identifiable.FullId}'"
                : string.Empty;
            AppendLine(report, $"  - {DescribeNode(current)}{identity}");
        }

        MiniMapIdentityCapture resolved = marker.ResolveIdentity(rawContext);
        AppendLine(report,
            $"resolve_identity: FullId={(resolved.FullId is null ? "<null>" : $"'{resolved.FullId}'")}"
            + $" EditorToolContext={resolved.EditorToolContext}");

        DumpSnapshot(report, marker.CaptureSnapshot(rawContext), "raw");
        DumpSnapshot(report, marker.CaptureSnapshot(overrideContext), "override");
    }

    private static void DumpSnapshot(StringBuilder report, MiniMapMarkerSnapshot snapshot, string contextName)
    {
        AppendLine(report, $"{contextName}_snapshot: origin=({snapshot.Origin.X.ToString("G9", CultureInfo.InvariantCulture)},"
            + $" {snapshot.Origin.Y.ToString("G9", CultureInfo.InvariantCulture)})");
        foreach (IMiniMapElement element in snapshot.Primitives)
        {
            if (element is MiniMapLabelSnapshot label)
            {
                AppendLine(report,
                    $"  label primitive='{label.PrimitivePath}' text={(label.Text is null ? "<null>" : $"'{label.Text}'")}"
                    + $" size={label.TextSizePixels.ToString(CultureInfo.InvariantCulture)} fill={label.Fill}");
            }
            else
            {
                AppendLine(report, $"  primitive='{element.GetType().Name}'");
            }
        }
    }

    /// <summary>
    /// One production render with the exact AI-013 subject settings — composed through
    /// <see cref="VisualContextCapture.ComposeMinimapSettings" /> (0.25 m margin, 512×1024 portrait, gold
    /// <c>*You*</c> override) — live subject centre and default inclusion distance; persists the dump, SVG and
    /// PNG artefacts and returns the render result for acceptance assertions.
    /// </summary>
    /// <returns>The persisted render's result.</returns>
    private static async Task<MiniMapRenderResult> RenderAndPersistAsync(
        StringBuilder report,
        Node fixtureRoot,
        Vector2 centre,
        MiniMapRenderSettings settings,
        string artefactPrefix)
    {
        string artefactDirectory = ProjectSettings.GlobalizePath(ArtefactDirectory);
        _ = Directory.CreateDirectory(artefactDirectory);

        MiniMapRenderer renderer = new(fixtureRoot, fixtureRoot);
        MiniMapRenderResult result = renderer.Render(centre, settings);
        Assert.Equal(MiniMapRenderStatus.Ready, result.Status);

        string svg = MiniMapTestHarness.Svg(result);
        AppendLine(report, string.Empty);
        AppendLine(report, $"render: status={result.Status} viewport={MiniMapTestHarness.FragmentNumber(result.Viewport.Position.X)},"
            + $"{MiniMapTestHarness.FragmentNumber(result.Viewport.Position.Y)},"
            + $"{MiniMapTestHarness.FragmentNumber(result.Viewport.Size.X)},"
            + $"{MiniMapTestHarness.FragmentNumber(result.Viewport.Size.Y)}"
            + $" scale={MiniMapTestHarness.FragmentNumber(result.Scale)} centre=({MiniMapTestHarness.FragmentNumber(centre.X)},"
            + $"{MiniMapTestHarness.FragmentNumber(centre.Y)})");
        _ = report.AppendLine("render_diagnostics:");
        foreach (MiniMapDiagnostic diagnostic in result.Diagnostics)
        {
            AppendLine(report, $"  - [{diagnostic.Code}] {diagnostic.Reference}: {diagnostic.Message}");
        }

        // Labels render as glyph outlines, so a well-formed render carries no literal <text> elements; the scan
        // documents what the SVG actually contains while snapshot texts above carry the readable evidence.
        AppendLine(report,
            $"svg_text_element_count={TextElementRegex().Matches(svg).Count.ToString(CultureInfo.InvariantCulture)}");
        AppendLine(report, $"svg_length={svg.Length.ToString(CultureInfo.InvariantCulture)}");

        // Glyph groups carry the label text as aria-label, so this inventory is exactly the set of labels that
        // produced SVG output; labels omitted by the glyph pipeline appear above as diagnostics instead.
        MatchCollection ariaLabels = AriaLabelRegex().Matches(svg);
        AppendLine(report, $"svg_aria_label_count={ariaLabels.Count.ToString(CultureInfo.InvariantCulture)}");
        AppendLine(report, "svg_aria_labels:");
        foreach (Match match in ariaLabels)
        {
            AppendLine(report, $"  - '{match.Groups[1].Value}'");
        }

        await File.WriteAllTextAsync(Path.Combine(artefactDirectory, $"{artefactPrefix}_identity_dump.txt"), report.ToString());
        await File.WriteAllTextAsync(Path.Combine(artefactDirectory, $"{artefactPrefix}_minimap.svg"), svg);
        Error pngError = MiniMapTestHarness.Image(result).SavePng(Path.Combine(artefactDirectory, $"{artefactPrefix}_minimap.png"));
        Assert.Equal(Error.Ok, pngError);
        return result;
    }

    private static string DescribeNode(Node node) =>
        $"name='{node.Name}' godot_type={node.GetClass()} managed_type={node.GetType().FullName}";

    private static void AppendLine(StringBuilder builder, string line)
    {
        _ = builder.AppendLine(line);
        GD.Print($"[minimap-diag] {line}");
    }

    /// <summary>
    /// SVG-level TR-20 shape-clearance evidence (S7 acceptance): reconstructs <c>char:ayana</c>'s label ink box
    /// from the persisted render — its glyph group's pen translate plus the glyph pipeline's ink bounds at the
    /// authored size — and asserts it intersects no projected shape box of the gold-overridden subject marker,
    /// the previously observed occlusion. Prints the decisive pen/box coordinates for the evidence record.
    /// </summary>
    private static void AssertAyanaLabelClearOfGoldShapes(string svg, int ayanaLabelSize)
    {
        Match pen = AyanaLabelPenRegex().Match(svg);
        Assert.True(pen.Success, "The render must carry char:ayana's glyph group with a pen translate.");
        Rect2 ink = MiniMapGlyphs.InkBounds("char:ayana", ayanaLabelSize);
        Rect2 ayanaInkBox = new(
            new Vector2(
                float.Parse(pen.Groups["x"].Value, CultureInfo.InvariantCulture) + ink.Position.X,
                float.Parse(pen.Groups["y"].Value, CultureInfo.InvariantCulture) + ink.Position.Y),
            ink.Size);

        List<Rect2> goldBoxes = [];
        foreach ((string fill, Vector2[] points) in MiniMapTestHarness.Polygons(svg).Where(polygon => polygon.Fill == "#ffd700"))
        {
            (Vector2 minimum, Vector2 maximum) = MiniMapTestHarness.Bounds(points);
            goldBoxes.Add(new Rect2(minimum, maximum - minimum));
        }

        Assert.NotEmpty(goldBoxes);
        foreach (Rect2 gold in goldBoxes)
        {
            Assert.False(ayanaInkBox.Intersects(gold),
                $"char:ayana's label ink box {DescribeBox(ayanaInkBox)} must clear the gold subject shape box {DescribeBox(gold)}"
                    + " (TR-20 shape clearance).");
        }

        GD.Print($"[minimap-s7] ayana label ink box {DescribeBox(ayanaInkBox)} clears gold subject shape boxes: "
            + string.Join("; ", goldBoxes.Select(DescribeBox)));
    }

    private static string DescribeBox(Rect2 box) =>
        $"[{box.Position.X.ToString("0.##", CultureInfo.InvariantCulture)}, {box.Position.Y.ToString("0.##", CultureInfo.InvariantCulture)}]"
            + $" × [{box.End.X.ToString("0.##", CultureInfo.InvariantCulture)}, {box.End.Y.ToString("0.##", CultureInfo.InvariantCulture)}]";

    [GeneratedRegex(@"<text\b", RegexOptions.CultureInvariant)]
    private static partial Regex TextElementRegex();

    [GeneratedRegex(@"aria-label=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AriaLabelRegex();

    [GeneratedRegex(
        @"<g fill=""#[0-9a-fA-F]{6}"" aria-label=""char:ayana"">.*?<path transform=""translate\((?<x>-?[0-9.]+) (?<y>-?[0-9.]+)\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex AyanaLabelPenRegex();
}
