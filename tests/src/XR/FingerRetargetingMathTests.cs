using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Unit coverage for the absolute anatomical parent-relative finger retargeting math: parent-relative
/// derivation <c>parentWorld⁻¹ · childWorld</c>, neutral-delta derivation, independence from session-entry
/// state and common world motion, the metacarpal-parent rule for non-thumb proximals, and quaternion
/// hemisphere stabilisation (XR-002 TR20, TR27-TR29).
/// </summary>
public sealed class FingerRetargetingMathTests
{
    private const float Epsilon = 1e-5f;

    /// <summary>
    /// The neutral delta is the normalised, identity-hemisphere-aligned quotient
    /// <c>S0⁻¹ · S</c>: at the profile neutral it is exactly identity, a handed composition reproduces the
    /// applied motion, and a hemisphere-flipped input yields the identical aligned output (XR-002 TR20-TR21).
    /// </summary>
    [Fact]
    public void DeriveNeutralDelta_ProfileNeutralAndHemisphereFlip_ReturnsStableIdentityAlignedDelta()
    {
        Quaternion sourceNeutral = new(new Vector3(0.2f, 0.8f, -0.5f).Normalized(), 0.7f);
        Quaternion rightLocalDelta = new(new Vector3(0.3f, -0.4f, 0.85f).Normalized(), 0.5f);
        Quaternion source = sourceNeutral * rightLocalDelta;

        Quaternion neutral = FingerRetargetingMath.DeriveNeutralDelta(sourceNeutral, sourceNeutral);
        Quaternion delta = FingerRetargetingMath.DeriveNeutralDelta(source, sourceNeutral);
        Quaternion flippedSource = new(-source.X, -source.Y, -source.Z, -source.W);
        Quaternion flippedDelta = FingerRetargetingMath.DeriveNeutralDelta(flippedSource, sourceNeutral);
        Quaternion negativeNeutral = new(-sourceNeutral.X, -sourceNeutral.Y, -sourceNeutral.Z, -sourceNeutral.W);
        Quaternion flippedNeutralDelta = FingerRetargetingMath.DeriveNeutralDelta(source, negativeNeutral);

        AssertAngularErrorBelowPointOneDegree(Quaternion.Identity, neutral);
        AssertAngularErrorBelowPointOneDegree(rightLocalDelta, delta);
        AssertAngularErrorBelowPointOneDegree(delta, flippedDelta);
        AssertAngularErrorBelowPointOneDegree(delta, flippedNeutralDelta);
        Assert.True(delta.W >= 0.0f);
        Assert.True(flippedDelta.W >= 0.0f);
        Assert.InRange(delta.Length(), 1.0f - Epsilon, 1.0f + Epsilon);
    }

    /// <summary>
    /// The parent-relative quotient of two pure world rotations equals the independently composed child
    /// orientation expressed in the parent frame (XR-002 TR20).
    /// </summary>
    [Fact]
    public void DeriveParentRelativeRotation_PureRotations_ReturnsChildInParentFrame()
    {
        Quaternion parentWorld = new(Vector3.Up, 0.7f);
        Quaternion childInParent = new Quaternion(Vector3.Right, -0.4f) * new Quaternion(Vector3.Back, 0.2f);
        Quaternion childWorld = parentWorld * childInParent;

        Quaternion result = FingerRetargetingMath.DeriveParentRelativeRotation(
            new Basis(parentWorld),
            new Basis(childWorld));

        AssertRotationApproximately(childInParent, result);
    }

    /// <summary>
    /// A uniform provider-frame world scale on both bases cancels exactly in the parent-relative quotient.
    /// </summary>
    [Fact]
    public void DeriveParentRelativeRotation_UniformlyScaledBases_ScaleCancels()
    {
        const float worldScale = 1.4f;
        Quaternion parentWorld = new(Vector3.Up, 1.1f);
        Quaternion childWorld = new Quaternion(new Vector3(0.3f, 0.5f, 0.8f).Normalized(), -0.9f) * parentWorld;

        Quaternion result = FingerRetargetingMath.DeriveParentRelativeRotation(
            ScaleBasis(new Basis(parentWorld), worldScale),
            ScaleBasis(new Basis(childWorld), worldScale));

        AssertRotationApproximately(parentWorld.Inverse() * childWorld, result);
    }

