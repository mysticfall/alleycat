using Godot;

#pragma warning disable CS1591, IDE0046

namespace AlleyCat.Navigation;

public readonly record struct NPCTransitionContinuitySample(
    Vector2 CurrentActorVelocity,
    Vector2 IncomingActorVelocity,
    Vector2 CurrentRootVelocity,
    Vector2 IncomingRootVelocity,
    float CurrentYawRate,
    float IncomingYawRate,
    float ActorAcceleration,
    float RootAcceleration,
    float ActorJerk,
    float RootJerk,
    float YawAcceleration,
    float RootTranslationResidual,
    float HipsTranslationResidual,
    float MaximumFootTranslationResidual,
    float RootRotationResidual,
    float HipsRotationResidual,
    float MaximumFootRotationResidual,
    float SupportSlipSample,
    float SupportSlipInterval,
    bool HasDeclaredSupportContact);

public readonly record struct NPCTransitionContinuityResult(bool IsContinuous, string RejectionReason);

/// <summary>NAV-001 reference-profile continuity oracle shared by planning and runtime validation.</summary>
public static class NPCTransitionContinuity
{
    public const float MaximumSpeedDiscontinuity = 0.20f;
    public const float MaximumAcceleration = 8f;
    public const float MaximumJerk = 180f;
    public const float MaximumYawRateDiscontinuity = 0.35f;
    public const float MaximumYawAcceleration = 10f;
    public const float MaximumRootOrHipsTranslationResidual = 0.010f;
    public const float MaximumFootTranslationResidual = 0.012f;
    public const float MaximumRootRotationResidual = 0.035f;
    public const float MaximumHipsOrFootRotationResidual = 0.087f;
    public const float MaximumSupportSlipSample = 0.005f;
    public const float MaximumSupportSlipInterval = 0.015f;

    public static NPCTransitionContinuityResult Evaluate(NPCTransitionContinuitySample sample)
    {
        if (!IsFinite(sample))
        {
            return new(false, "non_finite_sample");
        }
        if (sample.CurrentActorVelocity.DistanceTo(sample.IncomingActorVelocity) > MaximumSpeedDiscontinuity
            || sample.CurrentRootVelocity.DistanceTo(sample.IncomingRootVelocity) > MaximumSpeedDiscontinuity)
        {
            return new(false, "speed_discontinuity");
        }
        if (Mathf.Abs(sample.CurrentYawRate - sample.IncomingYawRate) > MaximumYawRateDiscontinuity)
        {
            return new(false, "yaw_rate_discontinuity");
        }
        if (Math.Max(sample.ActorAcceleration, sample.RootAcceleration) > MaximumAcceleration)
        {
            return new(false, "acceleration");
        }
        if (Math.Max(sample.ActorJerk, sample.RootJerk) > MaximumJerk)
        {
            return new(false, "jerk");
        }
        if (sample.YawAcceleration > MaximumYawAcceleration)
        {
            return new(false, "yaw_acceleration");
        }
        if (Math.Max(sample.RootTranslationResidual, sample.HipsTranslationResidual) > MaximumRootOrHipsTranslationResidual)
        {
            return new(false, "root_or_hips_translation");
        }
        if (sample.MaximumFootTranslationResidual > MaximumFootTranslationResidual)
        {
            return new(false, "foot_translation");
        }
        if (sample.RootRotationResidual > MaximumRootRotationResidual)
        {
            return new(false, "root_rotation");
        }
        if (Math.Max(sample.HipsRotationResidual, sample.MaximumFootRotationResidual) > MaximumHipsOrFootRotationResidual)
        {
            return new(false, "hips_or_foot_rotation");
        }
        if (sample.HasDeclaredSupportContact
            && (sample.SupportSlipSample > MaximumSupportSlipSample
                || sample.SupportSlipInterval > MaximumSupportSlipInterval))
        {
            return new(false, "support_slip");
        }
        return new(true, string.Empty);
    }

    public static bool IsVelocityCompatible(
        Vector2 currentActorVelocity,
        Vector2 incomingActorVelocity,
        Vector2 currentRootVelocity,
        Vector2 incomingRootVelocity,
        float currentYawRate,
        float incomingYawRate)
        => currentActorVelocity.IsFinite()
            && incomingActorVelocity.IsFinite()
            && currentRootVelocity.IsFinite()
            && incomingRootVelocity.IsFinite()
            && currentActorVelocity.DistanceTo(incomingActorVelocity) <= MaximumSpeedDiscontinuity
            && currentRootVelocity.DistanceTo(incomingRootVelocity) <= MaximumSpeedDiscontinuity
            && float.IsFinite(currentYawRate)
            && float.IsFinite(incomingYawRate)
            && Mathf.Abs(currentYawRate - incomingYawRate) <= MaximumYawRateDiscontinuity;

    public static Vector2 StepVelocityCommand(
        Vector2 current,
        Vector2 target,
        ref Vector2 acceleration,
        float delta,
        float maximumAcceleration = MaximumAcceleration,
        float maximumJerk = MaximumJerk)
    {
        if (!current.IsFinite() || !target.IsFinite() || !acceleration.IsFinite() || !float.IsFinite(delta) || delta <= 0f)
        {
            acceleration = Vector2.Zero;
            return current.IsFinite() ? current : Vector2.Zero;
        }

        Vector2 offset = target - current;
        float safeAcceleration = float.IsFinite(maximumAcceleration) ? Math.Max(maximumAcceleration, 0f) : 0f;
        float safeJerk = float.IsFinite(maximumJerk) ? Math.Max(maximumJerk, 0f) : 0f;
        Vector2 desiredAcceleration = (offset / delta).LimitLength(safeAcceleration);
        acceleration = acceleration.MoveToward(desiredAcceleration, safeJerk * delta);
        Vector2 step = acceleration * delta;
        if (step.Dot(offset) >= 0f && step.LengthSquared() >= offset.LengthSquared())
        {
            acceleration = offset / delta;
            return target;
        }
        return current + step;
    }

    private static bool IsFinite(NPCTransitionContinuitySample sample)
        => sample.CurrentActorVelocity.IsFinite()
            && sample.IncomingActorVelocity.IsFinite()
            && sample.CurrentRootVelocity.IsFinite()
            && sample.IncomingRootVelocity.IsFinite()
            && float.IsFinite(sample.CurrentYawRate)
            && float.IsFinite(sample.IncomingYawRate)
            && float.IsFinite(sample.ActorAcceleration)
            && float.IsFinite(sample.RootAcceleration)
            && float.IsFinite(sample.ActorJerk)
            && float.IsFinite(sample.RootJerk)
            && float.IsFinite(sample.YawAcceleration)
            && float.IsFinite(sample.RootTranslationResidual)
            && float.IsFinite(sample.HipsTranslationResidual)
            && float.IsFinite(sample.MaximumFootTranslationResidual)
            && float.IsFinite(sample.RootRotationResidual)
            && float.IsFinite(sample.HipsRotationResidual)
            && float.IsFinite(sample.MaximumFootRotationResidual)
            && float.IsFinite(sample.SupportSlipSample)
            && float.IsFinite(sample.SupportSlipInterval);
}
