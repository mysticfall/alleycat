using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Unit coverage for the calibrated world-space hand-pose composition math (XR-002 TR10).
/// </summary>
public sealed class XRHandPoseCompositionTests
{
    private const float Epsilon = 1e-5f;

    /// <summary>
    /// A unit world scale and identity inputs compose to the identity wrist.
    /// </summary>
    [Fact]
    public void ComposeCalibratedWrist_IdentityInputs_ReturnsIdentity()
    {
        Transform3D result = XRHandPoseComposition.ComposeCalibratedWrist(
            Transform3D.Identity,
            Transform3D.Identity,
            1.0f,
            Transform3D.Identity);

        AssertTransformApproximately(Transform3D.Identity, result);
    }

    /// <summary>
    /// The palm-to-wrist translation is world-scale corrected exactly once and rotated into the palm frame.
    /// </summary>
    [Fact]
    public void ComposeCalibratedWrist_NonUnitWorldScale_ScalesRelativeTranslationOnce()
    {
        Basis palmRotation = Basis.Identity.Rotated(Vector3.Up, Mathf.Pi / 2.0f);
        Transform3D palmWorld = new(palmRotation, new Vector3(1.0f, 2.0f, 3.0f));
        Transform3D palmToWrist = new(Basis.Identity, new Vector3(0.1f, 0.0f, 0.0f));

        Transform3D result = XRHandPoseComposition.ComposeCalibratedWrist(
            palmWorld,
            palmToWrist,
            2.0f,
            Transform3D.Identity);

        // rotY(+90°) maps +X to -Z: 0.1 m * 2.0 world scale = 0.2 m along the palm's local +X, i.e. world -Z.
        AssertTransformApproximately(
            new Transform3D(palmRotation, new Vector3(1.0f, 2.0f, 2.8f)),
            result);
    }

    /// <summary>
    /// The authored calibration anchor translation is world-scale corrected exactly once, in the wrist frame.
    /// </summary>
    [Fact]
    public void ComposeCalibratedWrist_AnchorTranslation_IsWorldScaleCorrectedOnce()
    {
        Basis palmRotation = Basis.Identity.Rotated(Vector3.Up, Mathf.Pi / 2.0f);
        Transform3D palmWorld = new(palmRotation, new Vector3(1.0f, 2.0f, 3.0f));
        Transform3D anchor = new(Basis.Identity, new Vector3(0.05f, 0.0f, 0.0f));

        Transform3D result = XRHandPoseComposition.ComposeCalibratedWrist(
            palmWorld,
            Transform3D.Identity,
            2.0f,
            anchor);

        // 0.05 m anchor * 2.0 world scale along the wrist's +X (world -Z at +90° yaw).
        AssertTransformApproximately(
            new Transform3D(palmRotation, new Vector3(1.0f, 2.0f, 2.9f)),
            result);
    }

    /// <summary>
    /// Rotation composes through the relative transform and the anchor, and the final basis is orthonormalised.
    /// </summary>
    [Fact]
    public void ComposeCalibratedWrist_RotatedInputs_ComposesRotationAndOrthonormalises()
    {
        Basis palmRotation = Basis.Identity.Rotated(Vector3.Up, Mathf.Pi / 2.0f);
        Basis relativeRotation = Basis.Identity.Rotated(Vector3.Up, Mathf.Pi / 2.0f);
        Basis anchorRotation = Basis.Identity.Rotated(Vector3.Right, Mathf.Pi / 6.0f);
        // Non-orthonormal authored anchor (uniform 2x scale) must not survive into the result.
        var scaledAnchorRotation = new Basis(anchorRotation.X * 2.0f, anchorRotation.Y * 2.0f, anchorRotation.Z * 2.0f);

        Transform3D result = XRHandPoseComposition.ComposeCalibratedWrist(
            new Transform3D(palmRotation, new Vector3(1.0f, 2.0f, 3.0f)),
            new Transform3D(relativeRotation, Vector3.Zero),
            1.0f,
            new Transform3D(scaledAnchorRotation, Vector3.Zero));

        Basis expectedRotation = palmRotation * relativeRotation * anchorRotation;

        AssertTransformApproximately(new Transform3D(expectedRotation, new Vector3(1.0f, 2.0f, 3.0f)), result);
        Assert.True(Mathf.IsEqualApprox(result.Basis.Determinant(), 1.0f, Epsilon));
        Assert.True(Mathf.IsEqualApprox(result.Basis.X.Length(), 1.0f, Epsilon));
        Assert.True(Mathf.IsEqualApprox(result.Basis.Y.Length(), 1.0f, Epsilon));
        Assert.True(Mathf.IsEqualApprox(result.Basis.Z.Length(), 1.0f, Epsilon));
    }

