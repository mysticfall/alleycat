using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCVectorConvergenceExecutorTests
{
    private const float Delta = 0.1f;
    private static readonly NPCVectorConvergenceConstraints _constraints = new(0.5f, 2f, 30f, 2f, 0.0001f);

    [Fact]
    public void ClosedWindow_RejectsFurtherMovingTicks()
    {
        NPCVectorConvergenceExecutor executor = OpenMoving(new(0.01f, 0f), 0f, movingCount: 2);
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 first));
        Assert.True(executor.RecordRealisation(first, Vector2.Zero));
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 second));
        Assert.True(executor.RecordRealisation(second, Vector2.Zero));

        Assert.Equal(NPCVectorConvergenceExecutionStatus.Complete, executor.Status);
        Assert.False(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 after));
        Assert.Equal(Vector2.Zero, after);
    }

    [Fact]
    public void InfeasibleWindow_IsRejectedBeforeInstallationAndFailsClosed()
    {
        var executor = new NPCVectorConvergenceExecutor();

        // A 5 m landing error cannot be converged by twelve 0.1 s intervals bounded at 0.5 m/s.
        Assert.False(executor.TryOpenMoving(
            Vector2.Zero,
            new(5f, 0f),
            0f,
            0f,
            Occurrence(),
            Intervals(0, 12),
            _constraints,
            out NPCVectorConvergenceResult plan));
        Assert.Equal(NPCVectorConvergenceInfeasibility.DirectionallyUnreachable, plan.FirstInfeasibilityReason);
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Rejected, executor.Status);
        Assert.False(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 correction));
        Assert.Equal(Vector2.Zero, correction);
    }

    [Fact]
    public void CommitmentInterval_RequiringCorrectionIsRejectedBeforeInstallation()
    {
        // The evaluated commitment root remains part of authored motion, but StartExit consumes it without a
        // correction. Only the second interval can service the remaining 0.05 m demand.
        var executor = new NPCVectorConvergenceExecutor();
        Assert.False(executor.TryOpenMoving(
            Vector2.Zero,
            new(0.0625f, 0f),
            0f,
            0f,
            Occurrence(),
            [
                new(0, 1, Delta, new Vector2(0.0125f, 0f), 0f),
                new(1, 2, Delta, Vector2.Zero, 0f),
            ],
            _constraints,
            out NPCVectorConvergenceResult plan,
            firstConsumedIntervalIndex: 1));

        Assert.Equal(new Vector2(0.05f, 0f), plan.RequiredCorrection);
        Assert.Equal(1, plan.LeadInIntervalCount);
        Assert.Equal(1, plan.MovingIntervalCount);
        Assert.Equal(NPCVectorConvergenceInfeasibility.DirectionallyUnreachable, plan.FirstInfeasibilityReason);
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Rejected, executor.Status);
    }

    [Fact]
    public void CommitmentInterval_ValidCandidateStartsTheFiniteMovingWindow()
    {
        var executor = new NPCVectorConvergenceExecutor();
        Assert.True(executor.TryOpenMoving(
            Vector2.Zero,
            new(0.0325f, 0f),
            0f,
            0f,
            Occurrence(),
            [
                new(0, 1, Delta, new Vector2(0.0125f, 0f), 0f),
                new(1, 2, Delta, Vector2.Zero, 0f),
            ],
            _constraints,
            out NPCVectorConvergenceResult plan,
            firstConsumedIntervalIndex: 1), plan.FirstInfeasibilityReason.ToString());

        Assert.Equal(new Vector2(0.02f, 0f), plan.RequiredCorrection);
        Assert.Equal(1, plan.LeadInIntervalCount);
        Assert.Equal(1, plan.MovingIntervalCount);
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Moving, executor.Status);
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 correction));
        Assert.NotEqual(Vector2.Zero, correction);
        Assert.True(executor.RecordRealisation(correction, Vector2.Zero));
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Complete, executor.Status);
        Assert.Equal(1, executor.Ledger.OwnedIntervalCount);
    }

    [Fact]
    public void MovingWindow_IsBoundedAndNeverCrossesPlanarOrYawTarget()
    {
        Vector2 target = new(0.08f, -0.03f);
        const float targetYaw = 0.18f;
        NPCVectorConvergenceExecutor executor = OpenMoving(target, targetYaw, 20);
        Vector2 pose = Vector2.Zero;
        float yaw = 0f;
        Vector2 previousVelocity = Vector2.Zero;
        Vector2 previousAcceleration = Vector2.Zero;

        while (executor.Status == NPCVectorConvergenceExecutionStatus.Moving)
        {
            Assert.True(executor.TryRequestMoving(Occurrence(), Delta, yaw, Vector2.Zero, Vector2.Zero, out Vector2 correction));
            float speed = correction.Length() / Delta;
            Assert.True(speed <= _constraints.MaximumSpeed + 0.000001f);
            NPCVectorConvergenceStepState state = executor.CurrentStepState;
            Assert.True(state.Velocity.Length() <= _constraints.MaximumSpeed + 0.000001f);
            Assert.True((state.Velocity - previousVelocity).Length() / Delta
                <= _constraints.MaximumAcceleration + 0.00001f);
            Assert.True((state.Acceleration - previousAcceleration).Length() / Delta
                <= _constraints.MaximumJerk + 0.00001f);
            Assert.True(correction.Dot(target - pose) >= -0.000001f);
            Assert.True(correction.Length() <= (target - pose).Length() + 0.000001f);
            float yawCorrection = executor.LastRequestedYawCorrection;
            Assert.True(Mathf.Abs(yawCorrection) <= Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(targetYaw - yaw)) + 0.000001f);
            pose += correction;
            yaw = NPCNavigationRoutePlan.WrapAngle(yaw + yawCorrection);
            previousVelocity = state.Velocity;
            previousAcceleration = state.Acceleration;
            Assert.True(executor.RecordRealisation(correction, Vector2.Zero));
        }
    }

    [Fact]
    public void FinalMovingInterval_IsConsumedExactlyOnceAndClosesAuthorityImmediatelyAfterRealisation()
    {
        NPCVectorConvergenceExecutor executor = OpenMoving(Vector2.Zero, 0f, movingCount: 2);

        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 first));
        Assert.False(executor.IsFinalIntervalPending);
        Assert.True(executor.RecordRealisation(first, Vector2.Zero));
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 final));
        Assert.True(executor.IsFinalIntervalPending);
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Moving, executor.Status);
        Assert.True(executor.RecordRealisation(final, Vector2.Zero));

        Assert.Equal(NPCVectorConvergenceExecutionStatus.Complete, executor.Status);
        Assert.Equal(2, executor.Ledger.OwnedIntervalCount);
        Assert.False(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 after));
        Assert.Equal(Vector2.Zero, after);
        Assert.Equal(2, executor.Ledger.OwnedIntervalCount);
        Assert.Equal(Vector2.Zero, executor.CurrentStepState.Velocity);
        Assert.Equal(0f, executor.CurrentStepState.AppliedYawRate);
    }

    [Fact]
    public void CollisionClipping_ReconcilesLaterDemandWithoutExtendingWindow()
    {
        NPCVectorConvergenceExecutor executor = OpenMoving(new(0.06f, 0f), 0f, movingCount: 12);
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 requested));
        Vector2 realised = requested * 0.25f;
        Assert.True(executor.RecordRealisation(realised, Vector2.Zero, collided: true));
        Assert.Equal(requested, realised + executor.Ledger.CollisionRemainder);

        int subsequent = 0;
        while (executor.Status == NPCVectorConvergenceExecutionStatus.Moving)
        {
            Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 correction));
            Assert.True(executor.RecordRealisation(correction, Vector2.Zero));
            subsequent++;
        }

        Assert.Equal(11, subsequent);
        Assert.Equal(12, executor.Ledger.OwnedIntervalCount);
    }

    [Fact]
    public void FinalInterval_NonZeroAuthoredRootIsOwnedAndCountedExactlyOnce()
    {
        // The authored f2i settle tail steps the character, so the final owned interval carries non-zero
        // authored root: its request must compose that authored displacement exactly once, and the closing
        // realisation must own it before authority completes.
        Vector2 finalAuthored = new(0.03f, -0.01f);
        var executor = new NPCVectorConvergenceExecutor();
        Assert.True(executor.TryOpenMoving(
            Vector2.Zero,
            new(0.04f, 0f),
            0f,
            0f,
            Occurrence(),
            Intervals(0, 3, finalAuthored),
            _constraints,
            out _));

        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 first));
        Assert.True(executor.RecordRealisation(first, Vector2.Zero));
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 second));
        Assert.True(executor.RecordRealisation(second, Vector2.Zero));
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Moving, executor.Status);

        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 final));
        Assert.True(executor.IsFinalIntervalPending);
        Assert.Equal(finalAuthored, executor.LastPlannedAuthoredRoot);
        Assert.True(executor.RecordRealisation(final, finalAuthored));

        Assert.Equal(NPCVectorConvergenceExecutionStatus.Complete, executor.Status);
        Assert.Equal(3, executor.Ledger.OwnedIntervalCount);
        Assert.False(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 after));
        Assert.Equal(Vector2.Zero, after);
        Assert.Equal(3, executor.Ledger.OwnedIntervalCount);
    }

    [Fact]
    public void StaleOccurrence_AtFinalInterval_IsRejectedWithoutConsumingIt()
    {
        NPCVectorConvergenceExecutor executor = OpenMoving(Vector2.Zero, 0f, movingCount: 2);
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 first));
        Assert.True(executor.RecordRealisation(first, Vector2.Zero));

        // A stale occurrence (different clock generation) must never consume the final owned interval or
        // authorise a correction for it; the window fails closed with no pending request and no tail motion.
        NPCReleaseOccurrence stale = Occurrence() with
        {
            ClockGeneration = 2
        };
        Assert.False(executor.TryRequestMoving(stale, Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 staleCorrection));
        Assert.Equal(Vector2.Zero, staleCorrection);
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Moving, executor.Status);
        Assert.False(executor.HasPendingRequest);
        Assert.Equal(0f, executor.LastRequestedYawCorrection);

        // The valid occurrence still owns the untouched final interval.
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 final));
        Assert.True(executor.IsFinalIntervalPending);
        Assert.True(executor.RecordRealisation(final, Vector2.Zero));
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Complete, executor.Status);
        Assert.Equal(2, executor.Ledger.OwnedIntervalCount);
    }

    [Fact]
    public void MissingOrInvalidFinalRealisation_FailsClosedWithoutTailMotion()
    {
        NPCVectorConvergenceExecutor executor = OpenMoving(Vector2.Zero, 0f, movingCount: 1);

        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out _));
        Assert.True(executor.IsFinalIntervalPending);

        Assert.False(executor.RecordRealisation(new Vector2(float.NaN, 0f), Vector2.Zero));
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Rejected, executor.Status);
        Assert.False(executor.HasPendingRequest);
        Assert.False(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 after));
        Assert.Equal(Vector2.Zero, after);
        Assert.Equal(0, executor.Ledger.OwnedIntervalCount);
        Assert.Equal(Vector2.Zero, executor.CurrentStepState.Velocity);
    }

    [Fact]
    public void Realisation_WithoutPendingRequest_FailsClosed()
    {
        NPCVectorConvergenceExecutor executor = OpenMoving(Vector2.Zero, 0f, movingCount: 2);

        Assert.False(executor.RecordRealisation(Vector2.Zero, Vector2.Zero));
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Rejected, executor.Status);
        Assert.False(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 correction));
        Assert.Equal(Vector2.Zero, correction);
    }

    [Fact]
    public void ConcurrentContactCorrection_IsAbsorbedByTheRemainingScheduleInTheSameInterval()
    {
        // Realised contact displacement inside the f2i window is realised motion carried by the same composed
        // move as the interval's correction. The request must absorb it immediately — pacing only the share the
        // synthetic steering still owes — so a persistent cancelling contact stream still lands exactly on the
        // target within the window instead of stranding the contact total past the closing boundary.
        Vector2 target = new(0.06f, 0f);
        Vector2 contactPerInterval = new(-0.002f, 0f);
        NPCVectorConvergenceExecutor executor = OpenMoving(target, 0f, movingCount: 12);
        Vector2 pose = Vector2.Zero;

        while (executor.Status == NPCVectorConvergenceExecutionStatus.Moving)
        {
            Assert.True(executor.TryRequestMoving(
                Occurrence(), Delta, 0f, Vector2.Zero, contactPerInterval, out Vector2 correction));
            Vector2 composed = correction + contactPerInterval;
            Assert.True(composed.Dot(target - pose) >= -0.000001f);
            pose += composed;
            Assert.True(executor.RecordRealisation(correction, Vector2.Zero));
        }

        Assert.True(pose.DistanceTo(target) <= 0.0001f, $"Landing stranded {pose.DistanceTo(target):R}m of contact-unabsorbed correction.");
        Assert.Equal(12, executor.Ledger.OwnedIntervalCount);
    }

    [Fact]
    public void ConcurrentContactCorrection_OnTheFinalInterval_ParksExactlyOnTheRemainingVector()
    {
        NPCVectorConvergenceExecutor executor = OpenMoving(new(0.008f, 0f), 0f, movingCount: 2);
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, Vector2.Zero, out Vector2 first));
        Assert.True(executor.RecordRealisation(first, Vector2.Zero));

        Vector2 contact = new(-0.003f, 0f);
        Assert.True(executor.TryRequestMoving(Occurrence(), Delta, 0f, Vector2.Zero, contact, out Vector2 final));
        Assert.True(executor.IsFinalIntervalPending);
        // The final interval parks on the remaining vector after the concurrently consumed contact share, so the
        // composed move (final + contact) still lands exactly on the target.
        Vector2 remaining = new(0.008f - first.X, 0f);
        Assert.True((final + contact).DistanceTo(remaining) <= 0.0001f);
        Assert.True(executor.RecordRealisation(final, Vector2.Zero));
        Assert.Equal(NPCVectorConvergenceExecutionStatus.Complete, executor.Status);
    }

    [Theory]
    [InlineData(0.001f, 0.001f, 0f, true)]
    [InlineData(0.001001f, 0.001f, -0.000001f, false)]
    public void TerminalMargins_AreSignedToleranceMinusErrorAndInclusive(
        float error,
        float tolerance,
        float expectedMargin,
        bool expectedWithin)
    {
        var tolerances = new NavigationTerminalTolerances(tolerance, Mathf.DegToRad(0.1f));
        var measured = new NavigationPlanarYawError(error, Mathf.DegToRad(0.1f));
        NavigationSignedMargins margins = tolerances.CalculateMargins(measured);

        Assert.Equal(expectedMargin, margins.PlanarDistance, 6);
        Assert.Equal(0f, margins.YawRadians, 6);
        Assert.Equal(expectedWithin, margins.PlanarDistance >= 0f && margins.YawRadians >= 0f);
    }

    private static NPCVectorConvergenceExecutor OpenMoving(Vector2 target, float targetYaw, int movingCount)
    {
        var executor = new NPCVectorConvergenceExecutor();
        Assert.True(executor.TryOpenMoving(
            Vector2.Zero,
            target,
            0f,
            targetYaw,
            Occurrence(),
            Intervals(0, movingCount),
            _constraints,
            out NPCVectorConvergenceResult plan), plan.FirstInfeasibilityReason.ToString());
        return executor;
    }

    private static NPCReleaseOccurrence Occurrence()
        => new(1, 1, 0.5f, NPCAnimationPhaseLane.Left, "release_l");

    private static List<NPCVectorConvergenceInterval> Intervals(long start, int count, Vector2? finalRoot = null)
        => [.. Enumerable.Range(0, count).Select(index =>
            new NPCVectorConvergenceInterval(
                start + index,
                start + index + 1,
                Delta,
                index == count - 1 ? finalRoot ?? Vector2.Zero : Vector2.Zero,
                0f))];
}
