using System.Globalization;
using System.Text;
using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.Arrival.NAV001ArrivalPlannerIntegrationTests;
using static AlleyCat.IntegrationTests.Navigation.NAV001ArrivalOutcomeIntegrationTests;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>
/// P4 unit 6b: measurement-only replay of the planner with swing correction on the two production rigs.
/// Only Idle immobility, double-support correction and blended phase agreement are acceptance assertions.
/// </summary>
public sealed class NAV001CorrectedArrivalIntegrationTests
{
    private const int IdleTicks = 60;
    private const string EvidenceDirectory = "res://temp/nav001-planted-seam-trial/after";

    /// <summary>Female rig, default correction caps including flight.</summary>
    [Fact]
    public Task Female_Defaults() => VerifyAsync(NPCNavigationRig.Female, true);

    /// <summary>Male rig, default correction caps including flight.</summary>
    [Fact]
    public Task Male_Defaults() => VerifyAsync(NPCNavigationRig.Male, true);

    /// <summary>Female rig, unchanged caps with flight correction disabled.</summary>
    [Fact]
    public Task Female_NoFlight() => VerifyAsync(NPCNavigationRig.Female, false);

    /// <summary>Male rig, unchanged caps with flight correction disabled.</summary>
    [Fact]
    public Task Male_NoFlight() => VerifyAsync(NPCNavigationRig.Male, false);

    private static async Task VerifyAsync(NPCNavigationRig rig, bool flight)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(rig);
        await fixture.StepAsync(WarmUpPhysicsTicks);
        Binding binding = Bind(fixture);
        string directory = EvidenceDestinations.Resolve(
            ProjectSettings.GlobalizePath($"{EvidenceDirectory}/{rig}-{(flight ? "defaults" : "no-flight")}"));
        var summary = new StringBuilder("scenario,leg,A1_mm,A1_deg,A1_pass,A2_mm,A2_pass,A3_changed_ticks,A3_mm,A3_deg,A4_mm,A4_deg,left_reach,left_tick,right_reach,right_tick,forward_mm,backward_mm,lateral_mm,yaw_abs_deg,FL_ticks,FL_corrected_ticks,collisions,phase_pass\n");
        List<string> failures = [];
        try
        {
            foreach ((string name, (Vector3 Destination, float Yaw)[] legs) in Scenarios())
            {
                await TeleportToIdleAtOriginAsync(fixture, binding);
                for (int leg = 0; leg < legs.Length; leg++)
                {
                    var target = new ArrivalTarget(new Vector2(legs[leg].Destination.X, legs[leg].Destination.Z), legs[leg].Yaw);
                    ArrivalPlan plan = binding.Planner.Plan(IdleStart(fixture, binding), target);
                    await MeasureAsync(fixture, binding, plan, name, leg, flight, directory, summary, failures);
                }
            }

            foreach (bool prompt in new[] { false, true })
            {
                await TeleportToIdleAtOriginAsync(fixture, binding);
                ArrivalPlan first = binding.Planner.Plan(IdleStart(fixture, binding), new ArrivalTarget(new Vector2(0f, -4f), 0f));
                RequirePlan(first);
                SequencePrediction prediction = binding.Planner.Predict(first);
                string walk = binding.Catalogue[StandingLocomotionCapability.CyclicForwardPrimary].Role;
                int walkTicks = 0;
                int cut = -1;
                for (int tick = 0; tick < prediction.Ticks.Length; tick++)
                {
                    PredictedTick at = prediction.Ticks[tick];
                    walkTicks = at.Current.Role == walk && at.FadingFrom is null ? walkTicks + 1 : 0;
                    if (walkTicks == 25)
                    {
                        cut = tick;
                        break;
                    }
                }

                if (cut < 0)
                {
                    throw new InvalidOperationException("SETUP: the 4 m plan has no 25-tick solo forward cycle.");
                }

                // Identical uncorrected lead-in and actual-pose-relative goal to the planner oracle.
                _ = await RunAsync(fixture, binding, first, prediction, cut + 1);
                SequencePose pose = PoseOf(fixture.Actor);
                var start = new ArrivalStart(pose, walk, prediction.Ticks[cut].Current.PositionSeconds) { Prompt = prompt };
                SequencePose goal = new PlanarMotion(0.6d, 2d, Mathf.DegToRad(-20f)).From(pose);
                ArrivalPlan plan = binding.Planner.Plan(start, new ArrivalTarget(goal.Position, goal.Yaw));
                await MeasureAsync(fixture, binding, plan, prompt ? "replan-mid-walk-prompt" : "replan-mid-walk", 0, flight, directory, summary, failures);
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "summary.csv"), summary.ToString());
            File.WriteAllLines(Path.Combine(directory, "assertion-failures.txt"), failures);
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    private static void RequirePlan(ArrivalPlan plan)
    {
        if (plan.Status != ArrivalPlanStatus.Planned)
        {
            throw new InvalidOperationException($"SETUP: planner refused the scenario: {plan.Failure} {plan.FailureDetail}");
        }
    }

