using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>
/// CORNER-07 selection regression on the INSTALLED female rig and the authored navigation scene: the recorded
/// second-corner boundary of the corner-route run (game/temp/nav001-corner-yaw-budget/run-01/candidate/corner/,
/// tick 733) is replayed against the production planner binding, the actual navigation-map route query, the
/// production leg decision and the actual authored navmesh polygon union. It locks in: the ungated section
/// selection for the recorded through-corner station composes the recorded arc whose COMPOSED trajectory (native
/// playback plus the reviewed capped-correction schedule) leaves the authored surface — the admission the route
/// owner now supplies must reject every such finalist; and the corner-anchored outside station the owner falls
/// back to must admit a composition whose composed path stays on the surface and whose continuation closes on the
/// destination within the arrival tolerances under the same schedule. Stations are built through the production
/// machinery (live map query, route compile, leg decision); the surface oracle is the scene's own polygon union.
/// </summary>
public sealed class NAV001ComposedSectionContainmentIntegrationTests
{
    private const string EvidenceRoot = "res://temp/nav001-corner-composed-containment/run-01/regression";

    // ---- Recorded section-5 boundary state (corner-route run-01, tick 733) ----
    private static readonly Vector3 _boundaryPosition = new(-0.49729007f, 0f, 4.585935f);
    private const float BoundaryYaw = -Mathf.Pi;
    private const string WalkRole = "wf";
    private const double BoundaryWalkSeconds = 0.8666666666666685d;

    // Recorded decision station: the route end at (0.45, 5.4) with the route's outgoing yaw.
    private static readonly ArrivalTarget _recordedDecisionStation = new(new Vector2(0.44999984f, 5.4f), -2.0344439f);

    // Destination of the replacement request (0.45, 5.4) facing +Z.
    private static readonly Vector3 _destinationPosition = new(0.45f, 0f, 5.4f);
    private static readonly ArrivalTarget _destination = new(new Vector2(0.45f, 5.4f), Mathf.Pi);

    // Recorded live phase budget at the boundary: LeftSwing, 18 original ticks, near-exhausted translation.
    private static readonly SwingCorrectionPhaseBudget _boundarySeed = new()
    {
        Phase = SupportPhase.LeftSwing,
        OriginalTicks = 18,
        Caps = new SwingCorrectionCaps(),
        FlightBudgetMetres = 0f,
        SpentRight = 0.05000000074505806d,
        SpentLeft = 0.0027777778450399637d,
        SpentForward = 0.029999998630955815d,
        SpentBackward = 0.0055555556900799274d,
    };

