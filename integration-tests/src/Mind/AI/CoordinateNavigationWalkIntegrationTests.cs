using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Navigation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// AI-015 TR-20: the production <c>walk_to</c>, <c>turn_to</c> and <c>stop</c> delegates — composed by the
/// <see cref="AgenticMind" /> of the minimal reference-female subject (<see cref="AgentNavigationSubject" />) — drive
/// real MotionBricks navigation, with native ONNX inference, on the baked navmesh of <c>res://tests/agent_navigation/agent_navigation.tscn</c>. Each case measures the
/// realised end state against NAV-001's configured tolerances (<see cref="INavigation.DestinationReachedDistance" />,
/// <see cref="INavigation.FacingToleranceDegrees" />) and counts the outcome observations the mind's own frame
/// processing delivers to its timeline. Per-physics-frame traces are written under
/// <c>res://temp/agent-nav-tools/walk-tests/</c>.
/// </summary>
/// <remarks>
/// Agent sessions are suppressed process-wide so no LLM is contacted; the subject mind's session preparation runs
/// explicitly with a non-LLM client so the tools are the production composition, bound to the real navigation and
/// polled by the in-tree mind. Realised facing is NAV-001's measured transform (realised root orientation, or actor yaw
/// during a stationary-turn plan); an independent hip-line bearing from the skinned skeleton is traced as a
/// cross-check but not gated, since idle hips need not align with the root to within the 3° facing tolerance.
/// Native inference runs in lockstep with simulated time: each traced step holds the scene thread until the subject's
/// submitted request is published, so these tool contracts do not depend on host inference speed or load. NAV-001's
/// real-time overlap budget is not exercised here.
/// </remarks>
[IsolatedProcess]
public sealed class CoordinateNavigationWalkIntegrationTests
{
    private const string ArtefactDirectory = "res://temp/agent-nav-tools/walk-tests";

    /// <summary>
    /// Upper bound of simulated time one move may take before the test fails: a 5 m walk at the default pace takes
    /// well under 15 s of generated motion, so 60 s covers departure, inference latency, arrival and any terminal turn.
    /// </summary>
    private const double MoveTimeoutSeconds = 60d;

    /// <summary>Simulated time observed after an outcome to prove no second outcome follows and to trace settling.</summary>
    private const double PostOutcomeWindowSeconds = 3d;

    /// <summary>
    /// Outcome texts round position to 0.1 m (±0.05 m) and heading to a whole degree (±0.5°); the extra 0.02 m and 0.5°
    /// absorb the at most one physics frame between a NAV-001 terminal commit inside a physics tick and the observation
    /// that follows it. A stop commits between ticks and is compared with the observation at that commit.
    /// </summary>
    private const float OutcomeTextPositionToleranceMetres = 0.07f;

    private const double OutcomeTextHeadingToleranceDegrees = 1d;

    /// <summary>
    /// Case 1: <c>walk_to(-2.5, -1, heading 90)</c> onto the rug ends within the arrival tolerance of the coordinates
    /// and within the facing tolerance of bearing 90, with exactly one <c>arrived</c> outcome whose text matches the
    /// realised transform.
    /// </summary>
    [Fact]
    public async Task WalkToWithHeading_ArrivesWithinToleranceAndReportsOneArrivedOutcome()
    {
        await using WalkScene scene = await WalkScene.LoadAsync();
        Trace trace = new(scene, "walk-with-heading");
        try
        {
            trace.RecordRouteProbe(new Vector3(-2.5f, 0f, -1f));
            string started = await scene.InvokeAsync(WalkToTool.ProductionToolName, new()
            {
                ["x"] = -2.5,
                ["z"] = -1.0,
                ["heading"] = 90
            });
            Assert.StartsWith("Started walking to (-2.5, -1) at walk pace, to face 90° on arrival.", started, StringComparison.Ordinal);

            ObservedNavigationOutcome outcome = await trace.AwaitSingleOutcomeAsync();
            Sample end = trace.AtOutcome!.Value;

            float positionError = end.PlanarDistanceTo(-2.5f, -1f);
            double facingError = AngularError(end.MeasuredBearing, 90d);
            trace.Record("positionErrorMetres", positionError);
            trace.Record("facingErrorDegrees", facingError);
            Assert.Equal(NavigationOutcomeKind.Arrived, outcome.Kind);
            Assert.Equal(CoordinateMoveKind.Walk, outcome.Move);
            AssertWithin(positionError, scene.Navigation.DestinationReachedDistance, "arrival position error (m)");
            AssertWithin(facingError, scene.Navigation.FacingToleranceDegrees, "arrival facing error (°)");
            AssertOutcomeTextMatches(outcome, end, scene.Subject);
        }
        finally
        {
            trace.Save();
        }
    }

