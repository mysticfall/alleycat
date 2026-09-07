using AlleyCat.Navigation;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCFiniteTransitionCadenceTests
{
    [Fact]
    public void DefaultPolicy_PreservesPackageSourceCadence() => Assert.Equal(1f, NPCFiniteTransitionCadence.SourceSecondsPerWallSecond);

    [Theory]
    [InlineData(0.5f, 1.2d, 0.5f)]
    [InlineData(1.2f, 1.2d, 1.2f)]
    [InlineData(1.3f, 1.2d, 1.2f)]
    public void SourceTimeAtElapsed_MapsWallTimeToBoundedSourceTime(float elapsed, double duration, float expected)
        => Assert.Equal(expected, NPCFiniteTransitionCadence.SourceTimeAtElapsed(elapsed, duration), 6);
}