    private static async Task MeasureAsync(
        NPCNavigationRigFixture fixture, Binding binding, ArrivalPlan plan, string scenario, int leg,
        bool flight, string directory, StringBuilder summary, List<string> failures)
    {
        RequirePlan(plan);
        SequencePrediction prediction = binding.Planner.Predict(plan);
        File.WriteAllText(Path.Combine(directory, $"{scenario}-{leg}-plan.txt"),
            FormattableString.Invariant($"kind={plan.Kind}\nresidual_mm={plan.PlanarResidual.Length() * 1000f:R}\nyaw_residual_deg={Mathf.RadToDeg(plan.YawResidual):R}\n")
            + string.Join('\n', plan.Segments.Select(segment => FormattableString.Invariant($"{segment.Role},{segment.EntrySeconds:R},{segment.ExitSeconds:R},{segment.ExitCycles}"))));
        var allocator = new SwingCorrectionAllocator(new SwingCorrectionCaps { AllowFlightCorrection = flight });
        var actor = (CharacterBody3D)fixture.Actor;
        Node3D reference = fixture.Navigation.RootMotionReference!;
        NPCAnimationTreeDriver driver = fixture.Driver;
        AnimationNodeStateMachinePlayback playback = fixture.Tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
        using AnimationGraphPlantedStateRecorder recorder = fixture.BindRecorder();
        recorder.Attach();
        var roots = new Dictionary<long, (ulong Tick, Vector3 Position, Quaternion Rotation)>();
        var reaches = new Dictionary<ulong, (float Left, float Right)>();
        var left = new LegChain(fixture.Skeleton, "Left");
        var right = new LegChain(fixture.Skeleton, "Right");
        void OnMixer()
        {
            roots[driver.EvaluationCount + 1] = (Engine.GetPhysicsFrames(), fixture.Tree.GetRootMotionPosition(), fixture.Tree.GetRootMotionRotation());
        }

        void OnSkeleton()
        {
            reaches[Engine.GetPhysicsFrames()] = (left.Usage(), right.Usage());
        }

        fixture.Tree.MixerApplied += OnMixer;
        fixture.Skeleton.SkeletonUpdated += OnSkeleton;
        var ticks = new StringBuilder("plan_tick,physics_tick,window,planted_left,planted_right,planar_right_mm,planar_forward_mm,yaw_deg,pivot,origin_right_mm,origin_forward_mm,residual_right_mm,residual_forward_mm,residual_yaw_deg,actor_x,actor_z,actor_yaw_deg,idle\n");
        var used = new CorrectionTotals();
        int collisions = 0;
        int changedIdleTicks = 0;
        float idlePosition = 0f;
        float idleYaw = 0f;
        float doublePlanar = 0f;
        float doubleYaw = 0f;
        ulong firstTick = 0;
        ulong lastTick = 0;
        Transform3D end = actor.GlobalTransform;
        int tick = 0;
        int windowIndex = 0;
        int leftToes = fixture.Skeleton.FindBone("LeftToes");
        int rightToes = fixture.Skeleton.FindBone("RightToes");
        AnimationGraphPlantedStateSource plantedSource = fixture.Actor.FindChildren("*", "", true, false)
            .OfType<AnimationGraphPlantedStateSource>().Single();
        SwingCorrection correction = default;
        SwingCorrectionResidual residual = default;
        SupportPhase phase = SupportPhase.BothPlanted;
        void MoveAfterAnimation()
        {
            (_, Vector3 rootPosition, Quaternion rootRotation) = roots[driver.EvaluationCount];
            bool idle = tick >= prediction.Ticks.Length;
            var frame = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis);
            float rootYaw = frame.EvaluatedRootRotationToActorLocal(rootRotation).GetEuler().Y;
            if (!float.IsFinite(rootYaw))
            {
                throw new InvalidOperationException("MEASUREMENT: non-finite root yaw.");
            }

            rootYaw = Mathf.IsZeroApprox(rootYaw) ? 0f : rootYaw;
            Basis afterRoot = actor.GlobalBasis * new Basis(Vector3.Up, rootYaw);
            Basis afterReference = afterRoot * actor.GlobalBasis.Inverse() * reference.GlobalBasis;
            Vector3 rootDelta = new NPCNavigationSemanticFrame(afterRoot, afterReference).EvaluatedRootDeltaToWorld(rootPosition);
            rootDelta.Y = 0f;
            var actualAfterRoot = new Transform3D(afterRoot, actor.GlobalPosition + rootDelta);
            correction = default;
            residual = default;
            phase = SupportPhase.BothPlanted;
            if (!idle)
            {
                while (tick > prediction.Windows[windowIndex].LastTick)
                {
                    windowIndex++;
                }

                SupportWindow window = prediction.Windows[windowIndex];
                if (!plantedSource.TryGetPlantedState(out PlantedFeetState planted))
                {
                    throw new InvalidOperationException("MEASUREMENT: current live planted state is unavailable.");
                }

                phase = SupportPhases.Of(planted);
                residual = Residual(actualAfterRoot, prediction.Ticks[tick].Pose, prediction.EndPose, plan.Target, out Vector2 endpointOffset);
                // Read the current mixer-written pose, not the previous tick's post-IK sample.
                Transform3D actorFromSkeleton = actor.GlobalTransform.AffineInverse() * fixture.Skeleton.GlobalTransform;
                Vector3 ball = phase == SupportPhase.LeftSwing
                    ? actorFromSkeleton * fixture.Skeleton.GetBoneGlobalPose(rightToes).Origin
                    : phase == SupportPhase.RightSwing ? actorFromSkeleton * fixture.Skeleton.GetBoneGlobalPose(leftToes).Origin : Vector3.Zero;
                correction = allocator.Step(residual, window, tick, prediction.Windows.AsSpan()[(windowIndex + 1)..],
                    new Vector2(ball.X, -ball.Z), endpointOffset, phase);
                used.Add(correction, phase);
            }

            // Like NPCNavigation.ApplyMovementOnce: rotate first, then commit at most one planar move.
            // Pivot rotation precedes translation: rotate Planar + OriginShift by correction yaw.
            Vector2 synthetic = correction.OriginShift + correction.Planar;
            Basis afterCorrection = afterRoot * new Basis(Vector3.Up, correction.Yaw);
            Vector3 delta = rootDelta + (afterCorrection * new Vector3(synthetic.X, 0f, -synthetic.Y));
            float yaw = rootYaw + correction.Yaw;
            if (yaw != 0f)
            {
                actor.RotateY(yaw);
            }

            if (!delta.IsZeroApprox() && actor.MoveAndCollide(delta) is not null)
            {
                collisions++;
            }
        }