    /// <summary>
    /// Case 2: <c>walk_to(3.5, 3)</c> without a heading ends facing along the planned route's final stretch (AI-015
    /// TR-7): at the end of the settling window the facing is within NAV-001's facing tolerance of the bearing of the
    /// route's final segment — computed here from the navigation map's own path, independently of the tool — and within
    /// <see cref="HeadinglessApproachBoundDegrees" /> of the direction actually travelled at arrival. A turn after the
    /// position is reached is allowed and is only traced.
    /// </summary>
    [Fact]
    public async Task WalkToWithoutHeading_EndsFacingTheRouteFinalStretch()
    {
        await using WalkScene scene = await WalkScene.LoadAsync();
        Trace trace = new(scene, "walk-without-heading");
        try
        {
            Vector3 start = scene.Subject.GlobalPosition;
            Vector3 destination = new(3.5f, start.Y, 3f);
            Vector3[] route = NavigationServer3D.MapGetPath(
                scene.Navigation.GetNavigationMap(), start, destination, optimize: true, scene.Navigation.NavigationLayers);
            double routeBearing = RouteFinalSegmentBearing(route)
                ?? throw new InvalidOperationException("The navigation map gives no route with a final segment.");

            string started = await scene.InvokeAsync(WalkToTool.ProductionToolName, new()
            {
                ["x"] = 3.5,
                ["z"] = 3.0
            });
            Assert.StartsWith("Started walking to (3.5, 3) at walk pace.", started, StringComparison.Ordinal);

            ObservedNavigationOutcome outcome = await trace.AwaitSingleOutcomeAsync();
            Sample end = trace.AtOutcome!.Value;
            float tolerance = scene.Navigation.DestinationReachedDistance;
            float positionError = end.PlanarDistanceTo(3.5f, 3f);
            double finalFacing = trace.Last.MeasuredBearing;
            double travelled = trace.ArrivalTravelBearing(destination, tolerance, ArrivalChordMetres)
                ?? throw new InvalidOperationException("No realised travel direction at arrival.");
            double routeError = AngularError(finalFacing, routeBearing);
            double approachError = AngularError(finalFacing, travelled);
            trace.Record("positionErrorMetres", positionError);
            trace.Record("routeFinalSegmentBearing", routeBearing);
            trace.Record("realisedArrivalTravelBearing", travelled);
            trace.Record("finalFacingBearing", finalFacing);
            trace.Record("finalFacingErrorToRouteDegrees", routeError);
            trace.Record("finalFacingErrorToTravelledDegrees", approachError);
            trace.Record("diagnosticNetTurnAfterPositionReachedDegrees", trace.NetTurnSincePositionReached(destination, tolerance));

            Assert.Equal(NavigationOutcomeKind.Arrived, outcome.Kind);
            AssertWithin(positionError, tolerance, "arrival position error (m)");
            AssertWithin(routeError, scene.Navigation.FacingToleranceDegrees, "final facing error to the route's final segment (°)");
            AssertWithin(approachError, HeadinglessApproachBoundDegrees, "final facing error to the direction travelled at arrival (°)");
            AssertOutcomeTextMatches(outcome, end, scene.Subject);
        }
        finally
        {
            trace.Save();
        }
    }

    /// <summary>
    /// Bound between a heading-less walk's final facing and the direction it actually travelled at arrival (AI-015
    /// TR-7): the final facing follows the planned route, so a realised approach that drifts from the plan may leave a
    /// post-arrival adjustment, but never one that faces away from the approach.
    /// </summary>
    private const double HeadinglessApproachBoundDegrees = 30d;

    /// <summary>
    /// Bearing of the last segment of a navigation-map path long enough (5 cm) to define a direction, or null when the
    /// path has none.
    /// </summary>
    private static double? RouteFinalSegmentBearing(Vector3[] route)
    {
        for (int index = route.Length - 2; index >= 0; index--)
        {
            Vector3 from = route[index];
            Vector3 to = route[^1];
            if (new Vector2(to.X - from.X, to.Z - from.Z).Length() >= 0.05f)
            {
                return PlanarBearing(from, to);
            }
        }

        return null;
    }

