using System.Collections.Immutable;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

public enum NPCVectorConvergenceExecutionStatus
{
    Inactive,
    Moving,
    Complete,
    Rejected,
}

public enum NPCVectorConvergenceResetReason
{
    None,
    OccurrenceExpired,
    Cancellation,
    RouteReplacement,
    DestinationCleared,
    ClockGenerationChanged,
}

public readonly record struct NPCVectorConvergenceExecutionLedger(
    NPCReleaseOccurrence Occurrence,
    Vector2 PlannedCorrection,
    Vector2 RequestedCorrection,
    Vector2 RealisedCorrection,
    Vector2 CollisionRemainder,
    Vector2 RemainingCorrection,
    int OwnedIntervalCount,
    NPCVectorConvergenceExecutionStatus Status);

/// <summary>
/// Fixed-window executor for terminal f2i convergence. The moving interval array is installed once at transition
/// commitment, consumed monotonically, and can never be extended after collision clipping. There is no pre-commit
/// state: feasibility belongs to the bounded imminent candidate check that precedes commitment.
/// </summary>
public sealed class NPCVectorConvergenceExecutor
{
    private const float IdentityTolerance = 0.000001f;
    /// <summary>Exponential smoothing retained on the previous cancellation-velocity sample per moving step.</summary>
    private const float CancellationSmoothing = 0.7f;
    private NPCVectorConvergenceStepState _stepState;
    private NPCVectorConvergenceConstraints _constraints;
    private ImmutableArray<NPCVectorConvergenceInterval> _movingIntervals = [];
    private Vector2 _planned;
    private Vector2 _remaining;
    private Vector2 _requested;
    private Vector2 _realised;
    private Vector2 _collisionRemainder;
    private Vector2 _pendingRequested;
    private Vector2 _cancellationVelocity;
    private float _targetYaw;
    private int _movingIntervalIndex;
    private int _movingIntervalCount;
    private int _ownedIntervalCount;

    public NPCReleaseOccurrence Occurrence
    {
        get; private set;
    }
    public NPCVectorConvergenceExecutionStatus Status
    {
        get; private set;
    }
    public bool HasPendingRequest
    {
        get; private set;
    }
    public bool IsFinalIntervalPending => HasPendingRequest && _movingIntervalIndex == _movingIntervalCount;
    public NPCVectorConvergenceStepState CurrentStepState => _stepState;
    public Vector2 LastPlannedAuthoredRoot
    {
        get; private set;
    }
    public Vector2 LastRealisedAuthoredRoot
    {
        get; private set;
    }
    public float LastPlannedAuthoredYaw
    {
        get; private set;
    }
    public float LastRequestedYawCorrection
    {
        get; private set;
    }
    public bool LastRealisationCollided
    {
        get; private set;
    }
    public NPCVectorConvergenceResetReason LastResetReason
    {
        get; private set;
    }

    public NPCVectorConvergenceExecutionLedger Ledger => new(
        Occurrence,
        _planned,
        _requested,
        _realised,
        _collisionRemainder,
        _remaining,
        _ownedIntervalCount,
        Status);