        using var replay = new PhysicsReplay(fixture, MoveAfterAnimation);
        try
        {
            long firstEvaluation = driver.EvaluationCount + 1;
            var segments = new ImmutableSegmentsList(plan);
            int next = 0;
            RequestIfDue(driver, binding, segments, prediction, 0, ref next);
            for (tick = 0; tick < prediction.Ticks.Length + IdleTicks; tick++)
            {
                await replay.StepAsync();
                long evaluation = firstEvaluation + tick;
                if (driver.EvaluationCount != evaluation || !roots.TryGetValue(evaluation, out (ulong Tick, Vector3 Position, Quaternion Rotation) root))
                {
                    throw new InvalidOperationException($"MEASUREMENT: missing or repeated tree evaluation at plan tick {tick}: expected {evaluation}, observed {driver.EvaluationCount}, roots {roots.Count}.");
                }

                // Movement has already consumed this evaluation, before the paired IK/skeleton update.
                PlantedStateSample sample = recorder.Samples[^1];
                if (sample.Tick != root.Tick || !reaches.ContainsKey(root.Tick))
                {
                    throw new InvalidOperationException($"MEASUREMENT: root/pre-IK/post-IK tick pairing failed at {tick}.");
                }

                firstTick = tick == 0 ? root.Tick : firstTick;
                lastTick = root.Tick;
                bool idle = tick >= prediction.Ticks.Length;
                if (sample.Planted.Left && sample.Planted.Right)
                {
                    doublePlanar = Math.Max(doublePlanar, Math.Max(correction.Planar.Length(), correction.OriginShift.Length()));
                    doubleYaw = Math.Max(doubleYaw, Math.Abs(correction.Yaw));
                }

                if (idle)
                {
                    if (!ExactlyEqual(end, actor.GlobalTransform))
                    {
                        changedIdleTicks++;
                    }

                    idlePosition = Math.Max(idlePosition, actor.GlobalPosition.DistanceTo(end.Origin));
                    idleYaw = Math.Max(idleYaw, Math.Abs(Mathf.AngleDifference(YawOf(end), YawOf(actor.GlobalTransform))));
                }
                else if (tick == prediction.Ticks.Length - 1)
                {
                    using StringName current = playback.GetCurrentNode();
                    using StringName fading = playback.GetFadingFromNode();
                    if (current != NPCAnimationProgramme.IdleState || !fading.IsEmpty)
                    {
                        throw new InvalidOperationException("MEASUREMENT: prediction ended before the final clip returned fully to Idle.");
                    }

                    end = actor.GlobalTransform;
                }

                _ = ticks.AppendLine(FormattableString.Invariant(
                    $"{tick},{root.Tick},{phase},{sample.Planted.Left},{sample.Planted.Right},{correction.Planar.X * 1000:R},{correction.Planar.Y * 1000:R},{Mathf.RadToDeg(correction.Yaw):R},{correction.Pivot},{correction.OriginShift.X * 1000:R},{correction.OriginShift.Y * 1000:R},{residual.Position.X * 1000:R},{residual.Position.Y * 1000:R},{Mathf.RadToDeg(residual.Yaw):R},{actor.GlobalPosition.X:R},{actor.GlobalPosition.Z:R},{Mathf.RadToDeg(YawOf(actor.GlobalTransform)):R},{idle}"));
                if (tick + 1 < prediction.Ticks.Length)
                {
                    RequestIfDue(driver, binding, segments, prediction, tick + 1, ref next);
                }
            }

            binding.IdlePosition = playback.GetCurrentPlayPosition();
        }
        finally
        {
            fixture.Tree.MixerApplied -= OnMixer;
            fixture.Skeleton.SkeletonUpdated -= OnSkeleton;
            recorder.Detach();
            File.WriteAllText(Path.Combine(directory, $"{scenario}-{leg}-corrections.csv"), ticks.ToString());
        }

