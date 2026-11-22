using AlleyCat.Mind.AI.Decision;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Decision;

/// <summary>Checks the retry delay shared by scene decision gates.</summary>
public sealed class DecisionFailureBackoffTests
{
    /// <summary>The first failure doubles the base interval; consecutive failures double up to the cap.</summary>
    [Fact]
    public void NextSeconds_FirstFailureDoublesTheIntervalThenDoublesUpToTheCap()
    {
        Assert.Equal(2d, DecisionFailureBackoff.NextSeconds(0d, 1d));
        Assert.Equal(4d, DecisionFailureBackoff.NextSeconds(2d, 1d));
        Assert.Equal(8d, DecisionFailureBackoff.NextSeconds(4d, 1d));
        Assert.Equal(30d, DecisionFailureBackoff.NextSeconds(16d, 1d));
        Assert.Equal(30d, DecisionFailureBackoff.NextSeconds(30d, 1d));
        Assert.Equal(1d, DecisionFailureBackoff.NextSeconds(0d, 0.5d));
    }

    /// <summary>Invalid backoff arguments are rejected.</summary>
    [Fact]
    public void NextSeconds_RejectsInvalidArguments()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => DecisionFailureBackoff.NextSeconds(0d, 0d));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => DecisionFailureBackoff.NextSeconds(0d, double.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => DecisionFailureBackoff.NextSeconds(-1d, 1d));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => DecisionFailureBackoff.NextSeconds(double.PositiveInfinity, 1d));
    }
}
