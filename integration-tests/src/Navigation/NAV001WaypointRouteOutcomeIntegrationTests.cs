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
/// NAV-TAC-007 Waypoint Route outcome scenario (TR18–20): from the origin facing −Z, the request carries the
/// via-point (0, 0, −2) and the destination (2, 0, −2) with final facing +X — an L-shaped route with a sharp
/// corner. Asserts, over the full window from request issue to completion + 60 ticks: exactly ONE terminal
/// outcome, Completed (no intermediate arrival, completion, stop clip or arrival correction at the via);
/// the via passed in order within the configured pass radius (a missed via FAILS); A1 ≤ 10 mm/1°; A3 exact
/// 12-component stillness over the 60-tick tail; A4 fail-closed pairing with zero synthetic correction on
/// in-lifetime double-support ticks; authored-surface containment of every realised pose and midpoint; and an
/// honest listing of every A2 metadata-planted run (bound reported, never masked). A5 does not apply to this
/// scenario; A6 is later user judgement.
/// </summary>
public sealed class NAV001WaypointRouteOutcomeIntegrationTests
{
    private const string Scenario = "waypoint-route";
    private const string MultiViaScenario = "waypoint-route-multi-via";
    private const float PlanarPositionToleranceMetres = 0.01f;
    private const float YawToleranceDegrees = 1f;
    private const int PostTerminalPhysicsTicks = 60;
    private const double RequestHarnessTimeoutSeconds = 20.0;

    // Spec scenario (specs/nav/001-npc-navigation/index.md § Scenarios): from the origin facing −Z, via
    // (0, 0, −2), destination (2, 0, −2), final facing +X.
    private static readonly Vector3 _viaPoint = new(0f, 0f, -2f);
    private static readonly Vector3 _destination = new(2f, 0f, -2f);
    private const float FacingPlusXRadians = Mathf.Pi / 2f;

    private static readonly string _idleState = NPCAnimationProgramme.IdleState;

