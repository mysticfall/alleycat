using System.Collections.Immutable;
using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCVectorConvergenceOracleTests
{
    private static readonly NPCVectorConvergenceConstraints _constraints = new(1f, 1f, 100f, 1f, 0.00001f);

    [Fact]
    public void DirectionMismatch_IsInfeasibleDespiteSufficientScalarCapacity()
    {
        NPCVectorConvergenceRequest request = Request(
            target: new Vector2(0.1f, 0f),
            moving: Intervals(0, 2, Vector2.Zero),
            velocity: new Vector2(-1f, 0f),
            constraints: _constraints with
            {
                MaximumAcceleration = 0f,
                MaximumJerk = 0f
            });

        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(request);

        Assert.Equal(NPCVectorConvergenceInfeasibility.DirectionallyUnreachable, result.FirstInfeasibilityReason);
        Assert.Equal(Vector2.Zero, result.VectorReachableCorrection);
        Assert.All(result.Schedule, step => Assert.Equal(Vector2.Zero, step.RequestedCorrection));
    }

    [Fact]
    public void ImmediateRelease_EmptyLeadInUsesMovingIntervalsOnly()
    {
        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(Request(
            target: new Vector2(0.01f, 0f),
            moving: Intervals(0, 2, Vector2.Zero)));

        Assert.Equal(0, result.LeadInIntervalCount);
        Assert.Equal(2, result.MovingIntervalCount);
        Assert.True(result.IsFeasible);
    }

    [Fact]
    public void FinalMovingInterval_IsOwnedOnceWithPreviousCurrentBoundary()
    {
        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(Request(
            target: new Vector2(0.02f, 0f),
            leadIn: Intervals(0, 3, Vector2.Zero),
            moving: Intervals(3, 1, Vector2.Zero)));

        Assert.Equal(3, result.LeadInIntervalCount);
        Assert.Equal(1, result.MovingIntervalCount);
        Assert.Equal(1, result.TotalIntervalCount - result.LeadInIntervalCount);
        Assert.Equal([4], result.Schedule.Select(step => step.CurrentTick));
    }

    [Fact]
    public void LeadInWindow_DoesNotAdvanceOrOwnCorrectionState()
    {
        NPCVectorConvergenceRequest request = Request(
            target: new Vector2(0.03f, 0f),
            leadIn: Intervals(0, 3, Vector2.Zero),
            moving: Intervals(3, 2, Vector2.Zero));
        NPCVectorConvergenceStepState expected = new(
            request.CurrentCorrectionVelocity,
            request.CurrentCorrectionAcceleration,
            request.CurrentAppliedYawRate);
        _ = NPCVectorConvergenceOracle.Step(
            ref expected,
            new Vector2(0.03f, 0f),
            0.1f,
            default,
            request.Constraints,
            out Vector2 firstOwnedRequest,
            out _);

        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(request);

        Assert.Equal(3, result.LeadInIntervalCount);
        NPCVectorConvergenceStep firstOwned = result.Schedule[0];
        Assert.Equal(4, firstOwned.CurrentTick);
        Assert.Equal(firstOwnedRequest, firstOwned.RequestedCorrection);
        Assert.Equal(expected.Velocity, firstOwned.CorrectionVelocity);
        Assert.Equal(expected.Acceleration, firstOwned.CorrectionAcceleration);
    }

    [Fact]
    public void ActorLocalRootRotation_IsFrameConsistent()
    {
        NPCVectorConvergenceInterval local = new(
            0,
            1,
            0.1f,
            new Vector2(0f, 1f),
            Mathf.Pi / 2f,
            AuthoredRootIsActorLocal: true);
        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(Request(
            current: Vector2.Zero,
            target: new Vector2(-1f, 0f),
            currentYaw: Mathf.Pi / 2f,
            targetYaw: Mathf.Pi / 2f,
            moving: [local],
            constraints: _constraints with
            {
                MaximumSpeed = 0f
            }));

        Assert.True(result.RequiredCorrection.Length() <= 0.00001f, $"required={result.RequiredCorrection}");
        Assert.True(result.IsFeasible);
    }

    [Fact]
    public void PartialCollisionRealisation_RetainsExplicitRemainder()
    {
        ImmutableArray<NPCVectorConvergenceInterval> moving =
        [new(
            0,
            1,
            1f,
            Vector2.Zero,
            0f,
            Realisation: new(true, new Vector2(0.25f, 0.5f), new Vector2(0.75f, -0.5f), 0f, 0f))];
        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(Request(
            target: new Vector2(1f, 0f),
            moving: moving,
            velocity: new Vector2(1f, 0f)));

        Assert.Equal(new Vector2(0.25f, 0.5f), result.VectorReachableCorrection);
        Assert.Equal(new Vector2(0.75f, -0.5f), result.Moving.CollisionRemainder);
        Assert.Equal(new Vector2(0.75f, -0.5f), result.PredictedEndpointResidual);
    }

    [Fact]
    public void ReplanningSameSnapshot_IsDeterministic()
    {
        NPCVectorConvergenceRequest request = Request(
            target: new Vector2(0.07f, -0.03f),
            leadIn: Intervals(0, 2, new Vector2(0.005f, 0f)),
            moving:
            [
                .. Intervals(2, 2, new Vector2(0f, -0.002f)),
                .. Intervals(4, 4, Vector2.Zero),
            ]);

        NPCVectorConvergenceResult first = NPCVectorConvergenceOracle.Evaluate(request);
        NPCVectorConvergenceResult second = NPCVectorConvergenceOracle.Evaluate(request);
        Assert.Equal(first.RequiredCorrection, second.RequiredCorrection);
        Assert.Equal(first.VectorReachableCorrection, second.VectorReachableCorrection);
        Assert.Equal(first.PredictedEndpointResidual, second.PredictedEndpointResidual);
        Assert.Equal([.. first.Schedule], [.. second.Schedule]);
    }

    [Fact]
    public void YawFeasibility_UsesProductionTerminalStepAndFailsWithoutEnoughIntervals()
    {
        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(Request(
            targetYaw: 0.5f,
            moving: Intervals(0, 1, Vector2.Zero)));

        Assert.False(result.YawFeasible);
        Assert.Equal(NPCVectorConvergenceInfeasibility.YawUnreachable, result.FirstInfeasibilityReason);
        Assert.True(Mathf.Abs(result.PredictedEndpointYawResidual) > NPCYawConvergence.NumericalToleranceRadians);
        Assert.NotEqual(0f, result.Moving.ReachableYawCorrection);
    }

    [Theory]
    [InlineData(-1f, -1)]
    [InlineData(-1f, 0)]
    [InlineData(-1f, 1)]
    [InlineData(1f, -1)]
    [InlineData(1f, 0)]
    [InlineData(1f, 1)]
    public void YawSchedule_ExactlyMatchesSharedRuntimeStateMachineAcrossMirroredCarriedRates(
        float side,
        int carriedRateDirection)
    {
        const float deltaSeconds = 0.1f;
        float targetYaw = side * 0.4f;
        float currentYaw = targetYaw - (side * (NPCYawConvergence.NumericalToleranceRadians + 0.0000005f));
        float appliedYawRate = side * carriedRateDirection * 0.02f;
        ImmutableArray<NPCVectorConvergenceInterval> intervals =
        [
            .. Enumerable.Range(0, 24).Select(index => new NPCVectorConvergenceInterval(
                index,
                index + 1,
                deltaSeconds,
                Vector2.Zero,
                currentYaw,
                AuthoredYawRadians: side * 0.0000005f)),
        ];

        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(Request(
            currentYaw: currentYaw,
            targetYaw: targetYaw,
            moving: intervals,
            appliedYawRate: appliedYawRate));

        foreach (NPCVectorConvergenceStep step in result.Schedule)
        {
            float expectedDelta = NPCYawConvergence.StepTerminalRotation(
                currentYaw,
                targetYaw,
                ref appliedYawRate,
                deltaSeconds,
                out NPCYawConvergenceStepTrace expectedTrace);
            currentYaw = NPCYawConvergence.RealiseRuntimeYaw(currentYaw, expectedDelta);

            Assert.Equal(expectedTrace, step.YawConvergenceTrace);
            Assert.Equal(currentYaw, step.PredictedYawAfter);
            Assert.Equal(appliedYawRate, step.AppliedYawRate);
        }

        Assert.True(result.YawFeasible, $"side={side}; carried={carriedRateDirection}; residual={result.PredictedEndpointYawResidual:R}");
    }

    [Theory]
    [MemberData(nameof(InvalidLayouts))]
    public void InvalidIntervalLayouts_FailClosed(ImmutableArray<NPCVectorConvergenceInterval> moving)
    {
        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(Request(moving: moving));

        Assert.Equal(NPCVectorConvergenceInfeasibility.InvalidInterval, result.FirstInfeasibilityReason);
        Assert.Empty(result.Schedule);
    }

    [Fact]
    public void DiscontiguousLeadInToMovingHandover_FailsClosed()
    {
        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(Request(
            leadIn: Intervals(0, 2, Vector2.Zero),
            moving: Intervals(3, 1, Vector2.Zero)));

        Assert.Equal(NPCVectorConvergenceInfeasibility.InvalidInterval, result.FirstInfeasibilityReason);
    }

    [Fact]
    public void BoundedImminentCheck_UsesOnlyMovingCrossfadeAndFiniteIntervalsForConvergence()
    {
        const float delta = 1f / 60f;
        NPCVectorConvergenceConstraints constraints = new(
            NPCVectorConvergenceOracle.MaximumCorrectionSpeed,
            2f,
            30f,
            NPCSettlementPlanarConvergence.TaperAcceleration,
            NPCAnimationProgramme.NumericalConvergenceThreshold);
        NPCVectorConvergenceResult shortWindow = NPCVectorConvergenceOracle.Evaluate(Request(
            target: new Vector2(0.19f, 0f),
            leadIn: Intervals(0, 1, Vector2.Zero, delta),
            moving: Intervals(1, 1, Vector2.Zero, delta),
            constraints: constraints));
        NPCVectorConvergenceResult fullWindow = NPCVectorConvergenceOracle.Evaluate(Request(
            target: new Vector2(0.19f, 0f),
            leadIn: Intervals(0, 1, Vector2.Zero, delta),
            moving:
            [
                .. Intervals(1, 50, Vector2.Zero, delta),
                .. Intervals(51, 1, Vector2.Zero, delta),
            ],
            constraints: constraints));

        Assert.False(shortWindow.IsFeasible);
        Assert.Equal(NPCVectorConvergenceInfeasibility.DirectionallyUnreachable, shortWindow.FirstInfeasibilityReason);
        Assert.True(fullWindow.IsFeasible, $"residual={fullWindow.PredictedEndpointResidual}");
        Assert.Equal(1, fullWindow.LeadInIntervalCount);
        Assert.Equal(51, fullWindow.MovingIntervalCount);
    }

    [Fact]
    public void ReplanAfterRedirectedCollision_RetainsStateAndVectorRemainder()
    {
        NPCVectorConvergenceRealisation redirected = new(
            true,
            new Vector2(0.02f, 0.03f),
            new Vector2(0.08f, -0.03f),
            0f,
            0f);
        NPCVectorConvergenceRequest firstRequest = Request(
            target: new Vector2(0.1f, 0f),
            moving: [new(0, 1, 0.1f, Vector2.Zero, 0f, Realisation: redirected)],
            velocity: new Vector2(1f, 0f));
        NPCVectorConvergenceResult first = NPCVectorConvergenceOracle.Evaluate(firstRequest);
        NPCVectorConvergenceStep finalStep = Assert.Single(first.Schedule);
        Vector2 realisedPose = firstRequest.CurrentPlanarPose + first.VectorReachableCorrection;
        float realisedYaw = firstRequest.CurrentYawRadians + first.ReachableYawCorrection;

        NPCVectorConvergenceRequest replanned = Request(
            current: realisedPose,
            target: firstRequest.TargetPlanarPose,
            currentYaw: realisedYaw,
            targetYaw: firstRequest.TargetYawRadians,
            moving: Intervals(1, 20, Vector2.Zero),
            velocity: finalStep.CorrectionVelocity,
            acceleration: finalStep.CorrectionAcceleration,
            appliedYawRate: finalStep.AppliedYawRate);
        NPCVectorConvergenceResult second = NPCVectorConvergenceOracle.Evaluate(replanned);

        Assert.Equal(first.PredictedEndpointResidual, first.Moving.CollisionRemainder);
        Assert.True(second.IsFeasible, $"position={second.PredictedEndpointResidual}, yaw={second.PredictedEndpointYawResidual}");
        Assert.Equal(first.PredictedEndpointResidual, second.RequiredCorrection);
    }

    [Fact]
    public void ContradictoryRealisationEvidence_FailsClosed()
    {
        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(Request(
            target: new Vector2(1f, 0f),
            moving:
            [
                new(
                    0,
                    1,
                    1f,
                    Vector2.Zero,
                    0f,
                    Realisation: new(true, new Vector2(0.25f, 0f), Vector2.Zero, 0f, 0f))
            ],
            velocity: new Vector2(1f, 0f)));

        Assert.Equal(NPCVectorConvergenceInfeasibility.InvalidInterval, result.FirstInfeasibilityReason);
        Assert.False(result.IsFeasible);
    }

    [Fact]
    public void Step_WarmedPathAllocatesNoManagedMemory()
    {
        NPCVectorConvergenceStepState state = new(Vector2.Zero, Vector2.Zero);
        NPCVectorConvergenceRealisation noEvidence = default;
        _ = NPCVectorConvergenceOracle.Step(
            ref state,
            new Vector2(1f, 0f),
            1f / 60f,
            noEvidence,
            _constraints,
            out _,
            out _);
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 1_000; index++)
        {
            _ = NPCVectorConvergenceOracle.Step(
                ref state,
                new Vector2(1f, 0f),
                1f / 60f,
                noEvidence,
                _constraints,
                out _,
                out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void FeasibilityWithoutSchedule_WarmedPathAllocatesNoManagedMemory()
    {
        NPCVectorConvergenceRequest request = Request(
            target: new Vector2(0.04f, 0.01f),
            currentYaw: 0.1f,
            targetYaw: 0.2f,
            leadIn: Intervals(0, 3, new Vector2(0.005f, 0f)),
            moving:
            [
                .. Intervals(3, 2, new Vector2(0.003f, 0f)),
                .. Intervals(5, 3, new Vector2(0.002f, 0f)),
            ]);
        _ = NPCVectorConvergenceOracle.Evaluate(request, captureSchedule: false);
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 1_000; index++)
        {
            _ = NPCVectorConvergenceOracle.Evaluate(request, captureSchedule: false);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    public static IEnumerable<object[]> InvalidLayouts()
    {
        yield return [ImmutableArray.Create(
            new NPCVectorConvergenceInterval(0, 1, 0.1f, Vector2.Zero, 0f),
            new NPCVectorConvergenceInterval(2, 3, 0.1f, Vector2.Zero, 0f))];
        yield return [ImmutableArray.Create(
            new NPCVectorConvergenceInterval(0, 1, 0.1f, Vector2.Zero, 0f),
            new NPCVectorConvergenceInterval(0, 1, 0.1f, Vector2.Zero, 0f))];
        yield return [ImmutableArray.Create(
            new NPCVectorConvergenceInterval(0, 2, 0.1f, Vector2.Zero, 0f))];
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteInputs_FailClosed(float value)
    {
        NPCVectorConvergenceRequest request = Request(
            target: new Vector2(value, 0f),
            moving: Intervals(0, 1, Vector2.Zero));

        NPCVectorConvergenceResult result = NPCVectorConvergenceOracle.Evaluate(request);

        Assert.False(result.IsFeasible);
        Assert.Equal(NPCVectorConvergenceInfeasibility.NonFiniteInput, result.FirstInfeasibilityReason);
        Assert.Empty(result.Schedule);
    }

    private static NPCVectorConvergenceRequest Request(
        Vector2? current = null,
        Vector2? target = null,
        float currentYaw = 0f,
        float targetYaw = 0f,
        ImmutableArray<NPCVectorConvergenceInterval> leadIn = default,
        ImmutableArray<NPCVectorConvergenceInterval> moving = default,
        Vector2? velocity = null,
        Vector2? acceleration = null,
        float appliedYawRate = 0f,
        NPCVectorConvergenceConstraints? constraints = null)
    {
        NPCReleaseOccurrence occurrence = new(1, 0, 0.5f, NPCAnimationPhaseLane.Left, "release_l");
        return new(
            current ?? Vector2.Zero,
            target ?? Vector2.Zero,
            currentYaw,
            targetYaw,
            occurrence,
            occurrence.UnwrappedPhase,
            leadIn.IsDefault ? [] : leadIn,
            moving.IsDefault ? [] : moving,
            velocity ?? Vector2.Zero,
            acceleration ?? Vector2.Zero,
            appliedYawRate,
            constraints ?? _constraints);
    }

    private static ImmutableArray<NPCVectorConvergenceInterval> Intervals(
        long startTick,
        int count,
        Vector2 root,
        float delta = 0.1f)
        => [.. Enumerable.Range(0, count).Select(index =>
            new NPCVectorConvergenceInterval(startTick + index, startTick + index + 1, delta, root, 0f))];
}
