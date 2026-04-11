using AlleyCat.IK;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK;

/// <summary>
/// Unit coverage for the look-at delta shoulder correction approach.
/// </summary>
public sealed class ShoulderCorrectionComputerTests
{
    private const float Epsilon = 1e-5f;

    private static readonly Vector3 _bodyUp = new(0f, 1f, 0f);

    /// <summary>
    /// Verifies that identical rest and current directions yield identity correction.
    /// </summary>
    [Fact]
    public void ComputeCorrection_NoDeviation_ReturnsIdentity()
    {
        Vector3 armDir = Vector3.Right;
        Basis restLook = ShoulderCorrectionComputer.BuildLookAtBasis(armDir, _bodyUp);
        Basis currentLook = ShoulderCorrectionComputer.BuildLookAtBasis(armDir, _bodyUp);

        Quaternion correction = ShoulderCorrectionComputer.ComputeCorrection(restLook, currentLook, 0.55f);

        AssertQuaternionEquivalent(Quaternion.Identity, correction);
    }

    /// <summary>
    /// Verifies that elevating the arm produces a non-trivial correction
    /// and that increasing the weight increases the correction magnitude.
    /// </summary>
    [Fact]
    public void ComputeCorrection_ElevationDeviation_RespondsToWeight()
    {
        Vector3 rest = Vector3.Right;
        Vector3 elevated = new Vector3(1f, 0.4f, 0f).Normalized();

        Basis restLook = ShoulderCorrectionComputer.BuildLookAtBasis(rest, _bodyUp);
        Basis currentLook = ShoulderCorrectionComputer.BuildLookAtBasis(elevated, _bodyUp);

        Quaternion correctionA = ShoulderCorrectionComputer.ComputeCorrection(restLook, currentLook, 0.3f);
        Quaternion correctionB = ShoulderCorrectionComputer.ComputeCorrection(restLook, currentLook, 0.6f);

        float angleA = QuaternionAngle(correctionA);
        float angleB = QuaternionAngle(correctionB);

        Assert.True(angleA > 1e-4f,
            $"Should produce non-trivial correction for elevation. Got: {angleA:F6} rad.");
        Assert.True(angleB > angleA * 1.5f,
            $"Doubling weight should roughly double correction angle. " +
            $"Weight 0.3: {angleA:F6} rad, weight 0.6: {angleB:F6} rad.");
    }

    /// <summary>
    /// Verifies overhead arm direction produces larger correction than forward-level.
    /// </summary>
    [Fact]
    public void ComputeCorrection_OverheadDeviation_LargerThanForwardLevel()
    {
        Vector3 rest = new Vector3(0.95f, -0.3f, 0.05f).Normalized();
        Vector3 forwardLevel = new Vector3(0.95f, 0.05f, -0.25f).Normalized();
        Vector3 overhead = new Vector3(0.35f, 0.93f, -0.1f).Normalized();

        Basis restLook = ShoulderCorrectionComputer.BuildLookAtBasis(rest, _bodyUp);
        Basis forwardLook = ShoulderCorrectionComputer.BuildLookAtBasis(forwardLevel, _bodyUp);
        Basis overheadLook = ShoulderCorrectionComputer.BuildLookAtBasis(overhead, _bodyUp);

        // Use the same weight for both — the angular difference itself should be larger
        // for overhead since it deviates further from rest.
        Quaternion forwardCorrection = ShoulderCorrectionComputer.ComputeCorrection(restLook, forwardLook, 0.55f);
        Quaternion overheadCorrection = ShoulderCorrectionComputer.ComputeCorrection(restLook, overheadLook, 0.55f);

        float forwardAngle = QuaternionAngle(forwardCorrection);
        float overheadAngle = QuaternionAngle(overheadCorrection);

        Assert.True(overheadAngle > forwardAngle + 0.01f,
            "Overhead should produce a larger correction than forward-level. " +
            $"Forward: {forwardAngle:F6} rad, overhead: {overheadAngle:F6} rad.");
    }

    /// <summary>
    /// Verifies forward arm deviation produces non-trivial correction.
    /// </summary>
    [Fact]
    public void ComputeCorrection_ForwardDeviation_ProducesCorrection()
    {
        Vector3 rest = new Vector3(1f, 0.3f, 0f).Normalized();
        Vector3 current = new Vector3(1f, 0.3f, -1f).Normalized();

        Basis restLook = ShoulderCorrectionComputer.BuildLookAtBasis(rest, _bodyUp);
        Basis currentLook = ShoulderCorrectionComputer.BuildLookAtBasis(current, _bodyUp);

        Quaternion correction = ShoulderCorrectionComputer.ComputeCorrection(restLook, currentLook, 0.5f);
        float angle = QuaternionAngle(correction);

        Assert.True(angle > 1e-4f,
            $"Should produce non-trivial forward correction. Got: {angle:F6} rad.");
    }