    /// <summary>
    /// The palm-to-wrist relative transform round-trips back to the raw wrist transform.
    /// </summary>
    [Fact]
    public void ComposePalmToWristRelative_RoundTripsToRawWrist()
    {
        Transform3D palm = new(
            Basis.Identity.Rotated(Vector3.Up, 0.6f),
            new Vector3(0.1f, -0.05f, 0.04f));
        Transform3D wrist = new(
            Basis.Identity.Rotated(Vector3.Up, 0.2f),
            new Vector3(0.12f, -0.03f, -0.01f));

        Transform3D relative = XRHandPoseComposition.ComposePalmToWristRelative(palm, wrist);

        AssertTransformApproximately(wrist, palm * relative);
    }

    /// <summary>
    /// A translated origin with world scale rotates and scales the raw origin-space translation exactly once.
    /// </summary>
    [Fact]
    public void ComposeWorldSpace_RotatedScaledOrigin_AppliesOriginAndWorldScaleOnce()
    {
        Basis originRotation = Basis.Identity.Rotated(Vector3.Up, Mathf.Pi / 2.0f);
        Transform3D originGlobal = new(originRotation, new Vector3(2.0f, 0.0f, 0.0f));
        Transform3D raw = new(Basis.Identity, new Vector3(1.0f, 0.0f, 0.0f));

        Transform3D result = XRHandPoseComposition.ComposeWorldSpace(originGlobal, 2.0f, raw);

        // Origin translation (2,0,0) scaled by 2 becomes (4,0,0); the raw +X translation becomes 2 m along
        // world -Z under the +90° yaw. The world scale must not apply twice to the translation (which would
        // move it to -4 m). The basis carries the uniform world scale, matching the mock oracle formula.
        Assert.Equal(new Vector3(4.0f, 0.0f, -2.0f), result.Origin);
        Assert.Equal(originRotation.X * 2.0f, result.Basis.X);
        Assert.Equal(originRotation.Y * 2.0f, result.Basis.Y);
        Assert.Equal(originRotation.Z * 2.0f, result.Basis.Z);
    }

    /// <summary>
    /// The world-space joint composition matches the mock runtime oracle formula term for term.
    /// </summary>
    [Fact]
    public void ComposeWorldSpace_MatchesMockOracleFormula()
    {
        Transform3D originGlobal = new(
            Basis.Identity.Rotated(Vector3.Right, 0.3f).Rotated(Vector3.Up, -0.7f),
            new Vector3(0.4f, 1.1f, -0.2f));
        Transform3D raw = new(
            Basis.Identity.Rotated(Vector3.Up, 0.9f),
            new Vector3(-0.2f, 0.05f, 0.3f));
        float worldScale = 1.35f;

        Transform3D expected = originGlobal.Scaled(new Vector3(worldScale, worldScale, worldScale)) * raw;
        Transform3D result = XRHandPoseComposition.ComposeWorldSpace(originGlobal, worldScale, raw);

        AssertTransformApproximately(expected, result);
    }

    private static void AssertTransformApproximately(Transform3D expected, Transform3D actual)
    {
        AssertVectorApproximately(expected.Origin, actual.Origin);
        AssertVectorApproximately(expected.Basis.X, actual.Basis.X);
        AssertVectorApproximately(expected.Basis.Y, actual.Basis.Y);
        AssertVectorApproximately(expected.Basis.Z, actual.Basis.Z);
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(actual.X, expected.X - Epsilon, expected.X + Epsilon);
        Assert.InRange(actual.Y, expected.Y - Epsilon, expected.Y + Epsilon);
        Assert.InRange(actual.Z, expected.Z - Epsilon, expected.Z + Epsilon);
    }
}