    /// <summary>
    /// User acceptance 2 (AI-015 UR-5): the three named paces produce visibly different walking speeds on the same
    /// 4 m straight route from the declared pose — each walk from a freshly loaded scene — with stroll &lt; walk &lt;
    /// hurry in realised cruise speed — the median speed of 0.5 s chunks over the route's middle, excluding the first and
    /// last 0.5 m of departure and arrival.
    /// </summary>
    [Fact]
    public async Task Paces_ProduceIncreasingRealisedWalkingSpeeds()
    {
        Dictionary<string, float> speeds = [];
        foreach (string pace in (string[])[NavigationPace.Stroll, NavigationPace.Walk, NavigationPace.Hurry])
        {
            await using WalkScene scene = await WalkScene.LoadAsync();
            Trace trace = new(scene, $"pace-{pace}");
            try
            {
                Vector3 start = scene.Subject.GlobalPosition;
                Vector3 destination = new((float)PaceRouteEndX, start.Y, start.Z);
                trace.RecordRouteProbe(destination);
                string started = await scene.InvokeAsync(WalkToTool.ProductionToolName, new()
                {
                    ["x"] = PaceRouteEndX,
                    ["z"] = (double)start.Z,
                    ["pace"] = pace,
                });
                Assert.StartsWith($"Started walking to ({PaceRouteEndX.ToString(CultureInfo.InvariantCulture)}, 1.5) at {pace} pace.", started, StringComparison.Ordinal);
                ObservedNavigationOutcome outcome = await trace.AwaitSingleOutcomeAsync();
                Assert.Equal(NavigationOutcomeKind.Arrived, outcome.Kind);
                float speed = trace.CruiseSpeed(start, destination, 0.5f);
                speeds[pace] = speed;
                trace.Record("cruiseSpeedMetresPerSecond", speed);
                trace.Record("secondsToOutcome", trace.AtOutcome!.Value.Time);
            }
            finally
            {
                trace.Save();
            }
        }

        Assert.True(
            speeds[NavigationPace.Stroll] < speeds[NavigationPace.Walk] && speeds[NavigationPace.Walk] < speeds[NavigationPace.Hurry],
            string.Create(CultureInfo.InvariantCulture,
                $"Expected stroll < walk < hurry, measured {speeds[NavigationPace.Stroll]:0.###} / {speeds[NavigationPace.Walk]:0.###} / {speeds[NavigationPace.Hurry]:0.###} m/s."));
    }

    /// <summary>Length of the chunks whose median planar speed is a walk's cruise speed.</summary>
    private const double CruiseChunkSeconds = 0.5d;

    /// <summary>World X of the pace route's end: 4 m straight along −X from the declared pose at (1, 1.5).</summary>
    private const double PaceRouteEndX = -3d;

    /// <summary>Length of the realised approach chord that defines the direction travelled at arrival.</summary>
    private const float ArrivalChordMetres = 0.5f;

    /// <summary>
    /// Case 3: <c>turn_to(110)</c> from the authored 200° pose turns in place: the root ends within the arrival tolerance
    /// of where it started — the requested destination of a stationary turn — and never strays further than
    /// <see cref="TurnExcursionBoundMetres" />; the facing ends within tolerance, with one <c>arrived</c> outcome.
    /// </summary>
    [Fact]
    public async Task TurnTo_TurnsInPlaceWithinToleranceAndReportsOneArrivedOutcome()
    {
        await using WalkScene scene = await WalkScene.LoadAsync();
        Trace trace = new(scene, "turn-in-place");
        try
        {
            Vector3 start = scene.Subject.GlobalPosition;
            string started = await scene.InvokeAsync(TurnToTool.ProductionToolName, new()
            {
                ["heading"] = 110
            });
            Assert.StartsWith("Started turning to face 110°.", started, StringComparison.Ordinal);

            ObservedNavigationOutcome outcome = await trace.AwaitSingleOutcomeAsync();
            Sample end = trace.AtOutcome!.Value;

            float displacement = end.PlanarDistanceTo(start.X, start.Z);
            float excursion = trace.MaximumPlanarExcursion(start, untilOutcome: true);
            double facingError = AngularError(end.MeasuredBearing, 110d);
            trace.Record("finalDisplacementMetres", displacement);
            trace.Record("maximumExcursionMetres", excursion);
            trace.Record("facingErrorDegrees", facingError);
            Assert.Equal(NavigationOutcomeKind.Arrived, outcome.Kind);
            Assert.Equal(CoordinateMoveKind.Turn, outcome.Move);
            AssertWithin(displacement, scene.Navigation.DestinationReachedDistance, "turn final displacement (m)");
            AssertWithin(excursion, TurnExcursionBoundMetres, "turn maximum excursion (m)");
            AssertWithin(facingError, scene.Navigation.FacingToleranceDegrees, "turn facing error (°)");
            AssertOutcomeTextMatches(outcome, end, scene.Subject);
        }
        finally
        {
            trace.Save();
        }
    }

    /// <summary>
    /// Bound on how far the root may move during a stationary turn: stepping turns shift the root by a fraction of a
    /// foot length, while a walking departure moves it a stride (≈ 0.6 m or more) within its first window, so 0.3 m
    /// distinguishes turning in place from walking.
    /// </summary>
    private const float TurnExcursionBoundMetres = 0.3f;

    /// <summary>
    /// Bound on how far the character may travel after <c>stop</c>: the idle transition from a walk takes at most a
    /// deceleration step or two at walking pace, well under the 1 m that would mean it kept walking.
    /// </summary>
    private const float StopDriftBoundMetres = 1.0f;

    /// <summary>Planar speed below which the character counts as standing still (2 cm over the last second).</summary>
    private const float StandingStillMetresPerSecond = 0.02f;

