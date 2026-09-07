using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

public enum NPCConfinedManoeuvreKind
{
    /// <summary>The request shape matches no confined side-step regime; another regime owns it.</summary>
    None,

    /// <summary>
    /// One or more authored side steps complete the lateral displacement, absorbing the curvature-safe yaw capacity
    /// on the final step; settlement owns the planar remainder beyond the chain and any within-tolerance yaw
    /// and may chain authored repetitions.
    /// </summary>
    SideStep,

    /// <summary>
    /// The terminal yaw exceeds the side step's curvature-safe absorption: the
    /// plan composes the side-step chain toward the displacement in the actor's current frame, then the chained pivot
    /// chain toward the terminal yaw, then the pivot's bounded convergence remainder and the planar shortfall
    /// through settlement.
    /// </summary>
    SideStepThenPivot,
}

/// <summary>
/// Ordered decomposition of one single-segment close request into confined manoeuvres. The
/// universal-arrival composition orders a side-step chain first, then the pivot chain toward the terminal yaw, then
/// bounded convergence — step chain → pivot chain → convergence — with corrections dispersing during each chain's
/// final clip and settlement polishing ≤ tolerance.
/// </summary>
public readonly record struct NPCConfinedManoeuvrePlan(
    NPCConfinedManoeuvreKind Kind,
    NPCSideStepPlan SideStep,
    NPCTurnInPlacePlan Turn)
{
    public bool PlaysSideStepClip => SideStep.PlaysSideStepClip;

    /// <summary>Whether the plan orders the pivot chain after the side-step chain within the same programme.</summary>
    public bool ComposesPivotAfterSideStep => Kind == NPCConfinedManoeuvreKind.SideStepThenPivot;
}

/// <summary>
/// Pure composition decision for short close requests. A mostly-lateral
/// request of any distance decomposes into its chained side steps; when its terminal yaw exceeds the side step's
/// curvature-safe yaw absorption, a chained pivot composition follows: step chain → pivot chain → bounded
/// convergence. Everything else falls through to the remaining regimes. Thresholds derive from package endpoint
/// data and existing absorption capacities only.
/// </summary>
public static class NPCConfinedManoeuvre
{
    public static NPCConfinedManoeuvrePlan Select(
        Vector2 actorSemanticOffset,
        float residualYawRadians,
        float sideStepEndpointDistance,
        float sideStepYawAbsorptionCapacityRadians,
        float sideStepPerSegmentPlanarCorrectionLimit,
        float pivotYawMagnitudeRadians,
        float pivotPureConvergenceThresholdRadians)
    {
        NPCSideStepPlan step = NPCSideStep.Select(
            actorSemanticOffset,
            residualYawRadians,
            sideStepEndpointDistance,
            sideStepYawAbsorptionCapacityRadians,
            sideStepPerSegmentPlanarCorrectionLimit);
        if (!step.PlaysSideStepClip)
        {
            // Mostly-forward shapes keep the short-bout splice or travel regimes — the composition never replaces
            // them.
            return new(NPCConfinedManoeuvreKind.None, step, default);
        }

        // A single confined manoeuvre defers everything beyond its curvature-safe yaw absorption. Beyond-capacity
        // confined composition applies instead
        // when that remainder still warrants clips: the step chain first in the actor's current frame, then the
        // pivot chain toward the terminal yaw; the chained pivots' convergence remainder and the planar shortfall
        // stay with settlement.
        NPCTurnInPlacePlan turn = NPCTurnInPlace.Select(
            step.SettlementYawRadians,
            Vector2.Zero,
            pivotYawMagnitudeRadians,
            pivotPureConvergenceThresholdRadians);
        return new(
            turn.PlaysPivotClip
                ? NPCConfinedManoeuvreKind.SideStepThenPivot
                : NPCConfinedManoeuvreKind.SideStep,
            step,
            turn);
    }
}
