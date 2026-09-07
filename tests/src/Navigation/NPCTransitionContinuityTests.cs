using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCTransitionContinuityTests
{
    [Fact]
    public void Evaluate_AcceptsReferenceProfileBoundaryValues()
    {
        NPCTransitionContinuityResult result = NPCTransitionContinuity.Evaluate(AtLimits());

        Assert.True(result.IsContinuous, result.RejectionReason);
    }

    [Fact]
    public void Evaluate_RejectsSpeedDiscontinuityBeyondReferenceProfile()
    {
        NPCTransitionContinuitySample sample = AtLimits() with
        {
            IncomingActorVelocity = new(NPCTransitionContinuity.MaximumSpeedDiscontinuity + 0.001f, 0f),
        };

        NPCTransitionContinuityResult result = NPCTransitionContinuity.Evaluate(sample);

        Assert.False(result.IsContinuous);
        Assert.Equal("speed_discontinuity", result.RejectionReason);
    }

    [Fact]
    public void Evaluate_RejectsDeclaredSupportSlipBeyondReferenceProfile()
    {
        NPCTransitionContinuitySample sample = AtLimits() with
        {
            SupportSlipInterval = NPCTransitionContinuity.MaximumSupportSlipInterval + 0.001f,
        };

        NPCTransitionContinuityResult result = NPCTransitionContinuity.Evaluate(sample);

        Assert.False(result.IsContinuous);
        Assert.Equal("support_slip", result.RejectionReason);
    }

    [Fact]
    public void StepVelocityCommand_BoundsAccelerationAndJerkWhileApproachingTarget()
    {
        const float delta = 1f / 60f;
        Vector2 acceleration = Vector2.Zero;
        Vector2 command = Vector2.Zero;

        Vector2 first = NPCTransitionContinuity.StepVelocityCommand(command, Vector2.Right, ref acceleration, delta);
        Vector2 firstAcceleration = acceleration;
        Vector2 second = NPCTransitionContinuity.StepVelocityCommand(first, Vector2.Right, ref acceleration, delta);

        Assert.InRange(firstAcceleration.Length(), 0f, (NPCTransitionContinuity.MaximumJerk * delta) + 0.0001f);
        Assert.InRange(acceleration.Length(), 0f, NPCTransitionContinuity.MaximumAcceleration + 0.0001f);
        Assert.InRange(
            firstAcceleration.DistanceTo(acceleration) / delta,
            0f,
            NPCTransitionContinuity.MaximumJerk + 0.001f);
        Assert.True(second.X > first.X);
        Assert.True(second.X < 1f);
    }

    private static NPCTransitionContinuitySample AtLimits() => new(
        Vector2.Zero,
        new(NPCTransitionContinuity.MaximumSpeedDiscontinuity, 0f),
        Vector2.Zero,
        new(NPCTransitionContinuity.MaximumSpeedDiscontinuity, 0f),
        0f,
        NPCTransitionContinuity.MaximumYawRateDiscontinuity,
        NPCTransitionContinuity.MaximumAcceleration,
        NPCTransitionContinuity.MaximumAcceleration,
        NPCTransitionContinuity.MaximumJerk,
        NPCTransitionContinuity.MaximumJerk,
        NPCTransitionContinuity.MaximumYawAcceleration,
        NPCTransitionContinuity.MaximumRootOrHipsTranslationResidual,
        NPCTransitionContinuity.MaximumRootOrHipsTranslationResidual,
        NPCTransitionContinuity.MaximumFootTranslationResidual,
        NPCTransitionContinuity.MaximumRootRotationResidual,
        NPCTransitionContinuity.MaximumHipsOrFootRotationResidual,
        NPCTransitionContinuity.MaximumHipsOrFootRotationResidual,
        NPCTransitionContinuity.MaximumSupportSlipSample,
        NPCTransitionContinuity.MaximumSupportSlipInterval,
        true);
}
