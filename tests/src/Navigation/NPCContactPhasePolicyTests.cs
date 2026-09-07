using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCContactPhasePolicyTests
{
    [Fact]
    public void DeclaredSupportLane_IsOppositeSelectedReleaseLane()
    {
        Assert.Equal(NPCAnimationPhaseLane.Right, NPCContactPhasePolicy.DeclaredSupportLane(NPCAnimationPhaseLane.Left));
        Assert.Equal(NPCAnimationPhaseLane.Left, NPCContactPhasePolicy.DeclaredSupportLane(NPCAnimationPhaseLane.Right));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.05f, false)]
    [InlineData(0.06f, true)]
    [InlineData(0.12f, true)]
    [InlineData(0.13f, false)]
    [InlineData(0.95f, false)]
    [InlineData(0.5f, false)]
    public void DeclaredSupportWindow_UsesMeasuredPostReleasePlateau(float phase, bool expected)
        => Assert.Equal(expected, NPCContactPhasePolicy.IsDeclaredSupportWindow(phase));

    [Fact]
    public void ContactQualification_RejectsRaisedOrVerticallyMovingFoot()
    {
        Assert.True(NPCContactPhasePolicy.QualifiesContact(0.005f, 0.04f));
        Assert.False(NPCContactPhasePolicy.QualifiesContact(0.016f, 0.04f));
        Assert.False(NPCContactPhasePolicy.QualifiesContact(0.005f, 0.11f));
    }

    [Fact]
    public void DeliberatelyTranslatedQualifiedFoot_ExceedsVisibleSlipGuard()
    {
        const float delta = 1f / 60f;
        Vector3 previous = new(0f, 0f, 0f);
        Vector3 translated = new(0.004f, 0f, 0f);

        Assert.True(NPCContactPhasePolicy.QualifiesContact(0f, 0f));
        Assert.Equal(0.24f, NPCContactPhasePolicy.PlanarSlipSpeed(previous, translated, delta), 5);
        Assert.True(NPCContactPhasePolicy.ExceedsVisibleSlip(0.004f, delta));
    }

    [Fact]
    public void SupportWindowAndYawDisplacement_ExposePotentialPlantedFootDrag()
    {
        Assert.True(NPCContactPhasePolicy.IsDeclaredSupportWindow(0.09f));
        Assert.Equal(0.005f, NPCContactPhasePolicy.YawInducedTangentialDisplacement(0.5f, 0.01f), 5);
    }
}
