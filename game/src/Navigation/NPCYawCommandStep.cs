using Godot;

namespace AlleyCat.Navigation;

/// <summary>Value-only carried state for one ordinary moving-yaw command step.</summary>
public record struct NPCYawCommandState(
    float SmoothedResidual,
    float PreviousAuthoredRate,
    float LastResidualRate,
    float RemainingCorrectionRadians);

/// <summary>Pure yaw-command stepping shared by live publication and commanded-release projection.</summary>
public static class NPCYawCommandStep
{
    /// <summary>Returns the realised magnitude runtime yaw-budget accounting accepts along the requested direction.</summary>
    public static float AccountedRealisedMagnitude(float requestedRadians, float realisedRadians)
        => !float.IsFinite(requestedRadians)
            || !float.IsFinite(realisedRadians)
            || Mathf.IsZeroApprox(requestedRadians)
                ? 0f
                : Mathf.Clamp(
                    realisedRadians * Mathf.Sign(requestedRadians),
                    0f,
                    Mathf.Abs(requestedRadians));

    /// <summary>Advances a projected budget after one modelled request is realised.</summary>
    public static void AccountRealised(
        float requestedRadians,
        float realisedRadians,
        ref NPCYawCommandState state)
    {
        float accounted = Math.Min(
            AccountedRealisedMagnitude(requestedRadians, realisedRadians),
            Math.Max(state.RemainingCorrectionRadians, 0f));
        state.RemainingCorrectionRadians = Math.Max(state.RemainingCorrectionRadians - accounted, 0f);
    }

    /// <summary>
    /// Advances one authored-plus-residual yaw command while carrying smoothing, acceleration and correction-budget
    /// state by value.
    /// </summary>
    public static float Step(
        float requestedYawRate,
        float authoredYawRate,
        float movingYawError,
        float terminalYawError,
        float speedWeight,
        float phaseWeight,
        float delta,
        float maximumResidualRate,
        float smoothingRate,
        ref NPCYawCommandState state)
    {
        if (!float.IsFinite(delta) || delta <= 0f)
        {
            return 0f;
        }

        float desired = float.IsFinite(requestedYawRate) ? requestedYawRate : 0f;
        float finiteMovingError = float.IsFinite(movingYawError) ? movingYawError : 0f;
        float stoppingRate = Mathf.Sqrt(
            2f * NPCTransitionContinuity.MaximumYawAcceleration * Mathf.Abs(finiteMovingError));
        desired = Mathf.Clamp(desired, -stoppingRate, stoppingRate);
        if (!Mathf.IsZeroApprox(finiteMovingError))
        {
            desired = Mathf.Sign(finiteMovingError) * Mathf.Abs(desired);
        }

        if (float.IsFinite(terminalYawError) && Mathf.Abs(terminalYawError) <= 0.05f)
        {
            desired = Mathf.Sign(terminalYawError) * Math.Min(
                Mathf.Abs(desired),
                Mathf.Abs(terminalYawError) / Math.Max(delta, float.Epsilon));
        }

        float authored = float.IsFinite(authoredYawRate) ? authoredYawRate : 0f;
        float limit = Math.Max(maximumResidualRate, 0f)
            * Mathf.Clamp(speedWeight, 0f, 1f)
            * Mathf.Clamp(phaseWeight, 0f, 1f);
        float target = Mathf.Clamp(desired - authored, -limit, limit);
        float blend = 1f - Mathf.Exp(-Math.Max(smoothingRate, 0f) * delta);
        float smoothedTarget = Mathf.Lerp(state.SmoothedResidual, target, blend);
        float previousTotalYawRate = state.PreviousAuthoredRate + state.LastResidualRate;
        float boundedTotalYawRate = Mathf.MoveToward(
            previousTotalYawRate,
            authored + smoothedTarget,
            0.999f * NPCTransitionContinuity.MaximumYawAcceleration * delta);
        float smoothedResidual = boundedTotalYawRate - authored;
        float remaining = float.IsFinite(state.RemainingCorrectionRadians)
            ? Math.Max(state.RemainingCorrectionRadians, 0f)
            : 0f;
        float residual = Mathf.Clamp(smoothedResidual * delta, -remaining, remaining) / delta;
        state = new(smoothedResidual, authored, residual, remaining);
        return authored + residual;
    }
}
