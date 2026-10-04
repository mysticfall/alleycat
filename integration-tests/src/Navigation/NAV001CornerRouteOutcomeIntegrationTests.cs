using System.Globalization;
using System.Text;
using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Outcome-only NAV-001 corner-route regression: a behind-the-cylinder destination issued directly
/// from the spawn Idle, whose runtime navmesh path must bend around the authored cylinder obstacle.
/// It drives the authored navigation test scene exclusively through the public
/// <see cref="INavigation"/> request/outcome API and observes only the actor transform, the rendered
/// skeleton, the AnimationTree graph state, and runtime <see cref="NavigationServer3D"/> queries, so
/// it stays valid across any rebuild of the NPC arrival/route machinery.
/// </summary>
public sealed class NAV001CornerRouteOutcomeIntegrationTests
{
    private const float PlanarPositionToleranceMetres = 0.01f;
    private const float YawToleranceDegrees = 1f;

    // String-pulling artifact reduction of the raw query polyline (NOT user waypoints): consecutive
    // duplicate portals and sub-degree collinear portal chains are removed before any ordered-progress
    // observation. True bends of this scene's around-the-cylinder route are 9.5 and 63.4 degrees.
    private const float DuplicatePointToleranceMetres = 0.001f;
    private const float CollinearTurnToleranceRadians = 0.017453293f;

    // Facing +Z is yaw pi for a destination transform basis (forward -Z rotated by pi).
    private const float FacingZYawRadians = Mathf.Pi;

    private static readonly Vector3 _cornerDestination = new(0.45f, 0f, 5.4f);

    private const double RequestHarnessTimeoutSeconds = 20.0;
    private const int PostTerminalPhysicsTicks = 60;

    private static Vector2 PlanarXZ(Vector3 value) => new(value.X, value.Z);

    /// <summary>
    /// Reduces the raw query polyline to its true direction-change corners: consecutive duplicate portals
    /// (below 1 mm apart) and interior points whose incoming/outgoing turn is below 1 degree are
    /// string-pulling artifacts, not waypoints. Returns each surviving interior corner with its outgoing
    /// leg direction, in route order.
    /// </summary>
    private static List<(Vector2 Corner, Vector2 Outgoing)> ReduceQueryCorners(Vector3[] path)
    {
        List<Vector2> points = [];
        foreach (Vector3 point in path)
        {
            Vector2 planar = PlanarXZ(point);
            if (points.Count == 0 || planar.DistanceTo(points[^1]) > DuplicatePointToleranceMetres)
            {
                points.Add(planar);
            }
        }

        var corners = new List<(Vector2 Corner, Vector2 Outgoing)>();
        for (int index = 1; index < points.Count - 1; index++)
        {
            Vector2 incoming = points[index] - points[index - 1];
            Vector2 outgoing = points[index + 1] - points[index];
            if (incoming.LengthSquared() <= 0f || outgoing.LengthSquared() <= 0f)
            {
                continue;
            }

            float turn = MathF.Abs(Mathf.Acos(Mathf.Clamp(incoming.Normalized().Dot(outgoing.Normalized()), -1f, 1f)));
            if (turn >= CollinearTurnToleranceRadians)
            {
                corners.Add((points[index], outgoing.Normalized()));
            }
        }

        return corners;
    }

    /// <summary>
    /// Signed projection of a pose onto a reduced corner's passing half-plane (the line through the corner
    /// with the outgoing leg direction as normal): non-negative once the actor is past the corner.
    /// </summary>
    private static float CornerPlaneOffset(Vector2 position, Vector2 corner, Vector2 outgoing)
        => (position - corner).Dot(outgoing);