    /// <summary>
    /// Opens correction at f2i commitment from the measured actor pose and only the remaining crossfade/full-clip
    /// model. This is the sole operation that installs a moving authority window. The window's leading interval may
    /// be an already-evaluated commitment root that the crossing frame's ordinary authored move consumes without a
    /// correction; <paramref name="firstConsumedIntervalIndex" /> excludes such leading intervals from executor
    /// consumption while keeping them in the plan's authored sum.
    /// </summary>
    public bool TryOpenMoving(
        Vector2 currentPose,
        Vector2 targetPose,
        float currentYaw,
        float targetYaw,
        in NPCReleaseOccurrence occurrence,
        IReadOnlyList<NPCVectorConvergenceInterval> movingIntervals,
        in NPCVectorConvergenceConstraints constraints,
        out NPCVectorConvergenceResult plan,
        int firstConsumedIntervalIndex = 0)
    {
        if (Status != NPCVectorConvergenceExecutionStatus.Inactive
            || !occurrence.IsValid
            || HasPendingRequest
            || firstConsumedIntervalIndex < 0
            || firstConsumedIntervalIndex > movingIntervals.Count)
        {
            plan = default;
            Reject();
            return false;
        }

        ImmutableArray<NPCVectorConvergenceInterval> moving = Rebase(movingIntervals, 0);
        // The crossing-frame root has already been evaluated and its composed move has no correction authority.
        // Keep it in the authored total as lead-in, then open the convergence state from rest at the first interval
        // the executor can actually correct. This is the same layout used by imminent-candidate forecasting.
        ImmutableArray<NPCVectorConvergenceInterval> commitment = moving[..firstConsumedIntervalIndex];
        ImmutableArray<NPCVectorConvergenceInterval> correctionEligible = moving[firstConsumedIntervalIndex..];
        var request = new NPCVectorConvergenceRequest(
            currentPose,
            targetPose,
            currentYaw,
            targetYaw,
            occurrence,
            occurrence.UnwrappedPhase,
            commitment,
            correctionEligible,
            Vector2.Zero,
            Vector2.Zero,
            0f,
            constraints);
        plan = NPCVectorConvergenceOracle.Evaluate(request);
        if (!plan.IsFeasible)
        {
            Reject();
            return false;
        }

        _stepState = default;
        _constraints = constraints;
        Occurrence = occurrence;
        _planned = plan.RequiredCorrection;
        _remaining = plan.RequiredCorrection;
        _cancellationVelocity = Vector2.Zero;
        _targetYaw = targetYaw;
        _movingIntervals = moving;
        _movingIntervalIndex = firstConsumedIntervalIndex;
        _movingIntervalCount = moving.Length;
        Status = _movingIntervalIndex >= _movingIntervalCount
            ? NPCVectorConvergenceExecutionStatus.Complete
            : NPCVectorConvergenceExecutionStatus.Moving;
        return true;
    }

    public bool TryRequestMoving(
        in NPCReleaseOccurrence occurrence,
        float delta,
        float currentActorYaw,
        Vector2 realisedAuthored,
        Vector2 concurrentContactCorrection,
        out Vector2 requestedCorrection)
    {
        requestedCorrection = Vector2.Zero;
        LastRequestedYawCorrection = 0f;
        if (!ValidateMovingTick(occurrence)
            || !float.IsFinite(delta)
            || delta <= 0f
            || !realisedAuthored.IsFinite()
            || !concurrentContactCorrection.IsFinite())
        {
            return false;
        }

        int intervalIndex = _movingIntervalIndex++;
        NPCVectorConvergenceInterval interval = _movingIntervals[intervalIndex];
        Vector2 plannedAuthored = NPCVectorConvergenceOracle.ResolveAuthoredRoot(interval, currentActorYaw);
        LastPlannedAuthoredRoot = plannedAuthored;
        LastRealisedAuthoredRoot = realisedAuthored;
        LastPlannedAuthoredYaw = interval.AuthoredYawRadians;
        _remaining += plannedAuthored - realisedAuthored;
        // The one composed move that carries this interval's correction also carries the already-consumed
        // contact contribution. Realised contact displacement is realised motion, so the remaining-correction
        // schedule absorbs it before stepping: the request then paces only the correction that synthetic
        // steering still owes, and the final interval can still park exactly (NAV-001 TR-16/17).
        _remaining -= concurrentContactCorrection;
        // A cancelling contact stream keeps absorbing part of every composed move, so a pure deadline chase
        // always trails it and strands the unabsorbed share past the final interval. Pace the deadline by the
        // smoothed cancellation velocity projected over the remaining delivery time; with no contact flow the
        // multiplier is exactly one and the neutral schedule is unchanged.
        float sampleWeight = 1f - CancellationSmoothing;
        Vector2 sampleVelocity = concurrentContactCorrection / delta;
        _cancellationVelocity = (_cancellationVelocity * CancellationSmoothing) + (sampleVelocity * sampleWeight);
        float deliverySeconds = (_movingIntervalCount - _movingIntervalIndex + 1) * delta;
        float magnitude = _remaining.Length();
        float projectedCancellation = _cancellationVelocity.Length() * deliverySeconds;
        float deadlinePaceMultiplier = magnitude > 0f
            ? (magnitude + projectedCancellation) / magnitude
            : 1f;

        _ = NPCVectorConvergenceOracle.Step(
            ref _stepState,
            _remaining,
            delta,
            default,
            _constraints,
            out requestedCorrection,
            out _,
            deliverySeconds,
            deadlinePaceMultiplier);
        float totalYaw = NPCYawConvergence.StepTerminalRotation(
            currentActorYaw,
            _targetYaw,
            ref _stepState.AppliedYawRate,
            delta);
        LastRequestedYawCorrection = NPCNavigationRoutePlan.WrapAngle(totalYaw - interval.AuthoredYawRadians);
        _pendingRequested = requestedCorrection;
        HasPendingRequest = true;
        return true;
    }

