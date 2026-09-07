using AlleyCat.Navigation;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCConfinedAbsorptionTests
{
    [Fact]
    public void CumulativeFraction_IsZeroAtBoundariesAndExactMidPhase()
    {
        Assert.Equal(0f, NPCConfinedAbsorption.CumulativeFraction(0f), 7);
        Assert.Equal(1f, NPCConfinedAbsorption.CumulativeFraction(1f), 7);
        Assert.Equal(0.5f, NPCConfinedAbsorption.CumulativeFraction(0.5f), 7);
        // Zero boundary slope: near-boundary phases absorb almost nothing.
        Assert.Equal(0f, NPCConfinedAbsorption.CumulativeFraction(0.001f), 4);
        Assert.Equal(1f, NPCConfinedAbsorption.CumulativeFraction(0.999f), 4);
    }

    [Fact]
    public void CumulativeFraction_ClampsAndSanitisesPhase()
    {
        Assert.Equal(0f, NPCConfinedAbsorption.CumulativeFraction(-0.5f), 7);
        Assert.Equal(1f, NPCConfinedAbsorption.CumulativeFraction(1.5f), 7);
        // Non-finite phases sanitise to a safe no-absorption boundary.
        Assert.Equal(0f, NPCConfinedAbsorption.CumulativeFraction(float.NaN), 7);
        Assert.Equal(0f, NPCConfinedAbsorption.CumulativeFraction(float.PositiveInfinity), 7);
    }

    [Fact]
    public void Increments_SumExactlyToAbsorbedResidualUnderFrameQuantisation()
    {
        foreach (float residual in new[] { 0.05f, -0.05f, 0.2f, -1.2398256f })
        {
            float sum = 0f;
            const int frames = 97;
            for (int frame = 1; frame <= frames; frame++)
            {
                sum += NPCConfinedAbsorption.Increment(
                    residual,
                    (frame - 1f) / frames,
                    frame / (float)frames);
            }

            Assert.Equal(residual, sum, 6);
        }
    }

    [Fact]
    public void Increments_AreMidPhaseWeightedWithZeroBoundarySteps()
    {
        float first = NPCConfinedAbsorption.Increment(0.08f, 0f, 1f / 98f);
        float last = NPCConfinedAbsorption.Increment(0.08f, 97f / 98f, 1f);
        float mid = NPCConfinedAbsorption.Increment(0.08f, 48f / 98f, 49f / 98f);

        Assert.True(first < mid / 20f, $"Boundary step {first} was not near-zero against mid {mid}.");
        Assert.True(last < mid / 20f, $"Boundary step {last} was not near-zero against mid {mid}.");
        Assert.True(mid > 0f);
        Assert.True(NPCConfinedAbsorption.Increment(float.NaN, 0f, 1f) == 0f);
    }

    [Fact]
    public void RateWeight_IsZeroAtBoundariesAndIntegratesToOne()
    {
        Assert.Equal(0f, NPCConfinedAbsorption.RateWeight(0f), 7);
        Assert.Equal(0f, NPCConfinedAbsorption.RateWeight(1f), 7);
        Assert.True(NPCConfinedAbsorption.RateWeight(0.5f) > NPCConfinedAbsorption.RateWeight(0.25f));

        const int steps = 1000;
        float integral = 0f;
        for (int step = 0; step < steps; step++)
        {
            integral += NPCConfinedAbsorption.RateWeight((step + 0.5f) / steps) / steps;
        }

        Assert.Equal(1f, integral, 3);
    }
}
