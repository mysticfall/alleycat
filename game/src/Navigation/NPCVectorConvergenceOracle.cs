using System.Collections.Immutable;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

public enum NPCVectorConvergenceInfeasibility
{
    None,
    NonFiniteInput,
    InvalidOccurrence,
    InvalidInterval,
    DirectionallyUnreachable,
    YawUnreachable,
}

/// <summary>
/// Optional realised correction evidence for one interval. Unlike a scalar scale, the realised vector may be shorter,
/// redirected, or zero. When present, requested = realised + collision remainder must hold component-wise.
/// </summary>
public readonly record struct NPCVectorConvergenceRealisation(
    bool HasEvidence,
    Vector2 RealisedCorrectionWorld,
    Vector2 CollisionRemainderWorld,
    float RealisedYawCorrection,
    float YawRemainder);

/// <summary>One exact <c>(PreviousTick, CurrentTick]</c> moving interval.</summary>
public readonly record struct NPCVectorConvergenceInterval(
    long PreviousTick,
    long CurrentTick,
    float DeltaSeconds,
    Vector2 AuthoredRootPlanar,
    float ActorYawRadians,
    float AuthoredYawRadians = 0f,
    NPCVectorConvergenceRealisation Realisation = default,
    bool AuthoredRootIsActorLocal = false);

public readonly record struct NPCVectorConvergenceConstraints(
    float MaximumSpeed,
    float MaximumAcceleration,
    float MaximumJerk,
    float TaperAcceleration,
    float EndpointTolerance,
    float YawTolerance = NPCYawConvergence.NumericalToleranceRadians);

/// <summary>
/// One bounded f2i correction feasibility request over caller-owned exact interval buffers. The lead-in window is
/// model input only; correction is owned exclusively by the moving crossfade/f2i window.
/// </summary>
public readonly record struct NPCVectorConvergenceRequest(
    Vector2 CurrentPlanarPose,
    Vector2 TargetPlanarPose,
    float CurrentYawRadians,
    float TargetYawRadians,
    NPCReleaseOccurrence Occurrence,
    double ExactUnwrappedTarget,
    IReadOnlyList<NPCVectorConvergenceInterval> LeadInIntervals,
    IReadOnlyList<NPCVectorConvergenceInterval> MovingIntervals,
    Vector2 CurrentCorrectionVelocity,
    Vector2 CurrentCorrectionAcceleration,
    float CurrentAppliedYawRate,
    NPCVectorConvergenceConstraints Constraints);

/// <summary>Diagnostic-only exact evidence for one owned moving interval.</summary>
public readonly record struct NPCVectorConvergenceStep(
    long PreviousTick,
    long CurrentTick,
    Vector2 AuthoredRootWorld,
    float AuthoredYaw,
    // Preserved as a zero-valued diagnostic column. Contact correction is deliberately excluded from NAV planning.
    Vector2 ContactWorld,
    float ContactYaw,
    Vector2 RequestedCorrection,
    Vector2 ReachableCorrection,
    Vector2 CollisionRemainder,
    float RequestedYawCorrection,
    float ReachableYawCorrection,
    float YawRemainder,
    Vector2 CorrectionVelocity,
    Vector2 CorrectionAcceleration,
    float AppliedYawRate,
    NPCYawConvergenceStepTrace YawConvergenceTrace,
    float PredictedYawAfter);

/// <summary>Aggregated authored and realised totals for one contiguous request window.</summary>
public readonly record struct NPCVectorConvergenceWindowAllocation(
    int IntervalCount,
    long FirstPreviousTick,
    long LastCurrentTick,
    Vector2 AuthoredRootWorld,
    float AuthoredYaw,
    // Preserved as a zero-valued diagnostic column. Contact correction is deliberately excluded from NAV planning.
    Vector2 ContactWorld,
    float ContactYaw,
    Vector2 RequestedCorrection,
    Vector2 ReachableCorrection,
    Vector2 CollisionRemainder,
    float RequestedYawCorrection,
    float ReachableYawCorrection,
    float YawRemainder);