    private static void AppendNavLog(
        StringBuilder navLog,
        INavigation navigation,
        Node3D actor,
        HashSet<long> seenOutcomes,
        StringBuilder outcomeLog)
    {
        ulong tick = Engine.GetPhysicsFrames();
        Vector3 origin = actor.GlobalPosition;
        NavigationCurrentStatus status = navigation.CurrentStatus;
        Vector3[] path = navigation.CurrentPath;
        Vector3 first = path.Length > 0 ? path[0] : Vector3.Zero;
        Vector3 last = path.Length > 0 ? path[^1] : Vector3.Zero;
        _ = navLog.AppendLine(FormattableString.Invariant(
            $"{tick},{origin.X:R},{origin.Y:R},{origin.Z:R},{Mathf.RadToDeg(NAV001ArrivalOutcomeIntegrationTests.YawOf(actor.GlobalTransform)):R},{status.Status},{status.Phase},{status.Revision},{(status.ActiveRequest is { } active ? $"{active.RequestID}/{active.Generation}" : "none")},{path.Length},{navigation.CurrentPathIndex},{(path.Length > 0 ? first.X : float.NaN):R},{(path.Length > 0 ? first.Z : float.NaN):R},{(path.Length > 0 ? last.X : float.NaN):R},{(path.Length > 0 ? last.Z : float.NaN):R},{(navigation is NavigationBase ownerBase && ownerBase.UsedAcceptedPathFallbackForLastPoll ? 1 : 0)},{navigation.TerminalOutcomes.Count}"));

        IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes = navigation.TerminalOutcomes;
        foreach (NavigationTerminalOutcomeRecord outcome in outcomes)
        {
            if (seenOutcomes.Add(outcome.Sequence))
            {
                _ = outcomeLog.AppendLine(CultureInfo.InvariantCulture,
                    $"outcome seq={outcome.Sequence} request={outcome.Request.RequestID}/{outcome.Request.Generation} revision={outcome.Revision} {outcome.Outcome}/{outcome.Reason} requested=({outcome.RequestedTransform.X:R},{outcome.RequestedTransform.Z:R},yaw {outcome.RequestedTransform.YawRadians:R}) final=({outcome.FinalTransform.X:R},{outcome.FinalTransform.Z:R},yaw {outcome.FinalTransform.YawRadians:R}) error={outcome.Error.PlanarDistance * 1000:0.###} mm/{Mathf.RadToDeg(outcome.Error.YawRadians):0.###}° margins={outcome.Margins.PlanarDistance * 1000:0.###} mm/{Mathf.RadToDeg(outcome.Margins.YawRadians):0.###}° accepted_tick={outcome.AcceptancePhysicsTick} boundary_tick={outcome.BoundaryPhysicsTick} terminal_tick={outcome.TerminalPhysicsTick} successor={(outcome.SuccessorRequest is { } successor ? $"{successor.RequestID}/{successor.Generation}" : "none")}");
            }
        }
    }

    /// <summary>
    /// The authored navigation surface of the loaded scene: the union of every <see cref="NavigationMesh"/> polygon
    /// of its <see cref="NavigationRegion3D"/>, in world XZ. This is the independent whole-mesh oracle for the
    /// realised trajectory (the approximate void rectangles above remain as the named authored voids).
    /// </summary>
    internal sealed record AuthoredNavigationSurface(Vector2[] Vertices, int[][] Polygons)
    {
        /// <summary>Cross-product slack (m²) for points exactly on a polygon edge, as authored vertices and sampled
        /// poses round differently; on-edge points must count as on-surface.</summary>
        private const float EdgeSlackSquaredMetres = 1e-6f;

        public int PolygonCount => Polygons.Length;

        public static AuthoredNavigationSurface FromRegion(NavigationRegion3D region)
        {
            NavigationMesh mesh = region.NavigationMesh;
            Vector2[] vertices = [.. mesh.Vertices.Select(vertex => new Vector2(vertex.X, vertex.Z))];
            int[][] polygons = [.. mesh.Polygons.Select(polygon => polygon.AsInt32Array())];
            Assert.True(polygons.Length > 0, "SETUP: the scene's navigation mesh has no polygons.");
            foreach (int[] polygon in polygons)
            {
                // Each authored polygon is convex (the winding-agnostic inside test below relies on it).
                for (int index = 0; index < polygon.Length; index++)
                {
                    Vector2 a = vertices[polygon[index]];
                    Vector2 b = vertices[polygon[(index + 1) % polygon.Length]];
                    Vector2 c = vertices[polygon[(index + 2) % polygon.Length]];
                    float turn = Cross(a, b, c);
                    Assert.True(turn is >= -EdgeSlackSquaredMetres or <= EdgeSlackSquaredMetres,
                        $"SETUP: authored navmesh polygon [{string.Join(',', polygon)}] is not convex.");
                }
            }

            return new AuthoredNavigationSurface(vertices, polygons);
        }

