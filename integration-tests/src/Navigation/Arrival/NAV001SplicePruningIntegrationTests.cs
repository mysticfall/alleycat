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

/// <summary>Isolated captured second-leg planner replay versus a seam-legal discarded direct construction.</summary>
public sealed class NAV001SplicePruningIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/nav001-splice-pruning-baseline";

    /// <summary>No mandatory direct winner: every planner family competes with the same known alternative.</summary>
    [Fact]
    public async Task Female_RepeatedSecondLegPreservesBetterLegalEndpoint()
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female, useReducedRenderSize: true);
        await fixture.StepAsync(WarmUpPhysicsTicks);
        Binding binding = Bind(fixture);
        Assert.Equal(0.190f, binding.Predictor.SeamToleranceMetres, 6);
        string directory = EvidenceDestinations.Resolve(Path.Combine(
            ProjectSettings.GlobalizePath(EvidenceDirectory),
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture)));
        Vector3 destination = new Vector3(-0.49705806f, 0f, -0.61596584f)
            + (new Basis(Vector3.Up, 0.6789687f) * Vector3.Forward * 0.3f);
        var target = new ArrivalTarget(new Vector2(destination.X, destination.Z), 0.6789687f);
        ReplayResult current = await ReplayAsync(fixture, binding, target, false, directory);
        ReplayResult alternative = await ReplayAsync(fixture, binding, target, true, directory);
        string report = FormattableString.Invariant(
            $"Current cost={current.Cost:R} m; alternative cost={alternative.Cost:R} m; improvement={current.Cost - alternative.Cost:R} m; allowed scoring bucket=.005 m.\nCurrent full gates:\n{current.Gates}\nAlternative full gates:\n{alternative.Gates}\n");
        File.WriteAllText(Path.Combine(directory, "comparison.txt"), report);
        Assert.True(current.Cost <= alternative.Cost + 0.005f, report);
    }

    private static async Task<ReplayResult> ReplayAsync(NPCNavigationRigFixture fixture, Binding binding, ArrivalTarget target, bool alternative, string directory)
    {
        await TeleportToIdleAtOriginAsync(fixture, binding);
        Assert.True(fixture.Driver.TryRequestTransition(NPCAnimationProgramme.IdleState, 6.533333333333333d, 0d));
        await fixture.StepAsync(1);
        fixture.Actor.GlobalTransform = new Transform3D(new Basis(Vector3.Up, 0.67922753f), new Vector3(-0.49490336f, 0f, -0.612971f));
        using AnimationGraphPlantedStateRecorder recorder = fixture.BindRecorder();
        recorder.Attach();
        await fixture.StepAsync(1);
        AnimationNodeStateMachinePlayback playback = fixture.Tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
        double idle = playback.GetCurrentPlayPosition();
        var pose = new SequencePose(Ground(fixture.Actor.GlobalPosition), YawOf(fixture.Actor.GlobalTransform));
        var start = new ArrivalStart(pose, binding.Planner.IdleRole, idle);
        ulong issue = Engine.GetPhysicsFrames();
        ArrivalPlan plan = binding.Planner.Plan(start, target);
        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        string entry = binding.Catalogue[StandingLocomotionCapability.EntryForwardRight].Role;
        string exit = binding.Catalogue[StandingLocomotionCapability.ExitForwardLeft].Role;
        SequenceSegment[] segments = alternative
            ? [new(entry, 4d / 30d, 0.15d, 0.5d), new(exit, 7d / 30d, 0.15d, 23d / 30d), new(binding.Planner.IdleRole, 6.4d, 0.15d, 6.4d + (8d / 60d))]
            : [.. plan.SequenceSegments];
        SequenceStart sequenceStart = alternative ? SequenceStart.Playing(pose, binding.Planner.IdleRole, idle, idle) : plan.SequenceStart!;
        SequencePrediction prediction = binding.Predictor.Predict(sequenceStart, segments);
        Assert.True(prediction.IsValid, prediction.Rejection.ToString());
        Assert.False(plan.Statistics.BudgetExhausted);
        string previous = sequenceStart.CurrentRole!;
        double outgoing = sequenceStart.ExitSeconds;
        var report = new StringBuilder();
        _ = report.AppendLine($"Isolated captured Female second-leg replay; alternative={alternative}; start={start}; target={target}; config={binding.Planner.Config}; statistics={plan.Statistics}");
        foreach (SequenceSegment segment in segments)
        {
            Assert.True(segment.CrossfadeSeconds > 0d);
            Assert.True(binding.Seams.TryGetPair(previous, segment.Role, out SeamPair? pair));
            Assert.True(binding.Seams.TryEvaluateSeam(previous, segment.Role, outgoing, segment.EntrySeconds, out SeamCandidate candidate));
            Assert.True(pair!.IsUsable(candidate) && candidate.IsAlignedWithin(binding.Predictor.SeamToleranceMetres));
            _ = report.AppendLine($"{previous}@{outgoing:R} -> {segment}; separation={candidate.MaxSeparation:R}");
            previous = segment.Role;
            outgoing = segment.ExitSeconds;
        }

        Assert.True(binding.Seams.TryEvaluateSeam(entry, exit, 0.5d, 7d / 30d, out SeamCandidate lost));
        Assert.True(binding.Seams.TryGetPair(entry, exit, out SeamPair? middle));
        SeamCandidate retained = middle!.Candidates.Where(candidate => Math.Abs(candidate.OutgoingTime - 0.5d) < 1e-8d
            && middle.IsUsable(candidate) && candidate.IsAlignedWithin(0.190f)).MinBy(candidate => candidate.MaxSeparation);
        Assert.True(retained.MaxSeparation < lost.MaxSeparation);
        _ = report.AppendLine($"Local pruning at .5: retained={retained}; lost={lost}. This complete alternative also uses discarded entry/exit boundary options.");
        if (alternative)
        {
            SequenceSegment[] retainedMiddle = [.. segments];
            retainedMiddle[1] = retainedMiddle[1] with
            {
                EntrySeconds = retained.IncomingTime
            };
            SequencePrediction retainedPrediction = binding.Predictor.Predict(sequenceStart, retainedMiddle);
            Assert.True(retainedPrediction.IsValid);
            _ = report.AppendLine($"Model-only same-boundary comparison: retained middle end={retainedPrediction.EndPose}; residual_mm={(retainedPrediction.EndPose.Position - target.Position).Length() * 1000:R}. Not an independent engine oracle.");
        }
        var actors = new Dictionary<ulong, Transform3D>();
        var roots = new StringBuilder("tick,x,y,z,yaw\n");
        int collisions = 0;
        var actor = (CharacterBody3D)fixture.Actor;
        Node3D reference = fixture.Navigation.RootMotionReference!;
        _ = report.AppendLine($"root_reference_basis={reference.GlobalBasis}; actor_basis={actor.GlobalBasis}; native rig-bound trajectories, no additional root scale multiplier");
        void ApplyRootMotion()
        {
            if (AnimationGraphPlantedStateSource.IsPreEvaluating(fixture.Tree))
            {
                return;
            }

            Vector3 root = fixture.Tree.GetRootMotionPosition();
            var frame = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis);
            float yaw = frame.EvaluatedRootRotationToActorLocal(fixture.Tree.GetRootMotionRotation()).GetEuler().Y;
            if (float.IsFinite(yaw) && !Mathf.IsZeroApprox(yaw))
            {
                actor.RotateY(yaw);
            }

            Vector3 delta = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis).EvaluatedRootDeltaToWorld(root);
            delta.Y = 0f;
            if (!delta.IsZeroApprox() && actor.MoveAndCollide(delta) is not null)
            {
                collisions++;
            }

            ulong tick = Engine.GetPhysicsFrames();
            actors.Add(tick, actor.GlobalTransform);
            _ = roots.AppendLine(FormattableString.Invariant($"{tick},{root.X:R},{root.Y:R},{root.Z:R},{yaw:R}"));
        }

        fixture.Tree.MixerApplied += ApplyRootMotion;
        ulong end;
        ulong windowEnd;
        Transform3D endPose;
        int count = prediction.Ticks.Length;
        try
        {
            int next = 0;
            long first = fixture.Driver.EvaluationCount;
            for (int tick = 0; tick < count; tick++)
            {
                if (next < segments.Length && prediction.EntryTicks[next] == tick)
                {
                    SequenceSegment segment = segments[next++];
                    Assert.True(fixture.Driver.TryRequestTransition(binding.StateOf[segment.Role], segment.EntrySeconds, segment.CrossfadeSeconds));
                }

                await fixture.StepAsync(1);
                Assert.Equal(first + tick + 1, fixture.Driver.EvaluationCount);
            }

            Assert.Equal(segments.Length, next);
            end = actors.Keys.Max();
            endPose = actor.GlobalTransform;
            for (int tick = 0; tick < 60; tick++)
            {
                await fixture.StepAsync(1);
                Assert.Equal(first + count + tick + 1, fixture.Driver.EvaluationCount);
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
        Assert.Equal(60, Engine.PhysicsTicksPerSecond);
        Assert.Equal(count + 60, actors.Count);
        Assert.Equal(0, collisions);
        Assert.Equal(0, recorder.UnpairedTicks);
        Assert.Equal(issue, actors.Keys.Min());
        Assert.Equal(60ul, windowEnd - end);
        Assert.All(window, sample => Assert.True(ExactlyEqual(actors[sample.Tick], sample.Actor)));
        Assert.All(window.Where(sample => sample.Tick >= end), sample => Assert.Equal(NPCAnimationProgramme.IdleState.ToString(), sample.Graph.Current));
        float positionError = (Ground(endPose.Origin) - target.Position).Length();
        float yawError = Math.Abs(Mathf.RadToDeg(Mathf.AngleDifference(YawOf(endPose), target.Yaw)));
        var failures = new List<string>();
        if (positionError > 0.01f || yawError > 1f)
        {
            failures.Add($"A1: {positionError * 1000:R} mm / {yawError:R} degrees exceeds 10 mm / 1 degree.");
        }

        int changed = window.Count(sample => sample.Tick >= end && !ExactlyEqual(sample.Actor, endPose));
        if (changed > 0)
        {
            failures.Add($"A3: {changed} post-plan changed transforms.");
        }

        var slip = SlipMetrics.Evaluate(window, Engine.PhysicsTicksPerSecond);
        var seams = CrossfadeSeamMetrics.Evaluate(window, recorder.Trajectories, Engine.PhysicsTicksPerSecond);
        if (slip.Worst is not { } worst || worst.MaxExcursionMetres > 0.005f)
        {
            failures.Add("A2: " + slip.Describe());
        }

        failures.AddRange(seams.Failures());
        failures.AddRange(seams.ConfiguredPoseFailures(binding.Predictor.SeamToleranceMetres));
        _ = report.AppendLine($"window={issue}..{windowEnd}; plan_end={end}; ticks={count}; post_plan=60; samples={window.Length}; changed_after_end={changed}; engine_end={endPose}; endpoint_mm={positionError * 1000:R}; yaw_error_deg={yawError:R}");
        _ = report.AppendLine($"model_engine_mm={(Ground(endPose.Origin) - prediction.EndPose.Position).Length() * 1000:R}; model_engine_deg={Mathf.RadToDeg(Mathf.AngleDifference(prediction.EndPose.Yaw, YawOf(endPose))):R}");
        _ = report.AppendLine(slip.Describe());
        _ = report.AppendLine(seams.Describe());
        _ = report.AppendLine("A4 native motion only. A5 no mandatory direct winner. A6 unverified. Full gates remain authoritative; endpoint comparison is narrower.\nFAILURES:\n" + string.Join('\n', failures));
        string prefix = Path.Combine(directory, alternative ? "female-alternative" : "female-planner");
        File.WriteAllText(prefix + "-report.txt", report.ToString());
        File.WriteAllText(prefix + "-samples.csv", FormatSamplesCsv(window));
        File.WriteAllText(prefix + "-roots.csv", roots.ToString());
        File.WriteAllText(prefix + "-planted-runs.csv", slip.FormatRunsCsv());
        File.WriteAllText(prefix + "-seams.csv", seams.FormatTicksCsv());
        return new ReplayResult(positionError + (yawError * 0.01f), string.Join('\n', failures));
    }

    private static Vector2 Ground(Vector3 value) => new(value.X, value.Z);

    private sealed record ReplayResult(float Cost, string Gates);
}
