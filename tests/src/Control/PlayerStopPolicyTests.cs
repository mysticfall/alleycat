using AlleyCat.Control.Locomotion;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

public sealed class PlayerStopPolicyTests
{
    [Theory]
    [InlineData(1.0f, 0.5275f, 0.4f, 0.2f, 1f, 4f, 2.637f)]
    [InlineData(1.2667f, 0.5864f, 0.4f, 0.2f, 1f, 4f, 3.167f)]
    [InlineData(0.56f, 0.20f, 0.4f, 0.2f, 1f, 4f, 1.4f)]
    public void ResolveStopPlaybackRate_UsesTheTighterOfTimeAndDisplacementBudgets(
        float clipSeconds,
        float authoredDisplacementMetres,
        float targetSeconds,
        float targetDisplacementMetres,
        float minimumRate,
        float maximumRate,
        float expectedRate)
    {
        // Production figures measured from the run-02 traces (runtime displacement inputs: catalogue
        // endpoint × the rig's ≈0.837 motion scale): the forward lane (1.0 s clip authoring 0.5275 m) is
        // displacement-bound — 0.2 × 1.0 / 0.5275 ≈ 0.379 s is tighter than the 0.4 s time target, so the
        // clip compresses by 1.0 / 0.379 ≈ 2.6375x. The backward lane (1.2667 s authoring 0.5864 m) is
        // time-bound (the displacement term is 0.432 s), so the rate is 1.2667 / 0.4 ≈ 3.167x. The third
        // row is the synthetic fixture lane (motion scale 1): 0.2 × 0.56 / 0.20 = 0.56 s, so the time
        // target binds at 0.56 / 0.4 = 1.4x.
        float rate = PlayerStopPolicy.ResolveStopPlaybackRate(
            clipSeconds, authoredDisplacementMetres, targetSeconds, targetDisplacementMetres, minimumRate, maximumRate);

        Assert.Equal(expectedRate, rate, precision: 2);
    }

    [Fact]
    public void ResolveStopPlaybackRate_ZeroDisplacementClip_FallsBackToTheTimeBudget()
    {
        // A clip that authors no planar travel (a pure pivot) has no displacement budget: the time
        // target alone governs, 1.0 / 0.4 = 2.5x.
        float rate = PlayerStopPolicy.ResolveStopPlaybackRate(1f, 0f, 0.4f, 0.2f, 1f, 4f);

        Assert.Equal(2.5f, rate, precision: 3);
    }

    [Fact]
    public void ResolveStopPlaybackRate_VeryGlidingClip_ClampsToTheMaximumRate()
    {
        // 2.0 s authoring 1.5 m wants 7.5x; governance clamps to the configured 4x ceiling and the
        // in-flight cut machinery owns the remainder of the budget.
        float rate = PlayerStopPolicy.ResolveStopPlaybackRate(2f, 1.5f, 0.4f, 0.2f, 1f, 4f);

        Assert.Equal(4f, rate, precision: 3);
    }

    [Fact]
    public void ResolveStopPlaybackRate_ShortLowTravelClip_ClampsToTheMinimumRate()
    {
        // 0.1 s authoring 0.01 m resolves to 0.25x, but a governed stop never plays slower than authored.
        float rate = PlayerStopPolicy.ResolveStopPlaybackRate(0.1f, 0.01f, 0.4f, 0.2f, 1f, 4f);

        Assert.Equal(1f, rate, precision: 3);
    }

    [Fact]
    public void ResolveStopPlaybackRate_MinimumRateNeverExceedsTheMaximum()
    {
        float rate = PlayerStopPolicy.ResolveStopPlaybackRate(1f, 0.584f, 0.4f, 0.2f, minimumRate: 6f, maximumRate: 4f);

        Assert.Equal(6f, rate, precision: 3);
    }

    [Theory]
    [InlineData(1f, 0f, true)]
    [InlineData(-1f, 0f, true)]
    [InlineData(0.8f, -0.3f, true)]
    [InlineData(0.7071f, 0.7071f, false)]
    [InlineData(0.3f, 0.8f, false)]
    [InlineData(0f, -1f, false)]
    [InlineData(0f, 0f, false)]
    public void IsLateralDominantRelease_ClassifiesByStrictAxisDominance(
        float lateral,
        float forward,
        bool expected)
        // Pure lateral and lateral-heavy diagonals are lateral-dominant (neutral stop policy); an even
        // 45-degree diagonal and every forward/backward-dominant blend keep their authored stop lanes.
        => Assert.Equal(expected, PlayerStopPolicy.IsLateralDominantRelease(lateral, forward));
}
