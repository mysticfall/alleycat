using AlleyCat.Navigation;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class AnimationPhasePublicationTests
{
    [Fact]
    public void FiniteClock_RetainsEntryInteriorAndEndpointSemantics()
    {
        float[] positions = [0f, 0.2f, 0.75f, 1f, 1.25f];
        float[] expected = [0f, 0.2f, 0.75f, 1f, 1f];
        float previous = -1f;

        for (int index = 0; index < positions.Length; index++)
        {
            Assert.True(AnimationPhasePublication.TryNormalise(positions[index], 1f, isCyclic: false, out float phase));
            Assert.Equal(expected[index], phase, 6);
            Assert.True(phase >= previous);
            previous = phase;
        }
    }

    [Fact]
    public void CyclicClock_PreservesWrappingSemantics()
    {
        Assert.True(AnimationPhasePublication.TryNormalise(0f, 1f, isCyclic: true, out float entry));
        Assert.True(AnimationPhasePublication.TryNormalise(0.75f, 1f, isCyclic: true, out float interior));
        Assert.True(AnimationPhasePublication.TryNormalise(1f, 1f, isCyclic: true, out float endpoint));
        Assert.True(AnimationPhasePublication.TryNormalise(1.25f, 1f, isCyclic: true, out float wrapped));

        Assert.Equal(0f, entry);
        Assert.Equal(0.75f, interior);
        Assert.Equal(0f, endpoint);
        Assert.Equal(0.25f, wrapped);
    }

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(-0.01f, 1f)]
    [InlineData(0f, float.NaN)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(0f, 0f)]
    [InlineData(0f, -1f)]
    public void InvalidClock_FailsClosed(float position, float length)
    {
        Assert.False(AnimationPhasePublication.TryNormalise(position, length, isCyclic: false, out float finitePhase));
        Assert.False(AnimationPhasePublication.TryNormalise(position, length, isCyclic: true, out float cyclicPhase));
        Assert.Equal(0f, finitePhase);
        Assert.Equal(0f, cyclicPhase);
    }
}
