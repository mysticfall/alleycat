using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCTerminalReleaseSchedulerTests
{
    [Fact]
    public void SharedPhase_AdvancesMonotonicallyAcrossSeam()
        => Assert.Equal(0.03f, NPCTerminalReleaseScheduler.MonotonicPhaseAdvance(0.98f, 0.01f), 5);

    [Fact]
    public void RejectedCandidates_DoNotMutatePublishedMotionState()
    {
        NPCTerminalReleaseScheduler scheduler = new();
        NPCTerminalPublishedState published = new(
            new Vector2(0.2f, 0.4f),
            1f,
            new Vector2(0f, 0.5f),
            0.1f,
            0.9f,
            NPCAnimationPhaseLane.Left);
        scheduler.Publish(published, true);

        NPCTerminalSchedulerDecision decision = scheduler.Evaluate(
            Candidate(0) with
            {
                EndpointServiceable = false
            },
            Candidate(1) with
            {
                VelocityCompatible = false
            });

        Assert.False(decision.Commit);
        Assert.Equal(-1, decision.CandidateIndex);
        Assert.True((decision.Rejection & NPCTerminalCandidateRejection.EndpointUnserviceable) != 0);
        Assert.True((decision.Rejection & NPCTerminalCandidateRejection.VelocityMismatch) != 0);
        Assert.Equal(published, scheduler.PublishedState);
    }

    [Fact]
    public void LaterCompatibleOccurrence_CommitsAfterRejectedPair()
    {
        NPCTerminalReleaseScheduler scheduler = new();
        NPCTerminalSchedulerDecision rejected = scheduler.Evaluate(
            Candidate(0) with
            {
                YawResidual = 2f
            },
            Candidate(1) with
            {
                VelocityCompatible = false
            });

        NPCTerminalSchedulerDecision later = scheduler.Evaluate(
            Candidate(0) with
            {
                EndpointServiceable = false
            },
            Candidate(1));

        Assert.False(rejected.Commit);
        Assert.True(later.Commit);
        Assert.Equal(1, later.CandidateIndex);
        Assert.Equal(NPCTerminalCandidateRejection.None, later.Rejection);
    }

    [Fact]
    public void RepeatedRejectedPairs_RemainDecisionNeutral()
    {
        NPCTerminalReleaseScheduler scheduler = new();

        for (int frame = 0; frame < 90; frame++)
        {
            NPCTerminalSchedulerDecision probe = scheduler.Evaluate(
                Candidate(0) with
                {
                    ContactCompatible = false
                },
                Candidate(1) with
                {
                    ContactCompatible = false
                });

            Assert.False(probe.Commit);
            Assert.Equal(-1, probe.CandidateIndex);
        }

        NPCTerminalSchedulerDecision afterRejections = scheduler.Evaluate(Candidate(0), Candidate(1));
        Assert.True(afterRejections.Commit);
    }

    [Fact]
    public void CompatibleCandidates_SelectEarliestCrossingBeforeResidualMagnitude()
    {
        NPCTerminalReleaseScheduler scheduler = new();

        NPCTerminalSchedulerDecision decision = scheduler.Evaluate(
            Candidate(0) with
            {
                TimeToRelease = 0.2f,
                ForecastResidual = 0.02f
            },
            Candidate(1) with
            {
                TimeToRelease = 0.1f,
                ForecastResidual = 0.2f
            });

        Assert.True(decision.Commit);
        Assert.Equal(1, decision.CandidateIndex);
    }

    [Fact]
    public void EqualCrossingsSelectFirstCurrentLaneDeterministically()
    {
        NPCTerminalReleaseScheduler scheduler = new();

        NPCTerminalSchedulerDecision decision = scheduler.Evaluate(
            Candidate(0) with
            {
                TimeToRelease = 0.25f,
                ForecastResidual = 0.3f
            },
            Candidate(1) with
            {
                TimeToRelease = 0.25f,
                ForecastResidual = 0.01f
            });

        Assert.Equal(0, decision.CandidateIndex);
    }

    private static NPCTerminalReleaseCandidate Candidate(int index) => new(
        index,
        NPCAnimationPhaseLane.Left,
        new Vector2(0f, 0.5f),
        0.5f,
        0.01f,
        0.02f,
        true,
        true,
        true,
        true);
}