    /// <summary>
    /// Case 4: <c>stop</c> issued once a 5 m walk has covered 1 m ends with exactly one <c>stopped</c> outcome, and the
    /// character comes to rest within <see cref="StopDriftBoundMetres" /> of where it was stopped and stays still.
    /// </summary>
    [Fact]
    public async Task StopMidWalk_ReportsOneStoppedOutcomeAndStandsStillNearTheStopPoint()
    {
        await using WalkScene scene = await WalkScene.LoadAsync();
        Trace trace = new(scene, "stop-mid-walk");
        try
        {
            Vector3 start = scene.Subject.GlobalPosition;
            string started = await scene.InvokeAsync(WalkToTool.ProductionToolName, new()
            {
                ["x"] = -4.0,
                ["z"] = 1.5
            });
            Assert.StartsWith("Started walking", started, StringComparison.Ordinal);

            await trace.RunUntilAsync(
                () => scene.Subject.GlobalPosition.DistanceTo(start) >= 1f || trace.OutcomeCount > 0,
                MoveTimeoutSeconds,
                "the walk to cover 1 m");
            Assert.Equal(0, trace.OutcomeCount);
            Vector3 stopPoint = scene.Subject.GlobalPosition;
            ulong frameBeforeStop = Engine.GetPhysicsFrames();
            string stopped = await scene.InvokeAsync(StopTool.ProductionToolName, []);
            // The cancellation commits its terminal record inside the call; observing before any further physics tick
            // compares the outcome with the realised transform at that commit, not with later idle-transition motion.
            Assert.Equal(frameBeforeStop, Engine.GetPhysicsFrames());
            Sample atStop = trace.ObserveNow();
            Assert.Equal("Stopped: you stand still where you are.", stopped);

            ObservedNavigationOutcome outcome = await trace.AwaitSingleOutcomeAsync();
            Sample rest = trace.Last;
            float drift = new Vector2(rest.X - stopPoint.X, rest.Z - stopPoint.Z).Length();
            float lastSecondSpeed = trace.PlanarSpeedOverLast(1d);
            trace.Record("stopPoint", new[] { stopPoint.X, stopPoint.Z });
            trace.Record("driftFromStopPointMetres", drift);
            trace.Record("lastSecondPlanarSpeedMetresPerSecond", lastSecondSpeed);
            Assert.Equal(NavigationOutcomeKind.Stopped, outcome.Kind);
            Assert.Equal(CoordinateMoveKind.Walk, outcome.Move);
            AssertWithin(drift, StopDriftBoundMetres, "drift after stop (m)");
            AssertWithin(lastSecondSpeed, StandingStillMetresPerSecond, "planar speed over the final second (m/s)");
            AssertOutcomeTextMatches(outcome, atStop, scene.Subject);
        }
        finally
        {
            trace.Save();
        }
    }

    private static void AssertWithin(double value, double bound, string what)
        => Assert.True(value <= bound, string.Create(CultureInfo.InvariantCulture, $"{what} {value:0.###} exceeds {bound:0.###}."));

    private static void AssertOutcomeTextMatches(ObservedNavigationOutcome outcome, Sample at, ICharacter subject)
    {
        string text = outcome.Render(subject);
        string expected = SelfPositionText.Format(outcome.FinalX, outcome.FinalZ, outcome.FinalBearing);
        Assert.EndsWith(expected, text, StringComparison.Ordinal);
        Assert.True(
            at.PlanarDistanceTo((float)outcome.FinalX, (float)outcome.FinalZ) <= OutcomeTextPositionToleranceMetres,
            $"Outcome position ({outcome.FinalX}, {outcome.FinalZ}) vs realised ({at.X}, {at.Z}).");
        // The rendered sentence parses back to the realised transform within its rounding.
        string[] numbers = expected["You are at (".Length..].Split([", ", "), facing ", "°."], StringSplitOptions.RemoveEmptyEntries);
        double textX = double.Parse(numbers[0], CultureInfo.InvariantCulture);
        double textZ = double.Parse(numbers[1], CultureInfo.InvariantCulture);
        double textBearing = double.Parse(numbers[2], CultureInfo.InvariantCulture);
        Assert.True(Math.Abs(textX - at.X) <= OutcomeTextPositionToleranceMetres, $"Text x {textX} vs realised {at.X}.");
        Assert.True(Math.Abs(textZ - at.Z) <= OutcomeTextPositionToleranceMetres, $"Text z {textZ} vs realised {at.Z}.");
        Assert.True(
            AngularError(textBearing, at.MeasuredBearing) <= OutcomeTextHeadingToleranceDegrees,
            $"Text heading {textBearing}° vs realised {at.MeasuredBearing:0.##}°.");
    }

    private static double AngularError(double a, double b)
    {
        double difference = Math.Abs(CompassBearing.Normalise(a) - CompassBearing.Normalise(b));
        return difference > 180d ? 360d - difference : difference;
    }

