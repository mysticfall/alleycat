using System.Collections.Immutable;
using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// The realised end of one exact prediction under <c>NPCNavigation</c>'s per-tick capped swing correction, as
/// <see cref="TerminalCorrectionSchedule.Simulate(SequencePrediction, ArrivalTarget, SwingCorrectionAllocator, SwingCorrectionPhaseBudget)"/> replays it.
/// </summary>
/// <param name="EndPose">Actor pose after the final predicted tick, with every capped correction applied.</param>
/// <param name="PositionErrorMetres">Planar distance from <paramref name="EndPose"/> to the target, in metres.</param>
/// <param name="YawErrorRadians">Wrapped yaw error from <paramref name="EndPose"/> to the target facing, in radians.</param>
internal readonly record struct TerminalCorrectionOutcome(
    SequencePose EndPose,
    float PositionErrorMetres,
    float YawErrorRadians)
{
    /// <summary>Whether the realised end satisfies arrival tolerances (A1: position and yaw).</summary>
    public bool ClosesWithin(float positionMetres, float yawRadians)
        => PositionErrorMetres <= positionMetres && YawErrorRadians <= yawRadians;
}

/// <summary>
/// Replays <c>NPCNavigation</c>'s arrival movement operation over an exact per-tick prediction: each tick applies the
/// predicted native root motion, then the allocator's capped correction for that tick's support window, exactly as
/// <c>NPCNavigation.ApplyMovementOnce</c> orders and applies them (rotate by the native yaw, translate by the native
/// planar delta through the rotated basis, project the endpoint through the predicted tick-to-end relative motion,
/// step the allocator, then rotate by the correction yaw and translate by the synthetic vector through the corrected
/// basis). The result is the realised end pose the schedule produces from the plan's own start pose.
/// </summary>
/// <remarks>
/// <para>
/// This is the planning-side feasibility model for a terminal continuation (CORNER-08): a candidate is admissible only
/// if this schedule — the actual per-window caps, geometry and expiry of <see cref="SwingCorrectionAllocator.Step(SwingCorrectionResidual, SupportWindow, int, ReadOnlySpan{SupportWindow}, Vector2, Vector2, SupportPhase, SwingCorrectionPhaseBudget, SwingCorrectionState)"/>
/// over the candidate's own support windows and per-tick planted balls — lands within the arrival tolerances. It is
/// strictly more faithful than <see cref="SwingCorrectionAllocator.CanClose"/>: the native per-tick poses, headings and
/// balls come from the exact prediction, so intervening native travel and heading changes are carried, and the first
/// (possibly truncated) window is budgeted exactly as the runtime budgets it.
/// </para>
/// <para>
/// Phase allowances continue across plan boundaries (CORNER-03): the first predicted window of the same physical
/// phase as the carried live budget inherits its consumed state (original grant basis and per-tick spreading
/// limits), so a truncated re-prediction of a partly spent phase is never a fresh grant; genuine later phases
/// receive fresh grants. The simulation consumes its own copy of the carried budget and charges every tick with the
/// same rule the runtime charges the live budget with, so assessment and execution share one allowance semantics.
/// </para>
/// <para>
/// Model limits, inherited from the runtime loop it mirrors: native motion is assumed to realise exactly as predicted
/// (no engine drift, no collision truncation of <c>MoveAndCollide</c> — the runtime charges the enabled proposal
/// conservatively and does not refund collision-blocked correction), the live planted phase is assumed to agree with
/// the predicted one (a mismatch refuses correction at runtime), and per-axis budget bookkeeping stays in the
/// current actor frame (heading changes within a phase are not rotated out of the axes). It is a completion
/// estimate under the legal caps, not a collision or phase-continuity guarantee; a phase observed across an
/// observation gap keeps its remaining allowance and gains nothing.
/// </para>
/// </remarks>
internal static class TerminalCorrectionSchedule
{
    /// <summary>Simulates the capped correction schedule of one exact prediction toward a target.</summary>
    /// <param name="prediction">The exact per-tick prediction of the candidate (its windows and balls drive the caps).</param>
    /// <param name="target">The destination and final facing the schedule must close to.</param>
    /// <param name="allocator">The allocator whose caps the runtime applies (the production default caps).</param>
    /// <exception cref="ArgumentException">The prediction was rejected and has no ticks.</exception>
    public static TerminalCorrectionOutcome Simulate(
        SequencePrediction prediction,
        ArrivalTarget target,
        SwingCorrectionAllocator allocator)
        => Simulate(prediction, target, allocator, continuingPhase: null);

