using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>
/// Shared dispersion profile for confined-manoeuvre cross-DOF residual absorption. One
/// manoeuvre absorbs a small residual of its non-primary degree of freedom, dispersed smoothly across the manoeuvre's
/// mid-phase so the correction is masked by the authored motion and never fights the clip endpoints: the cumulative
/// fraction is zero at both boundaries, rises with zero boundary slope, and integrates to exactly the absorbed
/// residual. Exact final landing remains owned by the settlement no-crossing guard.
/// </summary>
public static class NPCConfinedAbsorption
{
    /// <summary>
    /// Cumulative absorbed fraction of the residual at normalised manoeuvre phase <paramref name="phase"/>.
    /// Zero at phase 0 and 1; the derivative (per-frame share) is also zero at both boundaries.
    /// </summary>
    public static float CumulativeFraction(float phase)
    {
        float clamped = Mathf.Clamp(float.IsFinite(phase) ? phase : 0f, 0f, 1f);
        return 0.5f * (1f - Mathf.Cos(Mathf.Pi * clamped));
    }

    /// <summary>
    /// The absorbed increment between two normalised manoeuvre phases. Summing increments over the manoeuvre
    /// reproduces exactly <paramref name="absorbedResidual"/> at phase 1 regardless of frame quantisation.
    /// </summary>
    public static float Increment(float absorbedResidual, float previousPhase, float currentPhase)
        => float.IsFinite(absorbedResidual)
            ? absorbedResidual * (CumulativeFraction(currentPhase) - CumulativeFraction(previousPhase))
            : 0f;

    /// <summary>
    /// Per-frame rate weight of the dispersion profile at normalised phase. Peak-weighted mid-phase; zero at both
    /// boundaries. Normalised so the weight integrates to one over the unit phase interval.
    /// </summary>
    public static float RateWeight(float phase)
    {
        float clamped = Mathf.Clamp(float.IsFinite(phase) ? phase : 0f, 0f, 1f);
        return 0.5f * Mathf.Pi * Mathf.Max(Mathf.Sin(Mathf.Pi * clamped), 0f);
    }
}