    /// <summary>
    /// Common world rotation invariance — wrist motion and skeleton world rotation (XR-002 TR20): when the
    /// whole tracked pair is pre-multiplied by one common rotation, the derived parent-relative rotation is
    /// unchanged, so written locals never move when only the wrist or the skeleton root rotates. Under the
    /// removed temporal-delta scheme the written value additionally depended on a frozen session-entry
    /// reference; here no history enters at all.
    /// </summary>
    [Fact]
    public void DeriveParentRelativeRotation_CommonWorldRotation_CancelsExactly()
    {
        Quaternion parentWorld = new(Vector3.Up, 0.9f);
        Quaternion childInParent = new(new Vector3(0.3f, 0.5f, 0.8f).Normalized(), -0.6f);
        Quaternion commonRotation = new(new Vector3(-0.2f, 0.4f, 0.9f).Normalized(), 1.2f);

        Quaternion before = FingerRetargetingMath.DeriveParentRelativeRotation(
            new Basis(parentWorld),
            new Basis(parentWorld * childInParent));
        Quaternion after = FingerRetargetingMath.DeriveParentRelativeRotation(
            new Basis(commonRotation * parentWorld),
            new Basis(commonRotation * parentWorld * childInParent));

        AssertRotationApproximately(childInParent, before);
        AssertRotationApproximately(before, after);
    }

    /// <summary>
    /// Session-entry independence (XR-002 TR20): the write depends only on the CURRENT tracked pair. Two
    /// sessions that arrive at the same current joint configuration through entirely different entry poses
    /// and wrist paths write the identical rotation — the removed entry-reference/frame-map scheme instead
    /// composed the since-entry delta onto a rest baseline, so its output differed whenever the physical
    /// hand's entry pose differed.
    /// </summary>
    [Fact]
    public void DeriveParentRelativeRotation_IdenticalCurrentPair_WritesIdenticallyRegardlessOfHistory()
    {
        // The shared "now": a rotated wrist, a metacarpal spread offset, a curled proximal.
        Quaternion wristNow = new(new Vector3(0.35f, 0.2f, -0.9f).Normalized(), 0.8f);
        var metacarpalSpread = new Quaternion(Vector3.Forward, 0.5f);
        var proximalCurl = new Quaternion(Vector3.Right, 1.1f);
        Quaternion metacarpalNow = wristNow * metacarpalSpread;
        Quaternion proximalNow = metacarpalNow * proximalCurl;

        // Two sessions arrived at this same "now" through different entry poses: session A entered straight,
        // session B entered with a strongly curled proximal under a differently rotated wrist.
        Quaternion sessionAEntryProximal = metacarpalNow;
        Quaternion sessionBEntryWrist = new Quaternion(Vector3.Up, 2.1f) * new Quaternion(Vector3.Right, 1.6f) * wristNow;
        Quaternion sessionBEntryProximal = sessionBEntryWrist * metacarpalSpread * new Quaternion(Vector3.Right, 2.0f);

        // The new mapping: identical current tracked pair, identical write — no entry state exists to differ.
        Quaternion writeA = FingerRetargetingMath.DeriveParentRelativeRotation(
            new Basis(metacarpalNow),
            new Basis(proximalNow));
        Quaternion writeB = FingerRetargetingMath.DeriveParentRelativeRotation(
            new Basis(metacarpalNow),
            new Basis(proximalNow));

        AssertRotationApproximately(proximalCurl, writeA);
        AssertRotationApproximately(writeA, writeB);

        // Discriminator — the removed temporal-delta scheme composed the since-entry wrist-relative delta
        // onto a rest baseline, so its write depended on the entry pose even for an identical current pair
        // (delta = wristRelativeNow · wristRelativeEntry⁻¹, applied to a fixed rest local).
        var restLocal = new Quaternion(Vector3.Right, 0.05f);

        Quaternion wristRelativeNow = wristNow.Inverse() * proximalNow;
        Quaternion oldWriteA = wristRelativeNow * (wristNow.Inverse() * sessionAEntryProximal).Inverse() * restLocal;
        Quaternion oldWriteB = wristRelativeNow * (sessionBEntryWrist.Inverse() * sessionBEntryProximal).Inverse() * restLocal;

        Assert.True(
            oldWriteA.Normalized().AngleTo(oldWriteB.Normalized()) > 0.4f,
            "Expected the removed entry-referenced scheme to produce materially different writes for the two histories.");
        Assert.True(
            writeA.Normalized().AngleTo(oldWriteB.Normalized()) > 0.2f,
            "Expected the history-free parent-relative write to differ from the entry-referenced scheme's output.");
    }