    /// <summary>
    /// Simulates the capped correction schedule of one exact prediction toward a target, continuing an ongoing
    /// physical support phase's remaining allowance (CORNER-03): the first predicted window of the same phase
    /// inherits <paramref name="continuingPhase"/>'s consumed state instead of a fresh grant, every later genuine
    /// phase receives its normal fresh grant, and the simulation consumes its OWN copy of the carried budget — the
    /// seed instance is never mutated, so repeated or parallel candidate assessments stay pure.
    /// </summary>
    /// <param name="prediction"></param>
    /// <param name="target"></param>
    /// <param name="allocator"></param>
    /// <param name="continuingPhase">
    /// The live physical phase's committed budget at the tick this prediction would start from, or null when no
    /// allowance has been established (a cold start; the first covering window then establishes the basis).
    /// </param>
    public static TerminalCorrectionOutcome Simulate(
        SequencePrediction prediction,
        ArrivalTarget target,
        SwingCorrectionAllocator allocator,
        SwingCorrectionPhaseBudget? continuingPhase)
        => Simulate(prediction, target, allocator, continuingPhase, composedPositions: null);

    /// <summary>
    /// Simulates the capped correction schedule of one exact prediction toward a target, additionally recording
    /// every composed world position the operation realises: the start pose followed by each tick's pose after its
    /// native root motion and capped correction, in the same order the runtime applies them. The recorded samples
    /// are the composed trajectory a caller must check against the navigable surface (poses plus consecutive
    /// midpoints), never the native prediction alone.
    /// </summary>
    /// <param name="prediction">The exact per-tick prediction of the candidate (its windows and balls drive the caps).</param>
    /// <param name="target">The destination the schedule must close to.</param>
    /// <param name="allocator">The allocator whose caps the runtime applies (the production default caps).</param>
    /// <param name="continuingPhase">
    /// The live physical phase's committed budget at the tick this prediction would start from, or null when no
    /// allowance has been established (a cold start; the first covering window then establishes the basis).
    /// </param>
    /// <param name="composedPositions">
    /// Optional collector for the composed per-tick world planar positions; the schedule only appends to it and
    /// never retains it.
    /// </param>
    public static TerminalCorrectionOutcome Simulate(
        SequencePrediction prediction,
        ArrivalTarget target,
        SwingCorrectionAllocator allocator,
        SwingCorrectionPhaseBudget? continuingPhase,
        List<Vector2>? composedPositions)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        ArgumentNullException.ThrowIfNull(allocator);
        if (!prediction.IsValid)
        {
            throw new ArgumentException("A rejected prediction has no correction schedule.", nameof(prediction));
        }

        ImmutableArray<PredictedTick> ticks = prediction.Ticks;
        if (ticks.IsEmpty)
        {
            composedPositions?.Add(prediction.StartPose.Position);
            return Outcome(prediction.StartPose, target);
        }

