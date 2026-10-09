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
/// Current-planner stationary-turn regression, not live NPCNavigation completion. The immutable request window is
/// issue through plan end plus one second of Idle. Only engine root motion is applied, before the skeleton update;
/// no endpoint correction, diagnostic composition, walking arc, or in-window setup teleport is permitted.
/// </summary>
public sealed class NAV001StationaryPartialTurnIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/nav001-stationary-turn-candidate";

    /// <summary>
    /// Female rig in one booted session: the named ±20° and ±25° stationary requests plus the 5°–90° control
    /// angles, each reset to Idle at the origin before its request. Every angle keeps the identical full-window
    /// acceptance.
    /// </summary>
    [Fact]
    public Task Female_PartialTurnsAcrossAngles() => VerifyAsync(
        NPCNavigationRig.Female, [20, -20, 25, -25, 5, 10, 15, 30, 90]);

    /// <summary>Male rig in one booted session: the named ±20° and ±25° stationary requests (two-rig contract).</summary>
    [Fact]
    public Task Male_PartialTurnsAcrossAngles() => VerifyAsync(NPCNavigationRig.Male, [20, -20, 25, -25]);

    private static async Task VerifyAsync(NPCNavigationRig rig, int[] degrees)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(
            rig, useReducedRenderSize: true);
        await fixture.StepAsync(WarmUpPhysicsTicks);
        Binding binding = Bind(fixture);
        foreach (int degree in degrees)
        {
            await TeleportToIdleAtOriginAsync(fixture, binding);
            await VerifyTurnAsync(fixture, binding, rig, degree);
        }
    }

    private static async Task VerifyTurnAsync(NPCNavigationRigFixture fixture, Binding binding, NPCNavigationRig rig, int degrees)
    {
        using AnimationGraphPlantedStateRecorder recorder = fixture.BindRecorder();
        recorder.Attach();
        // Seed the recorder before issue, so a fade starting at issue has an independently observed predecessor.
        await fixture.StepAsync(1);
        AnimationNodeStateMachinePlayback playback = fixture.Tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
        double idlePosition = playback.GetCurrentPlayPosition();
        Assert.True(ExactlyEqual(fixture.Actor.GlobalTransform, Transform3D.Identity), "SETUP: origin/-Z start changed during Idle.");
        var start = new ArrivalStart(new SequencePose(Vector2.Zero, 0f), binding.Planner.IdleRole, idlePosition);
        var target = new ArrivalTarget(Vector2.Zero, Mathf.DegToRad(degrees));
        ulong issue = Engine.GetPhysicsFrames();
        ArrivalPlan plan = binding.Planner.Plan(start, target);
        var report = new StringBuilder();
        var failures = new List<string>();
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory));
        string prefix = Path.Combine(directory, $"{rig}-{degrees}");
        _ = report.AppendLine($"Isolated current-planner replay, NOT live NPCNavigation completion. rig={rig} request={degrees} degrees at origin facing -Z.");
        _ = report.AppendLine(FormattableString.Invariant($"status={plan.Status} kind={plan.Kind} failure={plan.Failure} detail={plan.FailureDetail} tolerance_mm={binding.Predictor.SeamToleranceMetres * 1000:R} ticks={plan.TickCount}"));
        _ = report.AppendLine($"start={plan.Start} sequence_start={plan.SequenceStart} statistics={plan.Statistics}");
        // Planning only: no simulation advance, replay replacement, or acceptance change. Measure active requests,
        // including materialisation and summary prediction, after warming the same request five times.
        const int timedPlans = 5;
        for (int iteration = 0; iteration < timedPlans; iteration++)
        {
            _ = binding.Planner.Plan(start, target);
        }

        double[] times = new double[timedPlans];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < times.Length; iteration++)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = binding.Planner.Plan(start, target);
            times[iteration] = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Array.Sort(times);
        // With five samples the 95th percentile is the maximum, so only median and max are reported.
        _ = report.AppendLine(FormattableString.Invariant(
            $"active_plan_iterations={times.Length} warmup={times.Length} median_ms={times[times.Length / 2]:R} max_ms={times[^1]:R} allocated_bytes_per_plan={allocated / (double)times.Length:R}"));
        foreach (ArrivalPlanSegment segment in plan.Segments)
        {
            _ = report.AppendLine(segment.ToString());
        }

        SequencePrediction? prediction = plan.Status == ArrivalPlanStatus.Planned ? binding.Planner.Predict(plan) : null;
        if (prediction is not { IsValid: true })
        {
            // A valid refusal is a behavioural failure for this reachable request, not a setup failure. Observe
            // the unchanged Idle for one second after refusal; never manufacture a replacement sequence.
            await fixture.StepAsync(Engine.PhysicsTicksPerSecond);
            _ = report.AppendLine($"No executable plan; refusal window {issue}..{Engine.GetPhysicsFrames()}. A2/seam replay coverage unavailable.");
            File.WriteAllText(prefix + "-report.txt", report.ToString());
            Assert.Fail($"{rig} {degrees}: no executable current plan: {plan.Status} {plan.Failure} {plan.FailureDetail}");
            return;
        }

        foreach (ArrivalPlanSegment segment in plan.Segments)
        {
            if (segment.Role != binding.Planner.IdleRole
                && binding.Catalogue.Clips[segment.Role].Capability is not (StandingLocomotionCapability.ConfinedPivotLeft or StandingLocomotionCapability.ConfinedPivotRight))
            {
                failures.Add($"A5: stationary request selected non-pivot {segment.Role}.");
            }

            if (segment.CrossfadeSeconds <= 0d)
            {
                failures.Add($"A4/replay: zero-fade teleport to {segment.Role}.");
            }
        }

        var rootCsv = new StringBuilder("tick,root_x,root_y,root_z,root_yaw,actor_x,actor_y,actor_z,actor_yaw\n");
        var actors = new Dictionary<ulong, Transform3D>();
        int collisions = 0;
        int nonzeroRootTicks = 0;
        int rootReads = 0;
        var actor = (CharacterBody3D)fixture.Actor;
        Node3D reference = fixture.Navigation.RootMotionReference!;
        void ApplyRootMotion()
        {
            if (AnimationGraphPlantedStateSource.IsPreEvaluating(fixture.Tree))
            {
                return;
            }

            rootReads++;
            Vector3 root = fixture.Tree.GetRootMotionPosition();
            Quaternion rotation = fixture.Tree.GetRootMotionRotation();
            // Exact component equality is deliberate: authored Idle/pivots have identically zero root position,
            // the diagnostic measured zero on every tick, and multiplying zero does not accumulate float drift.
            if (root.X != 0f || root.Y != 0f || root.Z != 0f)
            {
                nonzeroRootTicks++;
            }

            var frame = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis);
            float yaw = frame.EvaluatedRootRotationToActorLocal(rotation).GetEuler().Y;
            if (float.IsFinite(yaw) && !Mathf.IsZeroApprox(yaw))
            {
                actor.RotateY(yaw);
            }

            Vector3 planar = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis).EvaluatedRootDeltaToWorld(root);
            planar.Y = 0f;
            if (!planar.IsZeroApprox() && actor.MoveAndCollide(planar) is not null)
            {
                collisions++;
            }

            ulong tick = Engine.GetPhysicsFrames();
            actors.Add(tick, actor.GlobalTransform);
            _ = rootCsv.AppendLine(FormattableString.Invariant($"{tick},{root.X:R},{root.Y:R},{root.Z:R},{yaw:R},{actor.GlobalPosition.X:R},{actor.GlobalPosition.Y:R},{actor.GlobalPosition.Z:R},{YawOf(actor.GlobalTransform):R}"));
        }

        // Unlike the endpoint-only oracle's late Apply, this consumes native motion in MixerApplied, before IK.
        fixture.Tree.MixerApplied += ApplyRootMotion;
        ulong end;
        ulong windowEnd;
        Transform3D endPose;
        try
        {
            SequenceSegment[] segments = [.. plan.SequenceSegments];
            int next = 0;
            long firstEvaluation = fixture.Driver.EvaluationCount;
            for (int tick = 0; tick < plan.TickCount; tick++)
            {
                if (next < segments.Length && prediction.EntryTicks[next] == tick)
                {
                    SequenceSegment segment = segments[next++];
                    Assert.True(fixture.Driver.TryRequestTransition(binding.StateOf[segment.Role], segment.EntrySeconds, segment.CrossfadeSeconds), $"SETUP: driver refused {segment.Role} at {tick}.");
                }

                await fixture.StepAsync(1);
                Assert.Equal(firstEvaluation + tick + 1, fixture.Driver.EvaluationCount);
            }

            Assert.Equal(segments.Length, next);
            // PhysicsFrame resumes before that frame's node processing. Use the evaluated tick, not the next
            // frame counter, for the plan boundary; otherwise teardown would include an unconsumed extra tick.
            end = actors.Keys.Max();
            endPose = actor.GlobalTransform;
            for (int tick = 0; tick < Engine.PhysicsTicksPerSecond; tick++)
            {
                await fixture.StepAsync(1);
                Assert.Equal(firstEvaluation + plan.TickCount + tick + 1, fixture.Driver.EvaluationCount);
            }

            windowEnd = actors.Keys.Max();
        }
        finally
        {
            fixture.Tree.MixerApplied -= ApplyRootMotion;
        }

        await fixture.SettleAsync();
        recorder.Detach();
        PlantedStateSample[] window = RequireCompleteWindow(recorder, issue, windowEnd);
        Assert.Equal(plan.TickCount + Engine.PhysicsTicksPerSecond, rootReads);
        Assert.Equal(0, collisions);
        Assert.Equal(0, recorder.UnpairedTicks);
        Assert.Equal(issue, actors.Keys.Min());
        Assert.Equal((ulong)Engine.PhysicsTicksPerSecond, windowEnd - end);
        foreach (PlantedStateSample sample in window)
        {
            Assert.True(ExactlyEqual(actors[sample.Tick], sample.Actor), $"SETUP: post-IK sample {sample.Tick} did not observe the same-tick root application.");
        }

        float yawError = Math.Abs(Mathf.RadToDeg(Mathf.AngleDifference(YawOf(endPose), target.Yaw)));
        float positionError = Planar(endPose.Origin).Length();
        if (yawError > 1f || positionError > 0.01f)
        {
            failures.Add(FormattableString.Invariant($"A1 endpoint: {positionError * 1000:R} mm / {yawError:R} degrees (limits 10 mm / 1 degree)."));
        }

        int movingTicks = window.Count(sample => sample.Actor.Origin.X != 0f || sample.Actor.Origin.Y != 0f || sample.Actor.Origin.Z != 0f);
        if (movingTicks != 0 || nonzeroRootTicks != 0)
        {
            failures.Add($"Stationary root: actor moved on {movingTicks} ticks; native root position nonzero on {nonzeroRootTicks} ticks.");
        }

        int afterPlanChanges = window.Count(sample => sample.Tick >= end && !ExactlyEqual(sample.Actor, endPose));
        if (afterPlanChanges > 0)
        {
            failures.Add($"A3 analogue: actor changed on {afterPlanChanges} ticks after plan end.");
        }

        Assert.All(window.Where(sample => sample.Tick >= end), sample => Assert.Equal(NPCAnimationProgramme.IdleState.ToString(), sample.Graph.Current));
        var slip = SlipMetrics.Evaluate(window, Engine.PhysicsTicksPerSecond);
        var seams = CrossfadeSeamMetrics.Evaluate(window, recorder.Trajectories, Engine.PhysicsTicksPerSecond);
        if (slip.Worst is not { } worst || worst.MaxExcursionMetres > 0.005f)
        {
            failures.Add("A2: " + slip.Describe());
        }

        failures.AddRange(seams.Failures());
        failures.AddRange(seams.ConfiguredPoseFailures(binding.Predictor.SeamToleranceMetres));
        _ = report.AppendLine(FormattableString.Invariant($"window={issue}..{windowEnd} inclusive samples={window.Length} plan_end={end} post_plan_ticks={windowEnd - end} physics_hz={Engine.PhysicsTicksPerSecond} root_reads={rootReads}"));
        _ = report.AppendLine(FormattableString.Invariant($"engine_yaw_deg={Mathf.RadToDeg(YawOf(endPose)):R} endpoint_error_mm={positionError * 1000:R} endpoint_error_deg={yawError:R} nonzero_root_ticks={nonzeroRootTicks} actor_moving_ticks={movingTicks} after_plan_changes={afterPlanChanges}"));
        _ = report.AppendLine("A4: replay applies native root motion only; no synthetic correction. A6 not visually verified. A1/A3 are planner-end analogues, not Completed lifecycle evidence.");
        _ = report.AppendLine(slip.Describe());
        _ = report.AppendLine(seams.Describe());
        _ = report.AppendLine("This replay additionally asserts configured 190 mm pose separation and complete swing-trajectory coverage; the shared diagnostic's report-only pose label does not apply here.");
        _ = report.AppendLine("FAILURES:\n" + string.Join('\n', failures));
        File.WriteAllText(prefix + "-report.txt", report.ToString());
        File.WriteAllText(prefix + "-samples.csv", FormatSamplesCsv(window));
        File.WriteAllText(prefix + "-roots.csv", rootCsv.ToString());
        File.WriteAllText(prefix + "-planted-runs.csv", slip.FormatRunsCsv());
        File.WriteAllText(prefix + "-seams.csv", seams.FormatTicksCsv());
        Assert.True(failures.Count == 0, $"{rig} {degrees}: " + string.Join('\n', failures) + $"\nEvidence: {prefix}-report.txt");
    }
}