public readonly record struct NPCVectorConvergenceResult(
    Vector2 RequiredCorrection,
    Vector2 VectorReachableCorrection,
    float RequiredYawCorrection,
    float ReachableYawCorrection,
    NPCVectorConvergenceWindowAllocation LeadIn,
    NPCVectorConvergenceWindowAllocation Moving,
    ImmutableArray<NPCVectorConvergenceStep> Schedule,
    Vector2 PredictedEndpointResidual,
    float PredictedEndpointYawResidual,
    NPCVectorConvergenceInfeasibility FirstInfeasibilityReason,
    int LeadInIntervalCount,
    int MovingIntervalCount)
{
    public bool PositionFeasible => PredictedEndpointResidual.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold;
    public bool YawFeasible => Mathf.Abs(PredictedEndpointYawResidual) <= NPCYawConvergence.NumericalToleranceRadians;
    public bool IsFeasible => FirstInfeasibilityReason == NPCVectorConvergenceInfeasibility.None
        && PositionFeasible
        && YawFeasible;
    public int TotalIntervalCount => LeadInIntervalCount + MovingIntervalCount;
}

public struct NPCVectorConvergenceStepState(Vector2 velocity, Vector2 acceleration, float appliedYawRate = 0f)
{
    public Vector2 Velocity = velocity;
    public Vector2 Acceleration = acceleration;
    public float AppliedYawRate = appliedYawRate;
}

/// <summary>
/// Pure bounded f2i correction feasibility simulation and stepping mathematics; no node access or mutation.
/// </summary>
public static class NPCVectorConvergenceOracle
{
    public const float MaximumCorrectionSpeed = 0.5f;
    private const float RealisationIdentityTolerance = 0.000001f;

    /// <summary>
    /// Runs the bounded f2i correction feasibility simulation. Pass <paramref name="captureSchedule" /> as
    /// <see langword="false" /> on per-frame paths: the diagnostic schedule is the only allocation this model makes.
    /// </summary>
    public static NPCVectorConvergenceResult Evaluate(in NPCVectorConvergenceRequest request, bool captureSchedule = true)
    {
        if (!IsFinite(request))
        {
            return Invalid(NPCVectorConvergenceInfeasibility.NonFiniteInput);
        }
        if (!request.Occurrence.IsValid
            || Math.Abs(request.Occurrence.UnwrappedPhase - request.ExactUnwrappedTarget) > 0.0000001d)
        {
            return Invalid(NPCVectorConvergenceInfeasibility.InvalidOccurrence);
        }
        if (!HasContiguousLayout(request.LeadInIntervals, request.MovingIntervals))
        {
            return Invalid(NPCVectorConvergenceInfeasibility.InvalidInterval);
        }

        ImmutableArray<NPCVectorConvergenceStep>.Builder? schedule = captureSchedule
            ? ImmutableArray.CreateBuilder<NPCVectorConvergenceStep>()
            : null;
        NPCVectorConvergenceStepState state = new(
            request.CurrentCorrectionVelocity,
            request.CurrentCorrectionAcceleration,
            request.CurrentAppliedYawRate);
        Vector2 totalAuthored = SumAuthoredPlanar(request);
        float totalAuthoredYaw = SumAuthoredYaw(request);
        Vector2 required = request.TargetPlanarPose - request.CurrentPlanarPose - totalAuthored;
        float requiredYaw = NPCNavigationRoutePlan.WrapAngle(
            request.TargetYawRadians - request.CurrentYawRadians - totalAuthoredYaw);
        Vector2 remaining = required;
        float currentYaw = request.CurrentYawRadians;
        Vector2 reachable = Vector2.Zero;
        float reachableYaw = 0f;
        bool realisationEvidenceConsistent = true;

        NPCVectorConvergenceWindowAllocation leadIn = SimulateLeadIn(
            request.LeadInIntervals, ref currentYaw);
        NPCVectorConvergenceWindowAllocation moving = SimulateMoving(
            request.MovingIntervals,
            request,
            ref state,
            ref remaining,
            ref currentYaw,
            ref reachable,
            ref reachableYaw,
            ref realisationEvidenceConsistent,
            schedule!);

        float yawResidual = NPCNavigationRoutePlan.WrapAngle(request.TargetYawRadians - currentYaw);
        NPCVectorConvergenceInfeasibility reason = NPCVectorConvergenceInfeasibility.None;
        if (!realisationEvidenceConsistent)
        {
            reason = NPCVectorConvergenceInfeasibility.InvalidInterval;
        }
        else if (remaining.Length() > request.Constraints.EndpointTolerance)
        {
            reason = NPCVectorConvergenceInfeasibility.DirectionallyUnreachable;
        }
        else if (Mathf.Abs(yawResidual) > request.Constraints.YawTolerance)
        {
            reason = NPCVectorConvergenceInfeasibility.YawUnreachable;
        }

        return new(
            required,
            reachable,
            requiredYaw,
            reachableYaw,
            leadIn,
            moving,
            schedule?.ToImmutable() ?? [],
            remaining,
            yawResidual,
            reason,
            leadIn.IntervalCount,
            moving.IntervalCount);
    }

