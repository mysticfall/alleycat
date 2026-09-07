using AlleyCat.Control.Locomotion;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

public enum NPCSideStepKind
{
    /// <summary>The request is not a mostly-lateral short displacement; another regime owns it.</summary>
    None,

    /// <summary>Authored left side-step clips (<c>sl</c>, semantic −X lateral Root displacement).</summary>
    StepLeft,

    /// <summary>Authored right side-step clips (<c>sr</c>, semantic +X lateral Root displacement).</summary>
    StepRight,
}

/// <summary>
/// Pure classification of one mostly-lateral close request into a chain of authored side steps plus a bounded
/// settlement remainder. Side steps carry the package
/// lateral Root displacement and zero authored yaw; a small terminal yaw residual is absorbed mid-phase through
/// <see cref="NPCConfinedAbsorption"/> and anything beyond the absorption capacity is left to settlement
/// convergence or a composed pivot chain — the composition is decided by <see cref="NPCConfinedManoeuvre"/>, never
/// here. Chaining uses N =
/// round(distance / endpoint) repetitions of the loopable clip, where every repetition but the last plays as a pure
/// full clip and the final step carries the dispersed remainder — the clip-versus-request remainder disperses
/// across that final clip through the side-step-only planar schedule with zero end-velocity, finishing at the
/// destination on the clip's idle-compatible end pose. Any mostly-lateral distance classifies.
/// </summary>
public readonly record struct NPCSideStepPlan(
    NPCSideStepKind Kind,
    StandingLocomotionCapability? StepCapability,
    float AbsorbedYawRadians,
    float SettlementYawRadians,
    Vector2 AbsorbedPlanarSemantic,
    int StepCount = 1)
{
    public bool PlaysSideStepClip => Kind is NPCSideStepKind.StepLeft or NPCSideStepKind.StepRight;

    /// <summary>Number of chained side-step clips the plan executes; always ≥ 1 when a step clip plays.</summary>
    public int ChainedStepCount => PlaysSideStepClip ? Math.Max(StepCount, 1) : 0;

    public float TotalYawRadians => AbsorbedYawRadians + SettlementYawRadians;
}

public static class NPCSideStep
{
    /// <summary>
    /// Selects the side-step plan for an actor-frame displacement and residual terminal yaw. The request must be
    /// mostly lateral (lateral component dominant); any planar distance beyond the effective-zero floor classifies —
    /// Chains full <c>sl</c>/<c>sr</c> repetitions for arbitrary lateral distance with the final step
    /// carrying the dispersed remainder. Chaining is reserved for near-lateral displacement: the longitudinal
    /// component must fit the existing per-segment planar correction budget across the chained clips, so steeper
    /// X-dominant diagonals stay with the travel regime that turns toward the displacement and walks it. Otherwise
    /// <see cref="NPCSideStepKind.None"/> leaves the request to the remaining regimes.
    /// </summary>
    public static NPCSideStepPlan Select(
        Vector2 actorSemanticOffset,
        float residualYawRadians,
        float sideStepEndpointDistance,
        float yawAbsorptionCapacityRadians,
        float perSegmentPlanarCorrectionLimit)
    {
        if (!actorSemanticOffset.IsFinite()
            || !float.IsFinite(residualYawRadians)
            || !float.IsFinite(sideStepEndpointDistance)
            || sideStepEndpointDistance <= NPCShortBoutSplice.EffectiveZeroDistance
            || !float.IsFinite(yawAbsorptionCapacityRadians)
            || yawAbsorptionCapacityRadians < 0f)
        {
            return None();
        }

        float planarDistance = actorSemanticOffset.Length();
        if (planarDistance <= NPCShortBoutSplice.EffectiveZeroDistance)
        {
            return None();
        }

        // Mostly-lateral test in the actor frame: the lateral component must dominate the longitudinal component.
        if (Mathf.Abs(actorSemanticOffset.X) <= Mathf.Abs(actorSemanticOffset.Y))
        {
            return None();
        }

        // Chained repetitions use the nearest whole number of full clip endpoints, so the final step's
        // clip-versus-request remainder never exceeds half an endpoint in either direction.
        float stepRatio = planarDistance / sideStepEndpointDistance;
        int stepCount = Math.Max(1, (int)MathF.Floor(stepRatio + 0.5f));
        if (stepCount > 1)
        {
            // Chain eligibility: each chained clip may carry the existing per-segment planar correction bound of
            // longitudinal drift while remaining a lateral step; a longitudinal component beyond that budget makes
            // the request a steeper diagonal that travel owns.
            float longitudinalBudget = stepCount
                * (float.IsFinite(perSegmentPlanarCorrectionLimit)
                    ? Math.Max(perSegmentPlanarCorrectionLimit, 0f)
                    : 0f);
            if (Mathf.Abs(actorSemanticOffset.Y) > longitudinalBudget)
            {
                return None();
            }
        }

        bool right = actorSemanticOffset.X > 0f;
        float absorbedYaw = Mathf.Clamp(
            residualYawRadians,
            -yawAbsorptionCapacityRadians,
            yawAbsorptionCapacityRadians);
        return new(
            right ? NPCSideStepKind.StepRight : NPCSideStepKind.StepLeft,
             right ? StandingLocomotionCapability.ConfinedSideStepRight : StandingLocomotionCapability.ConfinedSideStepLeft,
            absorbedYaw,
            residualYawRadians - absorbedYaw,
            actorSemanticOffset,
            stepCount);
    }

    /// <summary>
    /// The yaw a side step may absorb mid-phase. Absorbing yaw during a lateral Root displacement curves the
    /// actor-relative authored path; the induced planar deviation is bounded by twice the package side-step endpoint
    /// displacement times the sine of half the absorbed yaw, so the capacity is the largest yaw whose worst-case
    /// curvature stays inside the existing per-segment planar correction bound. No new tuning values.
    /// </summary>
    public static float YawAbsorptionCapacity(float sideStepEndpointDistance, float perSegmentPlanarCorrectionLimit)
    {
        float endpoint = float.IsFinite(sideStepEndpointDistance) ? Math.Max(sideStepEndpointDistance, 0f) : 0f;
        float bound = float.IsFinite(perSegmentPlanarCorrectionLimit)
            ? Math.Max(perSegmentPlanarCorrectionLimit, 0f)
            : 0f;
        return endpoint <= 0f ? 0f : 2f * Mathf.Asin(Mathf.Clamp(bound / (2f * endpoint), 0f, 1f));
    }

    private static NPCSideStepPlan None() => new(NPCSideStepKind.None, null, 0f, 0f, Vector2.Zero, 0);
}