    private static double PlanarBearing(Vector3 from, Vector3 to)
        => CompassBearing.TryFromWorldFacing(new Vector3(to.X - from.X, 0f, to.Z - from.Z), out double bearing)
            ? bearing
            : double.NaN;

    /// <summary>One physics-frame observation of the subject.</summary>
    private readonly record struct Sample(
        double Time,
        float X,
        float Z,
        double MeasuredBearing,
        double? HipBearing,
        string Phase,
        int Outcomes,
        string? Role = null,
        bool Positioning = false,
        int PathIndex = -1)
    {
        public float PlanarDistanceTo(float x, float z) => new Vector2(X - x, Z - z).Length();
    }

    /// <summary>Per-case physics-frame trace, outcome tracking and JSON artefact.</summary>
    private sealed class Trace
    {
        private readonly WalkScene _scene;
        private readonly string _name;
        private readonly Lock _signalLock = new();
        private readonly List<object> _wakeProbe = [];
        private readonly List<Sample> _samples = [];
        private readonly Dictionary<string, object?> _measurements = [];
        private readonly int _outcomesBefore;
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private readonly ulong _startFrame = Engine.GetPhysicsFrames();
        private double _time;

        /// <summary>
        /// Starts the trace and the wake diagnostic: every fresh-turn delivery signal and every outcome commit is
        /// time-stamped, showing whether an outcome raises any signal a parked wait or the runner reacts to.
        /// </summary>
        public Trace(WalkScene scene, string name)
        {
            _scene = scene;
            _name = name;
            _outcomesBefore = scene.Outcomes.Count;
            scene.Mind.ObservationDeliverySignalled += OnDeliverySignalled;
            scene.Mind.ObservationCommitted += OnCommitted;
        }

        private void OnDeliverySignalled(AlleyCat.Mind.Mind.ObservationDeliverySignal signal)
        {
            lock (_signalLock)
            {
                _wakeProbe.Add(new
                {
                    wall = _wall.Elapsed.TotalSeconds,
                    kind = "delivery-signal",
                    urgency = signal.Urgency.ToString(),
                    signal.WaitOwned
                });
            }
        }

        private void OnCommitted(AlleyCat.Mind.Observation.Observation observation)
        {
            if (observation is ObservedNavigationOutcome outcome)
            {
                lock (_signalLock)
                {
                    _wakeProbe.Add(new
                    {
                        wall = _wall.Elapsed.TotalSeconds,
                        kind = "outcome-committed",
                        requiresFreshTurn = outcome.RequiresFreshTurn(new AlleyCat.Mind.Observation.ObservationContext(_scene.Subject)),
                        importance = outcome.CalculateImportance(new AlleyCat.Mind.Observation.ObservationContext(_scene.Subject)),
                    });
                }
            }
        }

        public Sample? AtOutcome
        {
            get; private set;
        }

        public Sample Last => _samples[^1];

        public int OutcomeCount => _scene.Outcomes.Count - _outcomesBefore;

        public void Record(string key, object? value) => _measurements[key] = value;

        /// <summary>Records the navmesh route and closest map point the production probe sees for a destination.</summary>
        public void RecordRouteProbe(Vector3 destination)
        {
            Rid map = _scene.Navigation.GetNavigationMap();
            Vector3 start = _scene.Subject.GlobalPosition;
            Vector3 closest = NavigationServer3D.MapGetClosestPoint(map, destination);
            Vector3[] route = NavigationServer3D.MapGetPath(map, start, closest, optimize: true, _scene.Navigation.NavigationLayers);
            static float[] Pack(Vector3 point)
            {
                return [point.X, point.Y, point.Z];
            }

            Record("routeProbe", new
            {
                start = Pack(start),
                startClosest = Pack(NavigationServer3D.MapGetClosestPoint(map, start)),
                destinationClosest = Pack(closest),
                layers = _scene.Navigation.NavigationLayers,
                regions = NavigationServer3D.MapGetRegions(map).Count,
                route = route.Select(Pack).ToArray(),
            });
        }

        public async Task RunUntilAsync(Func<bool> done, double timeoutSeconds, string what)
        {
            double deadline = _time + timeoutSeconds;
            while (!done())
            {
                Assert.True(_time < deadline, $"Timed out after {timeoutSeconds} s of simulated time waiting for {what}.");
                Assert.True(_scene.Navigation.Failure.Length == 0, $"Navigation failed: {_scene.Navigation.Failure}");
                await StepAsync();
            }
        }

