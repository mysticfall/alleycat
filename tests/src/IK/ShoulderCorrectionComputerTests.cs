using AlleyCat.IK;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK;

/// <summary>
/// Unit coverage for anatomical shoulder correction computations.
/// </summary>
public sealed class ShoulderCorrectionComputerTests
{
    private const float AngleEpsilon = 1e-4f;
    private const float LengthEpsilon = 1e-5f;

    /// <summary>
    /// Verifies anatomical neutral uses the expected left/right lateral sign and remains unit length.
    /// </summary>
    [Fact]
    public void ComputeAnatomicalNeutralDirection_UsesArmSideSignAndUnitLength()
    {
        Vector3 left = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Left, 0.3f);
        Vector3 right = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Right, 0.3f);

        Assert.True(left.X < 0f, $"Left neutral should have negative X. Got {left.X}.");
        Assert.True(right.X > 0f, $"Right neutral should have positive X. Got {right.X}.");
        Assert.True(Mathf.Abs(left.Length() - 1f) <= LengthEpsilon, $"Left neutral must be unit length. Got {left.Length()}.");
        Assert.True(Mathf.Abs(right.Length() - 1f) <= LengthEpsilon, $"Right neutral must be unit length. Got {right.Length()}.");
        Assert.True(left.Y <= 0f, $"Left neutral should point downward. Got Y={left.Y}.");
        Assert.True(right.Y <= 0f, $"Right neutral should point downward. Got Y={right.Y}.");
    }

    /// <summary>
    /// Verifies anatomical neutral lateral bias is clamped to supported bounds.
    /// </summary>
    [Fact]
    public void ComputeAnatomicalNeutralDirection_ClampsLateralBias()
    {
        Vector3 clampedHigh = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Right, 5f);
        Vector3 explicitHigh = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Right, 0.95f);
        Vector3 clampedLow = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Left, -3f);
        Vector3 explicitLow = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Left, 0f);

        AssertVectorClose(clampedHigh, explicitHigh);
        AssertVectorClose(clampedLow, explicitLow);
    }

    /// <summary>
    /// Verifies correction returns identity for degenerate input direction and for zero weight.
    /// </summary>
    [Fact]
    public void ComputeCorrection_DegenerateDirectionAndZeroWeight_ReturnIdentity()
    {
        float neutralY = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Right, 0.2f).Y;

        Quaternion degenerate = ShoulderCorrectionComputer.ComputeCorrection(
            Vector3.Zero,
            ArmSide.Right,
            neutralY,
            maxElevationAngle: 1.2f,
            maxOverheadElevationBoost: 0.4f,
            maxProtractionAngle: 0.8f,
            forwardElevationDamping: 0.5f,
            weight: 1f);

        Quaternion zeroWeight = ShoulderCorrectionComputer.ComputeCorrection(
            new Vector3(0.6f, 0.7f, 0.4f).Normalized(),
            ArmSide.Right,
            neutralY,
            maxElevationAngle: 1.2f,
            maxOverheadElevationBoost: 0.4f,
            maxProtractionAngle: 0.8f,
            forwardElevationDamping: 0.5f,
            weight: 0f);

        AssertQuaternionEquivalent(Quaternion.Identity, degenerate);
        AssertQuaternionEquivalent(Quaternion.Identity, zeroWeight);
    }

    /// <summary>
    /// Verifies increasing correction weight increases the resulting correction angle.
    /// </summary>
    [Fact]
    public void ComputeCorrection_IncreasesAngleAsWeightIncreases()
    {
        Vector3 armDirection = new Vector3(0.35f, 0.78f, 0.52f).Normalized();
        float neutralY = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Right, 0.2f).Y;

        Quaternion lowWeight = ShoulderCorrectionComputer.ComputeCorrection(
            armDirection,
            ArmSide.Right,
            neutralY,
            maxElevationAngle: 1.1f,
            maxOverheadElevationBoost: 0.35f,
            maxProtractionAngle: 0.8f,
            forwardElevationDamping: 0.25f,
            weight: 0.2f);

        Quaternion highWeight = ShoulderCorrectionComputer.ComputeCorrection(
            armDirection,
            ArmSide.Right,
            neutralY,
            maxElevationAngle: 1.1f,
            maxOverheadElevationBoost: 0.35f,
            maxProtractionAngle: 0.8f,
            forwardElevationDamping: 0.25f,
            weight: 0.8f);

        float lowAngle = QuaternionAngle(lowWeight);
        float highAngle = QuaternionAngle(highWeight);

        Assert.True(lowAngle > AngleEpsilon, $"Expected non-trivial low-weight correction. Got {lowAngle:F6} rad.");
        Assert.True(highAngle > lowAngle, $"Higher weight should increase correction angle. Low={lowAngle:F6}, high={highAngle:F6}.");
    }

    /// <summary>
    /// Verifies forward-elevation damping reduces elevation-only correction magnitude.
    /// </summary>
    [Fact]
    public void ComputeCorrection_ForwardElevationDampingReducesElevation()
    {
        Vector3 armDirection = new Vector3(0.1f, 0.82f, 0.56f).Normalized();
        float neutralY = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Right, 0.2f).Y;

        Quaternion noDamping = ShoulderCorrectionComputer.ComputeCorrection(
            armDirection,
            ArmSide.Right,
            neutralY,
            maxElevationAngle: 1.1f,
            maxOverheadElevationBoost: 0.2f,
            maxProtractionAngle: 0f,
            forwardElevationDamping: 0f,
            weight: 1f);

        Quaternion fullDamping = ShoulderCorrectionComputer.ComputeCorrection(
            armDirection,
            ArmSide.Right,
            neutralY,
            maxElevationAngle: 1.1f,
            maxOverheadElevationBoost: 0.2f,
            maxProtractionAngle: 0f,
            forwardElevationDamping: 1f,
            weight: 1f);

        float noDampingAngle = QuaternionAngle(noDamping);
        float fullDampingAngle = QuaternionAngle(fullDamping);

        Assert.True(fullDampingAngle < noDampingAngle,
            $"Forward damping should reduce elevation angle. No damping={noDampingAngle:F6}, full damping={fullDampingAngle:F6}.");
    }

    /// <summary>
    /// Verifies overhead elevation boost increases correction for near-overhead arm poses.
    /// </summary>
    [Fact]
    public void ComputeCorrection_OverheadBoostIncreasesCorrectionForOverheadPose()
    {
        Vector3 armDirection = new Vector3(0.04f, 0.999f, 0.02f).Normalized();
        float neutralY = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Right, 0.25f).Y;

        Quaternion withoutBoost = ShoulderCorrectionComputer.ComputeCorrection(
            armDirection,
            ArmSide.Right,
            neutralY,
            maxElevationAngle: 0.9f,
            maxOverheadElevationBoost: 0f,
            maxProtractionAngle: 0f,
            forwardElevationDamping: 0f,
            weight: 1f);

        Quaternion withBoost = ShoulderCorrectionComputer.ComputeCorrection(
            armDirection,
            ArmSide.Right,
            neutralY,
            maxElevationAngle: 0.9f,
            maxOverheadElevationBoost: 0.45f,
            maxProtractionAngle: 0f,
            forwardElevationDamping: 0f,
            weight: 1f);

        float withoutBoostAngle = QuaternionAngle(withoutBoost);
        float withBoostAngle = QuaternionAngle(withBoost);

        Assert.True(withBoostAngle > withoutBoostAngle,
            $"Overhead boost should increase overhead correction. No boost={withoutBoostAngle:F6}, with boost={withBoostAngle:F6}.");
    }

    /// <summary>
    /// Verifies mirrored left/right poses produce similar correction-angle magnitudes.
    /// </summary>
    [Fact]
    public void ComputeCorrection_LeftAndRightMirroredPosesHaveSimilarMagnitude()
    {
        Vector3 rightDirection = new Vector3(0.62f, 0.57f, 0.54f).Normalized();
        Vector3 leftDirection = new Vector3(-0.62f, 0.57f, 0.54f).Normalized();

        float rightNeutralY = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Right, 0.2f).Y;
        float leftNeutralY = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(ArmSide.Left, 0.2f).Y;

        Quaternion right = ShoulderCorrectionComputer.ComputeCorrection(
            rightDirection,
            ArmSide.Right,
            rightNeutralY,
            maxElevationAngle: 1.15f,
            maxOverheadElevationBoost: 0.35f,
            maxProtractionAngle: 0.75f,
            forwardElevationDamping: 0.3f,
            weight: 0.7f);

        Quaternion left = ShoulderCorrectionComputer.ComputeCorrection(
            leftDirection,
            ArmSide.Left,
            leftNeutralY,
            maxElevationAngle: 1.15f,
            maxOverheadElevationBoost: 0.35f,
            maxProtractionAngle: 0.75f,
            forwardElevationDamping: 0.3f,
            weight: 0.7f);

        float rightAngle = QuaternionAngle(right);
        float leftAngle = QuaternionAngle(left);

        Assert.True(rightAngle > AngleEpsilon, $"Right correction should be non-trivial. Got {rightAngle:F6}.");
        Assert.True(leftAngle > AngleEpsilon, $"Left correction should be non-trivial. Got {leftAngle:F6}.");
        Assert.True(Mathf.Abs(rightAngle - leftAngle) < 0.02f,
            $"Mirrored poses should have similar magnitudes. Right={rightAngle:F6}, left={leftAngle:F6}.");
    }

    private static float QuaternionAngle(Quaternion value)
    {
        float dot = Mathf.Abs(Quaternion.Identity.Dot(value.Normalized()));
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

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length() <= LengthEpsilon,
            $"Vector mismatch. Expected {expected}, got {actual} (|delta|={(expected - actual).Length()}).");
    }
}
