using System.Diagnostics;
using System.Globalization;
using System.Text;
using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>
/// NAV-001 P4 unit 5b-ii engine oracle for the <see cref="ArrivalPlanner"/> on both reference rigs. For each of the ten
/// <see cref="NAV001ArrivalOutcomeIntegrationTests"/> scenarios (same destinations and final yaws), the planner plans
/// from Idle at the origin; the plan is played through the real NPC's production <see cref="NPCAnimationTreeDriver"/>
/// with no correction (the recorded root motion is applied as the runtime movement operation does). The Repeated
/// scenario plans its second leg from the Idle state the first leg's engine run ends in. Two further cases replan from
/// mid-walk: freely (travelling on) and promptly (TR14, first aligned seam).
/// </summary>
/// <remarks>
/// <para>Asserted per played plan:</para>
/// <list type="bullet">
/// <item>NAV-TAC-008 phase agreement (the shuffle criterion): on every recorded tick that blends two clips, both clips'
/// ANIM-002 planted intervals at the engine's own playback positions agree for each foot;</item>
/// <item>the planner's predicted end pose matches the engine's within 1 mm and 0.05°;</item>
/// <item>every transition the driver issued is a planner-set pair the planner declares usable, and only the initial
/// test setup teleports (the plan has no zero crossfade).</item>
/// </list>
/// <para>
/// Reported (not asserted): the uncorrected residual against the destination, the chosen movement kind and segments,
/// the swing windows and a unit 6 feasibility estimate from the design's correction budget, the planning time
/// distribution, and the declared planner set with its worst best-seam separation.
/// </para>
/// </remarks>
public sealed class NAV001ArrivalPlannerIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/nav001-stationary-turn-candidate/planner-oracle";
    private const string SeamToleranceConfigPath = "res://assets/characters/templates/navigation/seam_tolerance_sad.tres";

    /// <summary>End-pose planar bound of the unit brief: 1 mm.</summary>
    private const float EndPositionToleranceMetres = 0.001f;

    /// <summary>End-pose yaw bound of the unit brief: 0.05°.</summary>
    private static readonly float _endYawTolerance = Mathf.DegToRad(0.05f);

    /// <summary>Current weight at or above which a recorded tick no longer blends the fading clip.</summary>
    private const double BlendedWeightLimit = 1d - 1e-4d;

    /// <summary>Idle position every scenario starts from (the test's setup teleport).</summary>
    private const double StartIdleSeconds = 0d;

    /// <summary>Plans timed per scenario (after warm-up) for the planning-time distribution.</summary>
    private const int TimedPlans = 5;

    /// <summary>Design correction budget (Q3): forward (lengthening) per single-swing step, conservative and optimistic.</summary>
    private const float ForwardPerStepConservative = 0.016f;

    private const float ForwardPerStepOptimistic = 0.040f;

    /// <summary>Lateral per single-swing window (Q3: 53–153 mm), conservative and optimistic.</summary>
    private const float LateralPerStepConservative = 0.05f;

    private const float LateralPerStepOptimistic = 0.10f;

    /// <summary>Backward (shortening) per single-swing window (Q3: "costs almost nothing", ≥ 377 mm), conservative.</summary>
    private const float BackwardPerStep = 0.10f;

    // The NAV001ArrivalOutcomeIntegrationTests requests (world space, identity spawn).
    private static readonly (Vector3 Destination, float Yaw) _oblique = (new Vector3(-0.49705806f, 0f, -0.61596584f), 0.6789687f);
    private static readonly (Vector3 Destination, float Yaw) _terminalSlide = (new Vector3(0.48621222f, 0f, -1.419342f), -0.33003297f);
    private static readonly (Vector3 Destination, float Yaw) _userShuffle = (new Vector3(-0.45992267f, 0f, -1.4639847f), 0.30439475f);

    /// <summary>NAV-001 P4 arrival planner oracle on the female rig.</summary>
    [Fact]
    public Task Female_PlansPlayAtAlignedSeamsAndMatchTheEngine() => VerifyAsync(NPCNavigationRig.Female);

    /// <summary>NAV-001 P4 arrival planner oracle on the male rig.</summary>
    [Fact]
    public Task Male_PlansPlayAtAlignedSeamsAndMatchTheEngine() => VerifyAsync(NPCNavigationRig.Male);

    internal static IEnumerable<(string Name, (Vector3 Destination, float Yaw)[] Legs)> Scenarios()
    {
        yield return ("oblique", [_oblique]);
        yield return ("terminal-slide", [_terminalSlide]);
        yield return ("terminal-slide-mirror", [(_terminalSlide.Destination with { X = -_terminalSlide.Destination.X }, -_terminalSlide.Yaw)]);
        yield return ("repeated-nearby", [_oblique, (_oblique.Destination + (new Basis(Vector3.Up, _oblique.Yaw) * Vector3.Forward * 0.3f), _oblique.Yaw)]);
        yield return ("lateral-0.5m", [(new Vector3(0.5f, 0f, 0f), 0f)]);
        yield return ("distant-lateral-3m", [(new Vector3(3f, 0f, 0f), 0f)]);
        yield return ("behind-1.5m", [(new Vector3(0f, 0f, 1.5f), 0f)]);
        yield return ("turn-only-90deg", [(Vector3.Zero, Mathf.Pi / 2f)]);
        yield return ("long-straight-4m", [(new Vector3(0f, 0f, -4f), 0f)]);
        yield return ("user-shuffle", [_userShuffle]);
    }

    private static async Task VerifyAsync(NPCNavigationRig rig)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(rig, useReducedRenderSize: true);
        await fixture.StepAsync(NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);
        Binding binding = Bind(fixture);

        List<string> failures = [];
        var summary = new StringBuilder();
        var detail = new StringBuilder();
        ReportPlannerSet(rig, binding, summary);
        _ = summary.AppendLine("scenario,leg,status,kind,segments,ticks,predicted_residual_mm,predicted_residual_deg,engine_residual_mm,engine_residual_deg,forward_mm,lateral_mm,end_error_mm,end_error_deg,blended_ticks,phase_mismatches,max_separation_mm,single_swing_windows,both_swing_ticks,unit6_estimate,plan_ms_min,plan_ms_median,plan_ms_p95,plan_ms_max,search_ms,estimate_error_mm,compositions,summary_predictions,joins");
        List<double> allTimes = [];
        foreach ((string name, (Vector3 Destination, float Yaw)[] legs) in Scenarios())
        {
            await TeleportToIdleAtOriginAsync(fixture, binding);
            for (int leg = 0; leg < legs.Length; leg++)
            {
                var target = new ArrivalTarget(new Vector2(legs[leg].Destination.X, legs[leg].Destination.Z), legs[leg].Yaw);
                ArrivalStart start = IdleStart(fixture, binding);
                (ArrivalPlan plan, double[] times) = TimedPlan(binding.Planner, start, target);
                allTimes.AddRange(times);
                await PlayAndCompareAsync(fixture, binding, $"{rig} {name}", name, leg, plan, times, plan.TickCount, failures, summary, detail);
            }
        }

        await ReplanMidWalkAsync(fixture, binding, rig, prompt: false, allTimes, failures, summary, detail);
        await ReplanMidWalkAsync(fixture, binding, rig, prompt: true, allTimes, failures, summary, detail);

        allTimes.Sort();
        _ = summary.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"planning_time_ms all plans n={allTimes.Count} min={allTimes[0]:0.###} median={Percentile(allTimes, 0.5):0.###} p95={Percentile(allTimes, 0.95):0.###} max={allTimes[^1]:0.###} over_2ms={allTimes.Count(time => time > 2d)}"));
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory));
        File.WriteAllText(Path.Combine(directory, $"{rig}-summary.csv"), summary.ToString() + string.Join('\n', failures));
        File.WriteAllText(Path.Combine(directory, $"{rig}-plans.txt"), detail.ToString());
        File.WriteAllText(
            Path.Combine(directory, $"{rig}-pivots.csv"),
            "right_m,forward_m,yaw_deg,steps,separation_mm\n" + string.Join('\n', binding.Planner.PivotBlocks.Select(block => string.Create(CultureInfo.InvariantCulture, $"{block.Right:0.#####},{block.Forward:0.#####},{Mathf.RadToDeg(block.Yaw):0.###},{block.Steps},{block.Separation * 1000f:0.#}"))));
        Assert.True(failures.Count == 0, $"{string.Join('\n', failures)}\n{summary}");
    }

    /// <summary>
    /// Plans, then continues a straight 4 m walk until it has played part of its forward cycle, and replans from
    /// there to a new target ahead-right with a new facing: freely (it may travel on) or promptly (TR14: its first
    /// transition is the first aligned seam).
    /// </summary>
    private static async Task ReplanMidWalkAsync(
        NPCNavigationRigFixture fixture,
        Binding binding,
        NPCNavigationRig rig,
        bool prompt,
        List<double> allTimes,
        List<string> failures,
        StringBuilder summary,
        StringBuilder detail)
    {
        string name = prompt ? "replan-mid-walk-prompt" : "replan-mid-walk";
        await TeleportToIdleAtOriginAsync(fixture, binding);
        ArrivalPlan first = binding.Planner.Plan(IdleStart(fixture, binding), new ArrivalTarget(new Vector2(0f, -4f), 0f));
        if (first.Status != ArrivalPlanStatus.Planned)
        {
            failures.Add($"{rig} {name}: SETUP: the first plan failed: {first.Failure} {first.FailureDetail}");
            return;
        }

        string walk = binding.Catalogue[StandingLocomotionCapability.CyclicForwardPrimary].Role;
        SequencePrediction firstPrediction = binding.Planner.Predict(first);
        int cut = -1;
        int walkTicks = 0;
        for (int tick = 0; tick < firstPrediction.Ticks.Length; tick++)
        {
            PredictedTick predicted = firstPrediction.Ticks[tick];
            walkTicks = predicted.Current.Role == walk && predicted.FadingFrom is null ? walkTicks + 1 : 0;
            if (walkTicks == 25)
            {
                cut = tick;
                break;
            }
        }

        if (cut < 0)
        {
            failures.Add($"{rig} {name}: SETUP: the 4 m plan never plays its forward cycle alone for 25 ticks.");
            return;
        }

        await PlayAndCompareAsync(fixture, binding, $"{rig} {name}", name, 0, first, [], cut + 1, failures, summary, detail);
        PredictedTick at = firstPrediction.Ticks[cut];
        SequencePose pose = PoseOf(fixture.Actor);
        var start = new ArrivalStart(pose, walk, at.Current.PositionSeconds)
        {
            Prompt = prompt
        };
        PlanarMotion offset = new(0.6d, 2.0d, Mathf.DegToRad(-20f));
        SequencePose goal = offset.From(pose);
        (ArrivalPlan second, double[] times) = TimedPlan(binding.Planner, start, new ArrivalTarget(goal.Position, goal.Yaw));
        allTimes.AddRange(times);
        await PlayAndCompareAsync(fixture, binding, $"{rig} {name}", name, 1, second, times, second.TickCount, failures, summary, detail);
        if (second.Status == ArrivalPlanStatus.Planned && prompt)
        {
            // TR14 promptness: the first transition is the walk's first aligned seam into its planner successors.
            string[] exits = [.. binding.Catalogue.Clips.Values.Where(clip => clip.Capability is StandingLocomotionCapability.ExitForwardLeft or StandingLocomotionCapability.ExitForwardRight).Select(clip => clip.Role)];
            if (binding.Seams.TryFindNextAligned(walk, exits, at.Current.PositionSeconds, binding.Predictor.SeamToleranceMetres, out SeamChoice choice)
                && Math.Abs(second.SequenceStart!.ExitSeconds - choice.Candidate.OutgoingTime) > 1e-6d)
            {
                failures.Add(FormattableString.Invariant($"{rig} {name}: the prompt replan leaves at {second.SequenceStart.ExitSeconds} s, not the first aligned seam at {choice.Candidate.OutgoingTime} s."));
            }
        }
    }

    private static (ArrivalPlan Plan, double[] Times) TimedPlan(ArrivalPlanner planner, ArrivalStart start, ArrivalTarget target)
    {
        ArrivalPlan plan = planner.Plan(start, target);
        for (int index = 0; index < 3; index++)
        {
            _ = planner.Plan(start, target);
        }

        double[] times = new double[TimedPlans];
        for (int index = 0; index < TimedPlans; index++)
        {
            long began = Stopwatch.GetTimestamp();
            _ = planner.Plan(start, target);
            times[index] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        }

        Array.Sort(times);
        return (plan, times);
    }

    internal static ArrivalStart IdleStart(NPCNavigationRigFixture fixture, Binding binding)
        => new(PoseOf(fixture.Actor), binding.Planner.IdleRole, binding.IdlePosition);

    /// <summary>
    /// Test setup: a ten-tick pad (longer than the 0.15 s crossfade, so the previous plan's final fade has fully
    /// elapsed), a driver <c>Start</c> into Idle at <see cref="StartIdleSeconds"/> (a teleport outside any plan),
    /// one tick for it to take effect, and the actor placed at the origin facing -Z.
    /// </summary>
    internal static async Task TeleportToIdleAtOriginAsync(NPCNavigationRigFixture fixture, Binding binding)
    {
        await fixture.StepAsync(10);
        if (!fixture.Driver.TryRequestTransition(NPCAnimationProgramme.IdleState, StartIdleSeconds, 0d))
        {
            throw new InvalidOperationException("SETUP: the driver refused the Idle start.");
        }

        await fixture.StepAsync(1);
        fixture.Actor.GlobalTransform = Transform3D.Identity;
        foreach (FootPlantingTargetProvider provider in fixture.Actor.FindChildren("*", "", true, false).OfType<FootPlantingTargetProvider>())
        {
            provider.ResetPin();
        }

        binding.IdlePosition = StartIdleSeconds;
    }

    private static async Task PlayAndCompareAsync(
        NPCNavigationRigFixture fixture,
        Binding binding,
        string label,
        string name,
        int leg,
        ArrivalPlan plan,
        double[] times,
        int tickLimit,
        List<string> failures,
        StringBuilder summary,
        StringBuilder detail)
    {
        _ = detail.AppendLine(FormattableString.Invariant($"# {label} leg {leg}: {plan.Status} {plan.Kind} {plan.Failure} {plan.FailureDetail}"));
        SequencePrediction? prediction = plan.Status == ArrivalPlanStatus.Planned ? binding.Planner.Predict(plan) : null;
        if (prediction is not { IsValid: true })
        {
            failures.Add($"{label} leg {leg}: planning failed: {plan.Failure} {plan.FailureDetail}");
            _ = summary.AppendLine(FormattableString.Invariant($"{name},{leg},{plan.Status},{plan.Kind},,,,,,,,,,,,,,,,{plan.Failure},,,,,,,{plan.Statistics.Compositions},{plan.Statistics.SummaryPredictions},"));
            return;
        }

        if (plan.Segments.Any(segment => segment.CrossfadeSeconds <= 0d))
        {
            failures.Add($"{label} leg {leg}: the plan contains a zero-crossfade (teleport) segment.");
        }

        ImmutableSegments(plan, detail);
        int issuedBefore = fixture.Driver.IssuedTransitions.Count;
        EngineRun run = await RunAsync(fixture, binding, plan, prediction, tickLimit);
        int blended = 0;
        int mismatches = 0;
        for (int index = 0; index < run.Ticks.Count; index++)
        {
            EngineTick engine = run.Ticks[index];
            string current = binding.RoleOf[engine.Current];
            string? fading = engine.Fading is null ? null : binding.RoleOf[engine.Fading];
            double weight = AnimationGraphWeightModel.CrossfadeWeights(fading is not null, engine.FadingPosition, engine.FadingLength).Current;
            if (fading is null || weight >= BlendedWeightLimit)
            {
                continue;
            }

            blended++;
            SeamClip fadingClip = binding.Seams.Clips[fading];
            SeamClip currentClip = binding.Seams.Clips[current];
            foreach (StandingLocomotionAnatomicalSide foot in Enum.GetValues<StandingLocomotionAnatomicalSide>())
            {
                if (fadingClip.IsPlanted(foot, engine.FadingFromPosition) != currentClip.IsPlanted(foot, engine.CurrentPosition))
                {
                    mismatches++;
                    failures.Add(FormattableString.Invariant(
                        $"{label} leg {leg} tick {index}: {foot} phase differs between {fading}@{engine.FadingFromPosition:0.######} and {current}@{engine.CurrentPosition:0.######} (weight {weight:0.####})."));
                }
            }
        }

        // Every issued transition is a planner-set pair the planner declares usable.
        for (int index = issuedBefore; index < fixture.Driver.IssuedTransitions.Count; index++)
        {
            NPCClipTransitionRecord issued = fixture.Driver.IssuedTransitions[index];
            if (!binding.RoleOf.TryGetValue(issued.From, out string? from)
                || !binding.RoleOf.TryGetValue(issued.To, out string? to)
                || !binding.Seams.TryGetPair(from, to, out SeamPair? pair)
                || !binding.Planner.UsablePairs.Contains(pair!)
                || issued.CrossfadeSeconds <= 0d)
            {
                failures.Add(FormattableString.Invariant($"{label} leg {leg}: issued transition {issued.From} → {issued.To} ({issued.CrossfadeSeconds} s) is outside the planner's usable set."));
            }
        }

        bool complete = tickLimit >= prediction.Ticks.Length;
        SequencePose predictedEnd = complete ? prediction.EndPose : prediction.Ticks[tickLimit - 1].Pose;
        Vector2 actorEnd = new(run.End.Origin.X, run.End.Origin.Z);
        float endError = actorEnd.DistanceTo(predictedEnd.Position);
        float actorYaw = run.End.Basis.GetEuler().Y;
        float yawError = Math.Abs(Mathf.Wrap(actorYaw - predictedEnd.Yaw, -Mathf.Pi, Mathf.Pi));
        if (prediction.Ticks.Length != plan.TickCount || prediction.EndPose != plan.PredictedEnd)
        {
            failures.Add($"{label} leg {leg}: the per-tick prediction differs from the plan's summary ({prediction.Ticks.Length} vs {plan.TickCount} ticks).");
        }

        if (endError > EndPositionToleranceMetres || yawError > _endYawTolerance)
        {
            failures.Add(FormattableString.Invariant($"{label} leg {leg}: predicted end differs from the engine by {endError * 1000f:0.####} mm / {Mathf.RadToDeg(yawError):0.#####}°."));
        }

        if (run.Collisions > 0)
        {
            failures.Add($"{label} leg {leg}: SETUP: {run.Collisions} movement(s) collided; the oracle needs the open floor.");
        }

        if (blended == 0 && plan.Segments.Length > 0 && complete)
        {
            failures.Add($"{label} leg {leg}: SETUP: no recorded tick blended two clips.");
        }

        if (!complete)
        {
            _ = summary.AppendLine(FormattableString.Invariant($"{name},{leg},cut-at-tick-{tickLimit},{plan.Kind},{plan.Segments.Length},{tickLimit},,,,,,,{endError * 1000f:0.####},{Mathf.RadToDeg(yawError):0.#####},{blended},{mismatches},,,,,,,,,,,,,"));
            return;
        }

        Vector2 residual = plan.PlanarResidual;
        float engineResidual = plan.Target.Position.DistanceTo(actorEnd);
        float engineYawResidual = Mathf.Wrap(plan.Target.Yaw - actorYaw, -Mathf.Pi, Mathf.Pi);
        // Residual in the NPC's final frame: forward (target ahead: a shortfall to lengthen) and right.
        Vector2 local = SequencePose.SemanticToWorld(residual, plan.PredictedEnd.Yaw);
        int singleSwing = plan.Windows.Count(window => window.Phase.IsSingleSwing());
        int bothSwingTicks = plan.Windows.Where(window => window.Phase == SupportPhase.BothSwing).Sum(window => window.TickCount);
        string estimate = Unit6Estimate(local, plan.YawResidual, singleSwing);
        _ = summary.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name},{leg},{plan.Status},{plan.Kind},{plan.Segments.Length},{prediction.Ticks.Length},{residual.Length() * 1000f:0.#},{Mathf.RadToDeg(plan.YawResidual):0.##},{engineResidual * 1000f:0.#},{Mathf.RadToDeg(engineYawResidual):0.##},{local.Y * 1000f:0.#},{local.X * 1000f:0.#},{endError * 1000f:0.####},{Mathf.RadToDeg(yawError):0.#####},{blended},{mismatches},{plan.MaxSeparation * 1000f:0.#},{singleSwing},{bothSwingTicks},{estimate},{(times.Length > 0 ? times[0] : double.NaN):0.###},{(times.Length > 0 ? Percentile(times, 0.5) : double.NaN):0.###},{(times.Length > 0 ? Percentile(times, 0.95) : double.NaN):0.###},{(times.Length > 0 ? times[^1] : double.NaN):0.###},{plan.Statistics.SearchMilliseconds:0.###},{plan.Statistics.EstimateErrorMetres * 1000d:0.###},{plan.Statistics.Compositions},{plan.Statistics.SummaryPredictions},{Joins(plan)}"));
    }

    /// <summary>
    /// Unit 6 feasibility estimate from the design's correction budget (Q3): forward (lengthening) 16–40 mm per
    /// single-swing step, backward (shortening) about free (≥ 100 mm per step assumed), lateral 50–100 mm per window,
    /// yaw about the planted ball free once any foot swings. Both-swing windows are not counted as budget.
    /// </summary>
    private static string Unit6Estimate(Vector2 local, float yawResidual, int steps)
    {
        if (steps == 0)
        {
            return local.Length() <= 0.01f && Math.Abs(Mathf.RadToDeg(yawResidual)) <= 1f ? "within-A1" : "infeasible(no-swing)";
        }

        float forward = local.Y;
        float lateral = Math.Abs(local.X);
        bool conservative = (forward >= 0f ? forward <= steps * ForwardPerStepConservative : -forward <= steps * BackwardPerStep)
                            && lateral <= steps * LateralPerStepConservative;
        bool optimistic = (forward >= 0f ? forward <= steps * ForwardPerStepOptimistic : -forward <= steps * BackwardPerStep)
                          && lateral <= steps * LateralPerStepOptimistic;
        return conservative ? "feasible" : optimistic ? "feasible-optimistic" : "infeasible";
    }

    private static void ImmutableSegments(ArrivalPlan plan, StringBuilder detail)
    {
        _ = detail.AppendLine(FormattableString.Invariant($"start {plan.Start.Role}@{plan.Start.PositionSeconds:0.####} exit {plan.SequenceStart!.ExitSeconds:0.####}+{plan.SequenceStart.ExitCycles}"));
        foreach (ArrivalPlanSegment segment in plan.Segments)
        {
            _ = detail.AppendLine(FormattableString.Invariant(
                $"  {segment.Stage,-7} {segment.Role,-6} in@{segment.EntrySeconds:0.####} xf {segment.CrossfadeSeconds * 1000d:0}ms sep {segment.EntrySeparation * 1000f:0.0}mm out@{segment.ExitSeconds:0.####}+{segment.ExitCycles}"));
        }

        _ = detail.AppendLine(FormattableString.Invariant(
            $"  predicted end ({plan.PredictedEnd.Position.X:0.####}, {plan.PredictedEnd.Position.Y:0.####}) yaw {Mathf.RadToDeg(plan.PredictedEnd.Yaw):0.###}° target ({plan.Target.Position.X:0.####}, {plan.Target.Position.Y:0.####}) yaw {Mathf.RadToDeg(plan.Target.Yaw):0.###}° stats {plan.Statistics}"));
    }

    private static string Joins(ArrivalPlan plan)
    {
        string previous = plan.Start.Role;
        List<string> joins = [];
        foreach (ArrivalPlanSegment segment in plan.Segments)
        {
            joins.Add(string.Create(CultureInfo.InvariantCulture, $"{previous}>{segment.Role}({segment.EntrySeparation * 1000f:0}mm)"));
            previous = segment.Role;
        }

        return string.Join(' ', joins);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
        => sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>The declared planner set (the pairs the planner uses) and each pair's best aligned seam separation.</summary>
    private static void ReportPlannerSet(NPCNavigationRig rig, Binding binding, StringBuilder summary)
    {
        float tolerance = binding.Predictor.SeamToleranceMetres;
        SeamPair[] used = [.. binding.Planner.UsablePairs];
        (SeamPair Pair, float Best)[] best = [.. used.Select(pair => (pair, pair.UsableCandidates.Where(candidate => candidate.IsAlignedWithin(tolerance)).Min(candidate => candidate.MaxSeparation)))];
        (SeamPair worstPair, float worst) = best.MaxBy(entry => entry.Best);
        SeamPair[] unused = [.. binding.Seams.Pairs.Where(pair => !used.Contains(pair))];
        _ = summary.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"rig={rig} tolerance_mm={tolerance * 1000f:0.#} planner_set_pairs={binding.Seams.Pairs.Length} planner_uses={used.Length} worst_best_separation_mm={worst * 1000f:0.#} ({worstPair.Outgoing.Role}->{worstPair.Incoming.Role})"));
        _ = summary.AppendLine("used_pairs(best_mm): " + string.Join(' ', best.OrderByDescending(entry => entry.Best).Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Pair.Outgoing.Role}->{entry.Pair.Incoming.Role}({entry.Best * 1000f:0.#})"))));
        _ = summary.AppendLine("unused_pairs: " + string.Join(' ', unused.Select(pair => $"{pair.Outgoing.Role}->{pair.Incoming.Role}")));
    }

    internal static async Task<EngineRun> RunAsync(NPCNavigationRigFixture fixture, Binding binding, ArrivalPlan plan, SequencePrediction prediction, int tickLimit)
    {
        NPCAnimationTreeDriver driver = fixture.Driver;
        var actor = (CharacterBody3D)fixture.Actor;
        Node3D reference = fixture.Navigation.RootMotionReference!;
        AnimationNodeStateMachinePlayback playback = fixture.Tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
        var records = new Dictionary<long, EngineTick>();

        void OnMixerApplied()
        {
            long evaluation = driver.EvaluationCount + 1;
            using StringName current = playback.GetCurrentNode();
            using StringName fading = playback.GetFadingFromNode();
            records[evaluation] = new EngineTick(
                fixture.Tree.GetRootMotionPosition(),
                fixture.Tree.GetRootMotionRotation(),
                current.ToString(),
                playback.GetCurrentPlayPosition(),
                fading.IsEmpty ? null : fading.ToString(),
                playback.GetFadingFromPlayPosition(),
                playback.GetFadingPosition(),
                playback.GetFadingLength());
        }

        fixture.Tree.MixerApplied += OnMixerApplied;
        var run = new EngineRun();
        using var replay = new PhysicsReplay(fixture, () =>
        {
            EngineTick record = records[driver.EvaluationCount];
            Apply(actor, reference, record, run);
        });
        ImmutableSegmentsList segments = new(plan);
        try
        {
            long first = driver.EvaluationCount + 1;
            int next = 0;
            int ticks = Math.Min(tickLimit, prediction.Ticks.Length);
            RequestIfDue(driver, binding, segments, prediction, 0, ref next);
            for (int tick = 0; tick < ticks; tick++)
            {
                await replay.StepAsync();
                if (driver.EvaluationCount != first + tick)
                {
                    throw new InvalidOperationException($"SETUP: expected evaluation {first + tick} after tick {tick}, observed {driver.EvaluationCount}.");
                }

                EngineTick record = records[first + tick];
                run.Ticks.Add(record);
                if (tick + 1 < ticks)
                {
                    RequestIfDue(driver, binding, segments, prediction, tick + 1, ref next);
                }
            }

            run.End = actor.GlobalTransform;
            if (ticks > 0)
            {
                binding.IdlePosition = prediction.Ticks[ticks - 1].Current.PositionSeconds;
            }
        }
        finally
        {
            fixture.Tree.MixerApplied -= OnMixerApplied;
        }

        return run;
    }

    /// <summary>Owns manual tree advancement and movement before the deferred skeleton update, as NPCNavigation does.</summary>
    internal sealed class PhysicsReplay : IDisposable
    {
        private readonly NPCNavigationRigFixture _fixture;
        private readonly Action _move;
        private readonly bool _wasProcessing;
        private Exception? _failure;

        public PhysicsReplay(NPCNavigationRigFixture fixture, Action move)
        {
            _fixture = fixture;
            _move = move;
            _wasProcessing = fixture.Navigation.IsPhysicsProcessing();
            fixture.Navigation.SetPhysicsProcess(false);
            fixture.Actor.GetTree().PhysicsFrame += AdvanceAndMove;
        }

        private void AdvanceAndMove()
        {
            if (_failure is not null)
            {
                return;
            }

            try
            {
                _fixture.Driver.Advance(_fixture.Actor.GetPhysicsProcessDeltaTime());
                _move();
            }
            catch (Exception exception)
            {
                _failure = exception;
            }
        }

        public async Task StepAsync()
        {
            // PhysicsFrame precedes node processing and the deferred skeleton update. Wait for both
            // boundaries: advancing in the signal alone does not make the post-IK sample ready.
            await _fixture.StepAsync(1);
            if (_failure is not null)
            {
                throw new InvalidOperationException("MEASUREMENT: physics replay failed.", _failure);
            }

            _ = await _fixture.Skeleton.ToSignal(_fixture.Skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        }

        public void Dispose()
        {
            _fixture.Actor.GetTree().PhysicsFrame -= AdvanceAndMove;
            _fixture.Navigation.SetPhysicsProcess(_wasProcessing);
        }
    }

    internal static void RequestIfDue(NPCAnimationTreeDriver driver, Binding binding, ImmutableSegmentsList segments, SequencePrediction prediction, int tick, ref int next)
    {
        if (next >= segments.Count || prediction.EntryTicks[next] != tick)
        {
            return;
        }

        SequenceSegment segment = segments[next];
        if (!driver.TryRequestTransition(binding.StateOf[segment.Role], segment.EntrySeconds, segment.CrossfadeSeconds))
        {
            throw new InvalidOperationException($"SETUP: the driver refused {segment.Role} at tick {tick}.");
        }

        next++;
    }

    /// <summary>The runtime movement operation for one recorded tick: rotate, then one <c>MoveAndCollide</c>.</summary>
    private static void Apply(CharacterBody3D actor, Node3D reference, EngineTick record, EngineRun run)
    {
        var frame = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis);
        float yaw = frame.EvaluatedRootRotationToActorLocal(record.RootRotation).GetEuler().Y;
        if (float.IsFinite(yaw) && !Mathf.IsZeroApprox(yaw))
        {
            actor.RotateY(yaw);
        }

        Vector3 planar = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis).EvaluatedRootDeltaToWorld(record.RootPosition);
        planar.Y = 0f;
        if (!planar.IsZeroApprox() && actor.MoveAndCollide(planar) is not null)
        {
            run.Collisions++;
        }
    }

    internal static SequencePose PoseOf(Node3D actor)
    {
        Transform3D transform = actor.GlobalTransform;
        return new SequencePose(new Vector2(transform.Origin.X, transform.Origin.Z), transform.Basis.GetEuler().Y);
    }

    /// <summary>
    /// The planner over the NPC's own bound trajectory table, the package planted intervals, the seam table built from
    /// them and the configured sad seam tolerance, with the tree's loop modes (the production binding, as the
    /// predictor oracle binds it).
    /// </summary>
    internal static Binding Bind(NPCNavigationRigFixture fixture)
    {
        ClipTrajectoryTable trajectories = fixture.Navigation.Trajectories
            ?? throw new InvalidOperationException("SETUP: the NPC navigation has no trajectory table.");
        string cataloguePath = fixture.Navigation.CataloguePath;
        var catalogue = StandingLocomotionCatalogue.Parse(Godot.FileAccess.GetFileAsString(cataloguePath), cataloguePath);
        string library = catalogue.AnimationLibraryPath;
        var intervals = PerFootPlantedIntervals.Load($"{library[..(library.LastIndexOf('/') + 1)]}{PerFootPlantedIntervals.FileName}");
        intervals.ValidateAgainst(catalogue);
        var idleIntervals = PerFootPlantedIntervals.LoadValidatedIdleCatalogue(IdleCataloguePath(fixture));

        List<SeamClip> clips = [];
        foreach (StandingLocomotionClip clip in catalogue.Clips.Values.OrderBy(value => value.Role, StringComparer.Ordinal))
        {
            clips.Add(SeamClip.FromTrajectory(clip, trajectories[clip.Role], intervals[clip.Identity]));
        }

        ClipTrajectory idle = trajectories.Clips.Values.Single(trajectory => trajectory.Capability is null);
        clips.Add(SeamClip.FromTrajectory(idle, SeamClipRole.Idle, idleIntervals[idle.Identity]));
        var seams = SeamTable.Build(clips);

        NPCAnimationTreeDriver driver = fixture.Driver;
        Dictionary<string, StringName> stateOf = new(StringComparer.Ordinal)
        {
            [idle.Role] = NPCAnimationProgramme.IdleState
        };
        foreach ((StringName state, StandingLocomotionCapability capability) in NPCAnimationProgramme.StateCapabilities)
        {
            stateOf[catalogue[capability].Role] = state;
        }

        var roleOf = stateOf.ToDictionary(pair => pair.Value.ToString(), pair => pair.Key, StringComparer.Ordinal);
        HashSet<string> looping = [.. stateOf.Where(pair => driver.IsLooping(pair.Value)).Select(pair => pair.Key)];
        SeamToleranceConfig config = ResourceLoader.Load<SeamToleranceConfig>(SeamToleranceConfigPath)
            ?? throw new InvalidOperationException($"SETUP: seam tolerance config '{SeamToleranceConfigPath}' did not load.");
        float tolerance = config.ToSeamTolerance(catalogue.PackageVariant).ToleranceMetres;
        var predictor = new SequencePredictor(seams, trajectories.Clips, looping, tolerance);
        return new Binding(catalogue, seams, predictor, new ArrivalPlanner(seams, predictor), stateOf, roleOf);
    }

    private static string IdleCataloguePath(NPCNavigationRigFixture fixture)
    {
        string animation = fixture.Driver.ClipAnimation(NPCAnimationProgramme.IdleState).ToString();
        int separator = animation.IndexOf('/', StringComparison.Ordinal);
        string libraryPath = fixture.Tree.GetAnimationLibrary(separator > 0 ? animation[..separator] : string.Empty)?.ResourcePath ?? string.Empty;
        return libraryPath.EndsWith(".tres", StringComparison.Ordinal)
            ? $"{libraryPath[..^".tres".Length]}_catalogue.json"
            : throw new InvalidOperationException($"SETUP: the NPC Idle state plays '{animation}', which has no idle catalogue.");
    }

    internal sealed record Binding(
        StandingLocomotionCatalogue Catalogue,
        SeamTable Seams,
        SequencePredictor Predictor,
        ArrivalPlanner Planner,
        IReadOnlyDictionary<string, StringName> StateOf,
        IReadOnlyDictionary<string, string> RoleOf)
    {
        /// <summary>Idle playback position after the last played tick.</summary>
        public double IdlePosition
        {
            get; set;
        }
    }

    internal sealed class ImmutableSegmentsList(ArrivalPlan plan)
    {
        private readonly SequenceSegment[] _segments = [.. plan.SequenceSegments];

        public int Count => _segments.Length;

        public SequenceSegment this[int index] => _segments[index];
    }

    internal readonly record struct EngineTick(
        Vector3 RootPosition,
        Quaternion RootRotation,
        string Current,
        double CurrentPosition,
        string? Fading,
        double FadingFromPosition,
        double FadingPosition,
        double FadingLength);

    internal sealed class EngineRun
    {
        public int Collisions
        {
            get; set;
        }

        public List<EngineTick> Ticks { get; } = [];

        public Transform3D End
        {
            get; set;
        }
    }
}
