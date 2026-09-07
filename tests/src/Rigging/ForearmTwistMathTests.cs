using AlleyCat.Rigging;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Rigging;

/// <summary>Unit coverage for rest-relative axial twist extraction and single-helper affine transport.</summary>
public sealed class ForearmTwistMathTests
{
    private const float Epsilon = 1e-5f;

    /// <summary>
    /// A posed, translated, scaled upstream lower arm transports the weighted axial twist exactly:
    /// the helper skinning transform is <c>F = P_L × Tα × R_L⁻¹</c>, the hand global is re-asserted
    /// through <c>L_W = G_X⁻¹ × C</c>, and the helper write is a complete local transform.
    /// </summary>
    [Fact]
    public void AffineTargets_PosedArm_TransportsWeightedTwistAndReassertsHand()
    {
        Transform3D lowerRest = new(new Basis(new Quaternion(Vector3.Up, 0.31f)), new Vector3(0.2f, -0.3f, 0.4f));
        Vector3 wrist = lowerRest * new Vector3(0.0f, 0.0f, 0.8f);
        Transform3D handRest = new(new Basis(new Quaternion(Vector3.Up, -0.19f)), wrist);
        Transform3D helperRest = new(new Basis(new Quaternion(Vector3.Right, 0.27f)), wrist * 0.9f);
        Quaternion upstreamRotation = new(Vector3.Up, 0.47f);
        Transform3D upstream = new(new Basis(upstreamRotation), new Vector3(-0.6f, 0.25f, 0.43f));
        Transform3D lowerPose = upstream * new Transform3D(Basis.FromScale(Vector3.One * 1.2f), Vector3.Zero) * lowerRest;
        const float twistAngle = 0.54f;
        const float bend = -0.71f;
        const float alpha = 0.5f;
        Vector3 residual = new(0.09f, -0.04f, 0.07f);

        // The achieved hand carries the anatomical Swing(bend) x Twist(twist) delta in the
        // lower-arm rest frame plus a translated wrist, exactly as an authoritative writer
        // upstream of the helper would produce it.
        Basis composed = new Basis(Vector3.Right, bend) * new Basis(Vector3.Back, twistAngle);
        Transform3D achieved = lowerPose * new Transform3D(composed, Vector3.Zero)
            * lowerRest.AffineInverse() * handRest;
        achieved.Origin += residual;

        Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            lowerRest, lowerPose, helperRest, handRest, achieved, alpha,
            out Transform3D helperLocal, out Transform3D handLocal));

        // Independent expectation: the wrist lies along the lower-arm rest bone's local +Z by
        // construction, so the rest-frame longitudinal axis is Back and the weighted axial
        // transport is the posed lower arm conjugating that rest-frame rotation.
        Transform3D expectedSkinning = lowerPose
            * new Transform3D(new Basis(Vector3.Back, twistAngle * alpha), Vector3.Zero)
            * lowerRest.AffineInverse();
        Transform3D helperGlobal = lowerPose * helperLocal;
        AssertTransformCompleteApproximately(expectedSkinning, helperGlobal * helperRest.AffineInverse());
        AssertTransformCompleteApproximately(achieved, helperGlobal * handLocal);

        // A rigid change of skeleton frame transports both writes by conjugation: the local
        // poses are frame-invariant.
        Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            upstream * lowerRest, upstream * lowerPose, upstream * helperRest, upstream * handRest,
            upstream * achieved, alpha, out Transform3D movedHelper, out Transform3D movedHand));
        AssertTransformCompleteApproximately(helperLocal, movedHelper);
        AssertTransformCompleteApproximately(handLocal, movedHand);
    }

    /// <summary>The runtime weight scales the transported axial angle and zero rests the helper skinning.</summary>
    [Fact]
    public void AffineTargets_WeightScaling_FromZeroToFull()
    {
        Transform3D lowerRest = Transform3D.Identity;
        Transform3D handRest = new(Basis.Identity, Vector3.Back);
        Transform3D helperRest = new(new Basis(new Quaternion(Vector3.Right, 0.2f)), new Vector3(0.0f, 0.0f, 0.4f));
        const float twistAngle = 0.8f;
        Transform3D achieved = new Transform3D(new Basis(Vector3.Back, twistAngle), Vector3.Zero) * handRest;

        foreach (float alpha in new[] { 0.0f, 0.25f, 0.5f, 1.0f })
        {
            Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
                lowerRest, lowerRest, helperRest, handRest, achieved, alpha,
                out Transform3D helperLocal, out Transform3D handLocal));
            Transform3D skinning = lowerRest * helperLocal * helperRest.AffineInverse();
            float transportedAngle = SignedAngleAbout(skinning.Basis.GetRotationQuaternion(), Vector3.Back);
            Assert.InRange(MathF.Abs(transportedAngle - (twistAngle * alpha)), 0.0f, Epsilon);
            AssertTransformCompleteApproximately(achieved, lowerRest * helperLocal * handLocal);
        }
    }

    /// <summary>
    /// A pure bend (off-axis swing) delta leaves the helper at its complete rest while the hand is
    /// still re-asserted: the writer never drives the swing component anywhere.
    /// </summary>
    [Fact]
    public void AffineTargets_BendOnlyDelta_RestsHelperAndPreservesHand()
    {
        Transform3D lowerRest = new(new Basis(new Quaternion(Vector3.Up, 0.4f)), new Vector3(0.1f, 0.2f, -0.3f));
        Vector3 wrist = lowerRest * new Vector3(0.0f, 0.0f, 0.75f);
        Transform3D handRest = new(new Basis(new Quaternion(Vector3.Up, 0.2f)), wrist);
        Transform3D helperRest = new(new Basis(new Quaternion(Vector3.Left, -0.15f)), wrist * 0.8f);
        Transform3D lowerPose = new Transform3D(new Basis(new Quaternion(Vector3.Up, -0.35f)), new Vector3(0.4f, -0.1f, 0.2f)) * lowerRest;
        const float bend = 0.9f;

        var composed = new Basis(Vector3.Right, bend);
        Transform3D achieved = lowerPose * new Transform3D(composed, Vector3.Zero)
            * lowerRest.AffineInverse() * handRest;

        Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            lowerRest, lowerPose, helperRest, handRest, achieved, 1.0f,
            out Transform3D helperLocal, out Transform3D handLocal));
        // The bend axis (Right) is perpendicular to the rest longitudinal axis (Back), so the
        // helper keeps its rest-relative placement under the posed lower arm.
        AssertTransformCompleteApproximately(lowerRest.AffineInverse() * helperRest, helperLocal);
        AssertTransformCompleteApproximately(achieved, lowerPose * helperLocal * handLocal);
    }

    /// <summary>
    /// A combined bend-plus-twist delta transports only the axial component: the helper angle is
    /// unchanged by the bend regardless of its sign.
    /// </summary>
    [Fact]
    public void AffineTargets_CombinedBendAndTwist_TransportsOnlyTheAxialComponent()
    {
        Transform3D lowerRest = Transform3D.Identity;
        Transform3D handRest = new(Basis.Identity, Vector3.Back);
        Transform3D helperRest = new(Basis.Identity, new Vector3(0.0f, 0.0f, 0.5f));
        const float twistAngle = 0.62f;
        const float alpha = 0.5f;
        foreach (float bend in new[] { -0.83f, 0.83f })
        {
            Basis composed = new Basis(Vector3.Right, bend) * new Basis(Vector3.Back, twistAngle);
            Transform3D achieved = new Transform3D(composed, Vector3.Zero) * handRest;
            Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
                lowerRest, lowerRest, helperRest, handRest, achieved, alpha,
                out Transform3D helperLocal, out Transform3D handLocal));
            Transform3D skinning = lowerRest * helperLocal * helperRest.AffineInverse();
            Assert.InRange(
                MathF.Abs(SignedAngleAbout(skinning.Basis.GetRotationQuaternion(), Vector3.Back) - (twistAngle * alpha)),
                0.0f,
                Epsilon);
            AssertTransformCompleteApproximately(achieved, lowerRest * helperLocal * handLocal);
        }
    }

    /// <summary>
    /// The helper write does not consume the achieved wrist translation: a residual hand origin
    /// changes only the re-asserted hand local pose, never the helper.
    /// </summary>
    [Fact]
    public void AffineTargets_TranslatedWrist_ChangesOnlyTheReassertedHand()
    {
        Transform3D lowerRest = Transform3D.Identity;
        Transform3D handRest = new(Basis.Identity, Vector3.Back);
        Transform3D helperRest = new(Basis.Identity, new Vector3(0.0f, 0.0f, 0.5f));
        var achieved = new Transform3D(new Basis(Vector3.Back, 0.7f), Vector3.Back);
        Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            lowerRest, lowerRest, helperRest, handRest, achieved, 0.5f,
            out Transform3D helperLocal, out _));

        Transform3D translated = achieved;
        translated.Origin += new Vector3(0.05f, -0.03f, 0.11f);
        Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            lowerRest, lowerRest, helperRest, handRest, translated, 0.5f,
            out Transform3D helperLocalWithResidual, out Transform3D handLocalWithResidual));
        AssertTransformCompleteApproximately(helperLocal, helperLocalWithResidual);
        AssertTransformCompleteApproximately(translated, lowerRest * helperLocalWithResidual * handLocalWithResidual);
    }

    /// <summary>Rest inputs produce the helper's complete local rest and the rigidly-following hand.</summary>
    [Fact]
    public void AffineTargets_RestInputs_WriteCompleteLocalRests()
    {
        Transform3D lower = new(Basis.FromScale(Vector3.One * 1.0556f), new Vector3(0.17f, 0.24f, -0.13f));
        Transform3D hand = new(new Basis(new Quaternion(Vector3.Up, 0.15f)), lower * Vector3.Back);
        Transform3D helper = new(new Basis(new Quaternion(Vector3.Right, 0.34f)), new Vector3(0.24f, 0.36f, 0.17f));
        Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            lower, lower, helper, hand, hand, 0.5f, out Transform3D lx, out Transform3D lw));
        AssertTransformCompleteApproximately(lower.AffineInverse() * helper, lx);
        AssertTransformCompleteApproximately(hand, lower * lx * lw);
    }

    /// <summary>Unsupported basis domains, degenerate axes and out-of-range weights fail before any output.</summary>
    [Fact]
    public void AffineTargets_UnsupportedInputs_RejectWithoutPartialWrites()
    {
        Transform3D hand = new(Basis.Identity, Vector3.Back);
        Transform3D helper = Transform3D.Identity;
        Basis[] invalid =
        [
            new Basis(-Vector3.Right, Vector3.Up, Vector3.Back),
            new Basis(Vector3.Zero, Vector3.Up, Vector3.Back),
            new Basis(Vector3.Right * 1.2f, Vector3.Up, Vector3.Back),
            new Basis(Vector3.Right, Vector3.Up + (Vector3.Right * 0.2f), Vector3.Back),
        ];
        foreach (Basis basis in invalid)
        {
            Assert.False(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
                new Transform3D(basis, Vector3.Zero), Transform3D.Identity, helper, hand, hand, 0.5f,
                out Transform3D lx, out Transform3D lw));
            Assert.Equal(Transform3D.Identity, lx);
            Assert.Equal(Transform3D.Identity, lw);
        }

        // Degenerate longitudinal axis: the hand rest sits on the lower-arm origin.
        Assert.False(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            Transform3D.Identity, Transform3D.Identity, helper, Transform3D.Identity, Transform3D.Identity,
            0.5f, out _, out _));
        // Out-of-range and non-finite weights.
        foreach (float alpha in new[] { -0.1f, 1.5f, float.NaN })
        {
            Assert.False(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
                Transform3D.Identity, Transform3D.Identity, helper, hand, hand, alpha, out _, out _));
        }

        // Non-finite achieved pose.
        Transform3D nonFinite = new(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f));
        Assert.False(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            Transform3D.Identity, Transform3D.Identity, helper, hand, nonFinite, 0.5f, out _, out _));
    }

    /// <summary>Every sweep sample around the ±π boundaries is invariant under the quaternion double cover.</summary>
    [Fact]
    public void PrincipalTwist_AroundBothPiBoundaries_IsQuaternionDoubleCoverInvariant()
    {
        foreach (float degrees in new[] { -180.1f, -180.0f, -179.9f, 179.9f, 180.0f, 180.1f })
        {
            Quaternion sample = new(Vector3.Forward, Mathf.DegToRad(degrees));
            Quaternion negated = new(-sample.X, -sample.Y, -sample.Z, -sample.W);
            Assert.True(ForearmTwistMath.TryExtractSignedPrincipalTwist(sample, Vector3.Forward, out float expected));
            Assert.True(ForearmTwistMath.TryExtractSignedPrincipalTwist(negated, Vector3.Forward, out float actual));
            Assert.InRange(MathF.Abs(expected - actual), 0.0f, Epsilon);
        }
    }

    /// <summary>The extractor returns the short signed component and rejects unusable axes.</summary>
    [Fact]
    public void TryExtractSignedPrincipalTwist_SignedAndDegenerateAxes_AreHandledSafely()
    {
        Assert.True(ForearmTwistMath.TryExtractSignedPrincipalTwist(
            new Quaternion(Vector3.Forward, -0.75f), Vector3.Forward, out float angle));
        Assert.InRange(angle, -0.75f - Epsilon, -0.75f + Epsilon);
        Assert.False(ForearmTwistMath.TryExtractSignedPrincipalTwist(Quaternion.Identity, Vector3.Zero, out float invalidAngle));
        Assert.Equal(0.0f, invalidAngle);
        Assert.False(ForearmTwistMath.TryExtractSignedPrincipalTwist(
            new Quaternion(float.NaN, 0.0f, 0.0f, 1.0f), Vector3.Forward, out float nonFiniteAngle));
        Assert.Equal(0.0f, nonFiniteAngle);
    }

    /// <summary>
    /// The modifier's shared weight contract preserves the shipped default and inclusive boundaries, so clamped
    /// out-of-range assignments cannot make its helper calculation exceed a fully weighted hand twist.
    /// </summary>
    [Fact]
    public void TwistWeight_DefaultBoundariesAndOutOfRangeValues_AreClampedBeforeHelperCalculation()
    {
        Assert.Equal(0.50f, ForearmTwistModifier.DefaultTwistWeight);
        Assert.Equal(0.0f, ForearmTwistModifier.ClampTwistWeight(0.0f));
        Assert.Equal(1.0f, ForearmTwistModifier.ClampTwistWeight(1.0f));
        Assert.Equal(0.0f, ForearmTwistModifier.ClampTwistWeight(-0.5f));
        Assert.Equal(1.0f, ForearmTwistModifier.ClampTwistWeight(1.75f));

        Transform3D handRest = new(Basis.Identity, Vector3.Back);
        Transform3D helperRest = Transform3D.Identity;
        Transform3D achieved = new Transform3D(new Basis(Vector3.Back, 0.8f), Vector3.Zero) * handRest;
        Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            Transform3D.Identity, Transform3D.Identity, helperRest, handRest, achieved, 1.0f,
            out Transform3D fullyWeightedLocal, out _));
        Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            Transform3D.Identity, Transform3D.Identity, helperRest, handRest, achieved,
            ForearmTwistModifier.ClampTwistWeight(1.75f), out Transform3D clampedLocal, out _));
        AssertTransformCompleteApproximately(fullyWeightedLocal, clampedLocal);

        Assert.True(ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            Transform3D.Identity, Transform3D.Identity, helperRest, handRest, achieved,
            ForearmTwistModifier.ClampTwistWeight(-0.5f), out Transform3D zeroLocal, out _));
        AssertTransformCompleteApproximately(helperRest, zeroLocal);
    }

    private static float SignedAngleAbout(Quaternion rotation, Vector3 axis)
    {
        Quaternion canonical = rotation.W < 0.0f
            ? new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W).Normalized()
            : rotation.Normalized();
        Vector3 vector = new(canonical.X, canonical.Y, canonical.Z);
        float signedSinHalfAngle = vector.Dot(axis.Normalized());
        Quaternion twist = new Quaternion(
            axis.Normalized().X * signedSinHalfAngle,
            axis.Normalized().Y * signedSinHalfAngle,
            axis.Normalized().Z * signedSinHalfAngle,
            canonical.W).Normalized();
        float twistVectorLength = new Vector3(twist.X, twist.Y, twist.Z).Length();
        float angle = 2.0f * MathF.Atan2(twistVectorLength, twist.W);
        return signedSinHalfAngle < 0.0f ? -angle : angle;
    }

    private static void AssertTransformCompleteApproximately(Transform3D expected, Transform3D actual)
    {
        AssertVectorApproximately(expected.Origin, actual.Origin);
        AssertVectorApproximately(expected.Basis.X, actual.Basis.X);
        AssertVectorApproximately(expected.Basis.Y, actual.Basis.Y);
        AssertVectorApproximately(expected.Basis.Z, actual.Basis.Z);
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual)
        => Assert.InRange(expected.DistanceTo(actual), 0.0f, 5.0f * Epsilon);
}