    /// <inheritdoc/>
    [Fact]
    public async Task CornerBoundary_ComposedAdmissionRejectsTheCornerCutAndAdmitsTheOutsideApproach()
    {
        SceneTree sceneTree = GetSceneTree();

        // The actual authored navigation surface and the actual navigation-map route query of the scene.
        Node3D sceneRoot = Assert.IsType<Node3D>(
            LoadPackedScene(NAV001ArrivalOutcomeIntegrationTests.NavigationTestScenePath).Instantiate(), exactMatch: false);
        Vector3[] pathPoints;
        NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface surface;
        try
        {
            sceneTree.Root.AddChild(sceneRoot);
            NavigationRegion3D region = sceneRoot.GetNode<NavigationRegion3D>("NavigationRegion3D");
            for (int tick = 0; tick < 120; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                // The map's first sync can predate the region's navmesh commit; wait until the authored mesh is
                // actually queryable before asking for the route.
                if (NavigationServer3D.MapGetIterationId(region.GetNavigationMap()) > 0
                    && NavigationServer3D.MapGetClosestPoint(region.GetNavigationMap(), _boundaryPosition) != Vector3.Zero)
                {
                    break;
                }
            }

            surface = NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface.FromRegion(region);
            pathPoints = NavigationServer3D.MapGetPath(region.GetNavigationMap(),
                _boundaryPosition,
                _destinationPosition,
                optimize: true,
                region.NavigationLayers);
        }
        finally
        {
            sceneTree.Root.RemoveChild(sceneRoot);
            sceneRoot.Free();
            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(pathPoints.Length >= 2, $"SETUP: the navigation query returned {pathPoints.Length} points.");

        // The production route and leg decision for the recorded boundary state.
        Transform3D destinationTransform = new(new Basis(Vector3.Up, Mathf.Pi), _destinationPosition);
        var route = NPCNavigationRoutePlan.Compile(pathPoints, 0, destinationTransform, 1, 1, authoredExitDistance: 0f);
        RouteProjection projection = route.Project(_boundaryPosition, previousDistance: 0f, searchBacktrack: 0.25f);
        NPCNavigationLegDecision decision = NPCNavigationRouteSections.Decide(
            route, projection.Distance, NPCNavigationRouteConfiguration.Default, travellingStart: true);
        Assert.False(decision.Terminal, "SETUP: the recorded boundary is a section boundary, not a terminal one.");
        _ = Assert.NotNull(decision.BindingCorner);

        Vector3 decisionStation = route.Sample(decision.StationDistance, out Vector3 decisionDirection)
            + new Vector3(decision.StationOutwardOffset.X, 0f, decision.StationOutwardOffset.Y);
        ArrivalTarget decisionTarget = new(
            new Vector2(decisionStation.X, decisionStation.Z), NPCNavigationRoutePlan.YawFromDirection(decisionDirection));
        Assert.True(decisionTarget.Position.DistanceTo(_recordedDecisionStation.Position) <= 0.02f
            && Mathf.Abs(Mathf.AngleDifference(decisionTarget.Yaw, _recordedDecisionStation.Yaw)) <= 0.02f,
            $"SETUP: the reconstructed decision station ({decisionTarget.Position.X:0.###}, {decisionTarget.Position.Y:0.###}) "
            + $"yaw {decisionTarget.Yaw:0.####} does not reproduce the recorded one.");

        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female);
        NAV001ArrivalPlannerIntegrationTests.Binding binding = NAV001ArrivalPlannerIntegrationTests.Bind(fixture);
        var allocator = new SwingCorrectionAllocator(new SwingCorrectionCaps());
        var start = new ArrivalStart(new SequencePose(new Vector2(_boundaryPosition.X, _boundaryPosition.Z), BoundaryYaw), WalkRole, BoundaryWalkSeconds);

        var report = new StringBuilder();
        var failures = new List<string>();

        // ---- The recorded corner cut: the ungated through-corner selection composes off the surface. ----
        ArrivalPlan ungated = binding.Planner.PlanSection(start, decisionTarget, promptRedirect: false);
        Assert.True(ungated.Status == ArrivalPlanStatus.Planned,
            $"SETUP: the recorded decision station no longer plans: {ungated.Failure}: {ungated.FailureDetail}.");
        ComposedPathContainmentReport ungatedReport = ComposedReport(binding, allocator, surface, decisionTarget, ungated, out int ungatedTicks);
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"ungated decision winner [{DescribeSegments(ungated.Segments)}]: {ungatedTicks} composed ticks, contained={ungatedReport.IsContained} worst={ungatedReport.WorstDepthMetres * 1000:0.###} mm firstOffTick={ungatedReport.FirstOffSurfaceTick}");
        if (ungatedReport.IsContained)
        {
            failures.Add("The ungated through-corner selection is contained; the composed admission's red baseline no longer reproduces the recorded corner cut.");
        }

        // ---- The admission rejects every finalist of the through-corner station and of the half-lookahead retry. ----
        AssertGatedFailure(binding, allocator, surface, report, failures, "decision-route-end", decisionTarget);
        ArrivalTarget halfLookahead = HalfLookaheadStation(route, projection.Distance, decision.StationDistance);
        AssertGatedFailure(binding, allocator, surface, report, failures, "half-lookahead-retry", halfLookahead);

