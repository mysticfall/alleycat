using AlleyCat.IK;
using Xunit;

namespace AlleyCat.Tests.IK;

/// <summary>Independent scalar elapsed-history contracts.</summary>
public sealed class FootPlantingElapsedClockTests
{
    /// <summary>Initial anchoring and repeated adjacent inputs preserve exact doubles.</summary>
    [Fact]
    public void FirstCommitIsZeroAndAdjacentIntervalsRetainExactBits()
    {
        var clock = new FootPlantingElapsedClock();
        clock.Observe(10, 0.125);
        clock.Observe(11, 0.25);
        Assert.Equal(0d, clock.ForCommit(11));
        clock.Acknowledge(11);
        double delta = 1d / 60d;
        for (ulong tick = 12; tick < 512; tick++)
        {
            clock.Observe(tick, delta);
            clock.Observe(tick, delta);
            Assert.Equal(BitConverter.DoubleToInt64Bits(delta), BitConverter.DoubleToInt64Bits(clock.ForCommit(tick)));
            clock.Acknowledge(tick);
            clock.Observe(tick, delta);
        }
    }

    /// <summary>Skipped commits and non-consuming previews retain variable history.</summary>
    [Fact]
    public void SkippedCommitsAccumulateActualVariableHistoryAndPreviewDoesNotConsume()
    {
        var clock = new FootPlantingElapsedClock();
        clock.Observe(1, 0.5);
        clock.Acknowledge(1);
        clock.Observe(2, 0.125);
        clock.Observe(3, 0.0625);
        Assert.Equal(0.21875, clock.Peek(4, 0.03125));
        Assert.Equal(0.21875, clock.Peek(4, 0.03125));
        clock.Observe(4, 0.03125);
        Assert.Equal(0.21875, clock.Peek(4, 1));
        Assert.Equal(0.21875, clock.ForCommit(4));
        // A read without successful solver return must not acknowledge elapsed.
        Assert.Equal(0.21875, clock.ForCommit(4));
        clock.Acknowledge(4);
        clock.Observe(4, 0.03125);
        clock.Observe(5, 0);
        Assert.Equal(0d, clock.ForCommit(5));
        clock.Acknowledge(5);
        clock.Observe(6, 0.125);
        Assert.Equal(0.125, clock.ForCommit(6));
    }

    /// <summary>Invalid history requires an explicit binding reset.</summary>
    [Fact]
    public void ConflictingDuplicateMissingHistoryAndReversalRefuseUntilRebind()
    {
        foreach (int failure in new[] { 0, 1, 2, 3, 4 })
        {
            var clock = new FootPlantingElapsedClock();
            clock.Observe(10, 0.125);
            clock.Acknowledge(10);
            _ = Assert.Throws<InvalidOperationException>(() =>
            {
                switch (failure)
                {
                    case 0:
                        clock.Observe(10, 0.25);
                        break;
                    case 1:
                        clock.Observe(12, 0.125);
                        break;
                    case 2:
                        clock.Observe(9, 0.125);
                        break;
                    case 3:
                        clock.Observe(11, double.NaN);
                        break;
                    default:
                        _ = clock.ForCommit(11);
                        break;
                }
            });
            _ = Assert.Throws<InvalidOperationException>(() => clock.ForCommit(10));
            clock.ResetBinding();
            clock.Observe(100, 0.25);
            Assert.Equal(0d, clock.ForCommit(100));
            clock.Acknowledge(100);
            clock.Observe(101, 0.125);
            Assert.Equal(0.125, clock.ForCommit(101));
        }
    }

    /// <summary>Lazy rig binding does not double-consume a later observation of the anchor tick.</summary>
    [Fact]
    public void BindingAfterAdmissionStillAllowsLateSameTickObservation()
    {
        var clock = new FootPlantingElapsedClock();
        clock.Observe(20, 0.125);
        clock.ResetBinding();
        clock.Acknowledge(20);
        clock.Observe(20, 0.125);
        clock.Observe(21, 0.0625);
        Assert.Equal(0.0625, clock.ForCommit(21));
        _ = Assert.Throws<InvalidOperationException>(() => clock.Peek(23, 0.125));
        Assert.Equal(0.0625, clock.ForCommit(21));
    }
}