    /// <summary>Shared allocation-free planar stepping primitive.</summary>
    public static Vector2 Step(
        ref NPCVectorConvergenceStepState state,
        Vector2 remainingCorrection,
        float deltaSeconds,
        in NPCVectorConvergenceRealisation realisation,
        in NPCVectorConvergenceConstraints constraints,
        out Vector2 requestedCorrection,
        out Vector2 collisionRemainder,
        float remainingDeliverySeconds = float.PositiveInfinity,
        float deadlinePaceMultiplier = 1f)
    {
        float distance = remainingCorrection.Length();
        float taperedSpeed = distance > 0f ? Mathf.Sqrt(2f * constraints.TaperAcceleration * distance) : 0f;
        // Deadline pacing: inside a finite window the correction must complete with the final consumed interval.
        // A pure deceleration taper spends the vector early and parks the correction velocity at zero while
        // authored intervals still remain, so authored modelling drift absorbed in the closing intervals can no
        // longer be spent. Pacing the target at the remaining vector over the remaining delivery time keeps the
        // correction velocity alive through the final interval; the taper still parks it once the deadline is
        // exhausted. The multiplier lets the caller front-load a persistent realised deficit — a cancelling
        // contact stream or collision clipping — so the schedule still closes inside the window.
        float deadlineSpeed = float.IsFinite(remainingDeliverySeconds) && remainingDeliverySeconds > 0f
            ? distance / remainingDeliverySeconds * deadlinePaceMultiplier
            : float.PositiveInfinity;
        float speed = Mathf.Clamp(Mathf.Min(taperedSpeed, deadlineSpeed), 0f, constraints.MaximumSpeed);
        bool finalOwnedInterval = float.IsFinite(remainingDeliverySeconds)
            && remainingDeliverySeconds <= deltaSeconds + 0.000001f;
        if (finalOwnedInterval && state.Velocity.Dot(remainingCorrection) > 0f)
        {
            // Final owned interval with the correction velocity already tracking the remaining vector: the
            // window closes on a fixed clip boundary, so the step parks exactly on the remaining vector — the
            // planar counterpart of the terminal yaw rotation's exact park. A velocity-ramped step would strand
            // whatever authored modelling drift the closing intervals absorbed. An opposing velocity keeps the
            // dynamic step: the envelope never reverses a correction instantaneously.
            requestedCorrection = ClampToRemaining(
                remainingCorrection.LimitLength(constraints.MaximumSpeed * deltaSeconds),
                remainingCorrection);
            state.Velocity = requestedCorrection / Math.Max(deltaSeconds, float.Epsilon);
            state.Acceleration = Vector2.Zero;
        }
        else
        {
            Vector2 targetVelocity = distance > constraints.EndpointTolerance
                ? remainingCorrection / distance * speed
                : Vector2.Zero;
            state.Velocity = NPCTransitionContinuity.StepVelocityCommand(
                    state.Velocity,
                    targetVelocity,
                    ref state.Acceleration,
                    deltaSeconds,
                    constraints.MaximumAcceleration,
                    constraints.MaximumJerk)
                .LimitLength(constraints.MaximumSpeed);
            requestedCorrection = ClampToRemaining(state.Velocity * deltaSeconds, remainingCorrection);
        }

        if (realisation.HasEvidence)
        {
            collisionRemainder = realisation.CollisionRemainderWorld;
            return realisation.RealisedCorrectionWorld;
        }

        collisionRemainder = Vector2.Zero;
        return requestedCorrection;
    }

