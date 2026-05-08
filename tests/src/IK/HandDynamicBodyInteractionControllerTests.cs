using AlleyCat.IK;
using Xunit;

namespace AlleyCat.Tests.IK;

/// <summary>
/// Unit coverage for explicit hand-driven dynamic rigid-body interaction caps.
/// </summary>
public sealed class HandDynamicBodyInteractionControllerTests
{
    /// <summary>
    /// Verifies the default hand-strength tuning remains biased towards heavier dynamic-body feel.
    /// </summary>
    [Fact]
    public void DefaultStrengthParameters_MatchHeavierInteractionTuning()
    {
        Assert.Equal(0.9f, HandDynamicBodyInteractionController.DefaultImpactApproachSpeedThreshold, precision: 4);
        Assert.Equal(0.08f, HandDynamicBodyInteractionController.DefaultImpactImpulsePerSpeed, precision: 4);
        Assert.Equal(0.30f, HandDynamicBodyInteractionController.DefaultImpactImpulseCap, precision: 4);
        Assert.Equal(0.08f, HandDynamicBodyInteractionController.DefaultSustainedPushSpeedThreshold, precision: 4);
        Assert.Equal(8.0f, HandDynamicBodyInteractionController.DefaultSustainedForcePerSpeed, precision: 4);
        Assert.Equal(4.5f, HandDynamicBodyInteractionController.DefaultSustainedForceCap, precision: 4);
    }

    /// <summary>
    /// Verifies a new fast contact produces a capped impact impulse.
    /// </summary>
    [Fact]
    public void ComputeImpactImpulseMagnitude_NewFastContact_ClampsToCap()
    {
        float impulse = HandDynamicBodyInteractionController.ComputeImpactImpulseMagnitude(
            approachSpeed: 3.0f,
            hadContact: false,
            impactApproachSpeedThreshold: 0.5f,
            impactImpulsePerSpeed: 0.5f,
            impactImpulseCap: 0.8f);

        Assert.Equal(0.8f, impulse, precision: 4);
    }

    /// <summary>
    /// Verifies the impact channel stays silent once the contact is already active.
    /// </summary>
    [Fact]
    public void ComputeImpactImpulseMagnitude_ExistingContact_ReturnsZero()
    {
        float impulse = HandDynamicBodyInteractionController.ComputeImpactImpulseMagnitude(
            approachSpeed: 3.0f,
            hadContact: true,
            impactApproachSpeedThreshold: 0.5f,
            impactImpulsePerSpeed: 0.5f,
            impactImpulseCap: 0.8f);

        Assert.Equal(0.0f, impulse, precision: 4);
    }

    /// <summary>
    /// Verifies the sustained push channel only fires once pressing speed clears the threshold and remains capped.
    /// </summary>
    [Fact]
    public void ComputeSustainedForceMagnitude_PressingContact_ClampsToCap()
    {
        float belowThreshold = HandDynamicBodyInteractionController.ComputeSustainedForceMagnitude(
            approachSpeed: 0.02f,
            sustainedPushSpeedThreshold: 0.05f,
            sustainedForcePerSpeed: 10.0f,
            sustainedForceCap: 2.0f);

        float aboveThreshold = HandDynamicBodyInteractionController.ComputeSustainedForceMagnitude(
            approachSpeed: 0.50f,
            sustainedPushSpeedThreshold: 0.05f,
            sustainedForcePerSpeed: 10.0f,
            sustainedForceCap: 2.0f);

        Assert.Equal(0.0f, belowThreshold, precision: 4);
        Assert.Equal(2.0f, aboveThreshold, precision: 4);
    }

    /// <summary>
    /// Verifies only motion into the contacted body normal counts as pressing speed.
    /// </summary>
    [Fact]
    public void ComputePressSpeed_TangentialOrRetreatingMotion_ReturnsZero()
    {
        float tangentialPressSpeed = HandDynamicBodyInteractionController.ComputePressSpeed(
            targetVelocity: new Godot.Vector3(0.0f, 1.0f, 0.0f),
            contactNormal: new Godot.Vector3(-1.0f, 0.0f, 0.0f));

        float retreatingPressSpeed = HandDynamicBodyInteractionController.ComputePressSpeed(
            targetVelocity: new Godot.Vector3(-1.0f, 0.0f, 0.0f),
            contactNormal: new Godot.Vector3(-1.0f, 0.0f, 0.0f));

        Assert.Equal(0.0f, tangentialPressSpeed, precision: 4);
        Assert.Equal(0.0f, retreatingPressSpeed, precision: 4);
    }

    /// <summary>
    /// Verifies pressing speed measures only the positive component into the contact normal.
    /// </summary>
    [Fact]
    public void ComputePressSpeed_IntoContactNormal_ReturnsPositiveProjectedSpeed()
    {
        float pressSpeed = HandDynamicBodyInteractionController.ComputePressSpeed(
            targetVelocity: new Godot.Vector3(2.0f, 1.0f, 0.0f),
            contactNormal: new Godot.Vector3(-1.0f, 0.0f, 0.0f));

        Assert.Equal(2.0f, pressSpeed, precision: 4);
    }

    /// <summary>
    /// Verifies non-pressing motion produces no impact or sustained transfer.
    /// </summary>
    [Fact]
    public void ComputeTransferMagnitudes_NoPressingMotion_ReturnZero()
    {
        float pressSpeed = HandDynamicBodyInteractionController.ComputePressSpeed(
            targetVelocity: Godot.Vector3.Zero,
            contactNormal: new Godot.Vector3(-1.0f, 0.0f, 0.0f));

        float impact = HandDynamicBodyInteractionController.ComputeImpactImpulseMagnitude(
            approachSpeed: pressSpeed,
            hadContact: false,
            impactApproachSpeedThreshold: 0.5f,
            impactImpulsePerSpeed: 0.5f,
            impactImpulseCap: 0.8f);

        float sustained = HandDynamicBodyInteractionController.ComputeSustainedForceMagnitude(
            approachSpeed: pressSpeed,
            sustainedPushSpeedThreshold: 0.05f,
            sustainedForcePerSpeed: 10.0f,
            sustainedForceCap: 2.0f);

        Assert.Equal(0.0f, impact, precision: 4);
        Assert.Equal(0.0f, sustained, precision: 4);
    }
}
