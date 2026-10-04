using System.Globalization;
using System.Text;
using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>
/// NAV-001 P4 engine oracle for the <see cref="SequencePredictor"/> on both reference rigs. The real NPC of the navigation
/// test scene is driven through its production <see cref="NPCAnimationTreeDriver"/> (advanced once per physics tick
/// by <see cref="NPCNavigation"/>) along representative clip sequences joined at aligned seams of the bind-time seam
/// table: a forward walk (Idle → entry → cycle ×2 → exit → Idle), a turning walk through a lane switch into an arc, a
/// pivot then Idle, a side-step then Idle inside its double support, a side-step from its start (a teleport, covering
/// its whole step) then Idle, the limiting slice join (<c>i2b_l → b2i_l</c>) at its lowest-separation seam, a backward
/// walk (<c>i2b_r → wb → b2i_r</c>), an entry straight into an arc, a left ↔ right arc switch, and each whole side-step
/// and pivot joined from Idle and back (no teleport into the
/// manoeuvre; a looping manoeuvre crosses its loop point). Each tick's final evaluation is recorded (root motion,
/// state-machine state and positions) together with the production planted-state source's result; the recorded root
/// motion is applied to the actor exactly as the runtime movement operation does (rotate, then one
/// <c>MoveAndCollide</c> on the open floor). The predictor's per-tick clips, positions, weights and planted state must
/// match, and its accumulated pose must end within 1 mm and 0.05° of the actor's. On every recorded tick that blends
/// two clips, both clips' planted intervals at the engine's own playback positions must agree for each foot (the seam
/// table's phase agreement as the runtime realises it). The NPC tree's authored transitions must be exactly the seam
/// table's planner set, each with that pair's crossfade duration; the pivot-continuation self-pairs are realised
/// through the rigs' continuation alias arcs, which map to the same roles as the canonical states, and sequences
/// exercise a crossfaded same-role pivot restart and a two-restart chain (canonical -> alias -> canonical)
/// through the aliases on both rigs.
/// </summary>
public sealed class NAV001SequencePredictorOracleIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/nav001-p4-u5b-i/oracle";
    private const string SeamToleranceConfigPath = "res://assets/characters/templates/navigation/seam_tolerance_sad.tres";

    /// <summary>End-pose planar bound of the unit brief: 1 mm.</summary>
    private const float EndPositionToleranceMetres = 0.001f;

    /// <summary>End-pose yaw bound of the unit brief: 0.05°.</summary>
    private static readonly float _endYawTolerance = Mathf.DegToRad(0.05f);

    /// <summary>Clip-position match: the playback reads positions in single precision.</summary>
    private const double PositionToleranceSeconds = 1e-5d;

    /// <summary>Weight match: single-precision crossfade state read back, plus the mixer's epsilon floor.</summary>
    private const double WeightTolerance = 5e-5d;

    /// <summary>Idle playback after the final join, before the prediction ends.</summary>
    private const double FinalIdleSeconds = 0.6d;

    /// <summary>Authored crossfade match, as the driver checks it.</summary>
    private const double CrossfadeToleranceSeconds = 1e-4d;

    /// <summary>Current weight at or above which a recorded tick no longer blends the fading clip.</summary>
    private const double BlendedWeightLimit = 1d - 1e-4d;

    /// <summary>Scan step for a manoeuvre's step structure (its planted intervals are piecewise constant).</summary>
    private const double StepScanSeconds = 1d / 240d;

    /// <summary>NAV-001 P4 predictor oracle on the female rig.</summary>
    [Fact]
    public Task Female_PredictorMatchesTheEngineTickByTick() => VerifyAsync(NPCNavigationRig.Female);

    /// <summary>NAV-001 P4 predictor oracle on the male rig.</summary>
    [Fact]
    public Task Male_PredictorMatchesTheEngineTickByTick() => VerifyAsync(NPCNavigationRig.Male);

    private static async Task VerifyAsync(NPCNavigationRig rig)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(rig);
        await fixture.StepAsync(NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);
        Binding binding = Bind(fixture);

        List<string> failures = [];
        var summary = new StringBuilder(FormattableString.Invariant(
            $"rig={rig} tolerance_m={binding.Predictor.SeamToleranceMetres} base_crossfade_s={binding.Seams.BaseCrossfadeSeconds} entry_lag_s={binding.Seams.EntryLagSeconds}\n"));
        VerifyAuthoredTransitions(fixture, binding, failures, summary);
        _ = summary.AppendLine("sequence,ticks,end_position_error_mm,end_yaw_error_deg,max_tick_delta_error_mm,max_weight_error,planted_mismatches,clip_mismatches,blended_ticks,blended_phase_mismatches,collisions,travel_m,turn_deg,map_first_counterfactual_mm,joins");
        var detail = new StringBuilder();
        foreach ((string name, Func<Binding, SequencePose, (SequenceStart, SequenceSegment[])> build) in Sequences())
        {
            SequencePose pose = PoseOf(fixture.Actor);
            (SequenceStart start, SequenceSegment[] segments) = build(binding, pose);
            SequencePrediction prediction = binding.Predictor.Predict(start, segments);
            if (!prediction.IsValid)
            {
                failures.Add($"{rig} {name}: SETUP: the sequence was rejected: {prediction.Rejection}.");
                continue;
            }

            EngineRun run = await RunAsync(fixture, binding, segments, prediction);
            VerifyTwoRestartAliasPath(fixture, name);
            Compare(rig, name, binding, segments, prediction, run, failures, summary, detail);
        }

        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory));
        File.WriteAllText(Path.Combine(directory, $"{rig}-summary.csv"), summary.ToString() + string.Join('\n', failures));
        File.WriteAllText(Path.Combine(directory, $"{rig}-ticks.csv"), detail.ToString());
        Assert.True(failures.Count == 0, $"{string.Join('\n', failures)}\n{summary}");
    }

    /// <summary>
    /// PARITY-GAP-1 evidence for the two-restart row: the engine must have issued both self-join crossfades through
    /// the rig's native continuation aliases — canonical -> alias -> canonical — as recorded by the production
    /// driver's own transition ledger. Every other row is unaffected.
    /// </summary>
    private static void VerifyTwoRestartAliasPath(NPCNavigationRigFixture fixture, string name)
    {
        if (name != "pivot-restart-twice")
        {
            return;
        }

        (string From, string To)[] issued = [.. fixture.Driver.IssuedTransitions.TakeLast(4).Select(record => (record.From, record.To))];
        Assert.Equal(
            [
                ("NPCIdle", "NPCPivotR"),
                ("NPCPivotR", "NPCPivotRContinuation"),
                ("NPCPivotRContinuation", "NPCPivotR"),
                ("NPCPivotR", "NPCIdle"),
            ],
            issued);
    }

    private static IEnumerable<(string Name, Func<Binding, SequencePose, (SequenceStart, SequenceSegment[])> Build)> Sequences()
    {
        yield return ("walk", static (binding, pose) => Walk(binding, pose));
        yield return ("arc", static (binding, pose) => Chain(
            binding,
            pose,
            [
                binding.Catalogue[StandingLocomotionCapability.EntryForwardRight].Role,
                binding.Catalogue[StandingLocomotionCapability.CyclicForwardPrimary].Role,
                binding.Catalogue[StandingLocomotionCapability.CyclicArcRightRightLane].Role,
                binding.Catalogue[StandingLocomotionCapability.ExitForwardRight].Role,
            ],
            0));
        yield return ("pivot", static (binding, pose) => Manoeuvre(binding, pose, StandingLocomotionCapability.ConfinedPivotLeft));
        // A crossfaded same-role pivot restart (idle -> pr45 -> pr45 restart -> idle) issued through the rig's
        // continuation alias, covering the whole self-join crossfade window tick by tick.
        yield return ("pivot-restart", static (binding, pose) => Chain(
            binding,
            pose,
            [
                binding.Catalogue[StandingLocomotionCapability.ConfinedPivotRight].Role,
                binding.Catalogue[StandingLocomotionCapability.ConfinedPivotRight].Role,
            ],
            0));
        // A two-restart chain (idle -> pr45 -> pr45 -> pr45 -> idle): both self-join crossfades are issued
        // through the continuation aliases, alternating canonical -> alias -> canonical exactly as the
        // production runtime issues repeated pivot segments, so multi-restart per-tick parity is measured on
        // both rigs (PARITY-GAP-1).
        yield return ("pivot-restart-twice", static (binding, pose) => Chain(
            binding,
            pose,
            [
                binding.Catalogue[StandingLocomotionCapability.ConfinedPivotRight].Role,
                binding.Catalogue[StandingLocomotionCapability.ConfinedPivotRight].Role,
                binding.Catalogue[StandingLocomotionCapability.ConfinedPivotRight].Role,
            ],
            0));
        yield return ("side-step", static (binding, pose) => Manoeuvre(binding, pose, StandingLocomotionCapability.ConfinedSideStepLeft));
        yield return ("side-step-from-start", static (binding, pose) => Chain(binding, pose, [binding.Catalogue[StandingLocomotionCapability.ConfinedSideStepLeft].Role], 0, teleportIntoFirst: true));
        // The limiting pair's slice join at its lowest-separation seam after the entry crossfade.
        yield return ("slice-limiting", static (binding, pose) => Chain(
            binding,
            pose,
            [
                binding.Catalogue[StandingLocomotionCapability.EntryBackwardLeft].Role,
                binding.Catalogue[StandingLocomotionCapability.ExitBackwardLeft].Role,
            ],
            0,
            lowestSeparation: true));
        yield return ("backward-walk", static (binding, pose) => Chain(
            binding,
            pose,
            [
                binding.Catalogue[StandingLocomotionCapability.EntryBackwardRight].Role,
                binding.Catalogue[StandingLocomotionCapability.CyclicBackwardPrimary].Role,
                binding.Catalogue[StandingLocomotionCapability.ExitBackwardRight].Role,
            ],
            1));
        yield return ("entry-arc", static (binding, pose) => Chain(
            binding,
            pose,
            [
                binding.Catalogue[StandingLocomotionCapability.EntryForwardLeft].Role,
                binding.Catalogue[StandingLocomotionCapability.CyclicArcLeftRightLane].Role,
                binding.Catalogue[StandingLocomotionCapability.ExitForwardRight].Role,
            ],
            0));
        yield return ("arc-switch", static (binding, pose) => Chain(
            binding,
            pose,
            [
                binding.Catalogue[StandingLocomotionCapability.EntryForwardRight].Role,
                binding.Catalogue[StandingLocomotionCapability.CyclicArcRightLeftLane].Role,
                binding.Catalogue[StandingLocomotionCapability.CyclicArcLeftRightLane].Role,
                binding.Catalogue[StandingLocomotionCapability.ExitForwardRight].Role,
            ],
            0));
        foreach (StandingLocomotionCapability capability in (StandingLocomotionCapability[])[
                     StandingLocomotionCapability.ConfinedSideStepLeft,
                     StandingLocomotionCapability.ConfinedSideStepRight,
                     StandingLocomotionCapability.ConfinedPivotLeft,
                     StandingLocomotionCapability.ConfinedPivotRight])
        {
            string identifier = StandingLocomotionCatalogue.CapabilityIdentifier(capability);
            yield return ($"whole-{identifier}-largest", (binding, pose) => WholeManoeuvre(binding, pose, capability, largest: true));
            yield return ($"whole-{identifier}-shortest", (binding, pose) => WholeManoeuvre(binding, pose, capability, largest: false));
        }
    }

    /// <summary>
    /// Idle → one whole side-step or pivot → Idle, joined by crossfades at aligned seams (no teleport into the
    /// manoeuvre): the achievable one-step motion. Every aligned Idle join (its lowest-separation seam per entry
    /// position) is tried with every aligned return to Idle in the double-support run after the step, that is, once
    /// every foot has stepped and both stand again, before the next lift-off, across the loop point of a looping
    /// manoeuvre. With <paramref name="largest"/>, the predicted sequence with the largest travel (side-step) or turn
    /// (pivot) is kept, the shortest on a tie; otherwise the shortest, the largest motion on a tie. Together they bound
    /// the achievable motion of one whole step.
    /// </summary>
    private static (SequenceStart, SequenceSegment[]) WholeManoeuvre(Binding binding, SequencePose pose, StandingLocomotionCapability capability, bool largest)
    {
        string role = binding.Catalogue[capability].Role;
        string idle = binding.IdleRole;
        bool pivot = capability is StandingLocomotionCapability.ConfinedPivotLeft or StandingLocomotionCapability.ConfinedPivotRight;
        float tolerance = binding.Predictor.SeamToleranceMetres;
        double lag = binding.Seams.EntryLagSeconds;
        SeamClip clip = binding.Seams.Clips[role];
        double idleLength = binding.Seams.Clips[idle].DurationSeconds;
        SeamCandidate[] entries =
        [
            .. binding.Seams.AlignedSeams(idle, role, tolerance)
                .GroupBy(candidate => Math.Round(candidate.IncomingTime * clip.KeyRateHz))
                .Select(group => group.MinBy(candidate => candidate.MaxSeparation)),
        ];
        SequenceSegment[]? best = null;
        double bestMotion = double.NegativeInfinity;
        int bestTicks = int.MaxValue;
        foreach (SeamCandidate entry in entries)
        {
            double stepped = SteppedAndStanding(clip, NextLiftOff(clip, entry.IncomingTime));
            if (!binding.Seams.TryFindNextAligned(role, idle, stepped - lag, tolerance, out SeamChoice exit))
            {
                continue;
            }

            // Unwrapped outgoing time of each exit in the run: aligned exits one native grid step apart.
            double time = stepped - lag + exit.DelaySeconds;
            while (true)
            {
                double elapsed = time - entry.IncomingTime;
                int cycles = binding.Looping.Contains(role) ? (int)Math.Floor((elapsed + 1e-6d) / clip.DurationSeconds) : 0;
                double idleEntry = (((entry.OutgoingTime - 0.2d) % idleLength) + idleLength) % idleLength;
                SequenceSegment[] segments =
                [
                    new SequenceSegment(idle, idleEntry, 0d, entry.OutgoingTime),
                    new SequenceSegment(role, entry.IncomingTime, Crossfade(binding, idle, role), exit.Candidate.OutgoingTime, cycles),
                    new SequenceSegment(idle, exit.Candidate.IncomingTime, Crossfade(binding, role, idle), (exit.Candidate.IncomingTime + FinalIdleSeconds) % idleLength),
                ];
                SequencePrediction prediction = binding.Predictor.Predict(SequenceStart.Teleport(pose), segments);
                if (prediction.IsValid)
                {
                    double motion = pivot
                        ? Math.Abs(prediction.EndPose.Yaw - pose.Yaw)
                        : prediction.EndPose.Position.DistanceTo(pose.Position);
                    int ticks = prediction.Ticks.Length;
                    bool sameMotion = Math.Abs(motion - bestMotion) <= 1e-6d;
                    bool better = largest
                        ? motion > bestMotion + 1e-6d || (sameMotion && ticks < bestTicks)
                        : ticks < bestTicks || (ticks == bestTicks && motion > bestMotion + 1e-6d);
                    if (better)
                    {
                        (best, bestMotion, bestTicks) = (segments, motion, ticks);
                    }
                }

                if (!binding.Seams.TryFindNextAligned(role, idle, time + 1e-6d, tolerance, out SeamChoice next)
                    || next.DelaySeconds > (1d / clip.KeyRateHz) + 1e-6d)
                {
                    break;
                }

                time += 1e-6d + next.DelaySeconds;
                exit = next;
            }
        }

        return best is null
            ? throw new InvalidOperationException($"SETUP: no whole {role} step joins Idle at aligned seams.")
            : (SequenceStart.Teleport(pose), best);
    }

    /// <summary>
    /// Playback time (unwrapped) at or after <paramref name="from"/> when the clip first lifts a foot; the clip end
    /// for a clip played once that never does.
    /// </summary>
    private static double NextLiftOff(SeamClip clip, double from)
    {
        double limit = from + (clip.WrapsPlayback ? 2d * clip.DurationSeconds : clip.DurationSeconds - from);
        for (double time = from; time < limit; time += StepScanSeconds)
        {
            if (!clip.IsPlanted(StandingLocomotionAnatomicalSide.Left, time) || !clip.IsPlanted(StandingLocomotionAnatomicalSide.Right, time))
            {
                return time;
            }
        }

        return limit;
    }

    /// <summary>
    /// Playback time (unwrapped) after <paramref name="liftOff"/> when every foot has swung at least once and both are
    /// planted again: the end of one whole step.
    /// </summary>
    private static double SteppedAndStanding(SeamClip clip, double liftOff)
    {
        bool leftSwung = false;
        bool rightSwung = false;
        for (double time = liftOff; time < liftOff + (2d * clip.DurationSeconds); time += StepScanSeconds)
        {
            bool left = clip.IsPlanted(StandingLocomotionAnatomicalSide.Left, time);
            bool right = clip.IsPlanted(StandingLocomotionAnatomicalSide.Right, time);
            leftSwung |= !left;
            rightSwung |= !right;
            if (leftSwung && rightSwung && left && right)
            {
                return time;
            }
        }

        throw new InvalidOperationException($"SETUP: '{clip.Role}' does not complete a step after {liftOff} s.");
    }

    /// <summary>The seam table's crossfade duration of a planner-set pair.</summary>
    private static double Crossfade(Binding binding, string outgoing, string incoming)
        => binding.Seams.TryGetPair(outgoing, incoming, out SeamPair? pair)
            ? pair!.CrossfadeSeconds
            : throw new InvalidOperationException($"SETUP: {outgoing} → {incoming} is outside the planner set.");

    /// <summary>
    /// The NPC tree's authored transitions are exactly the seam table's planner set, each with that pair's
    /// crossfade duration: every planner-set pair has an authored realisation at the table's duration — directly
    /// between its canonical states, or, for the pivot-continuation self-pairs (Godot state machines cannot author
    /// from == to transitions), through the continuation alias arcs that map to the same role — and no NPC
    /// transition, alias or canonical, lies outside the set or uses a different duration.
    /// </summary>
    private static void VerifyAuthoredTransitions(NPCNavigationRigFixture fixture, Binding binding, List<string> failures, StringBuilder summary)
    {
        NPCAnimationTreeDriver driver = fixture.Driver;
        int authored = 0;
        foreach (SeamPair pair in binding.Seams.Pairs)
        {
            StringName from = binding.StateOf[pair.Outgoing.Role];
            StringName to = binding.StateOf[pair.Incoming.Role];
            double? crossfade = driver.AuthoredCrossfade(from, to);
            if (crossfade is not { } value || Math.Abs(value - pair.CrossfadeSeconds) > CrossfadeToleranceSeconds)
            {
                if (pair.Outgoing.Role == pair.Incoming.Role
                    && ContinuationAliasArc(driver, binding, pair.Outgoing.Role, pair.CrossfadeSeconds, out StringName aliasFrom, out StringName aliasTo))
                {
                    // The self-pair's authored realisation: the continuation alias re-entry arc.
                    _ = summary.AppendLine(FormattableString.Invariant(
                        $"self-pair {pair.Outgoing.Role} -> {pair.Incoming.Role} realised by alias arc {aliasFrom} -> {aliasTo}."));
                    authored++;
                    continue;
                }

                failures.Add(crossfade is { } authoredValue
                    ? FormattableString.Invariant($"{from} -> {to} is authored with {authoredValue} s, not the seam table's {pair.CrossfadeSeconds} s.")
                    : $"planner-set pair {pair.Outgoing.Role} → {pair.Incoming.Role} ({pair.Kind}) has no authored {from} → {to} transition.");
            }
            else
            {
                authored++;
            }
        }

        AnimationNodeStateMachine machine = Assert.IsType<AnimationNodeStateMachine>(
            Assert.IsType<AnimationNodeBlendTree>(fixture.Tree.TreeRoot).GetNode("States"));
        int outside = 0;
        for (int index = 0; index < machine.GetTransitionCount(); index++)
        {
            string from = machine.GetTransitionFrom(index).ToString();
            string to = machine.GetTransitionTo(index).ToString();
            if (!from.StartsWith(NPCAnimationTreeDriver.StatePrefix, StringComparison.Ordinal)
                && !to.StartsWith(NPCAnimationTreeDriver.StatePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!binding.RoleOf.TryGetValue(from, out string? outgoing)
                || !binding.RoleOf.TryGetValue(to, out string? incoming)
                || !binding.Seams.IsInPlannerSet(outgoing, incoming))
            {
                outside++;
                failures.Add($"authored NPC transition {from} → {to} is outside the planner set.");
            }
            else if (binding.Seams.TryGetPair(outgoing, incoming, out SeamPair? pair)
                && Math.Abs(machine.GetTransition(index).XfadeTime - pair!.CrossfadeSeconds) > CrossfadeToleranceSeconds)
            {
                failures.Add(FormattableString.Invariant(
                    $"{from} -> {to} is authored with {machine.GetTransition(index).XfadeTime} s, not the seam table's {pair.CrossfadeSeconds} s for {outgoing} → {incoming}."));
            }
        }

        _ = summary.AppendLine(FormattableString.Invariant(
            $"planner_set_pairs={binding.Seams.Pairs.Length} authored_with_table_crossfade={authored} authored_outside_planner_set={outside} crossfades_s=[{string.Join(' ', binding.Seams.Pairs.Select(pair => pair.CrossfadeSeconds).Distinct().Order())}]"));
    }

    /// <summary>Whether the rig wires a continuation alias arc realising the role's self-pair at the table's
    /// crossfade duration (canonical → alias or alias → canonical), returning the found arc.</summary>
    private static bool ContinuationAliasArc(
        NPCAnimationTreeDriver driver,
        Binding binding,
        string role,
        double crossfadeSeconds,
        out StringName from,
        out StringName to)
    {
        foreach ((StringName state, StandingLocomotionCapability capability) in NPCAnimationProgramme.PivotContinuationStates)
        {
            if (binding.Catalogue[capability].Role != role || !driver.HasState(state))
            {
                continue;
            }

            StringName canonical = binding.StateOf[role];
            double? reentry = driver.AuthoredCrossfade(canonical, state);
            if (reentry is { } value && Math.Abs(value - crossfadeSeconds) <= CrossfadeToleranceSeconds)
            {
                from = canonical;
                to = state;
                return true;
            }

            double? back = driver.AuthoredCrossfade(state, canonical);
            if (back is { } backValue && Math.Abs(backValue - crossfadeSeconds) <= CrossfadeToleranceSeconds)
            {
                from = state;
                to = canonical;
                return true;
            }
        }

        from = new StringName();
        to = new StringName();
        return false;
    }

    /// <summary>Idle → i2f_r → wf (entered, plus one extra whole cycle) → f2i_r → Idle at aligned seams.</summary>
    private static (SequenceStart, SequenceSegment[]) Walk(Binding binding, SequencePose pose)
    {
        string entry = binding.Catalogue[StandingLocomotionCapability.EntryForwardRight].Role;
        string cycle = binding.Catalogue[StandingLocomotionCapability.CyclicForwardPrimary].Role;
        string exit = binding.Catalogue[StandingLocomotionCapability.ExitForwardRight].Role;
        return Chain(binding, pose, [entry, cycle, exit], extraCycles: 1);
    }

    /// <summary>Idle → manoeuvre → Idle at aligned seams.</summary>
    private static (SequenceStart, SequenceSegment[]) Manoeuvre(Binding binding, SequencePose pose, StandingLocomotionCapability capability)
        => Chain(binding, pose, [binding.Catalogue[capability].Role], extraCycles: 0);

    /// <summary>
    /// A teleport into Idle 0.2 s before its aligned seam into the first role with the earliest entry position (or, with
    /// <paramref name="teleportIntoFirst"/>, a teleport into the first role at its start, covering its whole step up to
    /// its return to Idle), then each later role joined from the previous one once the previous crossfade has ended (see
    /// <see cref="TryChooseSeam"/>), then Idle for <see cref="FinalIdleSeconds"/>.
    /// </summary>
    private static (SequenceStart, SequenceSegment[]) Chain(
        Binding binding,
        SequencePose pose,
        string[] roles,
        int extraCycles,
        bool teleportIntoFirst = false,
        bool lowestSeparation = false)
    {
        List<SequenceSegment> segments = teleportIntoFirst ? [new SequenceSegment(roles[0], 0d, 0d, 0d)] : FromIdle(binding, roles[0]);
        foreach (string role in (string[])[.. roles[1..], binding.IdleRole])
        {
            SequenceSegment previous = segments[^1];
            SeamClip outgoing = binding.Seams.Clips[previous.Role];
            // The earliest request after the entry crossfade has ended: an n-tick crossfade ends on the (n − 1)-th tick
            // after its entry tick (8 ticks for 0.15 s at 60 Hz).
            double lag = binding.Seams.EntryLagSeconds;
            double earliest = previous.EntrySeconds + (previous.CrossfadeSeconds > 0d ? (Math.Round(previous.CrossfadeSeconds / lag) - 1d) * lag : 2d * lag);
            if (!TryChooseSeam(binding, previous.Role, role, earliest, lowestSeparation, out double exit, out double entry, out double delay))
            {
                throw new InvalidOperationException($"SETUP: no aligned seam {previous.Role} → {role} after {earliest} s.");
            }

            double elapsed = earliest - previous.EntrySeconds + delay;
            int cycles = binding.Looping.Contains(previous.Role)
                ? (int)Math.Floor((elapsed + 1e-6d) / outgoing.DurationSeconds) + (outgoing.Kind == SeamClipRole.Cycle ? extraCycles : 0)
                : 0;
            segments[^1] = previous with
            {
                ExitSeconds = exit,
                ExitCycles = cycles
            };
            segments.Add(new SequenceSegment(role, entry, Crossfade(binding, previous.Role, role), 0d));
        }

        SequenceSegment idle = segments[^1];
        double length = binding.Seams.Clips[idle.Role].DurationSeconds;
        segments[^1] = idle with
        {
            ExitSeconds = (idle.EntrySeconds + FinalIdleSeconds) % length
        };
        return (SequenceStart.Teleport(pose), [.. segments]);
    }

    /// <summary>A teleport into Idle 0.2 s before its aligned seam into <paramref name="role"/> with the earliest entry.</summary>
    private static List<SequenceSegment> FromIdle(Binding binding, string role)
    {
        // The first join enters its clip as early as possible; the teleport seeks Idle 0.2 s before that seam.
        SeamCandidate first = binding.Seams.AlignedSeams(binding.IdleRole, role, binding.Predictor.SeamToleranceMetres)
            .OrderBy(candidate => candidate.IncomingTime)
            .ThenBy(candidate => candidate.MaxSeparation)
            .FirstOrDefault(new SeamCandidate(double.NaN, double.NaN, 0f, 0f, false));
        if (double.IsNaN(first.OutgoingTime))
        {
            throw new InvalidOperationException($"SETUP: no aligned seam {binding.IdleRole} → {role}.");
        }

        double idleLength = binding.Seams.Clips[binding.IdleRole].DurationSeconds;
        double idleEntry = (((first.OutgoingTime - 0.2d) % idleLength) + idleLength) % idleLength;
        return
        [
            new SequenceSegment(binding.IdleRole, idleEntry, 0d, first.OutgoingTime),
            new SequenceSegment(role, first.IncomingTime, Crossfade(binding, binding.IdleRole, role), 0d),
        ];
    }

    /// <summary>
    /// The earliest aligned seam at or after <paramref name="earliest"/>, except that an exit or a manoeuvre returns to
    /// Idle at its latest aligned seam within its current lap, so it plays through its step.
    /// </summary>
    private static bool TryChooseSeam(Binding binding, string outgoing, string incoming, double earliest, bool lowestSeparation, out double exit, out double entry, out double delay)
    {
        float tolerance = binding.Predictor.SeamToleranceMetres;
        if (incoming == binding.IdleRole && binding.Seams.Clips[outgoing].Kind is SeamClipRole.Exit or SeamClipRole.Confined)
        {
            SeamCandidate[] latest = [.. binding.Seams.AlignedSeams(outgoing, incoming, tolerance)
                .Where(candidate => candidate.OutgoingTime >= earliest - 1e-9d)
                .OrderByDescending(candidate => candidate.OutgoingTime)
                .ThenBy(candidate => candidate.MaxSeparation)];
            (exit, entry, delay) = latest.Length > 0
                ? (latest[0].OutgoingTime, latest[0].IncomingTime, latest[0].OutgoingTime - earliest)
                : (0d, 0d, 0d);
            return latest.Length > 0;
        }

        bool found = lowestSeparation
            ? binding.Seams.TryFindLowestSeparationAligned(outgoing, incoming, earliest, tolerance, double.PositiveInfinity, out SeamChoice choice)
            : binding.Seams.TryFindNextAligned(outgoing, incoming, earliest, tolerance, out choice);
        (exit, entry, delay) = (choice.Candidate.OutgoingTime, choice.Candidate.IncomingTime, choice.DelaySeconds);
        return found;
    }

    private static async Task<EngineRun> RunAsync(
        NPCNavigationRigFixture fixture,
        Binding binding,
        SequenceSegment[] segments,
        SequencePrediction prediction)
    {
        NPCAnimationTreeDriver driver = fixture.Driver;
        var actor = (CharacterBody3D)fixture.Actor;
        Node3D reference = fixture.Navigation.RootMotionReference!;
        AnimationNodeStateMachinePlayback playback = fixture.Tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
        var records = new Dictionary<long, EngineTick>();
        var planted = new Dictionary<ulong, PlantedFeetState>();

        void OnMixerApplied()
        {
            // Evaluation index of the tick being evaluated; the final evaluation of an entry tick overwrites its
            // zero-delta pre-evaluation.
            long evaluation = driver.EvaluationCount + 1;
            using StringName current = playback.GetCurrentNode();
            using StringName fading = playback.GetFadingFromNode();
            records[evaluation] = new EngineTick(
                Engine.GetPhysicsFrames(),
                fixture.Tree.GetRootMotionPosition(),
                fixture.Tree.GetRootMotionRotation(),
                current.ToString(),
                playback.GetCurrentPlayPosition(),
                fading.IsEmpty ? null : fading.ToString(),
                playback.GetFadingFromPlayPosition(),
                playback.GetFadingPosition(),
                playback.GetFadingLength());
        }

        void OnSkeletonUpdated()
        {
            planted[fixture.PlantedSource.EvaluationTick] = fixture.PlantedSource.LastState;
        }

        fixture.Tree.MixerApplied += OnMixerApplied;
        fixture.Skeleton.SkeletonUpdated += OnSkeletonUpdated;
        var run = new EngineRun();
        try
        {
            await fixture.StepAsync(1);
            long first = driver.EvaluationCount + 1;
            Transform3D startTransform = actor.GlobalTransform;
            int next = 0;
            RequestIfDue(driver, binding, playback, segments, prediction, 0, ref next);
            for (int tick = 0; tick < prediction.Ticks.Length; tick++)
            {
                await fixture.StepAsync(1);
                if (driver.EvaluationCount != first + tick)
                {
                    throw new InvalidOperationException(
                        $"SETUP: expected evaluation {first + tick} after tick {tick}, observed {driver.EvaluationCount}.");
                }

                EngineTick record = records[first + tick];
                run.Ticks.Add(record);
                run.Moves.Add(Apply(actor, reference, record, run));
                RequestIfDue(driver, binding, playback, segments, prediction, tick + 1, ref next);
            }

            await fixture.SettleAsync();
            run.Start = startTransform;
            run.End = actor.GlobalTransform;
            foreach (EngineTick record in run.Ticks)
            {
                run.Planted.Add(planted.TryGetValue(record.PhysicsTick, out PlantedFeetState state) ? state : null);
            }
        }
        finally
        {
            fixture.Tree.MixerApplied -= OnMixerApplied;
            fixture.Skeleton.SkeletonUpdated -= OnSkeletonUpdated;
        }

        // Let the NPC settle in Idle before the next sequence.
        await fixture.StepAsync(30);
        return run;
    }

    /// <summary>Requests the segment entered on <paramref name="tick"/>, after the previous tick's evaluation.</summary>
    private static void RequestIfDue(
        NPCAnimationTreeDriver driver,
        Binding binding,
        AnimationNodeStateMachinePlayback playback,
        SequenceSegment[] segments,
        SequencePrediction prediction,
        int tick,
        ref int next)
    {
        if (next >= segments.Length || prediction.EntryTicks[next] != tick)
        {
            return;
        }

        SequenceSegment segment = segments[next];
        StringName state = binding.StateOf[segment.Role];
        // Same-role re-entry (the pivot-continuation self-pair) has no authored canonical self-transition: Godot
        // state machines reject from == to arcs, so the oracle issues restarts through the rig's continuation
        // aliases exactly as the production runtime's segment issuance does (NPCNavigation._PhysicsProcess): a
        // request whose destination equals the engine's current node re-enters through the alias, so consecutive
        // restarts alternate canonical -> alias -> canonical.
        using StringName current = playback.GetCurrentNode();
        if (current == state)
        {
            foreach ((StringName alias, StandingLocomotionCapability capability) in NPCAnimationProgramme.PivotContinuationStates)
            {
                if (binding.Catalogue[capability].Role == segment.Role && driver.HasState(alias))
                {
                    state = alias;
                    break;
                }
            }
        }

        double crossfade = 0d;
        if (segment.CrossfadeSeconds > 0d)
        {
            crossfade = driver.AuthoredCrossfade(current, state)
                ?? throw new InvalidOperationException($"SETUP: no authored transition {current} → {state}.");
            // The predictor, the seam table and the tree must agree on the crossfade the driver issues.
            if (Math.Abs(crossfade - segment.CrossfadeSeconds) > CrossfadeToleranceSeconds)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"SETUP: {current} → {state} is authored with {crossfade} s but predicted with {segment.CrossfadeSeconds} s."));
            }
        }

        if (!driver.TryRequestTransition(state, segment.EntrySeconds, crossfade))
        {
            throw new InvalidOperationException($"SETUP: the driver refused {state} at tick {tick}.");
        }

        next++;
    }

    /// <summary>
    /// The runtime movement operation (<c>NPCNavigation.ApplyMovementOnce</c>) for one recorded tick. It also
    /// accumulates the counterfactual of mapping the delta through the basis before the rotation, which shows how far
    /// the ordering moves the actor on this sequence.
    /// </summary>
    private static Vector3 Apply(CharacterBody3D actor, Node3D reference, EngineTick record, EngineRun run)
    {
        var frame = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis);
        Vector3 mapFirst = frame.EvaluatedRootDeltaToWorld(record.RootPosition);
        float yaw = frame.EvaluatedRootRotationToActorLocal(record.RootRotation).GetEuler().Y;
        if (float.IsFinite(yaw) && !Mathf.IsZeroApprox(yaw))
        {
            actor.RotateY(yaw);
        }

        Vector3 planar = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis)
            .EvaluatedRootDeltaToWorld(record.RootPosition);
        planar.Y = 0f;
        mapFirst.Y = 0f;
        run.MapFirstOffset += mapFirst - planar;
        if (!planar.IsZeroApprox() && actor.MoveAndCollide(planar) is not null)
        {
            run.Collisions++;
        }

        return planar;
    }

    private static void Compare(
        NPCNavigationRig rig,
        string name,
        Binding binding,
        SequenceSegment[] segments,
        SequencePrediction prediction,
        EngineRun run,
        List<string> failures,
        StringBuilder summary,
        StringBuilder detail)
    {
        string label = $"{rig} {name}";
        int plantedMismatches = 0;
        int clipMismatches = 0;
        int blendedTicks = 0;
        int blendedPhaseMismatches = 0;
        double maxWeightError = 0d;
        float maxDeltaError = 0f;
        _ = detail.AppendLine(FormattableString.Invariant($"# {label}: entries [{string.Join(' ', prediction.EntryTicks)}]"));
        _ = detail.AppendLine("tick,engine_current,engine_position,engine_fading,engine_fading_position,engine_weight,predicted_current,predicted_position,predicted_fading,predicted_fading_position,predicted_weight,engine_planted,predicted_planted,engine_dx_mm,engine_dz_mm,predicted_dx_mm,predicted_dz_mm");
        SequencePose before = prediction.StartPose;
        for (int index = 0; index < prediction.Ticks.Length; index++)
        {
            PredictedTick predicted = prediction.Ticks[index];
            EngineTick engine = run.Ticks[index];
            string current = binding.RoleOf[engine.Current];
            string? fading = engine.Fading is null ? null : binding.RoleOf[engine.Fading];
            double weight = AnimationGraphWeightModel.CrossfadeWeights(fading is not null, engine.FadingPosition, engine.FadingLength).Current;

            bool clipsMatch = current == predicted.Current.Role
                && Math.Abs(engine.CurrentPosition - predicted.Current.PositionSeconds) <= PositionToleranceSeconds
                && fading == predicted.FadingFrom?.Role
                && (fading is null || Math.Abs(engine.FadingFromPosition - predicted.FadingFrom!.Value.PositionSeconds) <= PositionToleranceSeconds);
            if (!clipsMatch)
            {
                clipMismatches++;
                failures.Add(FormattableString.Invariant(
                    $"{label} tick {index}: engine {current}@{engine.CurrentPosition:0.######} fading {fading}@{engine.FadingFromPosition:0.######} vs predicted {predicted.Current.Role}@{predicted.Current.PositionSeconds:0.######} fading {predicted.FadingFrom?.Role}@{predicted.FadingFrom?.PositionSeconds:0.######}."));
            }

            // Phase agreement as the runtime realises it: both blended clips, at the engine's own positions.
            if (fading is not null && weight < BlendedWeightLimit)
            {
                blendedTicks++;
                SeamClip fadingClip = binding.Seams.Clips[fading];
                SeamClip currentClip = binding.Seams.Clips[current];
                foreach (StandingLocomotionAnatomicalSide foot in Enum.GetValues<StandingLocomotionAnatomicalSide>())
                {
                    if (fadingClip.IsPlanted(foot, engine.FadingFromPosition) != currentClip.IsPlanted(foot, engine.CurrentPosition))
                    {
                        blendedPhaseMismatches++;
                        failures.Add(FormattableString.Invariant(
                            $"{label} tick {index}: {foot} phase differs between {fading}@{engine.FadingFromPosition:0.######} and {current}@{engine.CurrentPosition:0.######} (weight {weight:0.####})."));
                    }
                }
            }

            double weightError = Math.Abs(weight - predicted.Current.Weight);
            maxWeightError = Math.Max(maxWeightError, weightError);
            if (weightError > WeightTolerance)
            {
                failures.Add(FormattableString.Invariant($"{label} tick {index}: engine weight {weight:0.#######} vs predicted {predicted.Current.Weight:0.#######}."));
            }

            PlantedFeetState? engineState = run.Planted[index];
            if (engineState != predicted.Planted)
            {
                plantedMismatches++;
                failures.Add($"{label} tick {index}: engine planted {engineState?.ToString() ?? "unrecorded"} vs predicted {predicted.Planted}.");
            }

            Vector3 move = run.Moves[index];
            var predictedMove = new Vector2(predicted.Pose.Position.X - before.Position.X, predicted.Pose.Position.Y - before.Position.Y);
            maxDeltaError = Math.Max(maxDeltaError, predictedMove.DistanceTo(new Vector2(move.X, move.Z)));
            before = predicted.Pose;
            _ = detail.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{index},{current},{engine.CurrentPosition:0.######},{fading},{engine.FadingFromPosition:0.######},{weight:0.#######},{predicted.Current.Role},{predicted.Current.PositionSeconds:0.######},{predicted.FadingFrom?.Role},{predicted.FadingFrom?.PositionSeconds:0.######},{predicted.Current.Weight:0.#######},{Format(engineState)},{Format(predicted.Planted)},{move.X * 1000f:0.####},{move.Z * 1000f:0.####},{predictedMove.X * 1000f:0.####},{predictedMove.Y * 1000f:0.####}"));
        }

        Vector2 actorEnd = new(run.End.Origin.X, run.End.Origin.Z);
        float positionError = actorEnd.DistanceTo(prediction.EndPose.Position);
        float actorYaw = run.End.Basis.GetEuler().Y;
        float yawError = Math.Abs(Mathf.Wrap(actorYaw - prediction.EndPose.Yaw, -Mathf.Pi, Mathf.Pi));
        float travel = actorEnd.DistanceTo(new Vector2(run.Start.Origin.X, run.Start.Origin.Z));
        float turn = Mathf.Wrap(actorYaw - run.Start.Basis.GetEuler().Y, -Mathf.Pi, Mathf.Pi);
        _ = summary.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name},{prediction.Ticks.Length},{positionError * 1000f:0.####},{Mathf.RadToDeg(yawError):0.#####},{maxDeltaError * 1000f:0.####},{maxWeightError:0.########},{plantedMismatches},{clipMismatches},{blendedTicks},{blendedPhaseMismatches},{run.Collisions},{travel:0.####},{Mathf.RadToDeg(turn):0.###},{run.MapFirstOffset.Length() * 1000f:0.###},{Joins(binding, segments)}"));
        if (blendedTicks == 0 && segments.Skip(1).Any(segment => segment.CrossfadeSeconds > 0d))
        {
            failures.Add($"{label}: SETUP: no recorded tick blended two clips.");
        }
        if (positionError > EndPositionToleranceMetres || yawError > _endYawTolerance)
        {
            failures.Add(FormattableString.Invariant(
                $"{label}: end pose error {positionError * 1000f:0.####} mm / {Mathf.RadToDeg(yawError):0.#####}° exceeds 1 mm / 0.05°."));
        }

        if (run.Collisions > 0)
        {
            failures.Add($"{label}: SETUP: {run.Collisions} movement(s) collided; the oracle needs the open floor.");
        }
    }

    /// <summary>Each crossfade join as <c>out@a→in@b/d(separation)</c>.</summary>
    private static string Joins(Binding binding, SequenceSegment[] segments)
    {
        List<string> joins = [];
        for (int index = 1; index < segments.Length; index++)
        {
            SequenceSegment previous = segments[index - 1];
            SequenceSegment segment = segments[index];
            if (segment.CrossfadeSeconds <= 0d)
            {
                continue;
            }

            string separation = binding.Seams.TryEvaluateSeam(previous.Role, segment.Role, previous.ExitSeconds, segment.EntrySeconds, out SeamCandidate seam)
                ? (seam.MaxSeparation * 1000f).ToString("0.0", CultureInfo.InvariantCulture) + "mm"
                : "unaligned";
            joins.Add(string.Create(CultureInfo.InvariantCulture, $"{previous.Role}@{previous.ExitSeconds:0.###}>{segment.Role}@{segment.EntrySeconds:0.###}/{segment.CrossfadeSeconds * 1000d:0.#}ms({separation})"));
        }

        return string.Join(' ', joins);
    }

    private static string Format(PlantedFeetState? state)
        => state is { } value ? $"{(value.Left ? 'L' : '-')}{(value.Right ? 'R' : '-')}" : "?";

    private static SequencePose PoseOf(Node3D actor)
    {
        Transform3D transform = actor.GlobalTransform;
        return new SequencePose(new Vector2(transform.Origin.X, transform.Origin.Z), transform.Basis.GetEuler().Y);
    }

    /// <summary>
    /// The predictor over the NPC's own bound trajectory table (production <see cref="NPCNavigation"/> binding), the
    /// package planted intervals, the seam table built from them and the configured sad seam tolerance, with the
    /// tree's loop modes.
    /// </summary>
    private static Binding Bind(NPCNavigationRigFixture fixture)
    {
        ClipTrajectoryTable trajectories = fixture.Navigation.Trajectories
            ?? throw new InvalidOperationException("SETUP: the NPC navigation has no trajectory table.");
        // The NPC's own bound catalogue and idle catalogue (each rig's production binding), not the player's.
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
        // Continuation alias states map to their canonical roles exactly as the production binding does
        // (NPCNavigation.ActivateRuntimeBinding), so authored alias arcs are assessed at role level against the
        // planner set and the engine's per-tick state can be compared to the prediction.
        foreach ((StringName state, StandingLocomotionCapability capability) in NPCAnimationProgramme.PivotContinuationStates)
        {
            if (fixture.Driver.HasState(state))
            {
                roleOf[state.ToString()] = catalogue[capability].Role;
            }
        }
        HashSet<string> looping = [.. stateOf.Where(pair => driver.IsLooping(pair.Value)).Select(pair => pair.Key)];
        SeamToleranceConfig config = ResourceLoader.Load<SeamToleranceConfig>(SeamToleranceConfigPath)
            ?? throw new InvalidOperationException($"SETUP: seam tolerance config '{SeamToleranceConfigPath}' did not load.");
        float tolerance = config.ToSeamTolerance(catalogue.PackageVariant).ToleranceMetres;
        return new Binding(
            catalogue,
            seams,
            new SequencePredictor(seams, trajectories.Clips, looping, tolerance),
            idle.Role,
            stateOf,
            roleOf,
            looping);
    }

    /// <summary>
    /// Idle catalogue of the animation library the NPC tree's Idle state plays, as the production binding resolves it.
    /// </summary>
    private static string IdleCataloguePath(NPCNavigationRigFixture fixture)
    {
        string animation = fixture.Driver.ClipAnimation(NPCAnimationProgramme.IdleState).ToString();
        int separator = animation.IndexOf('/', StringComparison.Ordinal);
        string libraryPath = fixture.Tree.GetAnimationLibrary(separator > 0 ? animation[..separator] : string.Empty)?.ResourcePath ?? string.Empty;
        return libraryPath.EndsWith(".tres", StringComparison.Ordinal)
            ? $"{libraryPath[..^".tres".Length]}_catalogue.json"
            : throw new InvalidOperationException($"SETUP: the NPC Idle state plays '{animation}', which has no idle catalogue.");
    }

    private sealed record Binding(
        StandingLocomotionCatalogue Catalogue,
        SeamTable Seams,
        SequencePredictor Predictor,
        string IdleRole,
        IReadOnlyDictionary<string, StringName> StateOf,
        IReadOnlyDictionary<string, string> RoleOf,
        IReadOnlySet<string> Looping);

    private readonly record struct EngineTick(
        ulong PhysicsTick,
        Vector3 RootPosition,
        Quaternion RootRotation,
        string Current,
        double CurrentPosition,
        string? Fading,
        double FadingFromPosition,
        double FadingPosition,
        double FadingLength);

    private sealed class EngineRun
    {
        public int Collisions
        {
            get; set;
        }

        public Vector3 MapFirstOffset
        {
            get; set;
        }

        public List<EngineTick> Ticks { get; } = [];

        public List<Vector3> Moves { get; } = [];

        public List<PlantedFeetState?> Planted { get; } = [];

        public Transform3D Start
        {
            get; set;
        }

        public Transform3D End
        {
            get; set;
        }
    }
}