    /// <summary>
    /// Verifies left-arm and right-arm elevation produce mirrored corrections:
    /// same magnitude but different rotation sense.
    /// </summary>
    [Fact]
    public void ComputeCorrection_LeftVsRightElevation_ProducesMirroredCorrections()
    {
        Vector3 restRight = Vector3.Right;
        Vector3 elevatedRight = new Vector3(1f, 0.4f, 0f).Normalized();
        Vector3 restLeft = Vector3.Left;
        Vector3 elevatedLeft = new Vector3(-1f, 0.4f, 0f).Normalized();

        Basis restLookRight = ShoulderCorrectionComputer.BuildLookAtBasis(restRight, _bodyUp);
        Basis currentLookRight = ShoulderCorrectionComputer.BuildLookAtBasis(elevatedRight, _bodyUp);
        Basis restLookLeft = ShoulderCorrectionComputer.BuildLookAtBasis(restLeft, _bodyUp);
        Basis currentLookLeft = ShoulderCorrectionComputer.BuildLookAtBasis(elevatedLeft, _bodyUp);

        Quaternion correctionRight = ShoulderCorrectionComputer.ComputeCorrection(
            restLookRight, currentLookRight, 0.5f);
        Quaternion correctionLeft = ShoulderCorrectionComputer.ComputeCorrection(
            restLookLeft, currentLookLeft, 0.5f);

        float angleRight = QuaternionAngle(correctionRight);
        float angleLeft = QuaternionAngle(correctionLeft);

        Assert.True(angleRight > 1e-4f, "Right correction should be non-trivial.");
        Assert.True(angleLeft > 1e-4f, "Left correction should be non-trivial.");

        // Both sides should produce the same angular magnitude.
        Assert.True(Mathf.Abs(angleRight - angleLeft) < 0.02f,
            $"Left and right should produce similar magnitude corrections. " +
            $"Right: {angleRight:F6} rad, left: {angleLeft:F6} rad.");
    }

    /// <summary>
    /// Verifies backward deviation produces a non-trivial correction
    /// and that increasing weight increases the correction.
    /// </summary>
    [Fact]
    public void ComputeCorrection_BackwardDeviation_RespondsToWeight()
    {
        Vector3 rest = new Vector3(1f, 0.3f, 0f).Normalized();
        Vector3 current = new Vector3(1f, 0.3f, 1f).Normalized();

        Basis restLook = ShoulderCorrectionComputer.BuildLookAtBasis(rest, _bodyUp);
        Basis currentLook = ShoulderCorrectionComputer.BuildLookAtBasis(current, _bodyUp);

        Quaternion correctionA = ShoulderCorrectionComputer.ComputeCorrection(restLook, currentLook, 0.2f);
        Quaternion correctionB = ShoulderCorrectionComputer.ComputeCorrection(restLook, currentLook, 0.5f);

        float angleA = QuaternionAngle(correctionA);
        float angleB = QuaternionAngle(correctionB);

        Assert.True(angleA > 1e-4f,
            $"Should produce non-trivial backward correction. Got: {angleA:F6} rad.");
        Assert.True(angleB > angleA * 1.5f,
            $"Increasing weight should increase backward correction. " +
            $"Weight 0.2: {angleA:F6} rad, weight 0.5: {angleB:F6} rad.");
    }

    /// <summary>
    /// Verifies correction is deterministic for repeated identical inputs.
    /// </summary>
    [Fact]
    public void ComputeCorrection_SameInput_IsDeterministic()
    {
        Vector3 rest = new Vector3(0.95f, -0.3f, 0.05f).Normalized();
        Vector3 current = new Vector3(0.6f, 0.5f, -0.62f).Normalized();

        Basis restLook = ShoulderCorrectionComputer.BuildLookAtBasis(rest, _bodyUp);
        Basis currentLook = ShoulderCorrectionComputer.BuildLookAtBasis(current, _bodyUp);

        Quaternion first = ShoulderCorrectionComputer.ComputeCorrection(restLook, currentLook, 0.5f);

        for (int i = 0; i < 10; i++)
        {
            Quaternion next = ShoulderCorrectionComputer.ComputeCorrection(restLook, currentLook, 0.5f);

            Assert.Equal(first.X, next.X);
            Assert.Equal(first.Y, next.Y);
            Assert.Equal(first.Z, next.Z);
            Assert.Equal(first.W, next.W);
        }
    }