        /// <summary>
        /// Waits for the move's outcome, then observes the post-outcome window and asserts exactly one outcome arrived.
        /// </summary>
        public async Task<ObservedNavigationOutcome> AwaitSingleOutcomeAsync()
        {
            await RunUntilAsync(() => OutcomeCount > 0, MoveTimeoutSeconds, "the move's outcome");
            AtOutcome = Last;
            double windowEnd = _time + PostOutcomeWindowSeconds;
            while (_time < windowEnd)
            {
                await StepAsync();
            }

            ObservedNavigationOutcome[] outcomes = [.. _scene.Outcomes.Skip(_outcomesBefore)];
            Record("outcomeTexts", outcomes.Select(outcome => outcome.Render(_scene.Subject)).ToArray());
            Record("secondsToOutcome", AtOutcome.Value.Time);
            return Assert.Single(outcomes);
        }

        public float MaximumPlanarExcursion(Vector3 from, bool untilOutcome)
            => _samples.Where(sample => !untilOutcome || sample.Time <= AtOutcome!.Value.Time)
                .Select(sample => sample.PlanarDistanceTo(from.X, from.Z)).DefaultIfEmpty(0f).Max();

        public float PlanarSpeedOverLast(double seconds)
        {
            Sample last = Last;
            Sample first = _samples.First(sample => sample.Time >= last.Time - seconds);
            double span = Math.Max(last.Time - first.Time, 1e-6);
            return (float)(last.PlanarDistanceTo(first.X, first.Z) / span);
        }

        /// <summary>
        /// Bearing of the realised root chord covering the last <paramref name="chord" /> metres of approach, ending at
        /// the first sample within the arrival tolerance of the destination: the direction actually travelled at
        /// arrival.
        /// </summary>
        public double? ArrivalTravelBearing(Vector3 destination, float arrivalTolerance, float chord)
        {
            int reached = _samples.FindIndex(sample => sample.PlanarDistanceTo(destination.X, destination.Z) <= arrivalTolerance);
            if (reached < 0)
            {
                return null;
            }

            Sample arrival = _samples[reached];
            for (int index = reached - 1; index >= 0; index--)
            {
                if (_samples[index].PlanarDistanceTo(arrival.X, arrival.Z) >= chord)
                {
                    return PlanarBearing(new Vector3(_samples[index].X, 0, _samples[index].Z), new Vector3(arrival.X, 0, arrival.Z));
                }
            }

            return null;
        }

        /// <summary>
        /// Cruise speed between leaving <paramref name="margin" /> metres from the start and coming within
        /// <paramref name="margin" /> metres of the destination: the median planar speed of consecutive 0.5 s chunks, so
        /// a brief generation hold (for example while the arrival approach is generated) does not mask the walking pace.
        /// </summary>
        public float CruiseSpeed(Vector3 start, Vector3 destination, float margin)
        {
            Sample[] cruise = [.. _samples.Where(sample => sample.PlanarDistanceTo(start.X, start.Z) >= margin
                && sample.PlanarDistanceTo(destination.X, destination.Z) >= margin
                && sample.Time <= AtOutcome!.Value.Time)];
            List<double> chunks = [];
            // Chunks span simulated time, not a sample count: a slow frame runs several physics ticks per sample.
            int from = 0;
            for (int to = 1; to < cruise.Length; to++)
            {
                if (cruise[to].Time - cruise[from].Time >= CruiseChunkSeconds - 1e-9)
                {
                    chunks.Add(cruise[to].PlanarDistanceTo(cruise[from].X, cruise[from].Z) / (cruise[to].Time - cruise[from].Time));
                    from = to;
                }
            }

            Assert.True(chunks.Count >= 3, "The walk never cruised between its departure and arrival margins.");
            Record("cruiseChunkSpeedsMetresPerSecond", chunks.ToArray());
            double[] sorted = [.. chunks.Order()];
            return (float)(sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2d);
        }

        /// <summary>
        /// Net facing change from the first sample within the arrival tolerance of the destination to the end of the
        /// settling window.
        /// </summary>
        public double NetTurnSincePositionReached(Vector3 destination, float arrivalTolerance)
        {
            Sample? reached = _samples.FirstOrDefault(sample => sample.PlanarDistanceTo(destination.X, destination.Z) <= arrivalTolerance);
            return reached is { } first ? AngularError(first.MeasuredBearing, Last.MeasuredBearing) : double.NaN;
        }

        public void Save()
        {
            _scene.Mind.ObservationDeliverySignalled -= OnDeliverySignalled;
            _scene.Mind.ObservationCommitted -= OnCommitted;
            lock (_signalLock)
            {
                Record("wakeProbe", _wakeProbe.ToArray());
            }

            string directory = ProjectSettings.GlobalizePath(ArtefactDirectory);
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, _name + ".json"),
                JsonSerializer.Serialize(
                    new
                    {
                        name = _name,
                        wallSeconds = _wall.Elapsed.TotalSeconds,
                        destinationReachedDistance = _scene.Navigation.DestinationReachedDistance,
                        facingToleranceDegrees = _scene.Navigation.FacingToleranceDegrees,
                        failure = _scene.Navigation.Failure,
                        measurements = _measurements,
                        samples = _samples,
                    },
                    new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }

        /// <summary>Observes the subject now, without advancing or recording a trace sample.</summary>
        public Sample ObserveNow() => _scene.Observe(SimulatedTime(), OutcomeCount);

        /// <summary>
        /// Advances to the next physics frame, then holds the scene thread until any submitted native request of the
        /// subject is published, so inference completes within simulated time rather than wall-clock time: the result
        /// is consumed at the next physics tick, as at ordinary inference speed, whatever the host's load. Time is the
        /// engine's physics-frame count, so ticks run back-to-back after a slow frame are still counted.
        /// </summary>
        private async Task StepAsync()
        {
            await TestUtils.WaitForNextPhysicsFrameAsync(WalkScene.Tree);
            _scene.AwaitNativeDelivery();
            _time = SimulatedTime();
            _samples.Add(_scene.Observe(_time, OutcomeCount));
        }

        private double SimulatedTime() => (Engine.GetPhysicsFrames() - _startFrame) / (double)Engine.PhysicsTicksPerSecond;
    }

    /// <summary>
    /// The loaded agent-navigation scene with the subject's production tool inventory composed by its own mind.
    /// </summary>
    private sealed class WalkScene : IAsyncDisposable
    {
        private readonly Node _root;
        private readonly FixturePlayerCharacter _player = new();
        private readonly HoldingClientProvider _provider = new();
        private Skeleton3D? _skeleton;
        private int _leftHip = -1;
        private int _rightHip = -1;
        private Dictionary<string, AIFunction> _tools = [];
        // Resolved only after the NPC installer ran; the accessors fail clearly before that.
#pragma warning disable IDE0032
        private AgenticMind? _mind;
        private MotionBricksNavigation? _navigation;
#pragma warning restore IDE0032

        private WalkScene(Node root)
        {
            _root = root;
            Subject = root.GetNode<CharacterHub>("Subject");
            Companion = root.GetNode<CharacterHub>("Companion");
        }

        public static SceneTree Tree => TestUtils.GetSceneTree();

        public CharacterHub Subject
        {
            get;
        }

        public CharacterHub Companion
        {
            get;
        }

        public AgenticMind Mind => _mind ?? throw new InvalidOperationException("The subject mind is not installed yet.");

        public MotionBricksNavigation Navigation
            => _navigation ?? throw new InvalidOperationException("The subject navigation is not installed yet.");

        public IReadOnlyList<ObservedNavigationOutcome> Outcomes
            => [.. Mind.GetObservationTimelineSnapshot().OfType<ObservedNavigationOutcome>()];

        public static async Task<WalkScene> LoadAsync()
        {
            SceneTree tree = TestUtils.GetSceneTree();
            // Neither scene mind may start a production LLM session.
            AgentSessionSuppression.DisableForTesting();
            Node root = AgentNavigationSubject.InstantiateScene();
            WalkScene? scene = null;
            try
            {
                tree.Root.AddChild(root);
                scene = new WalkScene(root);
                // The NPC installer materialises the role template (mind, navigation) after tree entry.
                for (int frame = 0; frame < 600 && (scene._navigation = scene.Subject.GetChildren().OfType<MotionBricksNavigation>().SingleOrDefault()) is null; frame++)
                {
                    await TestUtils.WaitForNextPhysicsFrameAsync(tree);
                }

                MotionBricksNavigation navigation = scene.Navigation;
                scene._mind = scene.Subject.GetNode<AgenticMind>("Mind");
                // Map iteration alone is not enough: the region's polygons sync later, so wait until the map resolves
                // the subject's own position (closest point within 0.5 m planar of the start).
                for (int frame = 0; frame < 600 && !(navigation.Phase == "initial-idle"
                    && NavigationServer3D.MapGetIterationId(navigation.GetNavigationMap()) != 0
                    && navigation.SamplesRecorded > 0
                    && scene.MapResolvesSubject()); frame++)
                {
                    await TestUtils.WaitForNextPhysicsFrameAsync(tree);
                }

                Assert.True(navigation.Failure.Length == 0, $"MotionBricks initialisation failed: {navigation.Failure}");
                Assert.Equal("initial-idle", navigation.Phase);
                Assert.True(scene.MapResolvesSubject(), "The navigation map never resolved the subject's position.");
                scene.ResolveSkeleton();
                Assert.NotEqual(0L, NavigationServer3D.MapGetIterationId(navigation.GetNavigationMap()));
                Assert.Equal(new Vector3(1f, 0f, 1.5f), scene.Subject.GlobalPosition, new Vector3Comparer(0.01f));
                await scene.ComposeToolsAsync();
                return scene;
            }
            catch
            {
                if (scene is not null)
                {
                    await scene.DisposeAsync();
                }
                else
                {
                    root.QueueFree();
                    AgentSessionSuppression.ResetForTesting();
                }

                throw;
            }
        }

