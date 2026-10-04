using System.Globalization;
using System.Reflection;
using System.Text;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Arrival;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Read-only NAV-001 diagnostic probe for the reported "robot-like" arrival motion. It drives the authored
/// navigation test scene exactly like <see cref="NAV001ArrivalOutcomeIntegrationTests"/> (request through
/// <c>INavigation.SetDestination</c>, reference female NPC rig, Idle start at the identity transform) and records, per
/// physics tick from the request until one second after its terminal outcome: the AnimationTree graph state, every
/// playing clip's playback position, per-foot planted state, IK-003 foot-planting pin state (phases, pins, pin
/// releases), the planner's live plan and per-tick prediction (read reflectively from <see cref="NPCNavigation"/>
/// private fields, never written), the actor transform, the post-IK foot yaw relative to the actor, and the issued
/// clip transitions. S4 additionally rejects repeated complete interior pivot wind-down/restart episodes; the other
/// scenarios remain evidence-only beyond setup and terminal outcome.
/// </summary>
public sealed class NAV001RoboticMotionProbeIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/nav001-s4-stationary-ranking/run-01/candidate/s4";
    private const double RequestTimeoutSeconds = 20.0;
    private const double PostCompletionObservationSeconds = 1.0;

    // The requested probe scenarios in world space (the actor spawns at the identity transform); S6 repeats the
    // UserShuffle request of the arrival outcome test.
    private static readonly ProbeRequest _s1Oblique2m30Deg = new(new Vector3(-1f, 0f, -Mathf.Sqrt(3f)), Mathf.DegToRad(30f));
    private static readonly ProbeRequest _s2Straight4m = new(new Vector3(0f, 0f, -4f), 0f);
    private static readonly ProbeRequest _s3Turn90Left = new(Vector3.Zero, Mathf.Pi / 2f);
    private static readonly ProbeRequest _s4Turn180 = new(Vector3.Zero, Mathf.Pi);
    private static readonly ProbeRequest _s5LateralHalfMetre = new(new Vector3(0.5f, 0f, 0f), 0f);
    private static readonly ProbeRequest _s6UserShuffle = new(new Vector3(-0.45992267f, 0f, -1.4639847f), 0.30439475f);

    /// <summary>S1: 2 m ahead-left at about 30°, final facing along the path.</summary>
    [Fact]
    public Task S1_Oblique2m30Deg_RecordsPerTickEvidence()
        => RunScenarioAsync("s1-oblique-2m-30deg", _s1Oblique2m30Deg);

    /// <summary>S2: 4 m straight ahead, keeping the original facing.</summary>
    [Fact]
    public Task S2_Straight4m_RecordsPerTickEvidence()
        => RunScenarioAsync("s2-straight-4m", _s2Straight4m);

    /// <summary>S3: turn in place 90° to the left, no translation.</summary>
    [Fact]
    public Task S3_Turn90Left_RecordsPerTickEvidence()
        => RunScenarioAsync("s3-turn-90deg-left", _s3Turn90Left);

    /// <summary>S4: turn in place 180°, no translation.</summary>
    [Fact]
    public Task S4_Turn180_RecordsPerTickEvidence()
        => RunScenarioAsync("s4-turn-180deg", _s4Turn180);

    /// <summary>S5: 0.5 m directly to the right, keeping the original facing.</summary>
    [Fact]
    public Task S5_LateralHalfMetre_RecordsPerTickEvidence()
        => RunScenarioAsync("s5-lateral-0.5m", _s5LateralHalfMetre);

    /// <summary>S6: the UserShuffle request from the arrival outcome test (1.53 m at 17.4° final yaw).</summary>
    [Fact]
    public Task S6_UserShuffle_RecordsPerTickEvidence()
        => RunScenarioAsync("s6-user-shuffle", _s6UserShuffle);

    private static async Task RunScenarioAsync(string scenario, ProbeRequest request)
    {
        SceneTree sceneTree = GetSceneTree();
        int previousStepLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(
            LoadPackedScene(NAV001ArrivalOutcomeIntegrationTests.NavigationTestScenePath).Instantiate(),
            exactMatch: false);
        AnimationGraphPlantedStateRecorder? recorder = null;
        RoboticMotionProbe? probe = null;

        try
        {
            Node3D actor = root.GetNode<Node3D>(NAV001ArrivalOutcomeIntegrationTests.ActorNodeName);
            NAV001ArrivalOutcomeIntegrationTests.PrepareActorBeforeTreeEntry(actor);
            sceneTree.Root.AddChild(root);

            INavigation navigation = await NAV001ArrivalOutcomeIntegrationTests.WaitForReadyNavigationAsync(sceneTree, root, actor);
            recorder = NAV001ArrivalOutcomeIntegrationTests.BindRecorder(actor);
            recorder.Attach();
            probe = RoboticMotionProbe.Attach(actor, navigation, recorder);
            await WaitForPhysicsFramesAsync(sceneTree, NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);
            NAV001ArrivalOutcomeIntegrationTests.AssertIdleWarmUp(recorder, actor);

            int ticksPerSecond = Engine.PhysicsTicksPerSecond;
            int timeoutTicks = (int)Math.Ceiling(RequestTimeoutSeconds * ticksPerSecond);
            int settleTicks = (int)Math.Ceiling(PostCompletionObservationSeconds * ticksPerSecond);

            NavigationRequestIdentity identity = NAV001ArrivalOutcomeIntegrationTests.IssueRequest(
                navigation,
                new NAV001ArrivalOutcomeIntegrationTests.ArrivalRequest(request.Destination, request.YawRadians),
                scenario,
                0);
            ulong issueTick = Engine.GetPhysicsFrames();
            // The physics-frame signal fires before that tick's callbacks, so every capture completes the engine
            // iteration first: the plan, the first transition and the first movement all happen on the issue tick.
            await WaitForNextFrameAsync(sceneTree);
            probe.CaptureTick();

            NavigationTerminalOutcomeRecord? outcome = null;
            for (int tick = 0; tick < timeoutTicks && outcome is null; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                await WaitForNextFrameAsync(sceneTree);
                probe.CaptureTick();
                outcome = NAV001ArrivalOutcomeIntegrationTests.FindOutcome(navigation, identity);
            }

            for (int tick = 0; tick < settleTicks; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                await WaitForNextFrameAsync(sceneTree);
                probe.CaptureTick();
            }

            await WaitForFramesAsync(sceneTree, 1);
            recorder.Detach();
            probe.WriteEvidence(EvidenceDirectory, scenario, issueTick, request, outcome, ticksPerSecond);
            Assert.True(
                outcome is not null,
                $"[{scenario}] no terminal outcome within {RequestTimeoutSeconds:0} s (status {navigation.CurrentStatus.Status}); evidence was written to {EvidenceDirectory}.");
            if (scenario == "s4-turn-180deg")
            {
                Assert.Equal(NavigationTerminalOutcome.Completed, outcome.Value.Outcome);
                Assert.Equal(0, recorder.UnpairedTicks);
                Assert.Equal(probe.CapturedTicks, probe.PairedTicks);
                Assert.Equal(settleTicks, probe.CapturedTicks - probe.ExecutionTicks(outcome.Value.TerminalPhysicsTick));
                RoboticMotionProbe.VerifyS4DetectorNegativeControls(ticksPerSecond);
                string intervals = probe.S4InteriorWindDownReport(request.YawRadians, ticksPerSecond, out int repeated);
                File.WriteAllText(Path.Combine(
                    EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory)),
                    "s4-turn-180deg-wind-down.txt"), intervals);
                Assert.True(repeated < 2,
                    $"[S4] {repeated} complete interior pivot wind-down/restart episodes while >=45° remained; expected fewer than two. See s4-turn-180deg-wind-down.txt and the complete ticks.csv under {EvidenceDirectory}.\n{intervals}");
            }
        }
        finally
        {
            probe?.Dispose();
            recorder?.Detach();
            Engine.MaxPhysicsStepsPerFrame = previousStepLimit;
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private readonly record struct ProbeRequest(Vector3 Destination, float YawRadians);

    /// <summary>
    /// Per-tick sampler of everything the diagnosis needs: recorder samples, foot-planting provider diagnostics,
    /// post-IK foot yaw (captured inside <see cref="Skeleton3D.SignalName.SkeletonUpdated"/>), and the navigation
    /// component's live plan/prediction state, read reflectively and never modified.
    /// </summary>
    private sealed class RoboticMotionProbe : IDisposable
    {
        private static readonly FieldInfo _planField = Field("_plan");
        private static readonly FieldInfo _predictionField = Field("_prediction");
        private static readonly FieldInfo _tickField = Field("_tick");
        private static readonly FieldInfo _nextSegmentField = Field("_nextSegment");
        private static readonly FieldInfo _awaitingPlanField = Field("_awaitingPlan");
        private static readonly FieldInfo _roleOfField = Field("_roleOf");

        private readonly Node3D _actor;
        private readonly NPCNavigation _navigation;
        private readonly AnimationGraphPlantedStateRecorder _recorder;
        private readonly Skeleton3D _skeleton;
        private readonly FootPlantingTargetProvider _leftPlanting;
        private readonly FootPlantingTargetProvider _rightPlanting;
        private readonly int _leftFoot;
        private readonly int _rightFoot;
        private readonly int _leftToes;
        private readonly int _rightToes;
        private readonly Dictionary<ulong, FootPlantingTickDiagnostic> _leftDiagnostics = [];
        private readonly Dictionary<ulong, FootPlantingTickDiagnostic> _rightDiagnostics = [];
        private readonly Dictionary<ulong, (float Left, float Right)> _footYaws = [];
        private readonly List<string> _tickRows = [];
        private readonly List<MotionTick> _motionTicks = [];
        private readonly List<string> _planNotes = [];
        private readonly Dictionary<string, string> _roleOfState = [];
        private readonly List<NPCClipTransitionRecord> _transitions = [];
        private int _sampleCursor;
        private ulong? _firstTick;
        private ArrivalPlan? _lastPlan;

        public int CapturedTicks => _motionTicks.Count;

        public int PairedTicks
        {
            get; private set;
        }

        public int ExecutionTicks(ulong terminalTick) => _motionTicks.Count(tick => tick.Tick <= terminalTick);

        private RoboticMotionProbe(
            Node3D actor,
            NPCNavigation navigation,
            AnimationGraphPlantedStateRecorder recorder,
            Skeleton3D skeleton,
            FootPlantingTargetProvider leftPlanting,
            FootPlantingTargetProvider rightPlanting)
        {
            _actor = actor;
            _navigation = navigation;
            _recorder = recorder;
            _skeleton = skeleton;
            _leftPlanting = leftPlanting;
            _rightPlanting = rightPlanting;
            _leftFoot = RequireBone(skeleton, "LeftFoot");
            _rightFoot = RequireBone(skeleton, "RightFoot");
            _leftToes = RequireBone(skeleton, "LeftToes");
            _rightToes = RequireBone(skeleton, "RightToes");
        }

        public static RoboticMotionProbe Attach(Node3D actor, INavigation navigation, AnimationGraphPlantedStateRecorder recorder)
        {
            NPCNavigation npcNavigation = Assert.IsType<NPCNavigation>(navigation);
            Skeleton3D skeleton = actor.GetNode<Skeleton3D>(NAV001ArrivalOutcomeIntegrationTests.SkeletonPath);
            FootPlantingTargetProvider left = actor.GetNode<FootPlantingTargetProvider>("CharacterIK/LeftFootPlanting");
            FootPlantingTargetProvider right = actor.GetNode<FootPlantingTargetProvider>("CharacterIK/RightFootPlanting");
            RoboticMotionProbe probe = new(actor, npcNavigation, recorder, skeleton, left, right);
            left.TickRecorded += probe.OnFootTick;
            right.TickRecorded += probe.OnFootTick;
            skeleton.SkeletonUpdated += probe.OnSkeletonUpdated;
            if (_roleOfField.GetValue(npcNavigation) is System.Collections.IDictionary raw)
            {
                foreach (System.Collections.DictionaryEntry entry in raw)
                {
                    probe._roleOfState[entry.Key.ToString() ?? string.Empty] = entry.Value?.ToString() ?? string.Empty;
                }
            }

            return probe;
        }

        public void CaptureTick()
        {
            ulong tick = Engine.GetPhysicsFrames();
            CapturePlanIfNew(tick);
            _transitions.AddRange(_navigation.IssuedClipTransitions.Skip(_transitions.Count));

            PlantedStateSample? sample = NextSample(tick);
            if (sample is not null)
            {
                PairedTicks++;
            }
            var plan = _planField.GetValue(_navigation) as ArrivalPlan;
            var prediction = _predictionField.GetValue(_navigation) as SequencePrediction;
            int internalTick = (int)_tickField.GetValue(_navigation)!;
            int nextSegment = (int)_nextSegmentField.GetValue(_navigation)!;
            bool awaitingPlan = (bool)_awaitingPlanField.GetValue(_navigation)!;

            GraphRecord graph = sample?.Graph ?? default;
            string clips = sample is null
                ? string.Empty
                : string.Join(';', sample.Contributions.Select(contribution => contribution.ToString()));
            double currentPosition = sample?.Contributions
                .OrderByDescending(contribution => contribution.Weight)
                .Select(contribution => contribution.SourceTimeSeconds)
                .FirstOrDefault() ?? double.NaN;
            _ = _leftDiagnostics.TryGetValue(tick, out FootPlantingTickDiagnostic left);
            _ = _rightDiagnostics.TryGetValue(tick, out FootPlantingTickDiagnostic right);
            _ = _footYaws.TryGetValue(tick, out (float Left, float Right) footYaw);

            // The movement operation applied prediction tick (internalTick − 1) on this physics tick, if any.
            bool predicted = prediction is not null && internalTick > 0 && internalTick - 1 < prediction.Ticks.Length;
            SequencePose pose = predicted ? prediction!.Ticks[internalTick - 1].Pose : prediction?.StartPose ?? default;

            Transform3D actor = sample?.Actor ?? _actor.GlobalTransform;
            // Keep both physical slots and all outgoing/incoming sources. A fade is not a loss of evidence:
            // match the logical pivot role and each source clock independently, including its outgoing weight.
            PivotClock[] pivots = sample is null ? [] : [.. sample.Contributions
                .Where(contribution => _roleOfState.GetValueOrDefault(contribution.State) is "pl45" or "pr45")
                .Select(contribution => new PivotClock(
                    _roleOfState[contribution.State], contribution.State, contribution.SourceTimeSeconds,
                    contribution.Intervals.DurationSeconds, contribution.Weight))];
            bool expectsPivot = _roleOfState.GetValueOrDefault(graph.Current) is "pl45" or "pr45"
                || (graph.Fading is { } fading && _roleOfState.GetValueOrDefault(fading) is "pl45" or "pr45");
            _motionTicks.Add(new MotionTick(tick, NAV001ArrivalOutcomeIntegrationTests.YawOf(actor),
                sample is not null, expectsPivot, pivots));
            Vector3 origin = actor.Origin;
            _firstTick ??= tick;
            _tickRows.Add(FormattableString.Invariant(
                $"{tick},{(tick - _firstTick.Value) * (1000d / Engine.PhysicsTicksPerSecond):0.###},{graph.Current},{graph.Fading},{Format(graph.FadingPosition)},{Format(graph.FadingLength)},{Format(currentPosition)},\"{clips}\",{Flag(sample?.Planted.Left)},{Flag(sample?.Planted.Right)},{Flag(sample?.ExactPlanted.Left)},{Flag(sample?.ExactPlanted.Right)},{(int)left.Phase},{(int)right.Phase},{Format(left.Pin.X)},{Format(left.Pin.Z)},{Format(right.Pin.X)},{Format(right.Pin.Z)},{Format(left.ReachUsage)},{Format(right.ReachUsage)},{Flag(left.ReachBlendOutStarted)},{Flag(right.ReachBlendOutStarted)},{Flag(left.ContinuityReset)},{Flag(right.ContinuityReset)},{Format(origin.X)},{Format(origin.Z)},{Format(Mathf.RadToDeg(NAV001ArrivalOutcomeIntegrationTests.YawOf(actor)))},{Flag(plan is not null)},{internalTick},{nextSegment},{Flag(awaitingPlan)},{(predicted ? 1 : 0)},{Format(pose.Position.X)},{Format(pose.Position.Y)},{Format(Mathf.RadToDeg(pose.Yaw))},{Format(Mathf.RadToDeg(footYaw.Left))},{Format(Mathf.RadToDeg(footYaw.Right))},{Format(sample?.LeftBall.X)},{Format(sample?.LeftBall.Z)},{Format(sample?.RightBall.X)},{Format(sample?.RightBall.Z)}"));

            static string Format(double? value)
            {
                return value is { } finite && double.IsFinite(finite)
                    ? finite.ToString("R", CultureInfo.InvariantCulture)
                    : string.Empty;
            }

            static string Flag(bool? value)
            {
                return value == true ? "1" : "0";
            }
        }

        public void WriteEvidence(
            string directory,
            string scenario,
            ulong issueTick,
            ProbeRequest request,
            NavigationTerminalOutcomeRecord? outcome,
            int ticksPerSecond)
        {
            string path = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(directory));
            File.WriteAllText(Path.Combine(path, $"{scenario}-ticks.csv"), TicksCsv());
            File.WriteAllText(Path.Combine(path, $"{scenario}-segments.csv"), SegmentsCsv());
            File.WriteAllText(Path.Combine(path, $"{scenario}-plan.txt"), string.Join('\n', _planNotes) + "\n");
            Transform3D final = _actor.IsInsideTree() ? _actor.GlobalTransform : Transform3D.Identity;
            float planarError = new Vector2(final.Origin.X - request.Destination.X, final.Origin.Z - request.Destination.Z).Length();
            float yawError = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(
                request.YawRadians,
                NAV001ArrivalOutcomeIntegrationTests.YawOf(final))));
            StringBuilder summary = new();
            _ = summary.AppendLine(CultureInfo.InvariantCulture, $"scenario={scenario} ticks_per_second={ticksPerSecond}");
            _ = summary.AppendLine(FormattableString.Invariant(
                $"request=({request.Destination.X:R}, {request.Destination.Z:R}) yaw={Mathf.RadToDeg(request.YawRadians):0.###} deg issued_tick={issueTick}"));
            string outcomeText = outcome is { } terminal
                ? $"{terminal.Outcome}/{terminal.Reason}"
                : "none (timeout)";
            _ = summary.AppendLine(FormattableString.Invariant(
                $"outcome={outcomeText} final_planar_error={planarError * 1000:0.###} mm final_yaw_error={yawError:0.###} deg"));
            _ = summary.AppendLine(CultureInfo.InvariantCulture,
                $"recorded_ticks={_tickRows.Count} transitions={_transitions.Count} unpaired_recorder_ticks={_recorder.UnpairedTicks}");
            File.WriteAllText(Path.Combine(path, $"{scenario}-summary.txt"), summary.ToString());
        }

        public void Dispose()
        {
            _skeleton.SkeletonUpdated -= OnSkeletonUpdated;
            _leftPlanting.TickRecorded -= OnFootTick;
            _rightPlanting.TickRecorded -= OnFootTick;
        }

        private readonly record struct PivotClock(string Role, string State, double Source, double Duration, double Weight);

        private readonly record struct MotionTick(
            ulong Tick, float WorldYaw, bool Paired, bool PivotExpected, PivotClock[] Pivots);

        /// <summary>Test-local synthetic controls for alias/fade invariance and fail-closed observation coverage.</summary>
        public static void VerifyS4DetectorNegativeControls(int ticksPerSecond)
        {
            int cycle = (int)Math.Round(98d / 60d * ticksPerSecond);
            var overlapping = new MotionTick[4 * cycle];
            for (int index = 0; index < overlapping.Length; index++)
            {
                double source = index % cycle / (double)ticksPerSecond;
                double angle = source <= 0.5d ? 54d * source
                    : source <= 1d ? 27d + (30d * (source - 0.5d))
                    : source <= 1.5d ? 42d + (6d * (source - 1d)) : 45d;
                float yaw = Mathf.DegToRad((float)((45d * (index / cycle)) + angle));
                // Same source/yaw trace under either two independently seeking physical labels or one canonical
                // slot. Graph fade labels and weights must not make the repeat disappear from the observer.
                PivotClock[] clocks =
                [
                    new("pl45", "NPCPivotL", source, cycle / (double)ticksPerSecond, 0.45d),
                    new("pl45", "NPCPivotLContinuation", source, cycle / (double)ticksPerSecond, 0.55d),
                ];
                overlapping[index] = new MotionTick((ulong)index, yaw, true, true, clocks);
            }

            MotionTick[] single = [.. overlapping.Select(tick => tick with
            {
                Pivots = [tick.Pivots[0] with { Weight = 1d }]
            })];
            _ = S4InteriorWindDownReport(single, Mathf.Pi, ticksPerSecond, out int original);
            _ = S4InteriorWindDownReport(overlapping, Mathf.Pi, ticksPerSecond, out int relabelled);
            Assert.True(original >= 2, "SETUP: synthetic source/realised-yaw red control did not form two episodes.");
            Assert.Equal(original, relabelled);

            MotionTick[] missing = [.. overlapping];
            missing[60] = missing[60] with
            {
                Paired = false
            };
            _ = Assert.Throws<InvalidDataException>(() => S4InteriorWindDownReport(missing, Mathf.Pi, ticksPerSecond, out _));
            MotionTick[] invalid = [.. overlapping];
            invalid[60] = invalid[60] with
            {
                Pivots = [invalid[60].Pivots[0] with { Source = double.NaN }]
            };
            _ = Assert.Throws<InvalidDataException>(() => S4InteriorWindDownReport(invalid, Mathf.Pi, ticksPerSecond, out _));

            MotionTick[] mirrored = [.. overlapping.Select(tick => tick with { WorldYaw = -tick.WorldYaw })];
            _ = S4InteriorWindDownReport(mirrored, -Mathf.Pi, ticksPerSecond, out int opposite);
            Assert.Equal(original, opposite);
        }

        /// <summary>Count only complete native wind-downs that demonstrably restart while another pivot is needed.</summary>
        public string S4InteriorWindDownReport(float targetYaw, int ticksPerSecond, out int repeated)
            => S4InteriorWindDownReport(_motionTicks, targetYaw, ticksPerSecond, out repeated);

        private static string S4InteriorWindDownReport(
            IReadOnlyList<MotionTick> samples, float targetYaw, int ticksPerSecond, out int repeated)
        {
            const double sourceTolerance = 0.001d; // Source positions are recorded in single precision.
            double delta = 1d / ticksPerSecond;
            int halfSecondTicks = ticksPerSecond / 2;
            repeated = 0;
            StringBuilder report = new("start_tick,end_tick,restart_tick,restart_end_tick,tail_yaw_deg,restart_yaw_deg,remaining_deg,episode\n");
            foreach (MotionTick observed in samples)
            {
                if (!observed.Paired || !float.IsFinite(observed.WorldYaw)
                    || (observed.PivotExpected && observed.Pivots.Length == 0)
                    || observed.Pivots.Any(clock => !double.IsFinite(clock.Source) || !double.IsFinite(clock.Duration)
                        || !double.IsFinite(clock.Weight) || clock.Weight < 0d || clock.Duration <= 0d))
                {
                    throw new InvalidDataException($"S4 source/yaw coverage is incomplete or non-finite at physics tick {observed.Tick}.");
                }
            }

            for (int start = 0; start + halfSecondTicks < samples.Count; start++)
            {
                MotionTick beginning = samples[start];
                // An alias shares the logical role, not the independent source position of the outgoing slot.
                string[] roles = [.. beginning.Pivots.Select(clock => clock.Role).Distinct()];
                foreach (string role in roles)
                {
                    if (!HasPhase(beginning, role, 1d))
                    {
                        continue;
                    }

                    int end = start + halfSecondTicks;
                    if (!IsContiguousSource(start, end, 1d, role))
                    {
                        continue;
                    }

                    MotionTick ending = samples[end];
                    float remaining = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(ending.WorldYaw, targetYaw)));
                    if (!float.IsFinite(remaining))
                    {
                        throw new InvalidDataException($"S4 remaining-yaw observation is non-finite at tick {ending.Tick}.");
                    }

                    // One complete authored 45° pivot is still requested, so this is not final deceleration.
                    if (remaining < 45f)
                    {
                        continue;
                    }

                    // Source phase wraps at authored duration; use each contribution, including fades.
                    int restart = -1;
                    for (int index = end + 1; index < Math.Min(end + halfSecondTicks, samples.Count); index++)
                    {
                        if (HasPhase(samples[index], role, 0d))
                        {
                            restart = index;
                            break;
                        }
                    }

                    if (restart < 0 || restart + halfSecondTicks >= samples.Count
                        || !IsContiguousSource(restart, restart + halfSecondTicks, 0d, role))
                    {
                        continue;
                    }

                    MotionTick resumed = samples[restart];
                    MotionTick resumedEnd = samples[restart + halfSecondTicks];
                    float tailYaw = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(beginning.WorldYaw, ending.WorldYaw)));
                    float restartYaw = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(resumed.WorldYaw, resumedEnd.WorldYaw)));
                    bool episode = tailYaw < restartYaw;
                    if (episode)
                    {
                        repeated++;
                    }

                    _ = report.AppendLine(FormattableString.Invariant(
                        $"{beginning.Tick},{ending.Tick},{resumed.Tick},{resumedEnd.Tick},{tailYaw:R},{restartYaw:R},{remaining:R},{episode}"));
                }
            }

            _ = report.AppendLine(FormattableString.Invariant($"captured={samples.Count} paired={samples.Count(sample => sample.Paired)} qualifying_episodes={repeated}"));
            return report.ToString();

            bool IsContiguousSource(int from, int to, double sourceStart, string role)
            {
                ulong firstTick = samples[from].Tick;
                for (int offset = 0; offset <= to - from; offset++)
                {
                    MotionTick observed = samples[from + offset];
                    if (observed.Tick != firstTick + (ulong)offset
                        || !HasPhase(observed, role, sourceStart + (offset * delta)))
                    {
                        return false;
                    }
                }

                return true;
            }

            bool HasPhase(MotionTick tick, string role, double expected)
            {
                return tick.Pivots.Any(clock => clock.Role == role && clock.Weight > 0d
                                && double.IsFinite(clock.Source) && double.IsFinite(clock.Duration)
                                && Math.Abs((clock.Source >= clock.Duration - sourceTolerance ? 0d : clock.Source) - expected) <= sourceTolerance);
            }
        }

        private static FieldInfo Field(string name)
            => typeof(NPCNavigation).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
               ?? throw new InvalidOperationException($"NPCNavigation has no '{name}' field.");

        private static int RequireBone(Skeleton3D skeleton, string name)
        {
            int index = skeleton.FindBone(name);
            return index >= 0 ? index : throw new InvalidOperationException($"The rig has no '{name}' bone.");
        }

        private PlantedStateSample? NextSample(ulong tick)
        {
            IReadOnlyList<PlantedStateSample> samples = _recorder.Samples;
            while (_sampleCursor < samples.Count && samples[_sampleCursor].Tick < tick)
            {
                _sampleCursor++;
            }

            return _sampleCursor < samples.Count && samples[_sampleCursor].Tick == tick ? samples[_sampleCursor] : null;
        }

        private void CapturePlanIfNew(ulong tick)
        {
            if (_planField.GetValue(_navigation) is not ArrivalPlan plan || ReferenceEquals(plan, _lastPlan))
            {
                return;
            }

            _lastPlan = plan;
            var prediction = _predictionField.GetValue(_navigation) as SequencePrediction;
            _planNotes.Add(DescribePlan(tick, plan, prediction));
        }

        private static string DescribePlan(ulong tick, ArrivalPlan plan, SequencePrediction? prediction)
        {
            StringBuilder text = new();
            _ = text.AppendLine(CultureInfo.InvariantCulture,
                $"plan@tick={tick} kind={plan.Kind} status={plan.Status} target=({plan.Target.Position.X:R}, {plan.Target.Position.Y:R}) yaw={Mathf.RadToDeg(plan.Target.Yaw):0.###}deg");
            SequenceStart start = plan.SequenceStart!;
            string fadingText = start.Fading is { } fading
                ? $"{fading.FromRole}@{fading.FromPositionSeconds:0.#####}s fade {fading.FadePositionSeconds:0.###}/{fading.FadeLengthSeconds:0.###}"
                : "none";
            _ = text.AppendLine(FormattableString.Invariant(
                $"  start: role={start.CurrentRole} position={start.CurrentPositionSeconds:0.#####}s exit={start.ExitSeconds:0.#####}s exitCycles={start.ExitCycles} fading={fadingText}"));
            foreach ((ArrivalPlanSegment segment, int index) in plan.Segments.Select((segment, index) => (segment, index)))
            {
                _ = text.AppendLine(FormattableString.Invariant(
                    $"  segment[{index}] stage={segment.Stage} role={segment.Role} kind={segment.Kind} entry_b={segment.EntrySeconds:0.#####}s exit_a={segment.ExitSeconds:0.#####}s cycles={segment.ExitCycles} crossfade={segment.CrossfadeSeconds:0.###}s separation={segment.EntrySeparation * 1000:0.#}mm"));
            }

            if (prediction is not null)
            {
                _ = text.AppendLine(FormattableString.Invariant(
                    $"  prediction: ticks={prediction.Ticks.Length} entryTicks=[{string.Join(", ", prediction.EntryTicks)}] endPose=({prediction.EndPose.Position.X:0.###}, {prediction.EndPose.Position.Y:0.###}) yaw={Mathf.RadToDeg(prediction.EndPose.Yaw):0.###}deg windows={prediction.Windows.Length}"));
            }

            _ = text.Append(FormattableString.Invariant(
                $"  statistics: compositions={plan.Statistics.Compositions} summaryPredictions={plan.Statistics.SummaryPredictions} budgetExhausted={plan.Statistics.BudgetExhausted} elapsedMs={plan.Statistics.ElapsedMilliseconds:0.###}"));
            return text.ToString();
        }

        private void OnFootTick(FootPlantingTickDiagnostic diagnostic)
            => (diagnostic.Side == LimbSide.Left ? _leftDiagnostics : _rightDiagnostics)[diagnostic.Tick] = diagnostic;

        private void OnSkeletonUpdated()
        {
            ulong tick = Engine.GetPhysicsFrames();
            Transform3D actorFromSkeleton = _actor.GlobalTransform.AffineInverse() * _skeleton.GlobalTransform;
            _footYaws[tick] = (FootYaw(actorFromSkeleton, _leftFoot, _leftToes), FootYaw(actorFromSkeleton, _rightFoot, _rightToes));
            return;

            float FootYaw(Transform3D frame, int foot, int toes)
            {
                Vector3 direction = (frame * _skeleton.GetBoneGlobalPose(toes)).Origin - (frame * _skeleton.GetBoneGlobalPose(foot)).Origin;
                direction.Y = 0f;
                return direction.LengthSquared() < 1e-10f ? float.NaN : Mathf.Atan2(-direction.X, -direction.Z);
            }
        }

        private string TicksCsv()
        {
            StringBuilder csv = new(
                "tick,t_ms,current,fading,fade_pos,fade_len,cur_pos,clips,planted_l,planted_r,exact_planted_l,exact_planted_r,fp_phase_l,fp_phase_r,fp_pin_l_x,fp_pin_l_z,fp_pin_r_x,fp_pin_r_z,fp_reach_l,fp_reach_r,fp_reachout_l,fp_reachout_r,fp_reset_l,fp_reset_r,actor_x,actor_z,actor_yaw_deg,has_plan,applied_index,next_segment,awaiting_plan,pred_valid,pred_x,pred_z,pred_yaw_deg,foot_yaw_l_deg,foot_yaw_r_deg,ball_l_x,ball_l_z,ball_r_x,ball_r_z\n");
            foreach (string row in _tickRows)
            {
                _ = csv.AppendLine(row);
            }

            return csv.ToString();
        }

        private string SegmentsCsv()
        {
            StringBuilder csv = new("tick,from_state,to_state,from_role,to_role,entry_pos_s,crossfade_s\n");
            foreach (NPCClipTransitionRecord record in _transitions)
            {
                _ = csv.AppendLine(CultureInfo.InvariantCulture,
                    $"{record.PhysicsTick},{record.From},{record.To},{RoleOf(record.From)},{RoleOf(record.To)},{record.EntryPositionSeconds.ToString("R", CultureInfo.InvariantCulture)},{record.CrossfadeSeconds.ToString("R", CultureInfo.InvariantCulture)}");
            }

            return csv.ToString();

            string RoleOf(string state)
            {
                return _roleOfState.TryGetValue(state, out string? role) ? role : "?";
            }
        }
    }
}