        PlantedStateSample[] samples = RequireCompleteWindow(recorder, firstTick, lastTick);
        var slip = SlipMetrics.Evaluate(samples, Engine.PhysicsTicksPerSecond);
        var seam = CrossfadeSeamMetrics.Evaluate(samples, recorder.Trajectories, Engine.PhysicsTicksPerSecond);
        string[] phaseFailures = [.. seam.Failures().Where(failure => failure.StartsWith("NAV-TAC-008 phase:", StringComparison.Ordinal))];
        float a1 = new Vector2(end.Origin.X, end.Origin.Z).DistanceTo(plan.Target.Position) * 1000f;
        float a1Yaw = Math.Abs(Mathf.RadToDeg(Mathf.AngleDifference(YawOf(end), plan.Target.Yaw)));
        float a2 = (slip.Worst?.MaxExcursionMetres ?? float.NaN) * 1000f;
        (float leftPeak, ulong leftTick) = PeakReach(samples, reaches, true);
        (float rightPeak, ulong rightTick) = PeakReach(samples, reaches, false);
        _ = summary.AppendLine(FormattableString.Invariant(
            $"{scenario},{leg},{a1:R},{a1Yaw:R},{a1 <= 10f && a1Yaw <= 1f},{a2:R},{a2 <= 5f},{changedIdleTicks},{idlePosition * 1000:R},{Mathf.RadToDeg(idleYaw):R},{doublePlanar * 1000:R},{Mathf.RadToDeg(doubleYaw):R},{leftPeak:R},{leftTick},{rightPeak:R},{rightTick},{used.Forward * 1000:R},{used.Backward * 1000:R},{used.Lateral * 1000:R},{Mathf.RadToDeg(used.Yaw):R},{used.FlightTicks},{used.CorrectedFlightTicks},{collisions},{phaseFailures.Length == 0}"));
        File.WriteAllText(Path.Combine(directory, $"{scenario}-{leg}-samples.csv"), FormatSamplesCsv(samples));
        File.WriteAllText(Path.Combine(directory, $"{scenario}-{leg}-planted-runs.csv"), slip.FormatRunsCsv());
        File.WriteAllText(Path.Combine(directory, $"{scenario}-{leg}-seams.csv"), seam.FormatTicksCsv());
        File.WriteAllText(Path.Combine(directory, $"{scenario}-{leg}-metrics.txt"),
            $"A1 <= 10 mm / 1 degree: {a1.ToString("R", CultureInfo.InvariantCulture)} mm / {a1Yaw.ToString("R", CultureInfo.InvariantCulture)} degrees\n"
            + "A2 <= 5 mm. Swing excess is report-only in this harness (the reused Describe text names its original assertion contract).\n"
            + slip.Describe() + "\n" + seam.Describe() + "\n"
            + string.Join('\n', seam.Failures()) + "\n"
            + "Reach: post-modifier bone global poses at SkeletonUpdated; hip = UpperLeg, ankle = Foot; denominator = rest chain length. No specified reach acceptance threshold.\n");
        File.WriteAllLines(Path.Combine(directory, $"{scenario}-{leg}-reach.csv"),
            new[] { "tick,left_planted,right_planted,left_usage,right_usage" }.Concat(samples.Select(sample => FormattableString.Invariant(
                $"{sample.Tick},{sample.Planted.Left},{sample.Planted.Right},{reaches[sample.Tick].Left:R},{reaches[sample.Tick].Right:R}"))));
        if (changedIdleTicks != 0)
        {
            failures.Add($"{scenario}/{leg} A3: actor changed on {changedIdleTicks} of {IdleTicks} Idle ticks.");
        }

