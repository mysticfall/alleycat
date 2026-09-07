using AlleyCat.Control.Locomotion;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

public sealed class PlayerTravelSeamAlignmentTests
{
    [Fact]
    public void ResolveStartOffset_PlacesDeclaredLeftLaneReleaseAtSeam()
    {
        StandingLocomotionClip clip = SyntheticTravelFixture.CreateClip(
            duration: SyntheticTravelFixture.CycleSeconds,
            leftRelease: SyntheticTravelFixture.ReleaseTimeSeconds,
            rightRelease: 0d);

        float offset = PlayerTravelSeamAlignment.ResolveStartOffset(clip, rightLane: false, commonCycleSeconds: (float)SyntheticTravelFixture.CycleSeconds);

        Assert.Equal((float)SyntheticTravelFixture.ReleaseTimeSeconds, offset, 5);
    }

    [Fact]
    public void ResolveStartOffset_ScalesEventIntoShorterCommonCycle()
    {
        const double sourceCycle = 1.2d;
        const double release = 0.9d;
        StandingLocomotionClip clip = SyntheticTravelFixture.CreateClip(sourceCycle, release, null);

        float offset = PlayerTravelSeamAlignment.ResolveStartOffset(clip, rightLane: false, commonCycleSeconds: (float)SyntheticTravelFixture.CycleSeconds);

        Assert.Equal((float)(release * SyntheticTravelFixture.CycleSeconds / sourceCycle), offset, 5);
    }

    [Fact]
    public void ResolveStartOffset_KeepsRightLaneSeamAlignedWalkAtZero()
    {
        StandingLocomotionClip clip = SyntheticTravelFixture.CreateClip(SyntheticTravelFixture.CycleSeconds, SyntheticTravelFixture.ReleaseTimeSeconds, 0d);

        float offset = PlayerTravelSeamAlignment.ResolveStartOffset(clip, rightLane: true, commonCycleSeconds: (float)SyntheticTravelFixture.CycleSeconds);

        Assert.Equal(0f, offset, 5);
    }

    [Fact]
    public void ResolveStartOffset_RejectsMissingLaneEvent()
    {
        StandingLocomotionClip clip = SyntheticTravelFixture.CreateClip(duration: 1.1d, leftRelease: null, rightRelease: 0.4d);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PlayerTravelSeamAlignment.ResolveStartOffset(clip, rightLane: false, commonCycleSeconds: (float)SyntheticTravelFixture.CycleSeconds));

        Assert.Contains("declares no Left phase event", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveStartOffset_RejectsNonPositiveCommonCycle()
    {
        StandingLocomotionClip clip = SyntheticTravelFixture.CreateClip(SyntheticTravelFixture.CycleSeconds, SyntheticTravelFixture.ReleaseTimeSeconds, 0d);

        float nonPositive = PlayerTravelSeamAlignment.ResolveStartOffset(clip, rightLane: false, commonCycleSeconds: 0f);
        float nonFinite = PlayerTravelSeamAlignment.ResolveStartOffset(clip, rightLane: false, commonCycleSeconds: float.NaN);

        Assert.Equal((float)SyntheticTravelFixture.ReleaseTimeSeconds, nonPositive, 5);
        Assert.Equal((float)SyntheticTravelFixture.ReleaseTimeSeconds, nonFinite, 5);
    }
}