    /// <summary>
    /// Verifies weight of zero yields identity correction (full suppression).
    /// </summary>
    [Fact]
    public void ComputeCorrection_ZeroWeight_ReturnsIdentity()
    {
        Vector3 rest = Vector3.Right;
        Vector3 current = new Vector3(0.5f, 0.8f, -0.3f).Normalized();

        Basis restLook = ShoulderCorrectionComputer.BuildLookAtBasis(rest, _bodyUp);
        Basis currentLook = ShoulderCorrectionComputer.BuildLookAtBasis(current, _bodyUp);

        Quaternion correction = ShoulderCorrectionComputer.ComputeCorrection(restLook, currentLook, 0f);

        AssertQuaternionEquivalent(Quaternion.Identity, correction);
    }

    /// <summary>
    /// Verifies the adaptive weight yields lower values for lowered arms
    /// and higher values for raised arms.
    /// </summary>
    [Fact]
    public void ComputeAdaptiveWeight_LoweredArm_ProducesLowerWeightThanRaisedArm()
    {
        Vector3 loweredDir = new Vector3(1f, -0.7f, 0f).Normalized();
        Vector3 raisedDir = new Vector3(0.3f, 0.9f, 0f).Normalized();

        float loweredWeight = ShoulderCorrectionComputer.ComputeAdaptiveWeight(loweredDir, 0.55f);
        float raisedWeight = ShoulderCorrectionComputer.ComputeAdaptiveWeight(raisedDir, 0.55f);

        Assert.True(loweredWeight < raisedWeight,
            $"Lowered arm should produce lower adaptive weight than raised arm. " +
            $"Lowered: {loweredWeight:F6}, raised: {raisedWeight:F6}.");
        Assert.True(loweredWeight > 0f,
            $"Lowered arm weight should still be positive (residual). Got: {loweredWeight:F6}.");
    }

    /// <summary>
    /// Verifies BuildLookAtBasis returns identity for degenerate inputs.
    /// </summary>
    [Fact]
    public void BuildLookAtBasis_DegenerateInput_ReturnsIdentity()
    {
        Basis zeroDir = ShoulderCorrectionComputer.BuildLookAtBasis(Vector3.Zero, _bodyUp);
        Basis zeroUp = ShoulderCorrectionComputer.BuildLookAtBasis(Vector3.Right, Vector3.Zero);
        // Collinear: arm direction parallel to up reference.
        Basis collinear = ShoulderCorrectionComputer.BuildLookAtBasis(Vector3.Up, _bodyUp);

        AssertBasisEquivalent(Basis.Identity, zeroDir);
        AssertBasisEquivalent(Basis.Identity, zeroUp);
        AssertBasisEquivalent(Basis.Identity, collinear);
    }

    private static float QuaternionAngle(Quaternion q)
    {
        float dot = Mathf.Abs(Quaternion.Identity.Dot(q));
        dot = Mathf.Clamp(dot, -1f, 1f);
        return 2f * Mathf.Acos(dot);
    }

    private static void AssertQuaternionEquivalent(Quaternion expected, Quaternion actual)
    {
        Vector3 expectedRight = expected * Vector3.Right;
        Vector3 actualRight = actual * Vector3.Right;
        Vector3 expectedUp = expected * Vector3.Up;
        Vector3 actualUp = actual * Vector3.Up;
        Vector3 expectedForward = expected * Vector3.Forward;
        Vector3 actualForward = actual * Vector3.Forward;

        AssertVectorClose(expectedRight, actualRight);
        AssertVectorClose(expectedUp, actualUp);
        AssertVectorClose(expectedForward, actualForward);
    }

    private static void AssertBasisEquivalent(Basis expected, Basis actual)
    {
        AssertVectorClose(expected.Column0, actual.Column0);
        AssertVectorClose(expected.Column1, actual.Column1);
        AssertVectorClose(expected.Column2, actual.Column2);
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Mathf.IsEqualApprox(expected.X, actual.X), $"X mismatch. Expected {expected.X}, got {actual.X}.");
        Assert.True(Mathf.IsEqualApprox(expected.Y, actual.Y), $"Y mismatch. Expected {expected.Y}, got {actual.Y}.");
        Assert.True(Mathf.IsEqualApprox(expected.Z, actual.Z), $"Z mismatch. Expected {expected.Z}, got {actual.Z}.");

        Assert.True((expected - actual).Length() <= Epsilon,
            $"Vector mismatch. Expected {expected}, got {actual} (|delta|={(expected - actual).Length()}).");
    }
}