        if (doublePlanar != 0f || doubleYaw != 0f)
        {
            failures.Add($"{scenario}/{leg} A4: nonzero correction on double support.");
        }

        failures.AddRange(phaseFailures.Select(failure => $"{scenario}/{leg}: {failure}"));
    }

    private static SwingCorrectionResidual Residual(
        Transform3D actual, SequencePose predicted, SequencePose end, ArrivalTarget target, out Vector2 endpointOffset)
    {
        static Transform3D Transform(SequencePose pose)
        {
            return new Transform3D(new Basis(Vector3.Up, pose.Yaw), new Vector3(pose.Position.X, 0f, pose.Position.Y));
        }

        Transform3D projected = actual * Transform(predicted).AffineInverse() * Transform(end);
        Vector3 offset = actual.Basis.Inverse() * (projected.Origin - actual.Origin);
        endpointOffset = new Vector2(offset.X, -offset.Z);
        Vector3 error = actual.Basis.Inverse() * (new Vector3(target.Position.X, 0f, target.Position.Y) - projected.Origin);
        return new SwingCorrectionResidual(new Vector2(error.X, -error.Z), Mathf.AngleDifference(YawOf(projected), target.Yaw));
    }

    private static (float Usage, ulong Tick) PeakReach(
        PlantedStateSample[] samples, Dictionary<ulong, (float Left, float Right)> reaches, bool left)
    {
        PlantedStateSample peak = samples.Where(sample => left ? sample.Planted.Left : sample.Planted.Right)
            .MaxBy(sample => left ? reaches[sample.Tick].Left : reaches[sample.Tick].Right)
            ?? throw new InvalidOperationException("MEASUREMENT: no planted samples for reach.");
        return (left ? reaches[peak.Tick].Left : reaches[peak.Tick].Right, peak.Tick);
    }

    private sealed class LegChain
    {
        private readonly Skeleton3D _skeleton;
        private readonly int _hip;
        private readonly int _ankle;
        private readonly float _length;

        public LegChain(Skeleton3D skeleton, string side)
        {
            _skeleton = skeleton;
            _hip = skeleton.FindBone($"{side}UpperLeg");
            int knee = skeleton.FindBone($"{side}LowerLeg");
            _ankle = skeleton.FindBone($"{side}Foot");
            if (_hip < 0 || knee < 0 || _ankle < 0)
            {
                throw new InvalidOperationException($"SETUP: missing {side} leg bones.");
            }

            _length = skeleton.GetBoneGlobalRest(_hip).Origin.DistanceTo(skeleton.GetBoneGlobalRest(knee).Origin)
                + skeleton.GetBoneGlobalRest(knee).Origin.DistanceTo(skeleton.GetBoneGlobalRest(_ankle).Origin);
        }

        public float Usage() => _skeleton.GetBoneGlobalPose(_hip).Origin.DistanceTo(_skeleton.GetBoneGlobalPose(_ankle).Origin) / _length;
    }

    private sealed class CorrectionTotals
    {
        public float Forward;
        public float Backward;
        public float Lateral;
        public float Yaw;
        public int FlightTicks;
        public int CorrectedFlightTicks;

        public void Add(SwingCorrection correction, SupportPhase phase)
        {
            Forward += Math.Max(0f, correction.Planar.Y);
            Backward += Math.Max(0f, -correction.Planar.Y);
            Lateral += Math.Abs(correction.Planar.X);
            Yaw += Math.Abs(correction.Yaw);
            if (phase == SupportPhase.BothSwing)
            {
                FlightTicks++;
                CorrectedFlightTicks += correction.Planar != Vector2.Zero || correction.Yaw != 0f ? 1 : 0;
            }
        }
    }
}