    /// <summary>
    /// Advances authored lead-in identity without warming synthetic convergence state: the first f2i transition
    /// interval opens both correction windows from rest.
    /// </summary>
    private static NPCVectorConvergenceWindowAllocation SimulateLeadIn(
        IReadOnlyList<NPCVectorConvergenceInterval> intervals,
        ref float currentYaw)
    {
        Vector2 authored = Vector2.Zero;
        float authoredYaw = 0f;
        int count = 0;
        long first = 0;
        long last = 0;
        for (int index = 0; index < intervals.Count; index++)
        {
            NPCVectorConvergenceInterval interval = intervals[index];
            authored += ResolveAuthoredRoot(interval, currentYaw);
            authoredYaw += interval.AuthoredYawRadians;
            currentYaw = NPCYawConvergence.RealiseRuntimeYaw(currentYaw, interval.AuthoredYawRadians);
            if (count == 0)
            {
                first = interval.PreviousTick;
            }

            last = interval.CurrentTick;
            count++;
        }

        return new(
            count,
            first,
            last,
            authored,
            authoredYaw,
            Vector2.Zero,
            0f,
            Vector2.Zero,
            Vector2.Zero,
            Vector2.Zero,
            0f,
            0f,
            0f);
    }

    private static NPCVectorConvergenceWindowAllocation SimulateMoving(
        IReadOnlyList<NPCVectorConvergenceInterval> intervals,
        in NPCVectorConvergenceRequest request,
        ref NPCVectorConvergenceStepState state,
        ref Vector2 remaining,
        ref float currentYaw,
        ref Vector2 totalReachable,
        ref float totalReachableYaw,
        ref bool realisationEvidenceConsistent,
        ImmutableArray<NPCVectorConvergenceStep>.Builder? schedule)
    {
        Vector2 authored = Vector2.Zero;
        float authoredYaw = 0f;
        Vector2 requested = Vector2.Zero;
        Vector2 reachable = Vector2.Zero;
        Vector2 remainder = Vector2.Zero;
        float requestedYaw = 0f;
        float reachableYaw = 0f;
        float yawRemainder = 0f;
        int count = 0;
        long first = 0;
        long last = 0;

        for (int index = 0; index < intervals.Count; index++)
        {
            NPCVectorConvergenceInterval interval = intervals[index];
            Vector2 root = ResolveAuthoredRoot(interval, currentYaw);
            authored += root;
            authoredYaw += interval.AuthoredYawRadians;
            if (count == 0)
            {
                first = interval.PreviousTick;
            }

            last = interval.CurrentTick;
            Vector2 realisedStep = Step(
                ref state,
                remaining,
                interval.DeltaSeconds,
                interval.Realisation,
                request.Constraints,
                out Vector2 requestedStep,
                out Vector2 remainderStep,
                (intervals.Count - index) * interval.DeltaSeconds);
            if (interval.Realisation.HasEvidence
                && (requestedStep - realisedStep - remainderStep).Length() > RealisationIdentityTolerance)
            {
                realisationEvidenceConsistent = false;
            }

            remaining -= realisedStep;
            requested += requestedStep;
            reachable += realisedStep;
            remainder += remainderStep;
            totalReachable += realisedStep;

            float totalYawStep = NPCYawConvergence.StepTerminalRotation(
                currentYaw,
                request.TargetYawRadians,
                ref state.AppliedYawRate,
                interval.DeltaSeconds,
                out NPCYawConvergenceStepTrace yawTrace);
            float requestedYawStep = NPCNavigationRoutePlan.WrapAngle(
                totalYawStep - interval.AuthoredYawRadians);
            float realisedYawStep = interval.Realisation.HasEvidence
                ? interval.Realisation.RealisedYawCorrection
                : requestedYawStep;
            float yawRemainderStep = interval.Realisation.HasEvidence
                ? interval.Realisation.YawRemainder
                : 0f;
            if (interval.Realisation.HasEvidence
                && Mathf.Abs(requestedYawStep - realisedYawStep - yawRemainderStep) > RealisationIdentityTolerance)
            {
                realisationEvidenceConsistent = false;
            }

            float realisedTotalYawStep = interval.Realisation.HasEvidence
                ? NPCNavigationRoutePlan.WrapAngle(
                    interval.AuthoredYawRadians + realisedYawStep)
                : totalYawStep;
            currentYaw = NPCYawConvergence.RealiseRuntimeYaw(currentYaw, realisedTotalYawStep);
            requestedYaw += requestedYawStep;
            reachableYaw += realisedYawStep;
            yawRemainder += yawRemainderStep;
            totalReachableYaw += realisedYawStep;
            count++;
            schedule?.Add(new(
                interval.PreviousTick,
                interval.CurrentTick,
                root,
                interval.AuthoredYawRadians,
                Vector2.Zero,
                0f,
                requestedStep,
                realisedStep,
                remainderStep,
                requestedYawStep,
                realisedYawStep,
                yawRemainderStep,
                state.Velocity,
                state.Acceleration,
                state.AppliedYawRate,
                yawTrace,
                currentYaw));
        }

        return new(
            count,
            first,
            last,
            authored,
            authoredYaw,
            Vector2.Zero,
            0f,
            requested,
            reachable,
            remainder,
            requestedYaw,
            reachableYaw,
            yawRemainder);
    }