        public async Task<string> InvokeAsync(string tool, AIFunctionArguments arguments)
            => (await _tools[tool].InvokeAsync(arguments))?.ToString() ?? string.Empty;

        /// <summary>
        /// Blocks the scene thread while the subject's submitted native request is unpublished, bounded by NAV-001's
        /// 30-second request budget in wall-clock time; exceeding it is a genuine inference-availability failure.
        /// </summary>
        public void AwaitNativeDelivery()
        {
            MotionBricksNavigation navigation = Navigation;
            Assert.True(
                SpinWait.SpinUntil(() => !navigation.IsAwaitingNativeResult, TimeSpan.FromSeconds(NativeDeliveryBudgetSeconds)),
                $"Native inference delivered no result within {NativeDeliveryBudgetSeconds} s.");
        }

        private const double NativeDeliveryBudgetSeconds = 30d;

        public Sample Observe(double time, int outcomes)
        {
            Transform3D measured = Navigation.RecordingMeasuredTransform;
            double measuredBearing = CompassBearing.TryFromWorldFacing(-measured.Basis.Z, out double bearing) ? bearing : double.NaN;
            Vector3 position = Subject.GlobalPosition;
            // Role decision and native path cursor are traced diagnostics, not gated.
            return new Sample(time, position.X, position.Z, measuredBearing, HipBearing(), Navigation.Phase, outcomes,
                Navigation.RoleDecision?.Role, Navigation.RoleDecision?.Positioning ?? false,
                Navigation.LastSampledPathIndex);
        }

        public async ValueTask DisposeAsync()
        {
            Task? worker = _navigation?.WorkerCompletion;
            if (GodotObject.IsInstanceValid(_root))
            {
                _root.GetParent()?.RemoveChild(_root);
                _root.QueueFree();
            }

            await TestUtils.WaitForPhysicsFramesAsync(TestUtils.GetSceneTree(), 2);
            if (worker is not null)
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(30));
            }

            _provider.Free();
            _player.Free();
            AgentSessionSuppression.ResetForTesting();
        }

        /// <summary>
        /// Runs the subject mind's production session preparation with a non-LLM client, a fixed static prompt, visual
        /// context off and a fixture scene context, so the composed tools bind the real navigation and the in-tree mind
        /// polls their outcomes each frame.
        /// </summary>
        private async Task ComposeToolsAsync()
        {
            Mind.ClientProvider = _provider;
            Mind.ScenarioManager = null;
            Mind.SystemInstruction = new PromptStack
            {
                Sections = [new TextPromptSection { Text = "static", Name = "Static" }]
            };
            Mind.CurrentSceneStatus = new PromptStack
            {
                Sections = [new TextPromptSection { Text = "status", Name = "Status" }]
            };
            Mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(
                EnableRequestResponseLogging: false,
                EnableReasoningLogging: false,
                EnableSessionTranscriptLogging: false));
            Mind.SetVisualContextModeLoaderForTesting(() => VisualContextMode.Off);
            Mind.SetSceneContextLoaderForTesting(() => new SceneContext([Subject, Companion, _player]));
            AgenticMind.AgentSession session = await Mind.PrepareSessionAsync(CancellationToken.None);
            _tools = session.Tools.OfType<AIFunction>().ToDictionary(static tool => tool.Name);
            Assert.NotNull(Mind.NavigationController);
            Assert.Contains(WalkToTool.ProductionToolName, _tools.Keys);
        }

        private bool MapResolvesSubject()
        {
            Vector3 start = Subject.GlobalPosition;
            Vector3 closest = NavigationServer3D.MapGetClosestPoint(Navigation.GetNavigationMap(), start);
            return new Vector2(closest.X - start.X, closest.Z - start.Z).Length() < 0.5f;
        }

        private void ResolveSkeleton()
        {
            _skeleton = Subject.RequireMotionTarget();
            _leftHip = _skeleton.FindBone("LeftUpperLeg");
            _rightHip = _skeleton.FindBone("RightUpperLeg");
        }

        private double? HipBearing()
        {
            if (_skeleton is null || _leftHip < 0 || _rightHip < 0)
            {
                return null;
            }

            Transform3D skeleton = _skeleton.GlobalTransform;
            Vector3 right = (skeleton * _skeleton.GetBoneGlobalPose(_rightHip).Origin) - (skeleton * _skeleton.GetBoneGlobalPose(_leftHip).Origin);
            // A body facing f has its right side along f × up, so f = up × right.
            return CompassBearing.TryFromWorldFacing(Vector3.Up.Cross(right), out double bearing) ? bearing : null;
        }
    }

    private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => x.DistanceTo(y) <= tolerance;

        public int GetHashCode(Vector3 obj) => 0;
    }

    private sealed partial class HoldingClientProvider : ClientProvider
    {
        public override IChatClient CreateChatClient(string subjectFullId) => new HoldingClient();

        private sealed class HoldingClient : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