        /// <summary>Depth of one world XZ position below the authored polygon union: zero when on it (inside any
        /// polygon, with edge slack), otherwise the distance to the nearest polygon edge. Shared offline oracle
        /// for composed-trajectory containment checks.</summary>
        internal float OffSurfaceDepth(Vector2 planar)
        {
            foreach (int[] polygon in Polygons)
            {
                if (IsInside(planar, polygon))
                {
                    return 0f;
                }
            }

            return DistanceToSurface(planar);
        }

        private bool IsInside(Vector2 point, int[] polygon)
        {
            bool anyPositive = false;
            bool anyNegative = false;
            for (int index = 0; index < polygon.Length; index++)
            {
                float cross = Cross(Vertices[polygon[index]], Vertices[polygon[(index + 1) % polygon.Length]], point);
                anyPositive |= cross > EdgeSlackSquaredMetres;
                anyNegative |= cross < -EdgeSlackSquaredMetres;
            }

            return !(anyPositive && anyNegative);
        }

        private float DistanceToSurface(Vector2 point)
        {
            float best = float.MaxValue;
            foreach (int[] polygon in Polygons)
            {
                for (int index = 0; index < polygon.Length; index++)
                {
                    Vector2 a = Vertices[polygon[index]];
                    Vector2 b = Vertices[polygon[(index + 1) % polygon.Length]];
                    Vector2 segment = b - a;
                    float parameter = Math.Clamp(segment.Dot(point - a) / Math.Max(segment.LengthSquared(), 1e-12f), 0f, 1f);
                    best = MathF.Min(best, point.DistanceTo(a + (segment * parameter)));
                }
            }

            return best;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
    }

    private static string DescribeOutcomes(IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes)
        => outcomes.Count == 0
            ? "none"
            : string.Join("; ", outcomes.Select(outcome =>
                $"{outcome.Outcome}/{outcome.Reason} at tick {outcome.TerminalPhysicsTick} (error {outcome.Error.PlanarDistance * 1000:0.###} mm)"));

    /// <summary>
    /// From-idle obstacle destination regression (CORNER-idle): the user-reported blocker — issuing the
    /// behind-the-cylinder destination (0.45, 0, 5.4) DIRECTLY from the spawn Idle (no staging approach, no
    /// mid-walk replacement) failed every obstacle-avoiding request with "route following could not continue
    /// (the next route leg did not plan); holding bound exhausted", because PlanSection's Idle branch could
    /// only build straight forward walk runs while the first route station is purely route-geometric (one
    /// section horizon along the navmesh path, ~1.6 m, heading never an input). This scenario issues the
    /// destination from Idle and requires one Completed outcome, A1 within tolerance, exact 60-tick
    /// post-terminal stillness, the fail-closed A4 pairing, authored-surface containment of every realised
    /// pose and consecutive midpoint, and ordered crossing of the runtime query's reduced corners. All A2
    /// metadata-planted runs are listed honestly (the ≤5 mm bound is reported; IK-side reach-release
    /// families may legitimately stay red and are never masked or filtered).
    /// </summary>
    [Fact]
    public async Task CornerRoute_FromIdleObstacleDestination_Completes()
    {
        SceneTree sceneTree = GetSceneTree();
        int previousStepLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NAV001ArrivalOutcomeIntegrationTests.NavigationTestScenePath).Instantiate(), exactMatch: false);
        AnimationGraphPlantedStateRecorder? recorder = null;