    public bool RecordRealisation(Vector2 realisedCorrection, Vector2 realisedAuthored, bool collided = false)
    {
        if (!HasPendingRequest || !realisedCorrection.IsFinite() || !realisedAuthored.IsFinite())
        {
            Reject();
            return false;
        }

        Vector2 remainder = _pendingRequested - realisedCorrection;
        if ((_pendingRequested - realisedCorrection - remainder).Length() > IdentityTolerance)
        {
            Reject();
            return false;
        }

        _requested += _pendingRequested;
        _realised += realisedCorrection;
        _collisionRemainder += remainder;
        LastRealisationCollided = collided;
        _remaining -= realisedCorrection;
        _pendingRequested = Vector2.Zero;
        HasPendingRequest = false;
        _ownedIntervalCount++;
        if (_movingIntervalIndex == _movingIntervalCount)
        {
            _stepState = default;
            LastRequestedYawCorrection = 0f;
            Status = NPCVectorConvergenceExecutionStatus.Complete;
        }
        return true;
    }

    public bool SameOccurrence(in NPCReleaseOccurrence occurrence)
        => Occurrence.IsValid
            && occurrence.IsValid
            && Occurrence.ClockGeneration == occurrence.ClockGeneration
            && Occurrence.CycleOrdinal == occurrence.CycleOrdinal
            && Occurrence.Lane == occurrence.Lane
            && Occurrence.ReleaseEvent == occurrence.ReleaseEvent
            && Math.Abs(Occurrence.UnwrappedPhase - occurrence.UnwrappedPhase) <= 0.0000001d;

    public void Reset(NPCVectorConvergenceResetReason reason = NPCVectorConvergenceResetReason.None)
    {
        Occurrence = default;
        Status = NPCVectorConvergenceExecutionStatus.Inactive;
        _stepState = default;
        _constraints = default;
        _movingIntervals = [];
        _planned = Vector2.Zero;
        _remaining = Vector2.Zero;
        _requested = Vector2.Zero;
        _realised = Vector2.Zero;
        _collisionRemainder = Vector2.Zero;
        _pendingRequested = Vector2.Zero;
        _cancellationVelocity = Vector2.Zero;
        _targetYaw = 0f;
        _movingIntervalIndex = 0;
        _movingIntervalCount = 0;
        _ownedIntervalCount = 0;
        LastPlannedAuthoredRoot = Vector2.Zero;
        LastRealisedAuthoredRoot = Vector2.Zero;
        LastPlannedAuthoredYaw = 0f;
        LastRequestedYawCorrection = 0f;
        LastRealisationCollided = false;
        HasPendingRequest = false;
        LastResetReason = reason;
    }

    private bool ValidateMovingTick(in NPCReleaseOccurrence occurrence)
    {
        return Status == NPCVectorConvergenceExecutionStatus.Moving
            && !HasPendingRequest
            && _movingIntervalIndex < _movingIntervalCount
            && SameOccurrence(occurrence)
            && _remaining.IsFinite();
    }

    private void Reject()
    {
        Status = NPCVectorConvergenceExecutionStatus.Rejected;
        Occurrence = default;
        HasPendingRequest = false;
        _pendingRequested = Vector2.Zero;
        LastRequestedYawCorrection = 0f;
        _stepState = default;
    }

    private static ImmutableArray<NPCVectorConvergenceInterval> Rebase(
        IReadOnlyList<NPCVectorConvergenceInterval> source,
        int start)
    {
        if (source.Count == 0)
        {
            return [];
        }

        ImmutableArray<NPCVectorConvergenceInterval>.Builder builder =
            ImmutableArray.CreateBuilder<NPCVectorConvergenceInterval>(source.Count);
        for (int index = 0; index < source.Count; index++)
        {
            builder.Add(source[index] with
            {
                PreviousTick = start + index,
                CurrentTick = start + index + 1,
            });
        }
        return builder.MoveToImmutable();
    }
}