        // ---- The corner-anchored outside fallback admits a contained composition that still arrives. ----
        ArrivalTarget anchored = CornerAnchoredStation(route, decision.BindingCorner!.Value);
        ArrivalPlan gated = binding.Planner.PlanSection(
            start, anchored, promptRedirect: false, composedPathAdmission: Admission(allocator, surface, anchored, report, "corner-anchored-outside"));
        if (gated.Status != ArrivalPlanStatus.Planned)
        {
            failures.Add($"The corner-anchored outside station did not plan under the composed admission ({gated.Failure}: {gated.FailureDetail}).");
        }
        else
        {
            ComposedPathContainmentReport contained = ComposedReport(binding, allocator, surface, anchored, gated, out int gatedTicks);
            _ = report.AppendLine(CultureInfo.InvariantCulture,
                $"corner-anchored winner [{DescribeSegments(gated.Segments)}]: {gatedTicks} composed ticks, contained={contained.IsContained}");
            if (!contained.IsContained)
            {
                failures.Add("The corner-anchored winner's composed path leaves the authored surface.");
            }

            // The continuation: the terminal from the winner's own composed end pose closes on the destination
            // within the arrival tolerances under the same schedule.
            SequencePrediction winnerPrediction = binding.Planner.Predict(gated);
            List<Vector2> winnerPositions = [];
            TerminalCorrectionOutcome winnerOutcome = TerminalCorrectionSchedule.Simulate(
                winnerPrediction, anchored, allocator, _boundarySeed, winnerPositions);
            (string role, double position) = PlaybackAtSectionEnd(gated, winnerPrediction);
            ArrivalPlan continuation = binding.Planner.Plan(
                new ArrivalStart(winnerOutcome.EndPose, role, position), _destination, travellingOnly: true);
            if (continuation.Status != ArrivalPlanStatus.Planned)
            {
                failures.Add($"The continuation from the corner-anchored winner did not plan ({continuation.Failure}: {continuation.FailureDetail}).");
            }
            else
            {
                SequencePrediction continuationPrediction = binding.Planner.Predict(continuation);
                TerminalCorrectionOutcome continuationOutcome = TerminalCorrectionSchedule.Simulate(
                    continuationPrediction, _destination, allocator);
                _ = report.AppendLine(CultureInfo.InvariantCulture,
                    $"corner-anchored winner composed end {winnerOutcome.EndPose.Position.X:0.####},{winnerOutcome.EndPose.Position.Y:0.####} yaw {winnerOutcome.EndPose.Yaw:0.####} after {winnerPrediction.Ticks.Length} ticks; "
                    + $"continuation {continuationPrediction.Ticks.Length} ticks, closes A1 = {continuationOutcome.ClosesWithin(0.01f, Mathf.DegToRad(1f))} "
                    + $"(error {continuationOutcome.PositionErrorMetres * 1000:0.##} mm / {Mathf.RadToDeg(continuationOutcome.YawErrorRadians):0.###} deg)");
                Assert.True(continuationOutcome.ClosesWithin(0.01f, Mathf.DegToRad(1f)),
                    $"The continuation from the corner-anchored winner does not close within the arrival tolerances "
                    + $"({continuationOutcome.PositionErrorMetres * 1000:0.##} mm / {Mathf.RadToDeg(continuationOutcome.YawErrorRadians):0.###} deg).");
            }
        }

        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
            OS.GetEnvironment("ALLEYCAT_NAV001_COMPOSED_CONTAINMENT_EVIDENCE") is { Length: > 0 } path
                ? $"res://temp/{path.TrimStart('/')}"
                : EvidenceRoot));
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "regression-report.txt"), report.ToString());
        Assert.True(failures.Count == 0, $"{failures.Count} composed-containment regression failure(s):\n{string.Join("\n", failures)}\n{report}");
    }

    /// <summary>Asserts that the station's section fails under the real composed admission because every finalist
    /// leaves the authored surface, recording each finalist's verdict.</summary>
    private static void AssertGatedFailure(
        NAV001ArrivalPlannerIntegrationTests.Binding binding,
        SwingCorrectionAllocator allocator,
        NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface surface,
        StringBuilder report,
        List<string> failures,
        string name,
        ArrivalTarget station)
    {
        var start = new ArrivalStart(new SequencePose(new Vector2(_boundaryPosition.X, _boundaryPosition.Z), BoundaryYaw), WalkRole, BoundaryWalkSeconds);
        ArrivalPlan gated = binding.Planner.PlanSection(
            start, station, promptRedirect: false, composedPathAdmission: Admission(allocator, surface, station, report, name));
        if (gated.Status == ArrivalPlanStatus.Planned)
        {
            failures.Add($"{name}: the gated section planned although every finalist should leave the authored surface.");
        }
    }

    /// <summary>The production composed admission against the authored polygon union, recording each finalist's
    /// verdict in the report.</summary>
    private static ArrivalPlanner.SectionComposedPathAdmission Admission(
        SwingCorrectionAllocator allocator,
        NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface surface,
        ArrivalTarget station,
        StringBuilder report,
        string name)
    {
        return Admit;

        bool Admit(SequencePrediction prediction, ImmutableArray<ArrivalPlanSegment> segments)
        {
            ComposedPathContainmentReport containment = Check(prediction, allocator, surface, station, out _, out _);
            _ = report.AppendLine(CultureInfo.InvariantCulture,
                $"{name} finalist [{DescribeSegments(segments)}]: contained={containment.IsContained} worst={containment.WorstDepthMetres * 1000:0.###} mm firstOffTick={containment.FirstOffSurfaceTick}");
            return containment.IsContained;
        }
    }

    /// <summary>The composed containment report of one whole plan against the authored polygon union.</summary>
    private static ComposedPathContainmentReport ComposedReport(
        NAV001ArrivalPlannerIntegrationTests.Binding binding,
        SwingCorrectionAllocator allocator,
        NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface surface,
        ArrivalTarget station,
        ArrivalPlan plan,
        out int composedTicks) => Check(binding.Planner.Predict(plan), allocator, surface, station, out composedTicks, out _);

    private static ComposedPathContainmentReport Check(
        SequencePrediction prediction,
        SwingCorrectionAllocator allocator,
        NAV001CornerRouteOutcomeIntegrationTests.AuthoredNavigationSurface surface,
        ArrivalTarget station,
        out int composedTicks,
        out TerminalCorrectionOutcome outcome)
    {
        List<Vector2> composed = [];
        outcome = TerminalCorrectionSchedule.Simulate(prediction, station, allocator, _boundarySeed, composed);
        composedTicks = composed.Count;
        return ComposedPathContainment.Check(composed, surface.OffSurfaceDepth);
    }

    /// <summary>The existing half-lookahead retry station, exactly as <c>NPCNavigation.BeginNextLeg</c> builds it.</summary>
    private static ArrivalTarget HalfLookaheadStation(NPCNavigationRoutePlan route, float realised, float stationDistance)
    {
        Vector3 nearer = route.Sample(
            MathF.Max(realised + NPCNavigationRouteConfiguration.Default.MinSectionAdvanceMetres,
                realised + (0.5f * (stationDistance - realised))),
            out Vector3 direction);
        return new ArrivalTarget(new Vector2(nearer.X, nearer.Z), NPCNavigationRoutePlan.YawFromDirection(direction));
    }

    /// <summary>The corner-anchored outside station, exactly as <c>NPCNavigation.BeginNextLeg</c> builds it for its
    /// final bounded corner attempt: the binding corner's vertex, offset to the turn's outside with the shared
    /// rule, with the route heading at the corner as the station yaw.</summary>
    private static ArrivalTarget CornerAnchoredStation(NPCNavigationRoutePlan route, NPCNavigationRouteCorner corner)
    {
        Vector3 station = route.Sample(corner.Distance, out Vector3 direction);
        Vector2 outward = NPCNavigationRouteSections.OutsideOffset(direction, corner, NPCNavigationRouteConfiguration.Default);
        station += new Vector3(outward.X, 0f, outward.Y);
        return new ArrivalTarget(new Vector2(station.X, station.Z), NPCNavigationRoutePlan.YawFromDirection(direction));
    }

    /// <summary>The playback state at a section plan's end: the last segment's role and materialised exit
    /// position, or the walk's continuously advanced position for a pure run.</summary>
    private static (string Role, double Position) PlaybackAtSectionEnd(ArrivalPlan plan, SequencePrediction prediction)
    {
        if (!plan.Segments.IsEmpty)
        {
            ArrivalPlanSegment last = plan.Segments[^1];
            return (last.Role, last.ExitSeconds);
        }

        const double walkCycleSeconds = 1.26666666666667d;
        double position = BoundaryWalkSeconds + (prediction.Ticks.Length / 60d);
        return (WalkRole, position - (Math.Floor(position / walkCycleSeconds) * walkCycleSeconds));
    }

    private static string DescribeSegments(ImmutableArray<ArrivalPlanSegment> segments)
        => segments.IsEmpty
            ? "(pure run)"
            : string.Join(";", segments.Select(segment => string.Create(
                CultureInfo.InvariantCulture,
                $"{segment.Role}@{segment.EntrySeconds:0.######}->{segment.ExitSeconds:0.######}+{segment.ExitCycles}")));
}