    /// <summary>
    /// Metacarpal-parent rule for non-thumb proximals (XR-002 TR20): with a metacarpal holding a static
    /// wrist-relative spread — the WiVRn capture's roughly 29-degree little-metacarpal offset — the correct
    /// proximal write is the metacarpal-relative orientation <c>M⁻¹ · P</c>, which does NOT contain the
    /// spread; the collapsed wrist-relative derivation <c>W⁻¹ · P</c> does. The two differ materially, and
    /// the output follows the metacarpal.
    /// </summary>
    [Fact]
    public void DeriveParentRelativeRotation_NonThumbProximal_FollowsMetacarpalNotWrist()
    {
        Quaternion wristWorld = new(Vector3.Up, -0.4f);
        var staticMetacarpalSpread = new Quaternion(Vector3.Forward, Mathf.DegToRad(29.0f));
        var proximalFlex = new Quaternion(Vector3.Right, 0.35f);

        Quaternion metacarpalWorld = wristWorld * staticMetacarpalSpread;
        Quaternion proximalWorld = metacarpalWorld * proximalFlex;

        Quaternion metacarpalRelative = FingerRetargetingMath.DeriveParentRelativeRotation(
            new Basis(metacarpalWorld),
            new Basis(proximalWorld));
        Quaternion wristRelative = FingerRetargetingMath.DeriveParentRelativeRotation(
            new Basis(wristWorld),
            new Basis(proximalWorld));

        AssertRotationApproximately(proximalFlex, metacarpalRelative);
        AssertRotationApproximately((staticMetacarpalSpread * proximalFlex).Normalized(), wristRelative);
        Assert.True(
            metacarpalRelative.Normalized().AngleTo(wristRelative.Normalized()) > 0.4f,
            "Expected the metacarpal-parent and collapsed wrist-parent derivations to differ materially.");
    }

    /// <summary>
    /// Degenerate, non-orthonormal provider bases cannot inject scale or shear: the derived rotation stays
    /// unit length and its basis orthonormal with unit determinant.
    /// </summary>
    [Fact]
    public void DeriveParentRelativeRotation_NonOrthonormalBases_ReturnsOrthonormalisedUnitRotation()
    {
        var rotation = new Basis(new Quaternion(Vector3.Up, 0.8f) * new Quaternion(Vector3.Right, 0.3f));
        Basis sheared = new(rotation.X + (rotation.Y * 0.3f), rotation.Y * 1.7f, rotation.Z);

        Quaternion result = FingerRetargetingMath.DeriveParentRelativeRotation(Basis.Identity, sheared);

        Assert.True(Mathf.IsEqualApprox(result.Length(), 1.0f, Epsilon));

        Basis resultBasis = new(result);
        Assert.True(Mathf.IsEqualApprox(resultBasis.X.Length(), 1.0f, Epsilon));
        Assert.True(Mathf.IsEqualApprox(resultBasis.Y.Length(), 1.0f, Epsilon));
        Assert.True(Mathf.IsEqualApprox(resultBasis.Z.Length(), 1.0f, Epsilon));
        Assert.True(Mathf.IsEqualApprox(resultBasis.Determinant(), 1.0f, Epsilon));
    }

    /// <summary>
    /// Hemisphere stabilisation (XR-002 TR20): a provider sign flip of the same rotation is re-aligned to
    /// the previously written hemisphere, an already-aligned candidate passes through unchanged, and the
    /// encoded rotation is never changed — only the quaternion's sign.
    /// </summary>
    [Fact]
    public void StabiliseRotationHemisphere_ProviderSignFlip_RealignsToReferenceHemisphere()
    {
        Quaternion previousWrite = new(new Vector3(0.2f, -0.4f, 0.9f).Normalized(), 1.3f);
        var flipped = new Quaternion(-previousWrite.X, -previousWrite.Y, -previousWrite.Z, -previousWrite.W);

        Quaternion stabilisedFlip = FingerRetargetingMath.StabiliseRotationHemisphere(flipped, previousWrite);
        Quaternion stabilisedSame = FingerRetargetingMath.StabiliseRotationHemisphere(previousWrite, previousWrite);

        AssertRotationApproximately(previousWrite, stabilisedFlip);
        Assert.True(stabilisedFlip.Dot(previousWrite) > 0.0f, "Expected the flipped candidate to change hemisphere.");
        AssertRotationApproximately(previousWrite, stabilisedSame);
        Assert.True(stabilisedSame.Dot(previousWrite) > 0.0f);
    }