    private static bool HasContiguousLayout(
        IReadOnlyList<NPCVectorConvergenceInterval> leadIn,
        IReadOnlyList<NPCVectorConvergenceInterval> moving)
    {
        return IsContiguous(leadIn)
            && IsContiguous(moving)
            && (leadIn.Count == 0
                || moving.Count == 0
                || moving[0].PreviousTick == leadIn[^1].CurrentTick);
    }

    private static bool IsContiguous(IReadOnlyList<NPCVectorConvergenceInterval> intervals)
    {
        for (int index = 0; index < intervals.Count; index++)
        {
            NPCVectorConvergenceInterval interval = intervals[index];
            if (interval.CurrentTick != interval.PreviousTick + 1)
            {
                return false;
            }

            if (index > 0 && interval.PreviousTick != intervals[index - 1].CurrentTick)
            {
                return false;
            }
        }

        return true;
    }

    private static Vector2 SumAuthoredPlanar(in NPCVectorConvergenceRequest request)
    {
        Vector2 total = Vector2.Zero;
        float currentYaw = request.CurrentYawRadians;
        // The lead-in advances through its projected authored yaw commands — the same schedule SimulateLeadIn
        // applies. Synthesising an independent terminal-rotation schedule here rotates the exit travel by a turn
        // the plan neither predicts nor executes.
        for (int index = 0; index < request.LeadInIntervals.Count; index++)
        {
            NPCVectorConvergenceInterval interval = request.LeadInIntervals[index];
            total += ResolveAuthoredRoot(interval, currentYaw);
            currentYaw = NPCYawConvergence.RealiseRuntimeYaw(currentYaw, interval.AuthoredYawRadians);
        }

        // The moving window rotates exactly as its simulation does: terminal rotation owns the exit yaw.
        float appliedYawRate = request.CurrentAppliedYawRate;
        for (int index = 0; index < request.MovingIntervals.Count; index++)
        {
            NPCVectorConvergenceInterval interval = request.MovingIntervals[index];
            total += ResolveAuthoredRoot(interval, currentYaw);
            float totalYawStep = NPCYawConvergence.StepTerminalRotation(
                currentYaw,
                request.TargetYawRadians,
                ref appliedYawRate,
                interval.DeltaSeconds);
            currentYaw = NPCYawConvergence.RealiseRuntimeYaw(currentYaw, totalYawStep);
        }

        return total;
    }

