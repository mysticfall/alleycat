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
    private const float MinimumAlignmentWeight = 0.1f;

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

    /// <summary>
    /// A pure-vertical head offset directly above the hips is fully aligned with the hip rest up
    /// axis, so the vertical component keeps the full <see cref="HeadTrackingHipProfile.VerticalPositionWeight"/>
    /// response.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_VerticalOffsetHighAlignment_AppliesFullVerticalWeight()
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        Vector3 offset = new(0f, -0.24f, 0f);
        Vector3 currentHead = restHead + offset;

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHead,
            currentHead,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight,
            minimumAlignmentWeight: MinimumAlignmentWeight);

        // Hip-to-head direction is (0, restHead.Y - hipRest.Y + offset.Y, 0) which is pure +Y, so
        // alignment = 1 and the vertical component uses the full vertical weight.
        Vector3 expected = hipRest + new Vector3(0f, offset.Y * VerticalWeight, 0f);
        AssertClose(expected, result);
    }

    /// <summary>
    /// A head offset from rest whose direction is largely misaligned with the hip rest up axis
    /// (forward-dominant stoop from rest) damps the vertical component down towards
    /// <see cref="HeadTrackingHipProfile.VerticalPositionWeight"/> × <see cref="HeadTrackingHipProfile.MinimumAlignmentWeight"/>.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_VerticalOffsetLowAlignment_DampsVerticalComponent()
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        // Place the current head such that the head-offset-from-rest points mostly forward and
        // only slightly down, so its alignment with +Y is small.
        Vector3 currentHead = restHead + new Vector3(0f, -0.05f, -0.5f);

        // Decompose the head offset in the hip rest axes to anchor expectations.
        Vector3 headOffsetLocal = currentHead - restHead;
        float verticalComponent = headOffsetLocal.Dot(Vector3.Up);
        float forwardComponent = headOffsetLocal.Dot(Vector3.Forward);
        float lateralComponent = headOffsetLocal.Dot(Vector3.Right);

        float expectedAlignment = Mathf.Abs(headOffsetLocal.Normalized().Dot(Vector3.Up));
        Assert.True(
            expectedAlignment < 0.2f,
            $"Test setup expected low alignment (<0.2); observed {expectedAlignment:F4}.");
        float expectedAlignmentWeight = Mathf.Lerp(MinimumAlignmentWeight, 1.0f, expectedAlignment);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHead,
            currentHead,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight,
            minimumAlignmentWeight: MinimumAlignmentWeight);

        Vector3 expectedOffset =
            (Vector3.Right * (lateralComponent * LateralWeight))
            + (Vector3.Up * (verticalComponent * VerticalWeight * expectedAlignmentWeight))
            + (Vector3.Forward * (forwardComponent * ForwardWeight));

        AssertClose(hipRest + expectedOffset, result);

        // The damped vertical response must be close to MinimumAlignmentWeight, not the full weight.
        Assert.True(
            expectedAlignmentWeight <= MinimumAlignmentWeight + 0.1f,
            $"Expected vertical damping close to MinimumAlignmentWeight, observed alignment weight {expectedAlignmentWeight:F4}.");
    }

    /// <summary>
    /// Values outside <c>[0, 1]</c> supplied for <c>minimumAlignmentWeight</c> are clamped before
    /// being applied to the vertical component.
    /// </summary>
    [Theory]
    [InlineData(-0.25f, 0.0f)]
    [InlineData(1.5f, 1.0f)]
    public void ComputeHipLocalPosition_MinimumAlignmentWeightClampedToRange(
        float configuredMinimum,
        float expectedMinimum)
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        // Choose a setup where the head-offset-from-rest is purely lateral, so alignment is 0
        // and alignmentWeight collapses to the clamped minimum.
        Vector3 currentHead = restHead + new Vector3(0.5f, 0f, 0f);
        Vector3 headOffsetLocal = currentHead - restHead;

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHead,
            currentHead,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight,
            minimumAlignmentWeight: configuredMinimum);

        float verticalComponent = headOffsetLocal.Dot(Vector3.Up);
        float forwardComponent = headOffsetLocal.Dot(Vector3.Forward);
        float lateralComponent = headOffsetLocal.Dot(Vector3.Right);

        float alignment = Mathf.Abs(headOffsetLocal.Normalized().Dot(Vector3.Up));
        float expectedAlignmentWeight = Mathf.Lerp(expectedMinimum, 1.0f, alignment);

        Vector3 expectedOffset =
            (Vector3.Right * (lateralComponent * LateralWeight))
            + (Vector3.Up * (verticalComponent * VerticalWeight * expectedAlignmentWeight))
            + (Vector3.Forward * (forwardComponent * ForwardWeight));

        AssertClose(hipRest + expectedOffset, result);
    }

    /// <summary>
    /// A diagonal head offset with known components along each hip rest axis yields a result
    /// whose per-axis scaling matches <c>lateral × LateralWeight</c>,
    /// <c>forward × ForwardWeight</c>, and <c>vertical × VerticalWeight × alignmentWeight</c>.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_VerticalAlignmentDamping_PreservesPerAxisWeighting()
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        Vector3 offset = new(0.20f, -0.30f, -0.40f);
        Vector3 currentHead = restHead + offset;

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHead,
            currentHead,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight,
            minimumAlignmentWeight: MinimumAlignmentWeight);

        float alignment = Mathf.Abs(offset.Normalized().Dot(Vector3.Up));
        float alignmentWeight = Mathf.Lerp(MinimumAlignmentWeight, 1.0f, alignment);

        float verticalComponent = offset.Dot(Vector3.Up);
        float forwardComponent = offset.Dot(Vector3.Forward);
        float lateralComponent = offset.Dot(Vector3.Right);

        Vector3 expectedOffset =
            (Vector3.Right * (lateralComponent * LateralWeight))
            + (Vector3.Up * (verticalComponent * VerticalWeight * alignmentWeight))
            + (Vector3.Forward * (forwardComponent * ForwardWeight));

        AssertClose(hipRest + expectedOffset, result);
    }

    /// <summary>
    /// <c>+up</c> and <c>-up</c> offsets with symmetric hip-to-head geometries produce mirrored
    /// results, confirming the alignment damping uses an absolute value and does not
    /// discriminate between upward- and downward-pointing hip-to-head vectors.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_AlignmentDamping_PositiveAndNegativeUpAreSymmetric()
    {
        Vector3 hipRest = CreateHipRest();
        Vector3 restHead = CreateRestHead();
        const float magnitude = 0.24f;
        Vector3 positiveOffset = new(0f, magnitude, 0f);
        Vector3 negativeOffset = new(0f, -magnitude, 0f);

        Vector3 positiveResult = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHead,
            restHead + positiveOffset,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight,
            minimumAlignmentWeight: MinimumAlignmentWeight);

        Vector3 negativeResult = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHead,
            restHead + negativeOffset,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight,
            minimumAlignmentWeight: MinimumAlignmentWeight);

        Vector3 positiveDisplacement = positiveResult - hipRest;
        Vector3 negativeDisplacement = negativeResult - hipRest;

        AssertClose(positiveDisplacement, -negativeDisplacement);
    }

    /// <summary>
    /// IK-004 regression guard: adding a forward lean while already crouched must not scale the
    /// vertical hip drop back up towards zero. Because the head displacement from rest retains a
    /// large vertical component, the alignment-driven damping must stay high enough to preserve
    /// the crouch depth, and the forward lean should be additive rather than rescaling.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_CrouchThenForwardLean_RetainsCrouchDepth()
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Vector3 restHead = new(0f, 1.65f, 0f);

        Vector3 crouchHead = new(0f, 1.15f, 0f);
        Vector3 crouchAndForwardHead = new(0f, 1.15f, -0.25f);
        Vector3 crouchAndBackwardHead = new(0f, 1.15f, 0.25f);

        Vector3 crouchResult = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHead,
            crouchHead,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight,
            minimumAlignmentWeight: MinimumAlignmentWeight);

        Vector3 crouchAndForwardResult = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHead,
            crouchAndForwardHead,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight,
            minimumAlignmentWeight: MinimumAlignmentWeight);

        Vector3 crouchAndBackwardResult = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHead,
            crouchAndBackwardHead,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: VerticalWeight,
            lateralPositionWeight: LateralWeight,
            forwardPositionWeight: ForwardWeight,
            minimumAlignmentWeight: MinimumAlignmentWeight);

        // The residual alignment-driven damping from minimumAlignmentWeight=0.1 leaves a small
        // vertical shortfall relative to pure crouch (~0.05 m for this setup); tolerance is
        // chosen to accommodate that residual while remaining far tighter than the buggy
        // behaviour (which would rescale the hip back up by > 0.15 m).
        const float CrouchDepthTolerance = 0.06f;

        Assert.True(
            Mathf.Abs(crouchAndForwardResult.Y - crouchResult.Y) <= CrouchDepthTolerance,
            $"Forward lean while crouched must not restore hip height. " +
            $"crouch.Y={crouchResult.Y:F4}, crouchAndForward.Y={crouchAndForwardResult.Y:F4}.");

        Assert.True(
            crouchAndForwardResult.Y < 0.75f,
            $"Crouch depth must be preserved when forward lean is added. " +
            $"crouchAndForward.Y={crouchAndForwardResult.Y:F4}, hipRest.Y={hipRest.Y:F4}.");

        Assert.True(
            Mathf.Abs(crouchAndBackwardResult.Y - crouchResult.Y) <= CrouchDepthTolerance,
            $"Backward lean while crouched must not restore hip height. " +
            $"crouch.Y={crouchResult.Y:F4}, crouchAndBackward.Y={crouchAndBackwardResult.Y:F4}.");
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
