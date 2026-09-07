using AlleyCat.Navigation;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCExitReleaseGateTests
{
    [Theory]
    [InlineData(NPCAnimationPhaseLane.Left, "f2i_l", "release_l")]
    [InlineData(NPCAnimationPhaseLane.Right, "f2i_r", "release_r")]
    public void Plan_SelectsLaneReleaseAndExactWrapAroundTime(
        NPCAnimationPhaseLane lane,
        string role,
        string releaseEvent)
    {
        NPCExitReleasePlan plan = NPCExitReleaseGate.Plan(role, releaseEvent, lane, lane, 0.75f, 2f, 1f, 0f);

        Assert.Equal(role, plan.ExitRole);
        Assert.Equal(lane, plan.Lane);
        Assert.Equal(releaseEvent, plan.ReleaseEvent);
        Assert.Equal(0f, plan.TargetPhase);
        Assert.Equal(0.5f, plan.TimeToReleaseSeconds, 6);
    }

    [Fact]
    public void Plan_OppositeLaneUsesPackagedEvidenceInsteadOfHalfCycleAssumption()
    {
        float targetPhase = NPCExitReleaseGate.ResolveOppositeTargetPhase(0.6d, 1.0d, 1.6d);
        NPCExitReleasePlan plan = NPCExitReleaseGate.Plan(
            "f2i_r",
            "acquire_r",
            NPCAnimationPhaseLane.Right,
            NPCAnimationPhaseLane.Left,
            0.25f,
            2f,
            1f,
            targetPhase);

        Assert.Equal(0.25f, targetPhase, 6);
        Assert.Equal("acquire_r", plan.ReleaseEvent);
        Assert.Equal(0.25f, plan.TargetPhase, 6);
        Assert.Equal(0f, plan.TimeToReleaseSeconds, 6);
    }

    [Fact]
    public void OppositePhase_MapsWrappedPackageEvidence()
    {
        float targetPhase = NPCExitReleaseGate.ResolveOppositeTargetPhase(1.4d, 0.2d, 1.6d);

        Assert.Equal(0.25f, targetPhase, 6);
    }

    [Fact]
    public void ReleaseGate_AcceptsEitherSideOfWrappedBoundaryWithinStrictTolerance()
    {
        Assert.True(NPCExitReleaseGate.IsAtRelease(0.000009f, 0f));
        Assert.True(NPCExitReleaseGate.IsAtRelease(0.999991f, 0f));
        Assert.False(NPCExitReleaseGate.IsAtRelease(0.998f, 0f));
    }

    [Theory]
    [InlineData(0.0f, 0.0f, 0.0f, 1.0f / 60.0f, true)]
    [InlineData(0.48f, 0.49f, 0.5f, 0.02f, true)]
    [InlineData(0.97f, 0.99f, 0.0f, 0.02f, true)]
    [InlineData(0.97f, 0.01f, 0.0f, 0.01f, true)]
    [InlineData(0.10f, 0.20f, 0.75f, 0.10f, false)]
    public void ReleaseCrossing_CoversSameFrameHalfCycleMissedSampleWrapAndNoEarlyFire(
        float previous,
        float current,
        float target,
        float delta,
        bool expected)
    {
        NPCSustainedCycleObservation observation = Observation(previous, current, delta, wasArmed: true);

        Assert.Equal(expected, NPCExitReleaseGate.ReachesReleaseThisFrame(observation, target));
    }

    [Fact]
    public void ReleaseCrossing_LargeFrameCrossesAnyReleaseOnce()
    {
        NPCSustainedCycleObservation observation = Observation(0.1f, 0.2f, 1.1f, wasArmed: true);

        Assert.True(NPCExitReleaseGate.ReachesReleaseThisFrame(observation, 0.8f));
        Assert.False(NPCExitReleaseGate.ReachesReleaseThisFrame(observation, 0.8f, alreadyExecuted: true));
    }

    [Fact]
    public void ReleaseCrossing_InvalidOrNewlyArmedPastTargetFailsClosed()
    {
        NPCSustainedCycleObservation invalid = Observation(0.9f, 0.95f, 0.1f, wasArmed: true) with
        {
            IsAvailable = false,
        };
        NPCSustainedCycleObservation clampedStateButAdvancingChild = Observation(0.98f, 0.99f, 0.02f, wasArmed: true);
        NPCSustainedCycleObservation newlyArmedAfterCrossing = Observation(0.99f, 0.01f, 0.01f, wasArmed: false);

        Assert.False(NPCExitReleaseGate.ReachesReleaseThisFrame(invalid, 0f));
        Assert.True(NPCExitReleaseGate.ReachesReleaseThisFrame(clampedStateButAdvancingChild, 0f));
        Assert.False(NPCExitReleaseGate.ReachesReleaseThisFrame(newlyArmedAfterCrossing, 0f));
    }

    [Fact]
    public void SustainedClock_TracksWrapAndUsesExpectedAdvanceOnlyForWholeLargeFrameCycles()
    {
        var clock = NPCSustainedCycleClock.Reset(default);
        clock = NPCSustainedCycleClock.Observe(clock, 0.9f, 0f);
        clock = NPCSustainedCycleClock.Observe(clock, 0.1f, 0.2f);

        Assert.Equal(0.9d, clock.PreviousUnwrappedPhase, 5);
        Assert.Equal(1.1d, clock.CurrentUnwrappedPhase, 5);

        clock = NPCSustainedCycleClock.Observe(clock, 0.2f, 2.1f);
        Assert.Equal(3.2d, clock.CurrentUnwrappedPhase, 5);
    }

    [Fact]
    public void SustainedClock_ResetCreatesNewIdentityAndFirstObservationDoesNotInventAdvance()
    {
        var first = NPCSustainedCycleClock.Reset(default);
        first = NPCSustainedCycleClock.Observe(first, 0.7f, 3.4f);
        var reset = NPCSustainedCycleClock.Reset(first);
        reset = NPCSustainedCycleClock.Observe(reset, 0.2f, 1.1f);

        Assert.Equal(0.7d, first.CurrentUnwrappedPhase, 6);
        Assert.True(reset.Generation > first.Generation);
        Assert.Equal(0.2d, reset.CurrentUnwrappedPhase, 6);
    }

    [Fact]
    public void SustainedClock_PublishedTravelDoesNotCreatePhaseOrOccurrenceUntilTheTreeEvaluatesIt()
    {
        var clock = NPCSustainedCycleClock.Reset(default);
        var pending = NPCSustainedCycleClock.ObserveEvaluatedTravel(
            clock,
            sustainedStateEvaluated: false,
            phase: 0.66f,
            expectedAdvance: 0.12f);

        Assert.False(pending.IsAvailable);
        Assert.Equal(clock.Generation, pending.Generation);

        var firstLive = NPCSustainedCycleClock.ObserveEvaluatedTravel(
            pending,
            sustainedStateEvaluated: true,
            phase: 0.66f,
            expectedAdvance: 0.12f);
        var nextLive = NPCSustainedCycleClock.ObserveEvaluatedTravel(
            firstLive,
            sustainedStateEvaluated: true,
            phase: 0.78f,
            expectedAdvance: 0.12f);
        var departed = NPCSustainedCycleClock.ObserveEvaluatedTravel(
            nextLive,
            sustainedStateEvaluated: false,
            phase: 0f,
            expectedAdvance: 0f);

        Assert.Equal(0.66d, firstLive.PreviousUnwrappedPhase, 5);
        Assert.Equal(0.66d, firstLive.CurrentUnwrappedPhase, 5);
        Assert.Equal(0.66d, nextLive.PreviousUnwrappedPhase, 5);
        Assert.Equal(0.78d, nextLive.CurrentUnwrappedPhase, 5);
        Assert.False(departed.IsAvailable);
        Assert.True(departed.Generation > nextLive.Generation);
    }

    [Fact]
    public void OccurrenceCrossing_IgnoresEarlierSamePhaseAndCrossesSelectedLaterCycle()
    {
        NPCReleaseOccurrence occurrence = NPCExitReleaseGate.SelectOccurrence(
            4,
            0.9d,
            0f,
            NPCAnimationPhaseLane.Left,
            "release_l",
            additionalCycles: 1);
        NPCSustainedCycleObservation early = UnwrappedObservation(4, 0.9d, 1.1d, 0.2f, wasArmed: true);
        NPCSustainedCycleObservation selected = UnwrappedObservation(4, 1.9d, 2.01d, 0.11f, wasArmed: true);

        Assert.Equal(2L, occurrence.CycleOrdinal);
        Assert.Equal(
            NPCReleaseOccurrenceCrossingStatus.Pending,
            NPCExitReleaseGate.OccurrenceCrossingStatus(early, occurrence));
        Assert.Equal(
            NPCReleaseOccurrenceCrossingStatus.Crossed,
            NPCExitReleaseGate.OccurrenceCrossingStatus(selected, occurrence));
    }

    [Fact]
    public void OccurrenceCrossing_MissedResetAndDuplicateFailClosed()
    {
        NPCReleaseOccurrence occurrence = NPCExitReleaseGate.SelectOccurrence(
            2,
            0.4d,
            0.5f,
            NPCAnimationPhaseLane.Right,
            "release_r");
        NPCSustainedCycleObservation missed = UnwrappedObservation(2, 0.6d, 0.7d, 0.1f, wasArmed: true);
        NPCSustainedCycleObservation reset = UnwrappedObservation(3, 0.4d, 0.5d, 0.1f, wasArmed: true);

        Assert.Equal(
            NPCReleaseOccurrenceCrossingStatus.Missed,
            NPCExitReleaseGate.OccurrenceCrossingStatus(missed, occurrence));
        Assert.Equal(
            NPCReleaseOccurrenceCrossingStatus.Invalid,
            NPCExitReleaseGate.OccurrenceCrossingStatus(reset, occurrence));
        Assert.Equal(
            NPCReleaseOccurrenceCrossingStatus.Invalid,
            NPCExitReleaseGate.OccurrenceCrossingStatus(missed, occurrence, alreadyExecuted: true));
    }

    private static NPCSustainedCycleObservation Observation(float previous, float current, float delta, bool wasArmed)
        => new(true, wasArmed, previous, current, 1f, 1f, delta);

    private static NPCSustainedCycleObservation UnwrappedObservation(
        long generation,
        double previous,
        double current,
        float delta,
        bool wasArmed) => new(
        true,
        wasArmed,
        (float)(previous - Math.Floor(previous)),
        (float)(current - Math.Floor(current)),
        1f,
        1f,
        delta,
        generation,
        previous,
        current);

}
