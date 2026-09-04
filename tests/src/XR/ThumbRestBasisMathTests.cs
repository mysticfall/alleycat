using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Pure unit coverage for the tolerant thumb rest-basis rotation extraction: polar recovery of column-scaled
/// rotations within the documented tolerance and fail-closed rejection of reflection, degeneracy, non-finite
/// input, and scale spread beyond tolerance (XR-002 TR25).
/// </summary>
public sealed class ThumbRestBasisMathTests
{
    private const float RotationEpsilon = 1e-4f;
    private const float Epsilon = 1e-4f;

    /// <summary>
    /// A rotation whose basis columns carry mild non-uniform import scale — the observed artifact shape — has
    /// exactly that rotation as its polar factor, so the extraction recovers it within float precision.
    /// </summary>
    [Fact]
    public void TryExtractRotation_NonUniformColumnScale_RecoversPolarRotationExactly()
    {
        Quaternion rotation = new(new Vector3(0.2f, 0.9f, -0.3f).Normalized(), 1.234f);
        Basis scaled = new Basis(rotation) * new Basis(
            Vector3.Right * 1.18f,
            Vector3.Up * 0.94f,
            Vector3.Back * 1.06f);

        Assert.True(ThumbRestBasisMath.TryExtractRotation(scaled, "test", out Quaternion extracted, out string error), error);

        AssertRotationApproximately(rotation, extracted);
        Assert.True(IsFinite(extracted));
        Assert.InRange(extracted.Length(), 1.0f - Epsilon, 1.0f + Epsilon);
        Assert.True(extracted.W >= 0.0f, "The extraction must canonicalise to the identity hemisphere.");
    }

    /// <summary>Positive uniform scale — the ≈1.0556 reference-rig import scale — is stripped identically.</summary>
    [Fact]
    public void TryExtractRotation_UniformImportScale_StripsScaleWithoutRotationChange()
    {
        Quaternion rotation = new(new Vector3(-0.4f, 0.1f, 0.8f).Normalized(), -0.77f);
        Basis scaled = new Basis(rotation) * new Basis(
            Vector3.Right * 1.0556f,
            Vector3.Up * 1.0556f,
            Vector3.Back * 1.0556f);

        Assert.True(ThumbRestBasisMath.TryExtractRotation(scaled, "test", out Quaternion extracted, out string error), error);

        AssertRotationApproximately(rotation, extracted);
    }

    /// <summary>The tolerance boundary itself is accepted: ratio 1.4 with ≈23.5% mean deviation passes.</summary>
    [Fact]
    public void TryExtractRotation_ToleranceBoundaryScale_IsAccepted()
    {
        Basis boundary = new(Vector3.Right * 1.4f, Vector3.Up, Vector3.Back);

        Assert.True(ThumbRestBasisMath.TryExtractRotation(boundary, "test", out Quaternion extracted, out string error), error);

        AssertRotationApproximately(Quaternion.Identity, extracted);
    }

    /// <summary>
    /// A sheared basis within the documented tolerance — the unit shear with measured column scales
    /// (1, √2, 1): ratio ≈1.414 ≤ 1.5 and per-axis deviation from the mean ≈24.3% ≤ 25% — is accepted
    /// rather than failed closed and extracts a valid canonical rotation.
    /// </summary>
    /// <remarks>
    /// The polar refinement is seeded from the Gram-Schmidt-orthonormalised basis, and the Newton iteration
    /// <c>R ← ½(R + R⁻ᵀ)</c> is stationary on an exactly orthogonal seed, so the extracted rotation equals
    /// the orthonormalised seed — identity for this shear orientation, whose axes are already orthonormal in
    /// Gram-Schmidt order. The true closest-rotation (polar) factor instead rotates by atan(1/2) ≈ 26.57°
    /// about Z and requires seeding the refinement from the raw basis; asserting it would demand that
    /// production change, so this oracle pins the current seeding strategy until that decision is taken
    /// consciously.
    /// </remarks>
    [Fact]
    public void TryExtractRotation_ShearedWithinTolerance_IsAcceptedAtTheOrthonormalisedRotation()
    {
        Basis sheared = new(new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 0, 1));

        Assert.True(ThumbRestBasisMath.TryExtractRotation(sheared, "test", out Quaternion extracted, out string error), error);