        try
        {
            Node3D actor = root.GetNode<Node3D>(NAV001ArrivalOutcomeIntegrationTests.ActorNodeName);
            NAV001ArrivalOutcomeIntegrationTests.PrepareActorBeforeTreeEntry(actor);
            sceneTree.Root.AddChild(root);

            INavigation navigation = await NAV001ArrivalOutcomeIntegrationTests.WaitForReadyNavigationAsync(sceneTree, root, actor);
            NPCNavigation owner = Assert.IsType<NPCNavigation>(navigation, exactMatch: false);
            Assert.True(owner.IsMovementAuthorityActive, "SETUP: NPCNavigation is not the active movement authority.");

            recorder = NAV001ArrivalOutcomeIntegrationTests.BindRecorder(actor);
            recorder.Attach();
            await WaitForPhysicsFramesAsync(sceneTree, NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);
            NAV001ArrivalOutcomeIntegrationTests.AssertIdleWarmUp(recorder, actor);

            int ticksPerSecond = Engine.PhysicsTicksPerSecond;
            var navLog = new StringBuilder(
                "tick,actor_x,actor_y,actor_z,actor_yaw_deg,status,phase,revision,active_request,path_points,path_index,path_first_x,path_first_z,path_last_x,path_last_z,accepted_fallback,outcome_total\n");
            var outcomeLog = new StringBuilder();
            var seenOutcomes = new HashSet<long>();

            // ---- The destination behind the cylinder, issued DIRECTLY from the spawn Idle. ----
            NavigationRequestIdentity identity = NAV001ArrivalOutcomeIntegrationTests.IssueRequest(
                navigation, new NAV001ArrivalOutcomeIntegrationTests.ArrivalRequest(_cornerDestination, FacingZYawRadians), "corner-from-idle", 0);
            ulong windowStart = Engine.GetPhysicsFrames();

            NavigationTerminalOutcomeRecord? outcome = null;
            int harnessTimeoutTicks = (int)Math.Ceiling(RequestHarnessTimeoutSeconds * ticksPerSecond);
            ulong lastTick = windowStart;
            for (int step = 0; step <= harnessTimeoutTicks + PostTerminalPhysicsTicks; step++)
            {
                bool enough = outcome is { } finished
                    && lastTick - finished.TerminalPhysicsTick >= PostTerminalPhysicsTicks;
                if (enough || (outcome is null && lastTick - windowStart >= (ulong)harnessTimeoutTicks))
                {
                    break;
                }

                await WaitForNextPhysicsFrameAsync(sceneTree);
                lastTick = Engine.GetPhysicsFrames();
                AppendNavLog(navLog, navigation, actor, seenOutcomes, outcomeLog);
                outcome ??= NAV001ArrivalOutcomeIntegrationTests.FindOutcome(navigation, identity);
            }

            await WaitForFramesAsync(sceneTree, 1);
            recorder.Detach();

            var failures = new List<string>();
            var report = new StringBuilder();
            List<NavigationTerminalOutcomeRecord> allOutcomes = [.. navigation.TerminalOutcomes];
            _ = report.AppendLine(CultureInfo.InvariantCulture,
                $"scenario=corner-from-idle window_ticks={windowStart}..{lastTick} samples={recorder.Samples.Count} ticks_per_second={ticksPerSecond}");
            _ = report.AppendLine($"outcomes: {DescribeOutcomes(allOutcomes)}");
            if (allOutcomes.Count != 1 || allOutcomes[0].Request != identity)
            {
                failures.Add($"Outcomes: expected exactly one terminal outcome for the single from-idle request, got {DescribeOutcomes(allOutcomes)}.");
            }
            else if (allOutcomes[0].Outcome != NavigationTerminalOutcome.Completed)
            {
                failures.Add(FormattableString.Invariant(
                    $"Outcomes: the from-idle obstacle destination did not complete: {allOutcomes[0].Outcome}/{allOutcomes[0].Reason} at tick {allOutcomes[0].TerminalPhysicsTick} (error {allOutcomes[0].Error.PlanarDistance * 1000:0.###} mm); expected Completed (the user-reported 'route following could not continue' failure)."));
            }

            string a2RunsCsv = string.Empty;
            if (allOutcomes.Count == 1 && allOutcomes[0].Outcome is NavigationTerminalOutcome.Completed or NavigationTerminalOutcome.Failed)
            {
                ulong terminalTick = allOutcomes[0].TerminalPhysicsTick;
                PlantedStateSample[] window = NAV001ArrivalOutcomeIntegrationTests.RequireCompleteWindow(recorder, windowStart, lastTick);
                NavigationRegion3D region = root.GetNode<NavigationRegion3D>("NavigationRegion3D");
                var surface = AuthoredNavigationSurface.FromRegion(region);
                EvaluateFromIdleCriteria(
                    actor, navigation, window, windowStart, terminalTick, _cornerDestination, FacingZYawRadians, surface, region, report, failures, out a2RunsCsv);
            }

            string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
                OS.GetEnvironment("ALLEYCAT_NAV001_IDLE_SECTION_EVIDENCE") is { Length: > 0 } configured
                    ? $"res://temp/{configured.TrimStart('/')}"
                    : "res://temp/nav001-corner-idle-section/run-01/scenario"));
            _ = Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "corner-from-idle-report.txt"), report.ToString());
            await File.WriteAllTextAsync(Path.Combine(directory, "corner-from-idle-nav.txt"), outcomeLog.Append(navLog).ToString());
            await File.WriteAllTextAsync(Path.Combine(directory, "corner-from-idle-samples.csv"),
                recorder.Samples.Count > 0 ? NAV001ArrivalOutcomeIntegrationTests.FormatSamplesCsv(recorder.Samples) : string.Empty);
            await File.WriteAllTextAsync(Path.Combine(directory, "corner-from-idle-runs.csv"), a2RunsCsv);
            Assert.True(failures.Count == 0, $"{failures.Count} from-idle corner criterion failure(s):\n  {string.Join("\n  ", failures)}\n{report}");
        }
        finally
        {
            recorder?.Detach();
            Engine.MaxPhysicsStepsPerFrame = previousStepLimit;
            root.Free();
        }
    }

    /// <summary>Evaluates the from-idle scenario's criteria: A1, exact A3 stillness, the fail-closed A4
    /// pairing, authored-surface containment of realised poses and midpoints, ordered reduced-corner
    /// crossing, and an honest listing of every A2 metadata-planted run.</summary>
    private static void EvaluateFromIdleCriteria(
        Node3D actor,
        INavigation navigation,
        PlantedStateSample[] window,
        ulong windowStart,
        ulong terminalTick,
        Vector3 destination,
        float destinationYawRadians,
        AuthoredNavigationSurface surface,
        NavigationRegion3D region,
        StringBuilder report,
        List<string> failures,
        out string a2RunsCsv)
    {
        // ---- A1: terminal accuracy. ----
        PlantedStateSample? terminalSample = window.FirstOrDefault(sample => sample.Tick == terminalTick);
        Transform3D terminalTransform = terminalSample?.Actor ?? actor.GlobalTransform;
        float planarError = NAV001ArrivalOutcomeIntegrationTests.Planar(terminalTransform.Origin)
            .DistanceTo(NAV001ArrivalOutcomeIntegrationTests.Planar(destination));
        float yawErrorDegrees = MathF.Abs(Mathf.RadToDeg(
            Mathf.AngleDifference(destinationYawRadians, NAV001ArrivalOutcomeIntegrationTests.YawOf(terminalTransform))));
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"A1: terminal tick {terminalTick} planar_error={planarError * 1000:0.###} mm yaw_error={yawErrorDegrees:0.####} deg");
        if (planarError > PlanarPositionToleranceMetres || yawErrorDegrees > YawToleranceDegrees)
        {
            failures.Add($"A1: terminal error {planarError * 1000:0.###} mm / {yawErrorDegrees:0.####} deg exceeds 10 mm / 1 deg.");
        }

        // ---- A3: exact stillness, all twelve transform components, over the full 60-tick tail. ----
        PlantedStateSample[] tail = [.. window.Where(sample => sample.Tick > terminalTick && sample.Tick <= terminalTick + PostTerminalPhysicsTicks)];
        int stillTicks = tail.Count(sample => sample.Actor == terminalTransform);
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"A3: {stillTicks} of {tail.Length} post-terminal ticks match the terminal transform on all components exactly");
        if (tail.Length != PostTerminalPhysicsTicks || stillTicks != PostTerminalPhysicsTicks)
        {
            failures.Add($"A3: expected exactly {PostTerminalPhysicsTicks} exactly-still post-terminal ticks, got {stillTicks} of {tail.Length}.");
        }

        // ---- A4: fail-closed movement-diagnostics pairing over the request's lifetime (window start
        //      through the terminal tick; the settling tail has no movement owner by design). ----
        var diagnostics = new List<MovementDiagnosticsPairing.DiagnosticSample>();
        foreach (NPCNavigationMovementSample sample in ((NPCNavigation)navigation).MovementSamples)
        {
            diagnostics.Add(new MovementDiagnosticsPairing.DiagnosticSample(
                sample.PhysicsTick, sample.SyntheticSemantic.X, sample.SyntheticSemantic.Y, sample.SyntheticYawRadians));
        }

        List<ulong> doubleSupportTicks =
        [.. window.Where(sample => sample.Planted == new PlantedFeetState(true, true) && sample.Tick <= terminalTick)
            .Select(sample => sample.Tick)];
        MovementDiagnosticsPairing.Evaluation pairing = MovementDiagnosticsPairing.Evaluate(diagnostics, doubleSupportTicks);
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"A4: measured over {pairing.PairedTicks} of {pairing.DoubleSupportTicks} independently double-support ticks: {pairing.Violations} with non-zero applied synthetic correction");
        if (!pairing.Problems.IsEmpty)
        {
            failures.Add($"A4: incomplete movement-diagnostics pairing — {string.Join("; ", pairing.Problems)}.");
        }
        else if (pairing.Violations > 0)
        {
            failures.Add($"A4: applied synthetic actor correction was non-zero on {pairing.Violations} of {pairing.PairedTicks} double-support ticks.");
        }

        // ---- Containment: every realised pose and consecutive midpoint on the authored surface. ----
        int offMesh = 0;
        float worstDepth = 0f;
        for (int index = 0; index < window.Length; index++)
        {
            void Probe(Vector2 planar)
            {
                float depth = surface.OffSurfaceDepth(planar);
                if (depth > 0.0001f)
                {
                    offMesh++;
                    worstDepth = MathF.Max(worstDepth, depth);
                }
            }

            Probe(PlanarXZ(window[index].Actor.Origin));
            if (index + 1 < window.Length)
            {
                Probe((PlanarXZ(window[index].Actor.Origin) + PlanarXZ(window[index + 1].Actor.Origin)) * 0.5f);
            }
        }

        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"authored surface: polygons={surface.PolygonCount} offmesh probes={offMesh} worst_depth={worstDepth * 1000:0.###} mm");
        if (offMesh > 0)
        {
            failures.Add($"Containment: {offMesh} realised pose/midpoint probe(s) left the authored navmesh polygon union (worst depth {worstDepth * 1000:0.###} mm).");
        }

        // ---- Ordered crossing of the runtime query's reduced corners. ----
        Vector3[] queriedPath = NavigationServer3D.MapGetPath(
            region.GetNavigationMap(), window[0].Actor.Origin, destination, true);
        List<(Vector2 Corner, Vector2 Outgoing)> corners = ReduceQueryCorners(queriedPath);
        ulong previousCross = windowStart;
        for (int index = 0; index < corners.Count; index++)
        {
            (Vector2 corner, Vector2 outgoing) = corners[index];
            ulong? crossTick = null;
            float minDistance = float.MaxValue;
            foreach (PlantedStateSample sample in window)
            {
                float offset = CornerPlaneOffset(PlanarXZ(sample.Actor.Origin), corner, outgoing);
                if (offset >= 0f && (crossTick is null || sample.Tick < crossTick))
                {
                    crossTick = sample.Tick;
                }

                minDistance = MathF.Min(minDistance, MathF.Abs(offset));
            }

            _ = report.AppendLine(CultureInfo.InvariantCulture,
                $"route corner {index + 1} at ({corner.X:R}, {corner.Y:R}) outgoing ({outgoing.X:R}, {outgoing.Y:R}): min_distance={minDistance * 1000:0.###} mm cross_tick={crossTick?.ToString() ?? "never"}");
            if (crossTick is null)
            {
                failures.Add($"Route: reduced corner {index + 1} at ({corner.X:R}, {corner.Y:R}) was never crossed.");
            }
            else if (index > 0 && crossTick <= previousCross)
            {
                failures.Add($"Route: reduced corner {index + 1} crossed at {crossTick} not after the previous corner's {previousCross}.");
            }
            else
            {
                previousCross = crossTick.Value;
            }
        }

        // ---- A2: every metadata-planted run listed honestly (bound reported; IK-side reach-release
        //      families are never masked or filtered — known-red runs are reported, not asserted away). ----
        var runMetrics = NAV001ArrivalOutcomeIntegrationTests.SlipMetrics.Evaluate(window, Engine.PhysicsTicksPerSecond);
        _ = report.AppendLine(runMetrics.Describe());
        a2RunsCsv = runMetrics.FormatRunsCsv();
    }
}
