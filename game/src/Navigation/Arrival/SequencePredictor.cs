using System.Collections.Immutable;
using System.Globalization;
using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// Exact per-tick prediction of a clip-segment sequence as the NPC runtime plays it (NAV-001 Technical Requirements 5–7
/// and 14–16): the arrival planner's model of where a candidate sequence of seams leaves the actor. It reproduces the
/// tick semantics of <see cref="NPCAnimationTreeDriver"/>, the Godot 4.7 state machine and mixer, and the
/// <see cref="NPCNavigation"/> movement operation, from the bound clips' trajectory tables.
/// </summary>
/// <remarks>
/// <para>Per tick, in the runtime's order:</para>
/// <list type="number">
/// <item>A requested transition is issued (<c>Travel → Advance(0) → seek b</c>, or <c>Start</c> for the initial
/// teleport). The entered clip is sought to its entry position and contributes no root motion on its entry tick; the
/// outgoing clip keeps playing as the fading-from state.</item>
/// <item>The tree advances by the physics delta. The crossfade position accumulates in single precision; the current
/// state weighs <c>min(1, position / length)</c> and the fading-from state the remainder, each floored at the engine
/// epsilon, and the mixer normalises the weights of each track. The crossfade ends on the tick its position reaches
/// its length (approximately), after that tick's blend.</item>
/// <item>Each clip's root motion is its position-track delta over the tick expressed in its root frame at the end of
/// the tick (<c>root_motion_local</c>), split at the loop point for looping clips; its yaw is the rotation-track
/// delta. Both are blended by the normalised weights.</item>
/// <item>The actor turns first by the blended yaw, then moves by the blended delta mapped through its rotated
/// basis (one movement operation; obstruction is not modelled).</item>
/// <item>The planted state follows the ANIM-002 Planted Combination Rule from the contributing clips' positions and
/// normalised weights; the pre-IK balls are the weight-blended root-relative ball samples.</item>
/// </list>
/// <para>
/// Joins: every crossfade must be a usable seam of a planner-set pair aligned within the seam tolerance, with that
/// pair's crossfade duration (<see cref="SeamPair.CrossfadeSeconds"/>), requested when no other crossfade is running
/// (the driver refuses otherwise). Only the first segment of a teleport start enters with a zero crossfade. Each
/// segment's playback from entry to exit must be a whole number of ticks, so its transition is requested exactly when
/// the outgoing clip reaches the seam's outgoing time. The seam is <c>(outgoing exit, incoming entry)</c>, the pairing
/// the seam table evaluates with its entry lag (<see cref="SeamTable.EntryLagSeconds"/>), so the physics delta must
/// equal that lag.
/// </para>
/// <para>
/// Thread safety: <see cref="Predict"/> and <see cref="PredictSummary"/> are not thread-safe. They re-check each join
/// with <see cref="SeamTable.TryEvaluateSeam"/>, which reuses the seam table's shared scratch buffers, and reuse the
/// predictor's own planted-state scratch buffer, so one predictor (and every other user of the same seam table) must be
/// called from one thread at a time.
/// </para>
/// </remarks>
public sealed class SequencePredictor
{
    /// <summary>The runtime physics delta (60 Hz).</summary>
    public const double DefaultPhysicsDeltaSeconds = 1d / 60d;

    /// <summary>Godot's <c>CMP_EPSILON</c>.</summary>
    private const double EngineEpsilon = 1e-5d;

    /// <summary>C# <c>Mathf.Epsilon</c>, below which the movement operation skips a rotation or translation.</summary>
    private const float MovementEpsilon = 1e-6f;

    private const double GridToleranceSeconds = 1e-5d;
    private const double CrossfadeToleranceSeconds = 1e-4d;

    private readonly SeamTable _seams;
    private readonly Dictionary<string, PredictorClip> _clips = new(StringComparer.Ordinal);
    private readonly PlantedClipContribution[] _contributions = new PlantedClipContribution[2];