        AssertRotationApproximately(Quaternion.Identity, extracted);
        Assert.True(IsFinite(extracted));
        Assert.InRange(extracted.Length(), 1.0f - Epsilon, 1.0f + Epsilon);
        Assert.True(extracted.W >= 0.0f, "The extraction must canonicalise to the identity hemisphere.");
    }

    /// <summary>A reflected basis fails closed with the measured column scales in the rejection.</summary>
    [Fact]
    public void TryExtractRotation_ReflectedBasis_FailsClosedWithScaleEvidence()
    {
        Basis reflection = new(Vector3.Left, Vector3.Up, Vector3.Back);

        Assert.False(ThumbRestBasisMath.TryExtractRotation(reflection, "Left thumb metacarpal", out Quaternion extracted, out string error));

        Assert.Equal(Quaternion.Identity, extracted);
        Assert.Contains("reflection", error, StringComparison.Ordinal);
        Assert.Contains("Left thumb metacarpal", error, StringComparison.Ordinal);
        Assert.Contains("column scales", error, StringComparison.Ordinal);
    }

    /// <summary>A column-scale ratio beyond 1.5 — true shear-scale degeneracy — fails closed.</summary>
    [Fact]
    public void TryExtractRotation_ExcessiveColumnScaleRatio_FailsClosed()
    {
        Basis excessive = new(Vector3.Right * 1.9f, Vector3.Up, Vector3.Back);

        Assert.False(ThumbRestBasisMath.TryExtractRotation(excessive, "test", out _, out string error));

        Assert.Contains("beyond the tolerated import artifact", error, StringComparison.Ordinal);
        Assert.Contains($"{ThumbRestBasisMath.MaximumColumnScaleRatio}", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An isolated ratio-gate rejection: the symmetric stretch (1.25, 1, 0.8) stays inside the deviation
    /// gate — mean ≈1.0167, maximum per-axis deviation ≈22.95% below the 25% bound — while its pure
    /// column-scale ratio of 1.5625 exceeds the inclusive 1.5 maximum, so deleting the ratio check alone
    /// would admit this basis and fail this test.
    /// </summary>
    [Fact]
    public void TryExtractRotation_ExcessiveRatioWithinDeviationTolerance_FailsClosed()
    {
        Basis stretched = new(Vector3.Right * 1.25f, Vector3.Up, Vector3.Back * 0.8f);

        Assert.False(ThumbRestBasisMath.TryExtractRotation(stretched, "test", out Quaternion extracted, out string error));

        Assert.Equal(Quaternion.Identity, extracted);
        Assert.Contains("beyond the tolerated import artifact", error, StringComparison.Ordinal);
        Assert.Contains($"{ThumbRestBasisMath.MaximumColumnScaleRatio}", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ratio within 1.5 but with per-axis deviation from the mean beyond 25% (1.5/1.0/1.0 → ≈28.6%) still
    /// fails closed, exercising the deviation gate independently of the ratio gate.
    /// </summary>
    [Fact]
    public void TryExtractRotation_ExcessiveMeanDeviation_FailsClosed()
    {
        Basis excessive = new(Vector3.Right * 1.5f, Vector3.Up, Vector3.Back);

        Assert.False(ThumbRestBasisMath.TryExtractRotation(excessive, "test", out _, out string error));

        Assert.Contains("beyond the tolerated import artifact", error, StringComparison.Ordinal);
    }

    /// <summary>A near-zero axis fails closed as degenerate before any tolerance comparison.</summary>
    [Fact]
    public void TryExtractRotation_DegenerateAxis_FailsClosed()
    {
        Basis degenerate = new(Vector3.Right * 1e-6f, Vector3.Up, Vector3.Back);

        Assert.False(ThumbRestBasisMath.TryExtractRotation(degenerate, "test", out _, out string error));

        Assert.Contains("degenerate", error, StringComparison.Ordinal);
    }

    /// <summary>Non-finite entries fail closed before any arithmetic can propagate NaN.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void TryExtractRotation_NonFiniteBasis_FailsClosed(float poison)
    {
        Basis nonFinite = new(new Vector3(poison, 0.0f, 0.0f), Vector3.Up, Vector3.Back);

        Assert.False(ThumbRestBasisMath.TryExtractRotation(nonFinite, "test", out Quaternion extracted, out string error));

        Assert.Equal(Quaternion.Identity, extracted);
        Assert.Contains("non-finite", error, StringComparison.Ordinal);
    }

    /// <summary>The diagnostic helpers measure column scales and their ratio for hardware-run evidence.</summary>
    [Fact]
    public void ColumnScales_And_ScaleRatio_MeasureTheBasis()
    {
        Basis scaled = new(Vector3.Right * 1.2f, Vector3.Up * 0.8f, Vector3.Back);

        Vector3 scales = ThumbRestBasisMath.ColumnScales(scaled);

        Assert.Equal(new Vector3(1.2f, 0.8f, 1.0f), scales);
        Assert.Equal(1.5f, ThumbRestBasisMath.ScaleRatio(scales), 5);
    }

    private static void AssertRotationApproximately(Quaternion expected, Quaternion actual)
    {
        float dot = Mathf.Clamp(Mathf.Abs(expected.Normalized().Dot(actual.Normalized())), 0.0f, 1.0f);
        float angle = 2.0f * Mathf.Acos(dot);
        Assert.True(angle <= RotationEpsilon, $"Expected rotations within {RotationEpsilon} radians, got {angle}.");
    }

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
