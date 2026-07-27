using System.Reflection;
using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Focused pure coverage for the ANIM-003 compact response profiles.
/// </summary>
public sealed class LocomotionResponseProfileTests
{
    /// <inheritdoc/>
    [Theory]
    [InlineData(StandingLocomotionCharacter.ReferenceFemale)]
    [InlineData(StandingLocomotionCharacter.ReferenceMale)]
    public void AuthoredProfiles_ContainFiniteValidatedResponses(StandingLocomotionCharacter character)
    {
        LocomotionResponseProfile profile = StandingLocomotionResponseProfiles.Get(character);

        Assert.Equal(character, profile.Character);
        foreach (LocomotionCycleResponse response in Responses(profile))
        {
            Assert.True(response.PlanarDisplacement.IsFinite());
            Assert.True(response.PlanarVelocity.IsFinite());
            Assert.True(float.IsFinite(response.AveragePlanarSpeed));
            Assert.True(float.IsFinite(response.Yaw));
            Assert.True(float.IsFinite(response.AngularVelocity));
            Assert.True(response.MetricDurationSeconds > 0.0f);
            Assert.True(response.ImportedTimelineDurationSeconds > 0.0f);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void FemaleProfile_UsesTheMinimalRoleSetAndPreservesWalkArcSigns()
    {
        LocomotionResponseProfile profile = StandingLocomotionResponseProfiles.Get(
            StandingLocomotionCharacter.ReferenceFemale);

        Assert.NotEqual(profile.SideStepLeft.AveragePlanarSpeed, profile.SideStepRight.AveragePlanarSpeed);
        Assert.NotEqual(profile.WalkArcLeft.AveragePlanarSpeed, profile.WalkArcRight.AveragePlanarSpeed);
        Assert.NotEqual(Mathf.Abs(profile.WalkArcLeft.AngularVelocity), Mathf.Abs(profile.WalkArcRight.AngularVelocity));
        Assert.True(profile.SideStepLeft.PlanarVelocity.X < 0.0f);
        Assert.True(profile.SideStepRight.PlanarVelocity.X > 0.0f);
        Assert.True(profile.WalkArcLeft.AngularVelocity > 0.0f);
        Assert.True(profile.WalkArcRight.AngularVelocity < 0.0f);
        Assert.True(profile.TurnInPlaceLeft90.AngularVelocity > 0.0f);
        Assert.True(profile.TurnInPlaceRight90.AngularVelocity < 0.0f);
        Assert.Equal(Mathf.Pi / 2.0f, profile.TurnInPlaceLeft90.Yaw, 4);
        Assert.Equal(-Mathf.Pi / 2.0f, profile.TurnInPlaceRight90.Yaw, 4);
        Assert.NotEqual(profile.WalkArcLeft.MetricDurationSeconds, profile.WalkArcLeft.ImportedTimelineDurationSeconds);
    }

    /// <inheritdoc/>
    [Fact]
    public void CycleResponse_RejectsNonFiniteOrInvalidTimingData()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new LocomotionCycleResponse(new Vector2(float.NaN, 0.0f), 1.0f, 0.0f, 1.0f, 1.0f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new LocomotionCycleResponse(Vector2.Zero, -1.0f, 0.0f, 1.0f, 1.0f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new LocomotionCycleResponse(Vector2.Zero, 0.0f, 0.0f, 0.0f, 1.0f));
    }

    /// <inheritdoc/>
    [Fact]
    public void ProfileAndCycleResponse_ExposeNoWritablePublicProperties()
    {
        Assert.All(
            typeof(LocomotionResponseProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.False(property.CanWrite));
        Assert.All(
            typeof(LocomotionCycleResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.False(property.CanWrite));
    }

    private static IEnumerable<LocomotionCycleResponse> Responses(LocomotionResponseProfile profile)
    {
        yield return profile.Forwards;
        yield return profile.Backwards;
        yield return profile.SideStepLeft;
        yield return profile.SideStepRight;
        yield return profile.WalkArcLeft;
        yield return profile.WalkArcRight;
        yield return profile.TurnInPlaceLeft90;
        yield return profile.TurnInPlaceRight90;
    }
}