    /// <summary>Binds the predictor to one rig's seam table and trajectory tables.</summary>
    /// <param name="seams">The rig's seam table; its clips are the clips a sequence may use.</param>
    /// <param name="trajectories">Bound trajectories by role, covering every seam-table clip.</param>
    /// <param name="loopingRoles">
    /// Roles whose animation loops in the runtime tree (<c>LOOP_LINEAR</c>); every other clip clamps at its end.
    /// </param>
    /// <param name="seamToleranceMetres">Seam tolerance in force.</param>
    /// <exception cref="ArgumentException">A clip has no matching trajectory, a looping role is unknown, or the tolerance is invalid.</exception>
    public SequencePredictor(
        SeamTable seams,
        IReadOnlyDictionary<string, ClipTrajectory> trajectories,
        IEnumerable<string> loopingRoles,
        float seamToleranceMetres)
    {
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(trajectories);
        ArgumentNullException.ThrowIfNull(loopingRoles);
        if (!float.IsFinite(seamToleranceMetres) || seamToleranceMetres < 0f)
        {
            throw new ArgumentException("Seam tolerance must be finite and non-negative.", nameof(seamToleranceMetres));
        }

        HashSet<string> looping = new(loopingRoles, StringComparer.Ordinal);
        foreach (string role in looping)
        {
            if (!seams.Clips.ContainsKey(role))
            {
                throw new ArgumentException($"Looping role '{role}' is not a seam-table clip.", nameof(loopingRoles));
            }
        }

        // The seam table and the runtime tree must agree on looping both ways: a seam window or a whole step across a
        // loop point exists only if the runtime clip loops there, and a clip the tree loops but the table plays once
        // would be predicted past its end differently from how the table certified its seams. Clips outside the
        // planner set have no seams (and need no NPC state).
        foreach (SeamClip clip in seams.Clips.Values)
        {
            if (seams.IsInPlannerSet(clip.Role) && clip.WrapsPlayback != looping.Contains(clip.Role))
            {
                throw new ArgumentException(
                    clip.WrapsPlayback
                        ? $"Seam clip '{clip.Role}' wraps in the seam table but does not loop in the runtime tree."
                        : $"Seam clip '{clip.Role}' loops in the runtime tree but is played once in the seam table.",
                    nameof(loopingRoles));
            }
        }

        foreach ((string role, SeamClip seam) in seams.Clips)
        {
            if (!trajectories.TryGetValue(role, out ClipTrajectory? trajectory)
                || trajectory.Identity != seam.Identity
                || Math.Abs(trajectory.DurationSeconds - seam.DurationSeconds) > 1e-6d)
            {
                throw new ArgumentException($"Seam clip '{role}' has no matching trajectory.", nameof(trajectories));
            }

            _clips[role] = new PredictorClip(seam, trajectory, looping.Contains(role));
        }

        _seams = seams;
        SeamToleranceMetres = seamToleranceMetres;
    }

    /// <summary>Seam tolerance joins are checked against, in metres.</summary>
    public float SeamToleranceMetres
    {
        get;
    }

    /// <summary>Predicts a sequence tick by tick, or rejects it.</summary>
    /// <param name="start">Start state and actor pose.</param>
    /// <param name="segments">Segments in playback order.</param>
    /// <param name="deltaSeconds">Physics delta.</param>
    public SequencePrediction Predict(SequenceStart start, IReadOnlyList<SequenceSegment> segments, double deltaSeconds = DefaultPhysicsDeltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(segments);
        if (!TryValidate(start, segments, deltaSeconds, true, out Plan[] plans, out SequenceRejection rejection))
        {
            return new SequencePrediction(start.Pose, rejection);
        }

        int last = LastTick(start, plans, out int[] entries);
        ImmutableArray<PredictedTick>.Builder ticks = ImmutableArray.CreateBuilder<PredictedTick>(Math.Max(0, last + 1));
        if (!TrySimulate(start, plans, entries, last, deltaSeconds, true, ticks, out _, out _, out rejection))
        {
            return new SequencePrediction(start.Pose, rejection);
        }

        ImmutableArray<PredictedTick> result = ticks.DrainToImmutable();
        int[] segmentEntries = start.CurrentRole is not null ? entries[1..] : entries;
        return new SequencePrediction(start.Pose, result, Windows(start.Pose, result), [.. segmentEntries]);
    }

    /// <summary>
    /// Summary-only prediction for the arrival planner's candidate evaluation: the same validation and the same
    /// per-tick model as <see cref="Predict"/>, but it keeps no per-tick records and formats no rejection details. It
    /// returns the end pose, the tick count and a tally of the support windows.
    /// </summary>
    /// <param name="start">Start state and actor pose.</param>
    /// <param name="segments">Segments in playback order.</param>
    /// <param name="deltaSeconds">Physics delta.</param>
    /// <param name="collectWindows">Whether to list the support windows (<see cref="SequenceSummary.Windows"/>).</param>
    public SequenceSummary PredictSummary(
        SequenceStart start,
        IReadOnlyList<SequenceSegment> segments,
        double deltaSeconds = DefaultPhysicsDeltaSeconds,
        bool collectWindows = false)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(segments);
        if (!TryValidate(start, segments, deltaSeconds, false, out Plan[] plans, out SequenceRejection rejection))
        {
            return SequenceSummary.Rejected(start.Pose, rejection.Reason, rejection.Segment);
        }