    private static float SumAuthoredYaw(in NPCVectorConvergenceRequest request)
    {
        float total = 0f;
        Add(request.LeadInIntervals);
        Add(request.MovingIntervals);
        return total;

        void Add(IReadOnlyList<NPCVectorConvergenceInterval> intervals)
        {
            for (int index = 0; index < intervals.Count; index++)
            {
                total += intervals[index].AuthoredYawRadians;
            }
        }
    }

    public static Vector2 ResolveAuthoredRoot(in NPCVectorConvergenceInterval interval, float currentActorYaw)
        => interval.AuthoredRootIsActorLocal
            ? new Vector2(interval.AuthoredRootPlanar.X, -interval.AuthoredRootPlanar.Y).Rotated(-currentActorYaw)
            : interval.AuthoredRootPlanar;

    private static Vector2 ClampToRemaining(Vector2 proposed, Vector2 remaining)
        => proposed.Dot(remaining) <= 0f
            ? Vector2.Zero
            : proposed.LengthSquared() > remaining.LengthSquared() ? remaining : proposed;

    private static bool IsFinite(in NPCVectorConvergenceRequest request)
        => request.LeadInIntervals is not null
            && request.MovingIntervals is not null
            && request.CurrentPlanarPose.IsFinite()
            && request.TargetPlanarPose.IsFinite()
            && float.IsFinite(request.CurrentYawRadians)
            && float.IsFinite(request.TargetYawRadians)
            && double.IsFinite(request.ExactUnwrappedTarget)
            && request.CurrentCorrectionVelocity.IsFinite()
            && request.CurrentCorrectionAcceleration.IsFinite()
            && float.IsFinite(request.CurrentAppliedYawRate)
            && IsFinite(request.Constraints)
            && AllFinite(request.LeadInIntervals)
            && AllFinite(request.MovingIntervals);

    private static bool IsFinite(in NPCVectorConvergenceConstraints value)
        => float.IsFinite(value.MaximumSpeed) && value.MaximumSpeed >= 0f
            && float.IsFinite(value.MaximumAcceleration) && value.MaximumAcceleration >= 0f
            && float.IsFinite(value.MaximumJerk) && value.MaximumJerk >= 0f
            && float.IsFinite(value.TaperAcceleration) && value.TaperAcceleration >= 0f
            && float.IsFinite(value.EndpointTolerance) && value.EndpointTolerance >= 0f
            && float.IsFinite(value.YawTolerance) && value.YawTolerance >= 0f;

    private static bool AllFinite(IReadOnlyList<NPCVectorConvergenceInterval> intervals)
    {
        for (int index = 0; index < intervals.Count; index++)
        {
            NPCVectorConvergenceInterval interval = intervals[index];
            NPCVectorConvergenceRealisation realisation = interval.Realisation;
            if (!float.IsFinite(interval.DeltaSeconds) || interval.DeltaSeconds <= 0f
                || !interval.AuthoredRootPlanar.IsFinite()
                || !float.IsFinite(interval.ActorYawRadians)
                || !float.IsFinite(interval.AuthoredYawRadians)
                || (realisation.HasEvidence
                    && (!realisation.RealisedCorrectionWorld.IsFinite()
                        || !realisation.CollisionRemainderWorld.IsFinite()
                        || !float.IsFinite(realisation.RealisedYawCorrection)
                        || !float.IsFinite(realisation.YawRemainder))))
            {
                return false;
            }
        }

        return true;
    }

    private static NPCVectorConvergenceResult Invalid(NPCVectorConvergenceInfeasibility reason)
    {
        NPCVectorConvergenceWindowAllocation leadIn = default;
        NPCVectorConvergenceWindowAllocation moving = default;
        return new(
            Vector2.Zero,
            Vector2.Zero,
            0f,
            0f,
            leadIn,
            moving,
            [],
            Vector2.Zero,
            0f,
            reason,
            0,
            0);
    }
}