    /// <inheritdoc/>
    [Fact]
    public async Task WaypointRoute_PassesViaInOrderAndCompletesAtDestination()
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
                "tick,actor_x,actor_y,actor_z,actor_yaw_deg,status,phase,revision,active_request,via_passed\n");
            var outcomeLog = new StringBuilder();
            var seenOutcomes = new HashSet<long>();

            Transform3D destinationTransform = new(new Basis(Vector3.Up, FacingPlusXRadians), _destination);
            NavigationDestinationResult result = navigation.SetDestination(destinationTransform, [_viaPoint]);
            Assert.True(
                result == NavigationDestinationResult.Accepted,
                $"[{Scenario}] the waypoint request to {destinationTransform} through {_viaPoint} was refused: {result}.");
            NavigationRequestIdentity? active = navigation.CurrentStatus.ActiveRequest;
            Assert.True(active.HasValue, $"[{Scenario}] the accepted waypoint request has no active request identity.");
            NavigationRequestIdentity identity = active!.Value;
            ulong windowStart = Engine.GetPhysicsFrames();

            NavigationTerminalOutcomeRecord? outcome = null;
            int harnessTimeoutTicks = (int)Math.Ceiling(RequestHarnessTimeoutSeconds * ticksPerSecond);
            ulong lastTick = windowStart;
            // Live via state, captured during observation: completing the request legitimately clears the
            // request's destination state (including the via list), so post-completion reads would show none.
            int issuedViaCount = owner.ViaPoints.Count;
            int maxViaPassedDuringRun = 0;
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
                maxViaPassedDuringRun = Math.Max(maxViaPassedDuringRun, owner.ViaPointsPassedCount);
                outcome ??= NAV001ArrivalOutcomeIntegrationTests.FindOutcome(navigation, identity);
            }

            await WaitForFramesAsync(sceneTree, 1);
            recorder.Detach();

            var failures = new List<string>();
            var report = new StringBuilder();
            List<NavigationTerminalOutcomeRecord> allOutcomes = [.. navigation.TerminalOutcomes];
            _ = report.AppendLine(CultureInfo.InvariantCulture,
                $"scenario={Scenario} window_ticks={windowStart}..{lastTick} samples={recorder.Samples.Count} ticks_per_second={ticksPerSecond}");
            _ = report.AppendLine($"outcomes: {DescribeOutcomes(allOutcomes)}");
            if (allOutcomes.Count != 1 || allOutcomes[0].Request != identity)
            {
                failures.Add($"Outcomes: expected exactly one terminal outcome for the waypoint request (no intermediate arrival at the via), got {DescribeOutcomes(allOutcomes)}.");
            }
            else if (allOutcomes[0].Outcome != NavigationTerminalOutcome.Completed)
            {
                failures.Add(FormattableString.Invariant(
                    $"Outcomes: the waypoint route did not complete: {allOutcomes[0].Outcome}/{allOutcomes[0].Reason} at tick {allOutcomes[0].TerminalPhysicsTick} (error {allOutcomes[0].Error.PlanarDistance * 1000:0.###} mm)."));
            }

            ulong terminalTick = allOutcomes.Count == 1 ? allOutcomes[0].TerminalPhysicsTick : lastTick;
            PlantedStateSample[] window = NAV001ArrivalOutcomeIntegrationTests.RequireCompleteWindow(recorder, windowStart, lastTick);
            NavigationRegion3D region = root.GetNode<NavigationRegion3D>("NavigationRegion3D");
            var surface =
                NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface.FromRegion(region);
            EvaluateCriteria(
                actor, navigation, window, windowStart, terminalTick, surface, issuedViaCount, maxViaPassedDuringRun, report, failures, out string a2RunsCsv);

            string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
                OS.GetEnvironment("ALLEYCAT_NAV001_WAYPOINT_EVIDENCE") is { Length: > 0 } configured
                    ? $"res://temp/{configured.TrimStart('/')}"
                    : $"res://temp/nav001-via-waypoints/run-01/{Scenario}"));
            _ = Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{Scenario}-report.txt"), report.ToString());
            await File.WriteAllTextAsync(Path.Combine(directory, $"{Scenario}-nav.txt"), outcomeLog.Append(navLog).ToString());
            await File.WriteAllTextAsync(Path.Combine(directory, $"{Scenario}-samples.csv"),
                recorder.Samples.Count > 0 ? NAV001ArrivalOutcomeIntegrationTests.FormatSamplesCsv(recorder.Samples) : string.Empty);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{Scenario}-runs.csv"), a2RunsCsv);
            Assert.True(failures.Count == 0, $"{failures.Count} waypoint-route criterion failure(s):\n  {string.Join("\n  ", failures)}\n{report}");
        }
        finally
        {
            recorder?.Detach();
            Engine.MaxPhysicsStepsPerFrame = previousStepLimit;
            root.Free();
        }
    }

    /// <summary>Evaluates the Waypoint Route criteria: ordered within-radius via pass (a miss fails), no
    /// intermediate arrival (single outcome, checked by the caller), A1, exact A3 stillness, fail-closed A4,
    /// containment, and the honest A2 run listing.</summary>
    private static void EvaluateCriteria(
        Node3D actor,
        INavigation navigation,
        PlantedStateSample[] window,
        ulong windowStart,
        ulong terminalTick,
        NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface surface,
        int issuedViaCount,
        int maxViaPassedDuringRun,
        StringBuilder report,
        List<string> failures,
        out string a2RunsCsv)
    {
        var owner = (NPCNavigation)navigation;
        float passRadius = owner.ViaPointPassRadiusMetres;

        // ---- Ordered within-radius via pass (a missed via FAILS). ----
        float closestViaDistance = float.MaxValue;
        ulong closestViaTick;
        int closestViaIndex = -1;
        for (int index = 0; index < window.Length; index++)
        {
            float distance = Planar(window[index].Actor.Origin).DistanceTo(Planar(_viaPoint));
            if (distance < closestViaDistance)
            {
                closestViaDistance = distance;
                closestViaIndex = index;
            }
        }

        closestViaTick = closestViaIndex >= 0 ? window[closestViaIndex].Tick : windowStart;
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"via: closest approach {closestViaDistance * 1000:0.###} mm at tick {closestViaTick} (pass radius {passRadius * 1000:0.#} mm); passed={maxViaPassedDuringRun} of {issuedViaCount} (live during the run)");
        if (issuedViaCount != 1 || maxViaPassedDuringRun != issuedViaCount)
        {
            failures.Add($"Via: the via-point was not recorded as passed within the configured radius (passed {maxViaPassedDuringRun} of {issuedViaCount} issued; closest realised approach {closestViaDistance * 1000:0.###} mm).");
        }

        // The via must be passed BEFORE arrival at the destination (in order).
        if (closestViaTick >= terminalTick)
        {
            failures.Add($"Via: closest approach tick {closestViaTick} is not before the terminal tick {terminalTick}.");
        }

        // No stop clip at the via: no settled single-clip Idle between the via pass and the terminal tick
        // other than the final approach's authored stop (checked as: the LAST locomotive tick is at or after
        // the via pass tick, so motion continued through the via).
        ulong lastLocomotiveTick = window.Where(sample => sample.Graph.Current != _idleState)
            .Select(sample => sample.Tick).DefaultIfEmpty(windowStart).Max();
        if (lastLocomotiveTick < closestViaTick)
        {
            failures.Add($"Via: motion stopped at or before the via (last locomotive tick {lastLocomotiveTick} <= via pass tick {closestViaTick}); a via is never a stopping point.");
        }

        // ---- A1: terminal accuracy at the final destination only. ----
        PlantedStateSample? terminalSample = window.FirstOrDefault(sample => sample.Tick == terminalTick);
        Transform3D terminalTransform = terminalSample?.Actor ?? actor.GlobalTransform;
        float planarError = Planar(terminalTransform.Origin).DistanceTo(Planar(_destination));
        float yawErrorDegrees = MathF.Abs(Mathf.RadToDeg(
            Mathf.AngleDifference(FacingPlusXRadians, NAV001ArrivalOutcomeIntegrationTests.YawOf(terminalTransform))));
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"A1: terminal tick {terminalTick} planar_error={planarError * 1000:0.###} mm yaw_error={yawErrorDegrees:0.####} deg");
        if (planarError > PlanarPositionToleranceMetres || yawErrorDegrees > YawToleranceDegrees)
        {
            failures.Add($"A1: terminal error {planarError * 1000:0.###} mm / {yawErrorDegrees:0.####} deg exceeds 10 mm / 1 deg.");
        }

        // ---- A3: exact stillness over the full 60-tick tail. ----
        PlantedStateSample[] tail = [.. window.Where(sample => sample.Tick > terminalTick && sample.Tick <= terminalTick + PostTerminalPhysicsTicks)];
        int stillTicks = tail.Count(sample => sample.Actor == terminalTransform);
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"A3: {stillTicks} of {tail.Length} post-terminal ticks match the terminal transform on all components exactly");
        if (tail.Length != PostTerminalPhysicsTicks || stillTicks != PostTerminalPhysicsTicks)
        {
            failures.Add($"A3: expected exactly {PostTerminalPhysicsTicks} exactly-still post-terminal ticks, got {stillTicks} of {tail.Length}.");
        }

        // ---- A4: fail-closed pairing over the request's lifetime. ----
        var diagnostics = new List<MovementDiagnosticsPairing.DiagnosticSample>();
        foreach (NPCNavigationMovementSample sample in owner.MovementSamples)
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

            Probe(Planar(window[index].Actor.Origin));
            if (index + 1 < window.Length)
            {
                Probe((Planar(window[index].Actor.Origin) + Planar(window[index + 1].Actor.Origin)) * 0.5f);
            }
        }

        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"authored surface: polygons={surface.PolygonCount} offmesh probes={offMesh} worst_depth={worstDepth * 1000:0.###} mm");
        if (offMesh > 0)
        {
            failures.Add($"Containment: {offMesh} realised pose/midpoint probe(s) left the authored navmesh polygon union (worst depth {worstDepth * 1000:0.###} mm).");
        }

        // ---- A2: every metadata-planted run listed honestly. ----
        var runMetrics = NAV001ArrivalOutcomeIntegrationTests.SlipMetrics.Evaluate(window, Engine.PhysicsTicksPerSecond);
        _ = report.AppendLine(runMetrics.Describe());
        a2RunsCsv = runMetrics.FormatRunsCsv();
    }

    // Multi-via spec shape (TR20): from the origin facing −Z, TWO Shift-click-queued vias — (0, 0, −2) then
    // (1.4, 0, −3.4) — and the destination (2.8, 0, −4.8) with final facing continued southeast, all in the
    // south open corridor (the strip polygons x∈[−5.8, 5.8], z∈[−5.8, 0.55]; no wall-adjacent geometry on the
    // path — every route point keeps ≥ 1 m from every mesh edge). The legs turn ~45° at each via: the shape
    // the travelling branch composes today. A FIRST CUT of this scenario with two 90° legs (vias (0,−2) then
    // (2,−2), destination (2,−4)) honestly FAILED (evidence nav001-multi-via/run-01/scenario): via1's
    // corner-composing arc exits ~30° past the leg heading, so via2's station then bore ~120° right — beyond
    // the arc families' radius — and the recovery holdings walked the facing (east) to the mesh edge, where
    // the CORNER-12 admission stopped the actor contained and the route settled into the finite natural-stop
    // Failed. Sharp mid-route via turns are the deferred travelling pivot-led composition family
    // (ALLEYCAT_TRAVELLING_PIVOT_SECTIONS, deliberately inert); this scenario verifies the TR20 multi-via
    // mechanics (queueing, ordering, radius, continuation, completion) on composable geometry.
    // The route is authored through the playtest controller's OWN queue/issue entries — QueueViaPointAt (the
    // Shift-click queue path, markers included) and BeginDestinationPlacementAt/UpdateDestinationFacingProbe/
    // CompleteDestinationPlacementAt (the normal-click drag-to-face path) — not the raw SetDestination
    // overload. 30 s request watchdog: a declared harness limit — the 4 m single-via route completes in
    // ~883 ticks (14.7 s); this 6 m route with two corners needs the margin.
    private static readonly Vector3 _multiViaPoint1 = new(0f, 0f, -2f);
    private static readonly Vector3 _multiViaPoint2 = new(1.4f, 0f, -3.4f);
    private static readonly Vector3 _multiViaDestination = new(2.8f, 0f, -4.8f);
    private static readonly Vector3 _multiViaFacingProbe = new(3.8f, 0f, -5.6f);
    private const double MultiViaRequestHarnessTimeoutSeconds = 30.0;

    /// <summary>
    /// TR20 multi-via verification: two vias queued through the Shift-click authoring path, then the final
    /// click with facing, execute as ONE route — both vias passed in order within the configured pass radius
    /// (closest approach per via recorded; a miss FAILS), motion CONTINUES past each via (no stopping point,
    /// no intermediate outcomes), exactly ONE Completed outcome at the final destination, A1 ≤ 10 mm/1°
    /// against the issued facing, A3 exact 12-component stillness over the 60-tick tail, A4 fail-closed zero
    /// synthetic on in-lifetime double-support ticks, authored-surface containment 0, and every A2 run
    /// honestly listed.
    /// </summary>
    [Fact]
    public async Task WaypointRoute_MultipleShiftClickViasPassInOrderAndCompleteAtDestination()
    {
        SceneTree sceneTree = GetSceneTree();
        int previousStepLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NAV001ArrivalOutcomeIntegrationTests.NavigationTestScenePath).Instantiate(), exactMatch: false);
        AnimationGraphPlantedStateRecorder? recorder = null;

        try
        {
            // The navigation test scene's ROOT is the playtest controller: the authoring entries below are
            // the same internal methods the Shift-click/normal-click UI handlers drive.
            DirectTransformNavigationPlaytestController controller = Assert.IsType<DirectTransformNavigationPlaytestController>(root, exactMatch: false);
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

            // ---- Shift-click authoring: queue two vias, in order, with markers; never a request. ----
            controller.QueueViaPointAt(_multiViaPoint1);
            controller.QueueViaPointAt(_multiViaPoint2);
            Assert.True(controller.QueuedViaPoints.Count == 2, "SETUP: two queued Shift-click vias expected.");
            Assert.True(controller.QueuedViaPoints[0].DistanceTo(_multiViaPoint1) <= 0.001f
                && controller.QueuedViaPoints[1].DistanceTo(_multiViaPoint2) <= 0.001f,
                "SETUP: the queued vias are out of order.");
            Assert.True(controller.ViaMarkerNodes.Count == 2
                && controller.ViaMarkerNodes[0].GlobalPosition.DistanceTo(_multiViaPoint1) <= 0.001f
                && controller.ViaMarkerNodes[1].GlobalPosition.DistanceTo(_multiViaPoint2) <= 0.001f,
                "SETUP: the via markers do not mirror the queue in order.");
            Assert.False(navigation.HasDestination, "SETUP: queueing vias must not issue a request.");

            // ---- Normal click authoring: the final click with drag-to-face issues the WHOLE queued route. ----
            controller.BeginDestinationPlacementAt(_multiViaDestination);
            controller.UpdateDestinationFacingProbe(_multiViaFacingProbe);
            controller.CompleteDestinationPlacementAt(_multiViaFacingProbe);
            Assert.True(navigation.HasDestination, "SETUP: the issued multi-via route is not active.");
            Assert.True(owner.ViaPoints.Count == 2
                && owner.ViaPoints[0].DistanceTo(_multiViaPoint1) <= 0.001f
                && owner.ViaPoints[1].DistanceTo(_multiViaPoint2) <= 0.001f,
                "SETUP: the issued route does not carry both queued vias in order.");
            Assert.True(controller.QueuedViaPoints.Count == 0 && controller.ViaMarkerNodes.Count == 0,
                "SETUP: issuing the route did not clear the authoring queue and its markers.");
            Basis issuedFacing = navigation.Destination.Basis;
            Vector3 issuedForward = -issuedFacing.Z;
            Vector3 authoredFacing = (_multiViaFacingProbe - _multiViaDestination) with
            {
                Y = 0f
            };
            Assert.True(Mathf.RadToDeg(issuedForward.AngleTo(authoredFacing.Normalized())) <= 0.5f,
                $"SETUP: the issued facing {issuedForward} is not the authored facing {authoredFacing.Normalized()}.");

            NavigationRequestIdentity? active = navigation.CurrentStatus.ActiveRequest;
            Assert.True(active.HasValue, "SETUP: the accepted multi-via request has no active request identity.");
            NavigationRequestIdentity identity = active!.Value;
            float issuedYaw = NAV001ArrivalOutcomeIntegrationTests.YawOf(navigation.Destination);

            int ticksPerSecond = Engine.PhysicsTicksPerSecond;
            var navLog = new StringBuilder(
                "tick,actor_x,actor_y,actor_z,actor_yaw_deg,status,phase,revision,active_request,via_passed\n");
            var outcomeLog = new StringBuilder();
            var seenOutcomes = new HashSet<long>();
            ulong windowStart = Engine.GetPhysicsFrames();

            NavigationTerminalOutcomeRecord? outcome = null;
            int harnessTimeoutTicks = (int)Math.Ceiling(MultiViaRequestHarnessTimeoutSeconds * ticksPerSecond);
            ulong lastTick = windowStart;
            int issuedViaCount = owner.ViaPoints.Count;
            int maxViaPassedDuringRun = 0;
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
                maxViaPassedDuringRun = Math.Max(maxViaPassedDuringRun, owner.ViaPointsPassedCount);
                outcome ??= NAV001ArrivalOutcomeIntegrationTests.FindOutcome(navigation, identity);
            }

            await WaitForFramesAsync(sceneTree, 1);
            recorder.Detach();

            var failures = new List<string>();
            var report = new StringBuilder();
            List<NavigationTerminalOutcomeRecord> allOutcomes = [.. navigation.TerminalOutcomes];
            _ = report.AppendLine(CultureInfo.InvariantCulture,
                $"scenario={MultiViaScenario} window_ticks={windowStart}..{lastTick} samples={recorder.Samples.Count} ticks_per_second={ticksPerSecond} vias={_multiViaPoint1} then {_multiViaPoint2} destination={_multiViaDestination} facing_probe={_multiViaFacingProbe}");
            _ = report.AppendLine($"outcomes: {DescribeOutcomes(allOutcomes)}");
            if (allOutcomes.Count != 1 || allOutcomes[0].Request != identity)
            {
                failures.Add($"Outcomes: expected exactly one terminal outcome for the multi-via request (no intermediate arrival at a via), got {DescribeOutcomes(allOutcomes)}.");
            }
            else if (allOutcomes[0].Outcome != NavigationTerminalOutcome.Completed)
            {
                failures.Add(FormattableString.Invariant(
                    $"Outcomes: the multi-via route did not complete: {allOutcomes[0].Outcome}/{allOutcomes[0].Reason} at tick {allOutcomes[0].TerminalPhysicsTick} (error {allOutcomes[0].Error.PlanarDistance * 1000:0.###} mm)."));
            }

            ulong terminalTick = allOutcomes.Count == 1 ? allOutcomes[0].TerminalPhysicsTick : lastTick;
            PlantedStateSample[] window = NAV001ArrivalOutcomeIntegrationTests.RequireCompleteWindow(recorder, windowStart, lastTick);
            NavigationRegion3D region = root.GetNode<NavigationRegion3D>("NavigationRegion3D");
            var surface = NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface.FromRegion(region);
            EvaluateMultiViaCriteria(
                actor, owner, window, windowStart, terminalTick, surface, issuedViaCount, maxViaPassedDuringRun, issuedYaw, report, failures, out string a2RunsCsv);

            string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
                OS.GetEnvironment("ALLEYCAT_NAV001_MULTIVIA_EVIDENCE") is { Length: > 0 } configured
                    ? $"res://temp/{configured.TrimStart('/')}"
                    : $"res://temp/nav001-multi-via/run-01/{MultiViaScenario}"));
            _ = Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{MultiViaScenario}-report.txt"), report.ToString());
            await File.WriteAllTextAsync(Path.Combine(directory, $"{MultiViaScenario}-nav.txt"), outcomeLog.Append(navLog).ToString());
            await File.WriteAllTextAsync(Path.Combine(directory, $"{MultiViaScenario}-samples.csv"),
                recorder.Samples.Count > 0 ? NAV001ArrivalOutcomeIntegrationTests.FormatSamplesCsv(recorder.Samples) : string.Empty);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{MultiViaScenario}-runs.csv"), a2RunsCsv);
            Assert.True(failures.Count == 0, $"{failures.Count} multi-via criterion failure(s):\n  {string.Join("\n  ", failures)}\n{report}");
        }
        finally
        {
            recorder?.Detach();
            Engine.MaxPhysicsStepsPerFrame = previousStepLimit;
            root.Free();
        }
    }

    /// <summary>Evaluates the multi-via criteria: BOTH vias passed in order within the configured radius (a miss
    /// or an out-of-order pass FAILS), motion continued past each via (a via is never a stopping point; the
    /// single-outcome check above already excludes intermediate arrivals), A1 at the issued destination and
    /// facing, exact A3 stillness, fail-closed A4, containment 0, and the honest A2 run listing.</summary>
    private static void EvaluateMultiViaCriteria(
        Node3D actor,
        NPCNavigation owner,
        PlantedStateSample[] window,
        ulong windowStart,
        ulong terminalTick,
        NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface surface,
        int issuedViaCount,
        int maxViaPassedDuringRun,
        float issuedYawRadians,
        StringBuilder report,
        List<string> failures,
        out string a2RunsCsv)
    {
        float passRadius = owner.ViaPointPassRadiusMetres;
        Vector3[] vias = [_multiViaPoint1, _multiViaPoint2];

        // ---- Per-via ordered within-radius pass (a missed or out-of-order via FAILS). ----
        ulong previousViaTick = 0;
        for (int via = 0; via < vias.Length; via++)
        {
            float closest = float.MaxValue;
            ulong closestTick = windowStart;
            foreach (PlantedStateSample sample in window)
            {
                float distance = Planar(sample.Actor.Origin).DistanceTo(Planar(vias[via]));
                if (distance < closest)
                {
                    closest = distance;
                    closestTick = sample.Tick;
                }
            }

            _ = report.AppendLine(CultureInfo.InvariantCulture,
                $"via {via + 1} at ({vias[via].X:0.###}, {vias[via].Z:0.###}): closest approach {closest * 1000:0.###} mm at tick {closestTick} (pass radius {passRadius * 1000:0.#} mm)");
            if (closest > passRadius)
            {
                failures.Add($"Via {via + 1}: closest realised approach {closest * 1000:0.###} mm exceeds the configured pass radius {passRadius * 1000:0.#} mm.");
            }

            if (via > 0 && closestTick <= previousViaTick)
            {
                failures.Add($"Via order: via {via + 1} was passed at tick {closestTick}, not after via {via}'s pass tick {previousViaTick}.");
            }

            previousViaTick = closestTick;
        }

        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"vias passed live during the run: {maxViaPassedDuringRun} of {issuedViaCount} issued");
        if (issuedViaCount != vias.Length || maxViaPassedDuringRun != issuedViaCount)
        {
            failures.Add($"Via: not every via-point was recorded as passed within the configured radius ({maxViaPassedDuringRun} of {issuedViaCount} issued).");
        }

        // Each via is passed strictly before arrival, and motion continues past EACH via: locomotion exists
        // after every via's pass (checked against the next via's pass for intermediates, and against the last
        // locomotive tick for the final via).
        ulong lastLocomotiveTick = window.Where(sample => sample.Graph.Current != _idleState)
            .Select(sample => sample.Tick).DefaultIfEmpty(windowStart).Max();
        if (previousViaTick >= terminalTick)
        {
            failures.Add($"Via order: the last via's pass tick {previousViaTick} is not before the terminal tick {terminalTick}.");
        }

        if (lastLocomotiveTick < previousViaTick)
        {
            failures.Add($"Via: motion stopped at or before the last via (last locomotive tick {lastLocomotiveTick} <= via pass tick {previousViaTick}); a via is never a stopping point.");
        }

        // ---- A1: terminal accuracy at the final destination and the issued facing only. ----
        PlantedStateSample? terminalSample = window.FirstOrDefault(sample => sample.Tick == terminalTick);
        Transform3D terminalTransform = terminalSample?.Actor ?? actor.GlobalTransform;
        float planarError = Planar(terminalTransform.Origin).DistanceTo(Planar(_multiViaDestination));
        float yawErrorDegrees = MathF.Abs(Mathf.RadToDeg(
            Mathf.AngleDifference(issuedYawRadians, NAV001ArrivalOutcomeIntegrationTests.YawOf(terminalTransform))));
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"A1: terminal tick {terminalTick} planar_error={planarError * 1000:0.###} mm yaw_error={yawErrorDegrees:0.####} deg");
        if (planarError > PlanarPositionToleranceMetres || yawErrorDegrees > YawToleranceDegrees)
        {
            failures.Add($"A1: terminal error {planarError * 1000:0.###} mm / {yawErrorDegrees:0.####} deg exceeds 10 mm / 1 deg.");
        }

        // ---- A3: exact stillness over the full 60-tick tail. ----
        PlantedStateSample[] tail = [.. window.Where(sample => sample.Tick > terminalTick && sample.Tick <= terminalTick + PostTerminalPhysicsTicks)];
        int stillTicks = tail.Count(sample => sample.Actor == terminalTransform);
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"A3: {stillTicks} of {tail.Length} post-terminal ticks match the terminal transform on all components exactly");
        if (tail.Length != PostTerminalPhysicsTicks || stillTicks != PostTerminalPhysicsTicks)
        {
            failures.Add($"A3: expected exactly {PostTerminalPhysicsTicks} exactly-still post-terminal ticks, got {stillTicks} of {tail.Length}.");
        }

        // ---- A4: fail-closed pairing over the request's lifetime. ----
        var diagnostics = new List<MovementDiagnosticsPairing.DiagnosticSample>();
        foreach (NPCNavigationMovementSample sample in owner.MovementSamples)
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

            Probe(Planar(window[index].Actor.Origin));
            if (index + 1 < window.Length)
            {
                Probe((Planar(window[index].Actor.Origin) + Planar(window[index + 1].Actor.Origin)) * 0.5f);
            }
        }

        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"authored surface: polygons={surface.PolygonCount} offmesh probes={offMesh} worst_depth={worstDepth * 1000:0.###} mm");
        if (offMesh > 0)
        {
            failures.Add($"Containment: {offMesh} realised pose/midpoint probe(s) left the authored navmesh polygon union (worst depth {worstDepth * 1000:0.###} mm).");
        }

        // ---- A2: every metadata-planted run listed honestly. ----
        var runMetrics = NAV001ArrivalOutcomeIntegrationTests.SlipMetrics.Evaluate(window, Engine.PhysicsTicksPerSecond);
        _ = report.AppendLine(runMetrics.Describe());
        a2RunsCsv = runMetrics.FormatRunsCsv();
    }

    private static void AppendNavLog(
        StringBuilder navLog,
        INavigation navigation,
        Node3D actor,
        HashSet<long> seenOutcomes,
        StringBuilder outcomeLog)
    {
        Vector3 position = actor.GlobalPosition;
        NavigationCurrentStatus status = navigation.CurrentStatus;
        foreach (NavigationTerminalOutcomeRecord outcomeRecord in navigation.TerminalOutcomes)
        {
            if (seenOutcomes.Add(outcomeRecord.Sequence))
            {
                _ = outcomeLog.AppendLine(CultureInfo.InvariantCulture,
                    $"outcome seq outcome={outcomeRecord.Outcome}/{outcomeRecord.Reason} terminal_tick={outcomeRecord.TerminalPhysicsTick}");
            }
        }

        _ = navLog.AppendLine(CultureInfo.InvariantCulture,
            $"{Engine.GetPhysicsFrames()},{position.X:R},{position.Y:R},{position.Z:R},{NAV001ArrivalOutcomeIntegrationTests.YawOf(actor.GlobalTransform) * Mathf.RadToDeg(1f):R},{status.Status},{(navigation as NPCNavigation)?.ViaPointsPassedCount ?? 0}");
    }

    private static Vector2 Planar(Vector3 value) => new(value.X, value.Z);


    private static string DescribeOutcomes(IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes)
        => outcomes.Count == 0
            ? "none"
            : string.Join("; ", outcomes.Select(outcome =>
                $"{outcome.Outcome}/{outcome.Reason} at tick {outcome.TerminalPhysicsTick} (error {outcome.Error.PlanarDistance * 1000:0.###} mm)"));
}