        int last = LastTick(start, plans, out int[] entries);
        ImmutableArray<SupportWindow>.Builder? windows = collectWindows ? ImmutableArray.CreateBuilder<SupportWindow>() : null;
        return TrySimulate(start, plans, entries, last, deltaSeconds, false, null, out SequencePose end, out SupportTally tally, out rejection, windows)
            ? new SequenceSummary(null, -1, end, last + 1, tally.SingleSwingWindows, tally.SingleSwingTicks, tally.BothSwingWindows, tally.BothSwingTicks)
            {
                Windows = windows?.ToImmutable() ?? [],
            }
            : SequenceSummary.Rejected(start.Pose, rejection.Reason, rejection.Segment);
    }

    private bool TryValidate(
        SequenceStart start,
        IReadOnlyList<SequenceSegment> segments,
        double deltaSeconds,
        bool describe,
        out Plan[] plans,
        out SequenceRejection rejection)
    {
        plans = [];
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0d)
        {
            rejection = new(SequenceRejectionReason.InvalidRequest, -1, describe ? "The physics delta must be finite and positive." : string.Empty);
            return false;
        }

        if (Math.Abs(deltaSeconds - _seams.EntryLagSeconds) > GridToleranceSeconds)
        {
            rejection = new(
                SequenceRejectionReason.InvalidRequest,
                -1,
                describe ? Invariant($"The seam table certifies seams for a {_seams.EntryLagSeconds} s physics delta, not {deltaSeconds} s.") : string.Empty);
            return false;
        }

        if (start.CurrentRole is null && segments.Count == 0)
        {
            rejection = new(SequenceRejectionReason.InvalidRequest, -1, describe ? "A teleport start needs a first segment." : string.Empty);
            return false;
        }

        return TryBuildPlans(start, segments, deltaSeconds, describe, out plans, out rejection);
    }

    private bool TryBuildPlans(
        SequenceStart start,
        IReadOnlyList<SequenceSegment> segments,
        double delta,
        bool describe,
        out Plan[] plans,
        out SequenceRejection rejection)
    {
        bool playing = start.CurrentRole is not null;
        plans = new Plan[segments.Count + (playing ? 1 : 0)];
        rejection = default;
        int offset = 0;
        if (playing)
        {
            if (!TryPlan(-1, start.CurrentRole!, start.CurrentPositionSeconds, 0d, start.ExitSeconds, start.ExitCycles, delta, describe, out plans[0], out rejection))
            {
                return false;
            }

            if (start.Fading is { } fading
                && (!_clips.ContainsKey(fading.FromRole)
                    || !float.IsFinite(fading.FadeLengthSeconds) || fading.FadeLengthSeconds <= 0f
                    || !float.IsFinite(fading.FadePositionSeconds) || fading.FadePositionSeconds < 0f
                    || !IsWithin(_clips.GetValueOrDefault(fading.FromRole), fading.FromPositionSeconds)))
            {
                rejection = new(SequenceRejectionReason.InvalidTiming, -1, describe ? $"The running crossfade from '{fading.FromRole}' is not a valid state." : string.Empty);
                return false;
            }

            offset = 1;
        }

        for (int index = 0; index < segments.Count; index++)
        {
            SequenceSegment segment = segments[index];
            if (!TryPlan(index, segment.Role, segment.EntrySeconds, segment.CrossfadeSeconds, segment.ExitSeconds, segment.ExitCycles, delta, describe, out plans[index + offset], out rejection))
            {
                return false;
            }

            bool teleport = index == 0 && !playing;
            if (teleport != (segment.CrossfadeSeconds == 0d))
            {
                rejection = new(
                    SequenceRejectionReason.InvalidCrossfade,
                    index,
                    !describe ? string.Empty : teleport ? "The initial Start enters with a zero crossfade." : "Only the initial Start may enter with a zero crossfade.");
                return false;
            }

            if (teleport)
            {
                continue;
            }

            Plan outgoing = plans[index + offset - 1];
            string outgoingRole = outgoing.Clip.Seam.Role;
            if (!_seams.TryGetPair(outgoingRole, segment.Role, out SeamPair? pair))
            {
                rejection = new(SequenceRejectionReason.NotInPlannerSet, index, describe ? $"'{outgoingRole}' → '{segment.Role}' is outside the planner set." : string.Empty);
                return false;
            }

            if (!double.IsFinite(segment.CrossfadeSeconds) || Math.Abs(segment.CrossfadeSeconds - pair!.CrossfadeSeconds) > CrossfadeToleranceSeconds)
            {
                rejection = new(
                    SequenceRejectionReason.InvalidCrossfade,
                    index,
                    describe ? Invariant($"Crossfade {segment.CrossfadeSeconds} s is not the '{outgoingRole}' → '{segment.Role}' seams' {pair!.CrossfadeSeconds} s.") : string.Empty);
                return false;
            }

            if (!_seams.TryEvaluateSeam(outgoingRole, segment.Role, outgoing.Exit, segment.EntrySeconds, out SeamCandidate candidate)
                || !pair.IsUsable(candidate)
                || !candidate.IsAlignedWithin(SeamToleranceMetres))
            {
                rejection = new(
                    SequenceRejectionReason.NotAlignedSeam,
                    index,
                    describe ? Invariant($"'{outgoingRole}'@{outgoing.Exit:0.#####} s → '{segment.Role}'@{segment.EntrySeconds:0.#####} s is not a usable seam aligned within {SeamToleranceMetres} m.") : string.Empty);
                return false;
            }
        }

        return true;
    }

    private bool TryPlan(
        int segment,
        string role,
        double entry,
        double crossfade,
        double exit,
        int cycles,
        double delta,
        bool describe,
        out Plan plan,
        out SequenceRejection rejection)
    {
        plan = default;
        rejection = default;
        if (!_clips.TryGetValue(role, out PredictorClip? clip))
        {
            rejection = new(SequenceRejectionReason.UnknownClip, segment, describe ? $"'{role}' is not a clip of this binding." : string.Empty);
            return false;
        }

        double length = clip.Length;
        if (!IsWithin(clip, entry) || !IsWithin(clip, exit) || cycles < 0 || (!clip.Loops && (cycles != 0 || exit < entry - GridToleranceSeconds)))
        {
            rejection = new(
                SequenceRejectionReason.InvalidTiming,
                segment,
                describe ? Invariant($"'{role}' cannot play from {entry} s to {exit} s with {cycles} extra cycle(s) (length {length} s, looping {clip.Loops}).") : string.Empty);
            return false;
        }

        double elapsed = PlaybackSeconds(clip, entry, exit, cycles);
        double ticks = Math.Round(elapsed / delta);
        if (Math.Abs((ticks * delta) - elapsed) > GridToleranceSeconds)
        {
            rejection = new(
                SequenceRejectionReason.OffTickGrid,
                segment,
                describe ? Invariant($"'{role}' plays {elapsed} s from entry {entry} s to exit {exit} s, not a whole number of {delta} s ticks.") : string.Empty);
            return false;
        }

        plan = new Plan(segment, clip, entry, crossfade, exit, (int)ticks);
        return true;
    }

    /// <summary>Playback from entry to exit, as <see cref="SequenceSegment.ExitCycles"/> defines it.</summary>
    private static double PlaybackSeconds(PredictorClip clip, double entry, double exit, int cycles)
    {
        if (!clip.Loops)
        {
            return Math.Max(0d, exit - entry);
        }

        double elapsed = FPosMod(exit - entry, clip.Length);
        if (clip.Length - elapsed <= GridToleranceSeconds)
        {
            elapsed = 0d;
        }

        return elapsed + (cycles * clip.Length);
    }

    private static int LastTick(SequenceStart start, Plan[] plans, out int[] entries)
    {
        entries = new int[plans.Length];
        int tick = start.CurrentRole is not null ? -1 : 0;
        for (int index = 0; index < plans.Length; index++)
        {
            entries[index] = tick;
            tick += plans[index].Ticks + 1;
        }

        return entries[^1] + plans[^1].Ticks;
    }

    private bool TrySimulate(
        SequenceStart start,
        Plan[] plans,
        int[] entries,
        int last,
        double delta,
        bool describe,
        ImmutableArray<PredictedTick>.Builder? ticks,
        out SequencePose endPose,
        out SupportTally tally,
        out SequenceRejection rejection,
        ImmutableArray<SupportWindow>.Builder? windows = null)
    {
        bool playing = start.CurrentRole is not null;
        endPose = start.Pose;
        tally = default;
        rejection = default;

        // State after the last evaluated tick.
        ClipState? current = null;
        ClipState? fading = null;
        float fadePosition = 0f;
        float fadeLength = 0f;
        if (playing)
        {
            current = new ClipState(plans[0].Clip, start.CurrentPositionSeconds, plans[0].Segment);
            if (start.Fading is { } progress)
            {
                fading = new ClipState(_clips[progress.FromRole], progress.FromPositionSeconds, -1);
                fadePosition = progress.FadePositionSeconds;
                fadeLength = progress.FadeLengthSeconds;
            }
        }

        double x = start.Pose.Position.X;
        double z = start.Pose.Position.Y;
        double yaw = start.Pose.Yaw;
        int next = playing ? 1 : 0;
        PlantedClipContribution[] contributions = _contributions;
        SupportPhase? previousPhase = null;
        int windowStart = 0;
        SequencePose windowStartPose = start.Pose;
        SequencePose previousPose = start.Pose;
        for (int index = 0; index <= last; index++)
        {
            bool entered = false;
            if (next < plans.Length && entries[next] == index)
            {
                Plan plan = plans[next];
                if (current is { } outgoing)
                {
                    // The request is made after the previous tick: the driver refuses it while a crossfade runs.
                    if (fading is not null)
                    {
                        rejection = new(
                            SequenceRejectionReason.CrossfadeStillRunning,
                            plan.Segment,
                            describe ? Invariant($"A crossfade into '{outgoing.Clip.Seam.Role}' is still running when '{plan.Clip.Seam.Role}' is requested.") : string.Empty);
                        return false;
                    }

                    Plan left = plans[next - 1];
                    if (!IsAtExit(outgoing, left.Exit))
                    {
                        rejection = new(
                            SequenceRejectionReason.OffTickGrid,
                            plan.Segment,
                            describe ? Invariant($"'{outgoing.Clip.Seam.Role}' is at {outgoing.Position} s, not at its exit {left.Exit} s, when '{plan.Clip.Seam.Role}' is requested.") : string.Empty);
                        return false;
                    }
                }

                if (plan.Crossfade > 0d)
                {
                    fading = current;
                    fadePosition = 0f;
                    fadeLength = (float)plan.Crossfade;
                }
                else
                {
                    fading = null;
                    fadePosition = 0f;
                    fadeLength = 0f;
                }

                current = new ClipState(plan.Clip, plan.Entry, plan.Segment);
                entered = true;
                next++;
            }

            ClipState active = current!.Value;

            // State-machine crossfade weights (single precision, as the engine computes them).
            float currentWeight = 1f;
            float fadingWeight = 0f;
            if (fading is not null)
            {
                (currentWeight, fadingWeight) = CrossfadeWeights(ref fadePosition, fadeLength, delta);
            }

            // Mixer normalisation over the contributing instances (non-deterministic blending).
            float total = currentWeight + (fading is not null && !IsZeroApprox(fadingWeight) ? fadingWeight : 0f);
            float currentBlend = currentWeight / total;
            float fadingBlend = fading is not null ? fadingWeight / total : 0f;

            // Advance the clips.
            ClipAdvance currentStep = entered ? Seek(active.Clip, active.Position) : Advance(active.Clip, active.Position, delta);
            active = active with
            {
                Position = currentStep.Position
            };
            ClipAdvance fadingStep = default;
            ClipState? contributing = null;
            if (fading is { } fadingState)
            {
                fadingStep = Advance(fadingState.Clip, fadingState.Position, delta);
                fading = fadingState with
                {
                    Position = fadingStep.Position
                };
                if (!IsZeroApprox(fadingBlend))
                {
                    contributing = fading;
                }
            }

            double localX = 0d;
            double localY = 0d;
            double turn = 0d;
            if (!IsZeroApprox(currentBlend))
            {
                localX += currentStep.LocalX * currentBlend;
                localY += currentStep.LocalY * currentBlend;
                turn += currentStep.Yaw * currentBlend;
            }

            if (contributing is not null)
            {
                localX += fadingStep.LocalX * fadingBlend;
                localY += fadingStep.LocalY * fadingBlend;
                turn += fadingStep.Yaw * fadingBlend;
            }

            // The crossfade ends after this tick's blend.
            if (fading is not null && IsGreaterOrEqualApprox(fadePosition, fadeLength))
            {
                fading = null;
            }

            current = active;

            // Movement operation: rotate first, then map the delta through the rotated basis.
            float appliedYaw = (float)turn;
            if (Math.Abs(appliedYaw) < MovementEpsilon)
            {
                appliedYaw = 0f;
            }

            yaw += appliedYaw;
            var rootDelta = new Vector2((float)localX, (float)localY);
            Vector2 world = SequencePose.SemanticToWorld(rootDelta, yaw);
            if (Math.Abs(world.X) < MovementEpsilon && Math.Abs(world.Y) < MovementEpsilon)
            {
                rootDelta = Vector2.Zero;
            }
            else
            {
                x += world.X;
                z += world.Y;
            }

            // Planted state from the contributing clips.
            int count = 0;
            contributions[count++] = new PlantedClipContribution(active.Clip.Seam.Intervals, active.Position, currentBlend);
            if (contributing is { } other)
            {
                contributions[count++] = new PlantedClipContribution(other.Clip.Seam.Intervals, other.Position, fadingBlend);
            }

            PlantedFeetState planted = PlantedCombinationRule.Evaluate(contributions.AsSpan(0, count));
            var pose = new SequencePose(new Vector2((float)x, (float)z), (float)yaw);
            endPose = pose;
            SupportPhase phase = SupportPhases.Of(planted);
            tally.Add(phase, phase != previousPhase);
            if (windows is not null && previousPhase is { } open && open != phase)
            {
                windows.Add(new SupportWindow(open, windowStart, index - 1, windowStartPose, previousPose));
                windowStart = index;
                windowStartPose = previousPose;
            }

            previousPhase = phase;
            previousPose = pose;
            if (windows is not null && index == last)
            {
                windows.Add(new SupportWindow(phase, windowStart, index, windowStartPose, pose));
            }
            if (ticks is null)
            {
                continue;
            }

            // Pre-IK balls: the weight-blended root-relative ball samples.
            var currentSample = new PredictedClipSample(active.Clip.Seam.Role, active.Position, currentBlend);
            PredictedClipSample? fadingSample = null;
            Vector3 leftBall = active.Clip.Trajectory.Ball(StandingLocomotionAnatomicalSide.Left, active.Position) * currentBlend;
            Vector3 rightBall = active.Clip.Trajectory.Ball(StandingLocomotionAnatomicalSide.Right, active.Position) * currentBlend;
            if (contributing is { } blended)
            {
                fadingSample = new PredictedClipSample(blended.Clip.Seam.Role, blended.Position, fadingBlend);
                leftBall += blended.Clip.Trajectory.Ball(StandingLocomotionAnatomicalSide.Left, blended.Position) * fadingBlend;
                rightBall += blended.Clip.Trajectory.Ball(StandingLocomotionAnatomicalSide.Right, blended.Position) * fadingBlend;
            }

            ticks.Add(new PredictedTick(
                index,
                active.Segment,
                entered,
                currentSample,
                fadingSample,
                rootDelta,
                appliedYaw,
                pose,
                planted,
                leftBall,
                rightBall));
        }

        return true;
    }

    /// <summary>
    /// Advances a running crossfade by one tick and returns the state machine's current and fading weights (single
    /// precision, each floored at the engine epsilon), before the mixer normalises them.
    /// </summary>
    private static (float Current, float Fading) CrossfadeWeights(ref float fadePosition, float fadeLength, double delta)
    {
        fadePosition = (float)(fadePosition + delta);
        float currentWeight = Math.Min(1f, fadePosition / fadeLength);
        currentWeight = Math.Abs(currentWeight) < (float)EngineEpsilon ? (float)EngineEpsilon : currentWeight;
        double inverse = 1d - currentWeight;
        return (currentWeight, (float)(Math.Abs(inverse) < EngineEpsilon ? EngineEpsilon : inverse));
    }

    /// <summary>
    /// Number of ticks a crossfade of <paramref name="crossfadeSeconds"/> blends, from its entry tick to the tick after
    /// whose blend it ends; the next transition may be requested after that tick.
    /// </summary>
    internal static int CrossfadeTicks(double crossfadeSeconds, double deltaSeconds)
    {
        float position = 0f;
        float length = (float)crossfadeSeconds;
        int ticks = 0;
        do
        {
            _ = CrossfadeWeights(ref position, length, deltaSeconds);
            ticks++;
        }
        while (!IsGreaterOrEqualApprox(position, length) && ticks < 100_000);

        return ticks;
    }

    /// <summary>
    /// The actor motion over one crossfade as the runtime plays it, in the actor frame when the transition is
    /// requested: the outgoing clip advances from <paramref name="outgoingSeconds"/> and the incoming clip is sought to
    /// <paramref name="incomingSeconds"/> on the entry tick, until the tick whose blend ends the crossfade. The incoming
    /// clip is then at <c>b + (ticks − 1)·Δ</c>. Obstruction and the movement epsilon are not modelled.
    /// </summary>
    internal PlanarMotion CrossfadeMotion(string outgoingRole, double outgoingSeconds, string incomingRole, double incomingSeconds, double crossfadeSeconds, double deltaSeconds)
    {
        PredictorClip outgoing = _clips[outgoingRole];
        PredictorClip incoming = _clips[incomingRole];
        double outPosition = outgoingSeconds;
        double inPosition = incomingSeconds;
        float fadePosition = 0f;
        float fadeLength = (float)crossfadeSeconds;
        PlanarMotion motion = PlanarMotion.Identity;
        bool entered = true;
        while (true)
        {
            (float currentWeight, float fadingWeight) = CrossfadeWeights(ref fadePosition, fadeLength, deltaSeconds);
            float total = currentWeight + (!IsZeroApprox(fadingWeight) ? fadingWeight : 0f);
            float currentBlend = currentWeight / total;
            float fadingBlend = fadingWeight / total;
            ClipAdvance inStep = entered ? Seek(incoming, inPosition) : Advance(incoming, inPosition, deltaSeconds);
            ClipAdvance outStep = Advance(outgoing, outPosition, deltaSeconds);
            inPosition = inStep.Position;
            outPosition = outStep.Position;
            entered = false;
            double localX = 0d;
            double localY = 0d;
            double turn = 0d;
            if (!IsZeroApprox(currentBlend))
            {
                localX += inStep.LocalX * currentBlend;
                localY += inStep.LocalY * currentBlend;
                turn += inStep.Yaw * currentBlend;
            }

            if (!IsZeroApprox(fadingBlend))
            {
                localX += outStep.LocalX * fadingBlend;
                localY += outStep.LocalY * fadingBlend;
                turn += outStep.Yaw * fadingBlend;
            }

            motion = motion.Then(PlanarMotion.Tick(localX, localY, (float)turn));
            if (IsGreaterOrEqualApprox(fadePosition, fadeLength))
            {
                return motion;
            }
        }
    }

    /// <summary>
    /// The actor motion while one clip plays alone for <paramref name="elapsedSeconds"/> from
    /// <paramref name="startSeconds"/>, in the actor frame at the start: the root displacement in the root frame at
    /// the start, split at each loop point of a looping clip (the per-tick deltas telescope exactly because the actor
    /// turns with the clip).
    /// </summary>
    internal PlanarMotion SoloMotion(string role, double startSeconds, double elapsedSeconds)
    {
        PredictorClip clip = _clips[role];
        double length = clip.Length;
        if (!clip.Loops)
        {
            double end = Math.Min(length, startSeconds + Math.Max(0d, elapsedSeconds));
            return Piece(clip, startSeconds, end);
        }

        PlanarMotion motion = PlanarMotion.Identity;
        double position = FPosMod(startSeconds, length);
        double remaining = Math.Max(0d, elapsedSeconds);
        while (remaining > GridToleranceSeconds)
        {
            double step = Math.Min(remaining, length - position);
            motion = motion.Then(Piece(clip, position, position + step));
            remaining -= step;
            position += step;
            if (length - position <= GridToleranceSeconds)
            {
                position = 0d;
            }
        }

        return motion;
    }

    private static PlanarMotion Piece(PredictorClip clip, double startTime, double endTime)
    {
        ClipTrajectory trajectory = clip.Trajectory;
        Vector2 displacement = trajectory.RootPlanarAt(endTime) - trajectory.RootPlanarAt(startTime);
        float startYaw = trajectory.YawAt(startTime);
        return PlanarMotion.InFrame(displacement, startYaw + clip.BaseYaw, trajectory.YawAt(endTime) - startYaw);
    }

    /// <summary>Whether a role loops in the bound runtime tree.</summary>
    internal bool Loops(string role) => _clips[role].Loops;

    /// <summary>Tally of the support windows of a prediction.</summary>
    private struct SupportTally
    {
        public int SingleSwingWindows;
        public int SingleSwingTicks;
        public int BothSwingWindows;
        public int BothSwingTicks;

        public void Add(SupportPhase phase, bool opensWindow)
        {
            if (phase.IsSingleSwing())
            {
                SingleSwingTicks++;
                SingleSwingWindows += opensWindow ? 1 : 0;
            }
            else if (phase == SupportPhase.BothSwing)
            {
                BothSwingTicks++;
                BothSwingWindows += opensWindow ? 1 : 0;
            }
        }
    }


    private static ImmutableArray<SupportWindow> Windows(SequencePose startPose, ImmutableArray<PredictedTick> ticks)
    {
        ImmutableArray<SupportWindow>.Builder windows = ImmutableArray.CreateBuilder<SupportWindow>();
        int first = 0;
        SequencePose before = startPose;
        for (int index = 1; index <= ticks.Length; index++)
        {
            if (index < ticks.Length && ticks[index].Phase == ticks[first].Phase)
            {
                continue;
            }

            windows.Add(new SupportWindow(ticks[first].Phase, first, index - 1, before, ticks[index - 1].Pose));
            before = ticks[index - 1].Pose;
            first = index;
        }

        return windows.ToImmutable();
    }

    /// <summary>A seek on the entry tick: the clip jumps to the position and contributes no root motion.</summary>
    private static ClipAdvance Seek(PredictorClip clip, double position)
        => new(clip.Loops ? FPosMod(position, clip.Length) : Math.Clamp(position, 0d, clip.Length), 0d, 0d, 0d);

    /// <summary>
    /// One tick of <c>AnimationNodeAnimation</c> playback and the mixer's local root motion for it, as Godot 4.7
    /// computes them: the node time advances by the delta (wrapping or clamping), and the root delta is taken from
    /// <c>time − delta</c> to <c>time</c>, split at the loop point, each piece expressed in the root frame at its end.
    /// </summary>
    private static ClipAdvance Advance(PredictorClip clip, double position, double delta)
    {
        double length = clip.Length;
        double time = position + delta;
        double step = delta;
        if (clip.Loops)
        {
            time = FPosMod(time, length);
        }
        else
        {
            if (IsGreaterApprox(time, length))
            {
                // The engine's clamp at the end of a clip played once (reproduced as written).
                step += time - length;
                time = length;
            }

            if (!IsZeroApprox(step) && IsGreaterOrEqualApprox(position, length))
            {
                step = 0d;
            }
        }

        double previous = time - step;
        if (IsLessApprox(previous, 0d))
        {
            previous = clip.Loops ? FPosMod(previous, length) : 0d;
        }

        double localX = 0d;
        double localY = 0d;
        double yaw = 0d;
        if (IsGreaterApprox(previous, time))
        {
            AddPiece(clip, previous, length, ref localX, ref localY, ref yaw);
            previous = 0d;
        }

        AddPiece(clip, previous, time, ref localX, ref localY, ref yaw);
        return new ClipAdvance(time, localX, localY, yaw);
    }

    private static void AddPiece(PredictorClip clip, double startTime, double endTime, ref double localX, ref double localY, ref double yaw)
    {
        ClipTrajectory trajectory = clip.Trajectory;
        Vector2 displacement = trajectory.RootPlanarAt(endTime) - trajectory.RootPlanarAt(startTime);
        float endYaw = trajectory.YawAt(endTime);
        Vector2 local = ClipTrajectory.RotateSemantic(displacement, -(endYaw + clip.BaseYaw));
        localX += local.X;
        localY += local.Y;
        yaw += endYaw - trajectory.YawAt(startTime);
    }

    private static bool IsWithin(PredictorClip? clip, double time)
        => clip is not null && double.IsFinite(time) && time >= -GridToleranceSeconds && time <= clip.Length + GridToleranceSeconds;

    private static bool IsAtExit(ClipState state, double exit)
    {
        double difference = Math.Abs(state.Position - exit);
        return difference <= GridToleranceSeconds
               || (state.Clip.Loops && Math.Abs(difference - state.Clip.Length) <= GridToleranceSeconds);
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static double FPosMod(double value, double length)
    {
        double result = value % length;
        if ((result < 0d && length > 0d) || (result > 0d && length < 0d))
        {
            result += length;
        }

        return result + 0d;
    }

    private static bool IsZeroApprox(double value) => Math.Abs(value) < EngineEpsilon;

    private static bool IsEqualApprox(double left, double right)
    {
        if (left == right)
        {
            return true;
        }

        double tolerance = Math.Max(EngineEpsilon * Math.Abs(left), EngineEpsilon);
        return Math.Abs(left - right) < tolerance;
    }

    private static bool IsLessApprox(double left, double right) => left < right && !IsEqualApprox(left, right);

    private static bool IsGreaterApprox(double left, double right) => left > right && !IsEqualApprox(left, right);

    private static bool IsGreaterOrEqualApprox(double left, double right) => left > right || IsEqualApprox(left, right);

    private sealed class PredictorClip(SeamClip seam, ClipTrajectory trajectory, bool loops)
    {
        public SeamClip Seam => seam;

        public ClipTrajectory Trajectory => trajectory;

        public bool Loops => loops;

        /// <summary>Animation length, as the tree reads it.</summary>
        public double Length => trajectory.DurationSeconds;

        /// <summary>Absolute root yaw of the first key: the root frame the engine's local delta is expressed in.</summary>
        public float BaseYaw { get; } = trajectory.RootYaw[0];
    }

    private readonly record struct Plan(int Segment, PredictorClip Clip, double Entry, double Crossfade, double Exit, int Ticks);

    private readonly record struct ClipState(PredictorClip Clip, double Position, int Segment);

    private readonly record struct ClipAdvance(double Position, double LocalX, double LocalY, double Yaw);
}
