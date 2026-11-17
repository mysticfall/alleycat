using AlleyCat.Motion;
using Xunit;

namespace AlleyCat.Tests.Motion;

/// <summary>ANIM-002 elapsed phase, finite coverage and interruption contracts independent of Godot/inference.</summary>
public sealed class SourcePhaseClockTests
{
    /// <summary>Approved angular-episode offset is explicit phase, while elapsed time, cycle count and stop remain independent.</summary>
    [Fact]
    public void InitialCyclicPhase_LeavesBoundsElapsedRateAndFreezeCoherent()
    {
        var clock = new SourcePhaseClock();
        clock.Activate("left_turn", 46, 105, 10, 60, 11);
        SourcePhaseClock.Selection first = clock.Select(10, 49.0 / 30);
        Assert.Equal(57, first.SourceFrame);
        Assert.Equal(106, first.WindowStart);
        Assert.Equal(0, first.CycleIndex);
        Assert.Equal(57, first.InCycleFrame);
        SourcePhaseClock.Selection later = clock.Select(12, 49.0 / 30);
        Assert.Equal(117, later.SourceFrame);
        Assert.Equal(166, later.WindowStart);
        Assert.Equal(1, later.CycleIndex);
        Assert.Equal(57, later.InCycleFrame);
        clock.Stop(12);
        Assert.Equal(117, clock.Select(20, 0).SourceFrame);
        Assert.False(clock.IsCurrent(later));
        clock.Activate("forward", 58, 93, 20, 36);
        Assert.Equal(58, clock.Select(20, 0).SourceFrame); // No leaked angular offset on ordinary activation.
    }

    /// <summary>Explicit offsets never make finite content wrap or admit a phase outside its declared repeat.</summary>
    [Fact]
    public void InitialPhase_RequiresDeclaredCyclicCoverage()
    {
        var clock = new SourcePhaseClock();
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => clock.Activate("finite", 46, 106, 0, initialPhaseFrames: 11));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => clock.Activate("turn", 46, 105, 0, 60, 60));
        clock.Activate("finite", 46, 106, 0);
        Assert.Equal(46, clock.Select(0, 0).SourceFrame);
        Assert.False(clock.Select(2, 0).Available);
    }

    /// <inheritdoc/>
    [Fact]
    public void ElapsedClock_AdvancesDuringHoldsWithoutCommittingSampleAhead()
    {
        var clock = new SourcePhaseClock();
        clock.Activate("forward", 46, 106, 10);
        SourcePhaseClock.Selection a = clock.Select(10, .25);
        SourcePhaseClock.Selection b = clock.Select(10.6, .25);
        Assert.Equal(46, a.SourceFrame);
        Assert.Equal(53, a.WindowStart);
        Assert.Equal(64, b.SourceFrame, 8);
        Assert.Equal(71, b.WindowStart);
        Assert.True(clock.IsCurrent(a));
        Assert.Equal(64, clock.Select(10.6, 0).SourceFrame, 8);
    }

    /// <inheritdoc/>
    [Fact]
    public void NonCyclicEnd_IsExplicitWithoutWrappingOrClamping()
    {
        var clock = new SourcePhaseClock();
        clock.Activate("left_noncross", 196, 256, 0);
        Assert.Equal(253, clock.Select(1.65, .25).WindowStart);
        SourcePhaseClock.Selection exhausted = clock.Select(1.7, .25);
        Assert.Equal(254, exhausted.WindowStart);
        Assert.False(exhausted.Available);
        clock.Stop(1.7);
        Assert.Equal(247, clock.Select(20, .25).SourceFrame, 8);
        Assert.False(clock.Select(20, 0).Available);
    }

    /// <inheritdoc/>
    [Fact]
    public void ExplicitRoleChangeAndRelease_RetireOldSnapshotsImmediately()
    {
        var clock = new SourcePhaseClock();
        clock.Activate("forward", 46, 106, 0);
        SourcePhaseClock.Selection forward = clock.Select(.6, .25);
        clock.Activate("left_noncross", 196, 256, 1.4);
        Assert.False(clock.IsCurrent(forward));
        SourcePhaseClock.Selection left = clock.Select(1.4, .25);
        Assert.Equal(196, left.SourceFrame);
        Assert.Equal(203, left.WindowStart);
        Assert.True(clock.IsCurrent(left));
        clock.Stop(2);
        Assert.False(clock.IsCurrent(left));
        Assert.Equal(214, clock.Select(3, 0).SourceFrame, 8);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => clock.Select(2.9, 0));
    }
}
