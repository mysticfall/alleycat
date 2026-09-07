using AlleyCat.Control.Locomotion;
using Godot;

#pragma warning disable CS1591, IDE0046

namespace AlleyCat.Navigation;

public enum NPCTurnInPlaceKind
{
    /// <summary>Residual yaw is small enough to resolve through bounded convergence without playing a clip.</summary>
    PureConvergence,

    /// <summary>One or more authored left pivot clips (<c>pl45</c>, positive local-Y yaw).</summary>
    PivotLeft,

    /// <summary>One or more authored right pivot clips (<c>pr45</c>, negative local-Y yaw).</summary>
    PivotRight,
}

/// <summary>
/// Pure decomposition of an outstanding heading-dominated residual into a chain of authored confined pivots plus a
/// bounded convergence remainder. The pivots chain with N =
/// round(|yaw| / pivot) same-sign repetitions carry the bulk of the yaw (90° is exactly two p45 clips, 180° four)
/// and the signed remainder (|remainder| ≤ half a pivot) converges through the existing bounded settlement yaw
/// taper with no synthetic rotation beyond the convergence epsilon. Under confined cross-DOF absorption, the plan may
/// additionally carry a small planar offset (actor-semantic) that the first pivot disperses mid-phase via
/// <see cref="NPCConfinedAbsorption"/> instead of composing a second manoeuvre; endpoint convergence remains owned by
/// settlement.
/// </summary>
public readonly record struct NPCTurnInPlacePlan(
    NPCTurnInPlaceKind Kind,
    StandingLocomotionCapability? PivotCapability,
    float PivotYawRadians,
    float ConvergenceYawRadians,
    Vector2 AbsorbedPlanarSemantic,
    int PivotCount = 1)
{
    public bool PlaysPivotClip => Kind is NPCTurnInPlaceKind.PivotLeft or NPCTurnInPlaceKind.PivotRight;

    /// <summary>Number of chained pivot clips the plan executes; always ≥ 1 when a pivot clip plays.</summary>
    public int ChainedPivotCount => PlaysPivotClip ? Math.Max(PivotCount, 1) : 0;

    /// <summary>Total authored clip yaw across the chain (per-pivot yaw × count).</summary>
    public float AuthoredYawRadians => ChainedPivotCount * PivotYawRadians;
}

public static class NPCTurnInPlace
{
    /// <summary>
    /// Selects the turn-in-place plan for a residual terminal yaw. The plan chains same-sign authored pivots —
    /// N = round(|yaw| / pivot) — so the signed convergence remainder never exceeds half a pivot; pivots carry
    /// authored yaw only. The plan absorbs no planar offset.
    /// </summary>
    public static NPCTurnInPlacePlan Select(
        float residualYawRadians,
        float pivotYawMagnitudeRadians,
        float pureConvergenceThresholdRadians)
        => Select(residualYawRadians, Vector2.Zero, pivotYawMagnitudeRadians, pureConvergenceThresholdRadians);

    /// <summary>
    /// Selects the turn-in-place plan for a heading-dominated request carrying a planar offset within the pivot
    /// absorption capacity. The offset is dispersed mid-phase by the first chained pivot; the yaw decomposition is
    /// unchanged.
    /// </summary>
    public static NPCTurnInPlacePlan Select(
        float residualYawRadians,
        Vector2 absorbedPlanarSemantic,
        float pivotYawMagnitudeRadians,
        float pureConvergenceThresholdRadians)
    {
        if (!float.IsFinite(residualYawRadians)
            || !absorbedPlanarSemantic.IsFinite()
            || !float.IsFinite(pivotYawMagnitudeRadians)
            || pivotYawMagnitudeRadians <= 0f
            || !float.IsFinite(pureConvergenceThresholdRadians)
            || pureConvergenceThresholdRadians < 0f)
        {
            return new(NPCTurnInPlaceKind.PureConvergence, null, 0f, 0f, Vector2.Zero, 0);
        }

        if (Mathf.Abs(residualYawRadians) <= pureConvergenceThresholdRadians)
        {
            return new(
                NPCTurnInPlaceKind.PureConvergence,
                null,
                0f,
                residualYawRadians,
                absorbedPlanarSemantic,
                0);
        }

        // Chained pivots use the nearest whole number of authored pivots with the same sign, so the signed
        // convergence remainder stays within half a pivot and resolves through the bounded settlement taper.
        float pivotRatio = Mathf.Abs(residualYawRadians) / pivotYawMagnitudeRadians;
        int pivotCount = Math.Max(1, (int)MathF.Floor(pivotRatio + 0.5f));
        float authoredYaw = pivotCount * pivotYawMagnitudeRadians;
        return residualYawRadians > 0f
            ? new(
                NPCTurnInPlaceKind.PivotLeft,
                StandingLocomotionCapability.ConfinedPivotLeft,
                pivotYawMagnitudeRadians,
                residualYawRadians - authoredYaw,
                absorbedPlanarSemantic,
                pivotCount)
            : new(
                NPCTurnInPlaceKind.PivotRight,
                StandingLocomotionCapability.ConfinedPivotRight,
                -pivotYawMagnitudeRadians,
                residualYawRadians + authoredYaw,
                absorbedPlanarSemantic,
                pivotCount);
    }

    /// <summary>
    /// The planar offset a pivot may absorb mid-phase: never beyond the existing generic per-segment planar
    /// correction bound nor the terminal commitment correction budget — no new tuning values.
    /// </summary>
    public static float PivotPlanarAbsorptionCapacity(float perSegmentPlanarCorrectionLimit, float terminalCorrectionBudget)
        => Math.Min(
            float.IsFinite(perSegmentPlanarCorrectionLimit) ? Math.Max(perSegmentPlanarCorrectionLimit, 0f) : 0f,
            float.IsFinite(terminalCorrectionBudget) ? Math.Max(terminalCorrectionBudget, 0f) : 0f);
}
