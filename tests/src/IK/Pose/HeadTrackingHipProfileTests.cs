using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for <see cref="HeadTrackingHipProfile"/>, exercised through its pure static
/// helpers.
/// </summary>
public sealed class HeadTrackingHipProfileTests
{
    private const float Tolerance = 1e-4f;
    private const float VerticalWeight = 1.0f;
    private const float LateralWeight = 0.5f;
    private const float ForwardWeight = 0.1f;

    /// <summary>
    /// Zero head offset returns the rest hip position exactly.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_ZeroOffset_ReturnsHipRest()
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Vector3 restHead = new(0f, 1.65f, 0f);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            restHead,
            currentHeadLocal: restHead);

        Assert.Equal(hipRest, result);
    }

    /// <summary>
    /// Pure movement along the hip rest up/down axis keeps full positional weight.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_PureVerticalOffset_KeepsFullWeight()
    {
        Vector3 offset = new(0f, -0.24f, 0f);

        Vector3 result = ComputeDefaultWeightedHipPosition(Basis.Identity, offset);

        AssertClose(CreateHipRest() + offset, result);
    }

    /// <summary>
    /// Equal-magnitude offsets along the hip-rest local up axis remain mirrored after weighting.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_OppositeVerticalOffsets_PreserveSymmetry()
    {
        Basis hipRestBasisLocal = Basis.FromEuler(new Vector3(0.29f, -0.41f, 0.17f)).Orthonormalized();
        const float verticalMagnitude = 0.24f;
        Vector3 positiveVerticalOffsetLocal = hipRestBasisLocal * new Vector3(0f, verticalMagnitude, 0f);
        Vector3 negativeVerticalOffsetLocal = hipRestBasisLocal * new Vector3(0f, -verticalMagnitude, 0f);
        Vector3 hipRest = CreateHipRest();

        Vector3 positiveResult = ComputeDefaultWeightedHipPosition(hipRestBasisLocal, positiveVerticalOffsetLocal);
        Vector3 negativeResult = ComputeDefaultWeightedHipPosition(hipRestBasisLocal, negativeVerticalOffsetLocal);
        Vector3 positiveDisplacement = positiveResult - hipRest;
        Vector3 negativeDisplacement = negativeResult - hipRest;

        AssertClose(positiveDisplacement, -negativeDisplacement);
        Assert.True(
            Mathf.Abs(positiveDisplacement.Length() - negativeDisplacement.Length()) <= Tolerance,
            $"Expected mirrored vertical displacement magnitudes, got {positiveDisplacement.Length()} and {negativeDisplacement.Length()}.");
    }

    /// <summary>
    /// Pure movement along the hip rest lateral axis uses the configured lateral weight.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_PureLateralOffset_UsesHalfWeight()
    {
        Vector3 offset = new(0.24f, 0f, 0f);

        Vector3 result = ComputeDefaultWeightedHipPosition(Basis.Identity, offset);

        AssertClose(CreateHipRest() + new Vector3(offset.X * LateralWeight, 0f, 0f), result);
    }

    /// <summary>
    /// Pure movement along the hip rest forward/back axis uses the configured forward weight.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_PureForwardOffset_UsesForwardWeight()
    {
        Vector3 offset = new(0f, 0f, -0.24f);

        Vector3 result = ComputeDefaultWeightedHipPosition(Basis.Identity, offset);

        AssertClose(CreateHipRest() + new Vector3(0f, 0f, offset.Z * ForwardWeight), result);
    }

    /// <summary>
    /// Mixed offsets weight each hip-rest-axis component independently.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_MixedOffset_WeightsEachAxisIndependently()
    {
        Vector3 offset = new(0.20f, -0.30f, -0.40f);

        Vector3 result = ComputeDefaultWeightedHipPosition(Basis.Identity, offset);

        Vector3 expectedOffset = new(
            offset.X * LateralWeight,
            offset.Y * VerticalWeight,
            offset.Z * ForwardWeight);

        AssertClose(CreateHipRest() + expectedOffset, result);
    }

    /// <summary>
    /// Interpolating between vertical and lateral movement changes the weighted output smoothly.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_VerticalToLateralInterpolation_VariesContinuously()
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 verticalOffset = new(0f, -0.24f, 0f);
        Vector3 lateralOffset = new(0.24f, 0f, 0f);
        float[] interpolationSamples = [0.0f, 0.25f, 0.5f, 0.75f, 1.0f];

        Vector3? previousWeightedOffset = null;
        Vector3? previousExpectedWeightedOffset = null;

        foreach (float t in interpolationSamples)
        {
            Vector3 inputOffset = verticalOffset.Lerp(lateralOffset, t);
            Vector3 weightedOffset = ComputeDefaultWeightedHipPosition(Basis.Identity, inputOffset) - hipRest;
            Vector3 expectedWeightedOffset = new(
                inputOffset.X * LateralWeight,
                inputOffset.Y * VerticalWeight,
                inputOffset.Z * ForwardWeight);

            AssertClose(expectedWeightedOffset, weightedOffset);
            Assert.True(weightedOffset.X >= -Tolerance, $"Expected non-negative lateral response at t={t}, got {weightedOffset}.");
            Assert.True(weightedOffset.Y <= Tolerance, $"Expected non-positive vertical response at t={t}, got {weightedOffset}.");
            Assert.True(Mathf.Abs(weightedOffset.Z) <= Tolerance, $"Expected no forward/back response at t={t}, got {weightedOffset}.");

            if (previousWeightedOffset is Vector3 previousActual
                && previousExpectedWeightedOffset is Vector3 previousExpected)
            {
                Assert.True(
                    weightedOffset.X + Tolerance >= previousActual.X,
                    $"Expected lateral response to increase smoothly between samples, got {previousActual} then {weightedOffset}.");
                Assert.True(
                    weightedOffset.Y + Tolerance >= previousActual.Y,
                    $"Expected vertical response to move smoothly towards zero between samples, got {previousActual} then {weightedOffset}.");
                Assert.True(
                    Mathf.Abs((weightedOffset - previousActual).Length() - (expectedWeightedOffset - previousExpected).Length()) <= Tolerance,
                    $"Expected interpolation step size to remain continuous between samples, got {previousActual} then {weightedOffset}.");
            }

            previousWeightedOffset = weightedOffset;
            previousExpectedWeightedOffset = expectedWeightedOffset;
        }
    }

    /// <summary>
    /// Axis weighting is evaluated in the rotated hip rest basis, not world axes.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_RotatedRestBasis_UsesRestLocalAxesForWeighting()
    {
        Basis hipRestBasisLocal = Basis.FromEuler(new Vector3(0.31f, -0.82f, 0.47f)).Orthonormalized();
        Vector3 offsetInHipRestLocal = new(0.20f, -0.30f, -0.40f);
        Vector3 headOffsetLocal = hipRestBasisLocal * offsetInHipRestLocal;

        Vector3 result = ComputeDefaultWeightedHipPosition(hipRestBasisLocal, headOffsetLocal);

        Vector3 expectedOffsetLocal = hipRestBasisLocal * new Vector3(
            offsetInHipRestLocal.X * LateralWeight,
            offsetInHipRestLocal.Y * VerticalWeight,
            offsetInHipRestLocal.Z * ForwardWeight);

        AssertClose(CreateHipRest() + expectedOffsetLocal, result);
    }

    /// <summary>
    /// Per-axis positional weights clamp into the supported [0, 1] range.
    /// </summary>
    [Theory]
    [InlineData(-0.25f, 0.0f)]
    [InlineData(1.5f, 1.0f)]
    public void ComputeHipLocalPosition_AxisWeightsOutsideRange_ClampPerAxis(
        float configuredWeight,
        float expectedWeight)
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        Vector3 offsetInHipRestLocal = new(0.20f, -0.30f, -0.40f);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            Basis.Identity,
            restHead,
            restHead + offsetInHipRestLocal,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: configuredWeight,
            lateralPositionWeight: configuredWeight,
            forwardPositionWeight: configuredWeight);

        AssertClose(hipRest + (offsetInHipRestLocal * expectedWeight), result);
    }

    /// <summary>
    /// Rotation compensation is applied in the opposite direction from the rotation displacement.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_RotationCompensation_UsesOppositeDirection()
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        Vector3 headOffset = new(0.2f, -0.1f, 0.05f);
        Vector3 rotationDisplacement = new(0.03f, 0.02f, -0.04f);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            Basis.Identity,
            restHead,
            restHead + headOffset,
            rotationDisplacement,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: 1.0f,
            lateralPositionWeight: 1.0f,
            forwardPositionWeight: 1.0f);

        AssertClose(hipRest + (headOffset - rotationDisplacement), result);
    }

    /// <summary>
    /// Rotation compensation weight scales the rotation-derived displacement.
    /// </summary>
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.5f)]
    [InlineData(1.5f)]
    public void ComputeHipLocalPosition_RotationCompensationWeight_ScalesDisplacement(float weight)
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        Vector3 headOffset = new(0.12f, -0.22f, 0.07f);
        Vector3 rotationDisplacement = new(0.03f, -0.01f, 0.05f);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            Basis.Identity,
            restHead,
            restHead + headOffset,
            rotationDisplacement,
            weight,
            verticalPositionWeight: 1.0f,
            lateralPositionWeight: 1.0f,
            forwardPositionWeight: 1.0f);

        AssertClose(hipRest + (headOffset - (rotationDisplacement * weight)), result);
    }

    /// <summary>
    /// Negative rotation compensation weights clamp to zero.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_NegativeRotationCompensationWeight_ClampsToZero()
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        Vector3 headOffset = new(0.12f, -0.18f, 0.03f);
        Vector3 rotationDisplacement = new(0.04f, 0.01f, -0.02f);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            Basis.Identity,
            restHead,
            restHead + headOffset,
            rotationDisplacement,
            rotationCompensationWeight: -2.0f,
            verticalPositionWeight: 1.0f,
            lateralPositionWeight: 1.0f,
            forwardPositionWeight: 1.0f);

        AssertClose(hipRest + headOffset, result);
    }

    /// <summary>
    /// Sub-epsilon positional offsets snap back to the rest hip pose.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_SubEpsilonOffset_SnapsToHipRest()
    {
        const float subEpsilon = 1e-5f;

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            CreateHipRest(),
            CreateRestHead(),
            CreateRestHead() + new Vector3(subEpsilon, -subEpsilon, subEpsilon));

        Assert.Equal(CreateHipRest(), result);
    }

    /// <summary>
    /// Epsilon snap uses the combined positional and rotational correction.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_CombinedSubEpsilonOffset_SnapsToHipRest()
    {
        const float subEpsilon = 5e-5f;

        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        Vector3 headOffset = new(1e-3f, 0f, 0f);
        Vector3 rotationDisplacement = headOffset - new Vector3(subEpsilon, 0f, 0f);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            Basis.Identity,
            restHead,
            restHead + headOffset,
            rotationDisplacement,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: 1.0f,
            lateralPositionWeight: 1.0f,
            forwardPositionWeight: 1.0f);

        Assert.Equal(hipRest, result);
    }

    /// <summary>
    /// The legacy four-argument overload remains equivalent to the unit-weight five-argument overload.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_FourArgOverload_EqualsFiveArgUnitWeight()
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        Vector3 currentHead = restHead + new Vector3(0.18f, -0.27f, 0.06f);
        Vector3 rotationDisplacement = new(0.07f, -0.02f, 0.01f);

        Vector3 fourArg = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            restHead,
            currentHead,
            rotationDisplacement);

        Vector3 fiveArg = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            restHead,
            currentHead,
            rotationDisplacement,
            rotationCompensationWeight: 1.0f);

        Assert.Equal(fiveArg, fourArg);
    }

    private static Vector3 ComputeDefaultWeightedHipPosition(Basis hipRestBasisLocal, Vector3 headOffsetLocal)
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();

        return HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestBasisLocal,
            restHead,
            restHead + headOffsetLocal,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight);
    }

    private static Vector3 CreateHipRest() => new(0f, 0.95f, 0f);

    private static Vector3 CreateRestHead() => new(0f, 1.65f, 0f);

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        float delta = (expected - actual).Length();
        Assert.True(
            delta <= Tolerance,
            $"Expected {expected}, got {actual} (|delta|={delta}).");
    }
}