        ImmutableArray<SupportWindow> windows = prediction.Windows;
        int windowIndex = 0;
        SwingCorrectionPhaseBudget? phase = null;
        Vector2 position = prediction.StartPose.Position;
        float yaw = prediction.StartPose.Yaw;
        composedPositions?.Add(position);
        for (int tick = 0; tick < ticks.Length; tick++)
        {
            PredictedTick step = ticks[tick];
            // Native movement operation: rotate first, then the planar delta through the rotated basis.
            float yawAfterRoot = yaw + step.YawDelta;
            position += SequencePose.SemanticToWorld(step.RootDelta, yawAfterRoot);

            while (tick > windows[windowIndex].LastTick)
            {
                windowIndex++;
            }

            // Phase budgeting, as the runtime tracks it: the ongoing phase continues its remaining allowance; a
            // genuine phase change receives a fresh grant from the window that covers it; an ineligible phase
            // (double support) has no budget and no correction.
            SupportPhase livePhase = step.Phase;
            if (phase is null || phase.Phase != livePhase)
            {
                phase = livePhase == windows[windowIndex].Phase
                    ? SwingCorrectionPhaseBudget.ForWindow(windows[windowIndex], allocator.Caps)
                    : null;
            }

            if (phase is not null && tick == 0 && continuingPhase is { } seed && seed.Phase == phase.Phase)
            {
                phase = seed;
            }

            // The endpoint residual, projected through this tick's predicted remaining motion, as the runtime
            // recomputes it every tick from the realising actor transform.
            Transform3D afterRoot = new(new Basis(Vector3.Up, yawAfterRoot), new Vector3(position.X, 0f, position.Y));
            SwingCorrectionResidual residual = Residual(
                afterRoot, step.Pose, prediction.EndPose, target, out Vector2 endpointOffset);
            Vector3 plantedBall = livePhase == SupportPhase.LeftSwing ? step.RightBall
                : livePhase == SupportPhase.RightSwing ? step.LeftBall : Vector3.Zero;
            SwingCorrection correction = phase is null
                ? allocator.Step(
                    residual,
                    windows[windowIndex],
                    tick,
                    windows.AsSpan()[(windowIndex + 1)..],
                    new Vector2(plantedBall.X, -plantedBall.Z),
                    endpointOffset,
                    livePhase)
                : allocator.Step(
                    residual,
                    windows[windowIndex],
                    tick,
                    windows.AsSpan()[(windowIndex + 1)..],
                    new Vector2(plantedBall.X, -plantedBall.Z),
                    endpointOffset,
                    livePhase,
                    phase);

            // The simulation charges its own copy under the same rule the runtime charges the live budget with.
            phase = phase?.Charge(correction);

            // Correction: rotate by the yaw, then the synthetic vector through the corrected basis.
            yaw = yawAfterRoot + correction.Yaw;
            position += SequencePose.SemanticToWorld(correction.OriginShift + correction.Planar, yaw);
            composedPositions?.Add(position);
        }

        return Outcome(new SequencePose(position, yaw), target);
    }

    /// <summary>The runtime's per-tick residual: the endpoint this tick is heading to, projected from the realising
    /// transform through the predicted tick-to-end relative motion, with the remaining target error in its frame.</summary>
    private static SwingCorrectionResidual Residual(
        Transform3D actual,
        SequencePose predicted,
        SequencePose end,
        ArrivalTarget target,
        out Vector2 endpointOffset)
    {
        static Transform3D Transform(SequencePose pose)
        {
            return new(new Basis(Vector3.Up, pose.Yaw), new Vector3(pose.Position.X, 0f, pose.Position.Y));
        }

        Transform3D projected = actual * Transform(predicted).AffineInverse() * Transform(end);
        Vector3 offset = actual.Basis.Inverse() * (projected.Origin - actual.Origin);
        endpointOffset = new Vector2(offset.X, -offset.Z);
        Vector3 error = actual.Basis.Inverse() * (new Vector3(target.Position.X, 0f, target.Position.Y) - projected.Origin);
        return new SwingCorrectionResidual(new Vector2(error.X, -error.Z), Mathf.AngleDifference(NPCNavigationRoutePlan.YawOf(projected.Basis), target.Yaw));
    }

    private static TerminalCorrectionOutcome Outcome(SequencePose end, ArrivalTarget target) => new(
        end,
        end.Position.DistanceTo(target.Position),
        Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(end.Yaw - target.Yaw)));
}
