using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Tolerant nearest-rotation extraction for imported thumb rest bases (XR-002 TR25).
/// </summary>
/// <remarks>
/// <para>
/// Imported thumb rest bases — the thumb metacarpal/proximal/distal global rests and the authored local rests
/// that supply the thumb neutrals — may carry non-uniform scale as an import/source artifact, while remaining
/// perfectly usable orientation evidence. This helper replaces the strict uniform-scale-or-reject policy for
/// those thumb-only bases: it extracts the closest proper rotation via polar decomposition and fails closed
/// only for genuine degeneracy — non-finite entries, a near-zero axis, a reflected (non-positive-determinant)
/// basis, or scale/shear spread beyond the documented tolerance.
/// </para>
/// <para>
/// <strong>Acceptance tolerance.</strong> The measured per-column scale lengths <c>(sx, sy, sz)</c> must satisfy
/// <c>max/min ≤ <see cref="MaximumColumnScaleRatio" /> (1.5)</c> and a per-axis deviation from the mean scale of
/// at most <see cref="MaximumScaleDeviationFromMean" /> (25%). This is generous enough for observed import
/// artifacts (the reference rig shows ≈1.0556 uniform scale with mild per-axis variation) while still rejecting
/// true shear or degeneracy, whose column-scale spread explodes far past both bounds.
/// </para>
/// <para>
/// <strong>Polar decomposition.</strong> The seed is the Gram-Schmidt-orthonormalised basis, refined by a fixed
/// <see cref="PolarRefinementIterations" /> (16) iterations of the Higham-style Newton iteration
/// <c>R ← ½(R + R⁻ᵀ)</c>. The iteration converges quadratically once the seed is near-orthonormal, reaching
/// float-precision residuals within a handful of iterations for every input the tolerance above admits; the
/// fixed iteration cap keeps the result deterministic with no early exit. This runs only at binding time, never
/// in the per-frame hot path.
/// </para>
/// <para>
/// Origins are points and unaffected by basis scale, so callers keep consuming rest origins unchanged. The
/// non-thumb rest validation stays strictly uniform-scale-or-reject and must not call into this class.
/// </para>
/// </remarks>
public static class ThumbRestBasisMath
{
    /// <summary>Minimum measurable column length; shorter axes are degenerate (XR-002 TR25).</summary>
    public const float MinimumAxisLength = 1e-4f;

    /// <summary>Maximum ratio between the longest and shortest measured column scale (XR-002 TR25).</summary>
    public const float MaximumColumnScaleRatio = 1.5f;

    /// <summary>Maximum per-axis deviation from the mean measured column scale (XR-002 TR25).</summary>
    public const float MaximumScaleDeviationFromMean = 0.25f;

    /// <summary>Fixed Higham-style polar refinement iteration count after the orthonormalised seed.</summary>
    public const int PolarRefinementIterations = 16;

    /// <summary>
    /// Extracts the closest proper rotation from a thumb rest basis, tolerating non-uniform import scale within
    /// the documented bounds (XR-002 TR25).
    /// </summary>
    /// <param name="basis">Thumb rest basis (global or authored local).</param>
    /// <param name="label">Diagnostic label, for example "Left thumb metacarpal".</param>
    /// <param name="rotation">Canonical (identity-hemisphere) unit rotation on success.</param>
    /// <param name="error">Failure detail including the measured column scales, or empty on success.</param>
    /// <returns>
    /// <see langword="true" /> when the basis is finite, non-degenerate, unreflected, and within the
    /// scale-spread tolerance; the polar rotation is then available in <paramref name="rotation" />.
    /// </returns>
    public static bool TryExtractRotation(Basis basis, string label, out Quaternion rotation, out string error)
    {
        rotation = Quaternion.Identity;
        if (!IsFinite(basis))
        {
            error = $"The {label} rest basis is non-finite.";
            return false;
        }

        Vector3 scales = ColumnScales(basis);
        string evidence = FormatColumnScales(scales);
        if (scales.X <= MinimumAxisLength || scales.Y <= MinimumAxisLength || scales.Z <= MinimumAxisLength)
        {
            error = $"The {label} rest basis is degenerate; measured column scales {evidence}.";
            return false;
        }

        if (basis.Determinant() <= 0.0f)
        {
            error = $"The {label} rest basis has a non-positive determinant (reflection); measured column " +
                $"scales {evidence}.";
            return false;
        }

        float ratio = ScaleRatio(scales);
        float meanScale = (scales.X + scales.Y + scales.Z) / 3.0f;
        float maximumDeviation = Mathf.Max(
            Mathf.Abs(scales.X - meanScale),
            Mathf.Max(Mathf.Abs(scales.Y - meanScale), Mathf.Abs(scales.Z - meanScale))) / meanScale;
        if (ratio > MaximumColumnScaleRatio || maximumDeviation > MaximumScaleDeviationFromMean)
        {
            error = $"The {label} rest basis carries scale or shear beyond the tolerated import artifact " +
                $"(ratio {MaximumColumnScaleRatio}, deviation {MaximumScaleDeviationFromMean:P0}); measured " +
                $"column scales {evidence}, ratio {ratio:F3}, deviation {maximumDeviation:P1}.";
            return false;
        }

        Basis rotationBasis = basis.Orthonormalized();
        for (int iteration = 0; iteration < PolarRefinementIterations; iteration++)
        {
            rotationBasis = HalfSum(rotationBasis, rotationBasis.Inverse().Transposed());
        }

        if (!IsFinite(rotationBasis))
        {
            error = $"The {label} rest basis polar refinement produced a non-finite rotation; measured column " +
                $"scales {evidence}.";
            return false;
        }

        rotation = CanonicalNormalise(rotationBasis.GetRotationQuaternion());
        error = string.Empty;
        return true;
    }

    /// <summary>Measured per-column scale lengths of a basis: <c>(|X|, |Y|, |Z|)</c>.</summary>
    public static Vector3 ColumnScales(Basis basis)
        => new(basis.X.Length(), basis.Y.Length(), basis.Z.Length());

    /// <summary>Ratio between the longest and shortest measured column scale.</summary>
    public static float ScaleRatio(Vector3 scales)
        => Mathf.Max(scales.X, Mathf.Max(scales.Y, scales.Z))
            / Mathf.Min(scales.X, Mathf.Min(scales.Y, scales.Z));

    /// <summary>Formats the measured column scales for diagnostics, for example "(1.180, 0.940, 1.060)".</summary>
    public static string FormatColumnScales(Vector3 scales)
        => $"({scales.X:F3}, {scales.Y:F3}, {scales.Z:F3})";

    private static Basis HalfSum(Basis left, Basis right)
        => new(
            (left.X + right.X) * 0.5f,
            (left.Y + right.Y) * 0.5f,
            (left.Z + right.Z) * 0.5f);

    private static Quaternion CanonicalNormalise(Quaternion value)
    {
        Quaternion normalised = value.Normalized();
        return normalised.W < 0.0f
            ? new Quaternion(-normalised.X, -normalised.Y, -normalised.Z, -normalised.W)
            : normalised;
    }

    private static bool IsFinite(Basis basis)
        => IsFinite(basis.X) && IsFinite(basis.Y) && IsFinite(basis.Z);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
