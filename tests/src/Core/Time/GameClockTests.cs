using AlleyCat.Core.Time;
using Xunit;

namespace AlleyCat.Tests.Core.Time;

/// <summary>
/// Deterministic unit coverage for game clock elapsed-time semantics.
/// </summary>
public sealed class GameClockTests
{
    /// <summary>
    /// A freshly created clock reports zero elapsed time.
    /// </summary>
    [Fact]
    public void NowSeconds_StartsAtZeroWhenCreated()
    {
        GameClock clock = new(() => 500d);

        Assert.Equal(0d, clock.NowSeconds);
    }

    /// <summary>
    /// Elapsed seconds advance with the supplied timestamp source.
    /// </summary>
    [Fact]
    public void NowSeconds_AdvancesWithTimestampSource()
    {
        double probe = 1000d;
        GameClock clock = new(() => probe);

        probe = 1002.5d;

        Assert.Equal(2.5d, clock.NowSeconds);
    }

    /// <summary>
    /// Sources that move backwards are clamped to zero rather than reporting negative time.
    /// </summary>
    [Fact]
    public void NowSeconds_ClampsBackwardsSourcesToZero()
    {
        double probe = 10d;
        GameClock clock = new(() => probe);

        probe = 4d;

        Assert.Equal(0d, clock.NowSeconds);
    }

    /// <summary>
    /// Non-finite timestamps fail fast instead of propagating invalid elapsed values.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NowSeconds_ThrowsWhenSourceReturnsNonFiniteTimestamp(double timestamp)
    {
        GameClock clock = new(() => timestamp);

        _ = Assert.Throws<InvalidOperationException>(() => clock.NowSeconds);
    }

    /// <summary>
    /// The default source reports finite, non-negative, non-decreasing time.
    /// </summary>
    [Fact]
    public void NowSeconds_DefaultSourceIsMonotonicAndNonNegative()
    {
        GameClock clock = new();

        double first = clock.NowSeconds;
        double second = clock.NowSeconds;

        Assert.True(double.IsFinite(first));
        Assert.True(first >= 0d);
        Assert.True(second >= first);
    }

    /// <summary>
    /// The testing seam swaps the source and re-baselines the origin to the new source's current value.
    /// </summary>
    [Fact]
    public void SetTimestampSourceForTesting_RebaselinesOriginToNewSource()
    {
        double probe = 10d;
        GameClock clock = new(() => probe);

        probe = 15d;
        Assert.Equal(5d, clock.NowSeconds);

        double restart = 100d;
        clock.SetTimestampSourceForTesting(() => restart);

        Assert.Equal(0d, clock.NowSeconds);

        restart = 103d;
        Assert.Equal(3d, clock.NowSeconds);
    }

    /// <summary>
    /// The testing seam rejects a null source.
    /// </summary>
    [Fact]
    public void SetTimestampSourceForTesting_ThrowsForNullSource()
    {
        GameClock clock = new(() => 0d);

        _ = Assert.Throws<ArgumentNullException>(() => clock.SetTimestampSourceForTesting(null!));
    }
}
