using AlleyCat.Control.Locomotion;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

public sealed class ManualTranslationAcceptanceTests
{
    [Fact]
    public void NonNeutralPermittedTranslation_IsAccepted()
        => Assert.True(ManualTranslationAcceptance.IsAccepted(new Vector2(0.4f, 0.6f), 0.15f, LocomotionPermissions.Allowed));

    [Fact]
    public void NeutralisedRawTranslation_IsNotAccepted()
        => Assert.False(ManualTranslationAcceptance.IsAccepted(new Vector2(0.1f, 0f), 0.15f, LocomotionPermissions.Allowed));

    [Fact]
    public void NeutralWaitingOrBrakingTranslation_IsNotAccepted()
        => Assert.False(ManualTranslationAcceptance.IsAccepted(Vector2.Zero, 0.15f, LocomotionPermissions.Allowed));

    [Fact]
    public void BlockedOrLostMovementPermission_IsNotAccepted()
    {
        Vector2 translation = new(0.4f, 0.6f);

        Assert.True(ManualTranslationAcceptance.IsAccepted(translation, 0.15f, LocomotionPermissions.Allowed));
        Assert.False(ManualTranslationAcceptance.IsAccepted(translation, 0.15f, LocomotionPermissions.RotationOnly));
        Assert.False(ManualTranslationAcceptance.IsAccepted(translation, 0.15f, LocomotionPermissions.Blocked));
    }

    [Fact]
    public void NonFiniteTranslation_IsNotAccepted()
        => Assert.False(ManualTranslationAcceptance.IsAccepted(new Vector2(float.NaN, 1f), 0.15f, LocomotionPermissions.Allowed));
}