    /// <summary>
    /// Hemisphere stabilisation across a real motion sequence: consecutive writes stay numerically continuous
    /// (positive dot products) even when the provider alternates quaternion hemispheres between samples, and
    /// each stabilised value still encodes the current tracked rotation exactly.
    /// </summary>
    [Fact]
    public void StabiliseRotationHemisphere_AlternatingHemisphereSamples_StaysContinuousAndExact()
    {
        var previous = new Quaternion(Vector3.Up, 0.2f);

        for (int step = 1; step <= 6; step++)
        {
            var current = new Quaternion(Vector3.Up, 0.2f + (0.15f * step));
            Quaternion sample = step % 2 == 0
                ? new Quaternion(-current.X, -current.Y, -current.Z, -current.W)
                : current;

            Quaternion stabilised = FingerRetargetingMath.StabiliseRotationHemisphere(sample, previous);

            AssertRotationApproximately(current, stabilised);
            Assert.True(
                stabilised.Dot(previous) > 0.0f,
                $"Expected consecutive stabilised writes to stay in one hemisphere at step {step}.");
            previous = stabilised;
        }
    }

    /// <summary>
    /// Degenerate and non-normalised inputs cannot leak into the outputs: parent-relative derivations and
    /// stabilised candidates stay unit length, and a near-zero candidate falls back to the identity.
    /// </summary>
    [Fact]
    public void NonNormalisedInputs_ReturnUnitRotations()
    {
        Quaternion parentWorld = new(Vector3.Up, 0.9f);
        Quaternion childInParent = new(new Vector3(0.5f, 0.5f, 0.2f).Normalized(), -0.7f);

        Quaternion derived = FingerRetargetingMath.DeriveParentRelativeRotation(
            ScaleBasis(new Basis(parentWorld), 2.5f),
            ScaleBasis(new Basis(parentWorld * childInParent), 0.4f));
        Quaternion stabilised = FingerRetargetingMath.StabiliseRotationHemisphere(
            new Quaternion(childInParent.X * 3.0f, childInParent.Y * 3.0f, childInParent.Z * 3.0f, childInParent.W * 3.0f),
            Quaternion.Identity);
        Quaternion degenerate = FingerRetargetingMath.StabiliseRotationHemisphere(
            new Quaternion(0.0f, 0.0f, 0.0f, 0.0f),
            childInParent);

        Assert.True(Mathf.IsEqualApprox(derived.Length(), 1.0f, Epsilon));
        Assert.True(Mathf.IsEqualApprox(stabilised.Length(), 1.0f, Epsilon));
        AssertRotationApproximately(Quaternion.Identity, degenerate);
    }

    private static Basis ScaleBasis(Basis basis, float scale)
        => new(basis.X * scale, basis.Y * scale, basis.Z * scale);

    private static void AssertRotationApproximately(Quaternion expected, Quaternion actual)
    {
        // Quaternion double cover: q and -q encode the same rotation, so align hemispheres before comparing
        // component-wise for precise failure diagnostics.
        Quaternion aligned = actual.Dot(expected) < 0.0f
            ? new Quaternion(-actual.X, -actual.Y, -actual.Z, -actual.W)
            : actual;

        Assert.InRange(aligned.X, expected.X - Epsilon, expected.X + Epsilon);
        Assert.InRange(aligned.Y, expected.Y - Epsilon, expected.Y + Epsilon);
        Assert.InRange(aligned.Z, expected.Z - Epsilon, expected.Z + Epsilon);
        Assert.InRange(aligned.W, expected.W - Epsilon, expected.W + Epsilon);
    }

    private static void AssertAngularErrorBelowPointOneDegree(Quaternion expected, Quaternion actual)
        => Assert.True(
            expected.Normalized().AngleTo(actual.Normalized()) <= Mathf.DegToRad(0.1f),
            $"Expected angular error at most 0.1 degrees, got {Mathf.RadToDeg(expected.AngleTo(actual))} degrees.");
}
