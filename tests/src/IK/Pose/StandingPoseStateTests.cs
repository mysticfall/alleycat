using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for the standing-crouching blend math in
/// <see cref="StandingPoseState"/>.
/// </summary>
public sealed class StandingPoseStateTests
{
    private const float RestHeadHeight = 1.6f;
    private const float FullCrouchReferenceHipHeightRatio = 0.45f;
    private const float FullCrouchDepthMetres = RestHeadHeight * FullCrouchReferenceHipHeightRatio;
    private const float FullCrouchReferenceForwardShiftRatio = 0.04f;
    private static readonly HipLimitEnvelope _uprightEnvelope = new(0.15f, null, 0.2f, 0.2f, 0.25f, 0.15f);
    private static readonly HipLimitEnvelope _crouchedEnvelope = new(null, 0.03f, 0.06f, 0.06f, 0.08f, 0.05f);

    /// <summary>
    /// Zero descent must yield a fully standing (0.0) blend.
    /// </summary>
    [Fact]
    public void ComputePoseBlend_ZeroDescent_ReturnsZero()
    {
        float blend = StandingPoseState.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.6f,
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio);

        Assert.Equal(0f, blend);
    }

    /// <summary>
    /// Descent equal to the configured full crouch ratio must yield fully crouched (1.0).
    /// </summary>
    [Fact]
    public void ComputePoseBlend_DescentEqualsFullCrouchDepth_ReturnsOne()
    {
        float blend = StandingPoseState.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.6f - FullCrouchDepthMetres,
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio);

        Assert.Equal(1f, blend);
    }

    /// <summary>
    /// Descent beyond full crouch depth must clamp at 1.0 rather than extrapolate.
    /// </summary>
    [Fact]
    public void ComputePoseBlend_DescentBeyondFullCrouchDepth_ClampsToOne()
    {
        float blend = StandingPoseState.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.6f - (FullCrouchDepthMetres * 2f),
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio);

        Assert.Equal(1f, blend);
    }

    /// <summary>
    /// Negative descent (head above rest) must clamp at 0.0.
    /// </summary>
    [Fact]
    public void ComputePoseBlend_NegativeDescent_ClampsToZero()
    {
        float blend = StandingPoseState.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.8f,
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio);

        Assert.Equal(0f, blend);
    }

    /// <summary>
    /// Half of full crouch descent must produce approximately 0.5 blend.
    /// </summary>
    [Fact]
    public void ComputePoseBlend_HalfDescent_ReturnsHalf()
    {
        float blend = StandingPoseState.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.6f - (FullCrouchDepthMetres * 0.5f),
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio);

        Assert.Equal(0.5f, blend, precision: 5);
    }

    /// <summary>
    /// Non-positive ratio normalisation inputs must not produce NaN and must stay within [0, 1].
    /// </summary>
    [Theory]
    [InlineData(0f, 0.375f)]
    [InlineData(-1f, 0.375f)]
    [InlineData(1.6f, 0f)]
    [InlineData(1.6f, -0.1f)]
    public void ComputePoseBlend_NonPositiveNormalisationInputs_DoesNotProduceNaN(
        float restHeadHeight,
        float fullCrouchReferenceHipHeightRatio)
    {
        float blend = StandingPoseState.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.4f,
            restHeadHeight: restHeadHeight,
            fullCrouchReferenceHipHeightRatio: fullCrouchReferenceHipHeightRatio);

        Assert.False(float.IsNaN(blend));
        Assert.InRange(blend, 0f, 1f);
    }

    /// <summary>
    /// At the upright end, the absent downward clamp remains inactive.
    /// </summary>
    [Fact]
    public void ComputeHipLimitFrame_Upright_DownwardClampIsInactive()
    {
        HipLimitFrame frame = StandingPoseState.ComputeHipLimitFrame(
            hipLocalRest: new Vector3(0f, 0.95f, 0f),
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            restHeadY: 1.6f,
            currentHeadY: 1.6f,
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio,
            fullCrouchReferenceForwardShiftRatio: FullCrouchReferenceForwardShiftRatio,
            uprightEnvelope: _uprightEnvelope,
            crouchedEnvelope: _crouchedEnvelope);

        Assert.True(frame.OffsetEnvelope.HasValue);
        Assert.True(frame.AbsoluteBounds.HasValue);
        HipLimitEnvelope envelope = frame.OffsetEnvelope.Value;
        HipLimitBounds bounds = frame.AbsoluteBounds.Value;
        AssertLimitApproximately(envelope.Up, _uprightEnvelope.Up);
        Assert.Null(envelope.Down);
        AssertBoundApproximately(bounds.Up, 1.19f);
        AssertBoundApproximately(bounds.Down, 0.672f);
    }

    /// <summary>
    /// Upright-only upward clamping remains anchored to rest while crouched-only downward clamping
    /// remains anchored to the full-crouch reference throughout the continuum.
    /// </summary>
    [Fact]
    public void ComputeHipLimitFrame_MidCrouch_PreservesSingleSidedVerticalAnchors()
    {
        HipLimitFrame frame = StandingPoseState.ComputeHipLimitFrame(
            hipLocalRest: new Vector3(0f, 0.95f, 0f),
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            restHeadY: 1.6f,
            currentHeadY: 1.6f - (FullCrouchDepthMetres * 0.5f),
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio,
            fullCrouchReferenceForwardShiftRatio: FullCrouchReferenceForwardShiftRatio,
            uprightEnvelope: _uprightEnvelope,
            crouchedEnvelope: _crouchedEnvelope);

        AssertApproximately(frame.ReferenceHipLocalPosition, new Vector3(0f, 0.835f, -0.032f));

        Assert.True(frame.AbsoluteBounds.HasValue);
        HipLimitBounds bounds = frame.AbsoluteBounds.Value;
        AssertBoundApproximately(bounds.Up, 1.19f);
        AssertBoundApproximately(bounds.Down, 0.672f);

        Assert.True(frame.OffsetEnvelope.HasValue);
        HipLimitEnvelope envelope = frame.OffsetEnvelope.Value;
        AssertLimitApproximately(envelope.Up, _uprightEnvelope.Up);
        AssertLimitApproximately(envelope.Down, _crouchedEnvelope.Down);
        AssertLimitApproximately(envelope.Left, 0.13f);
        AssertLimitApproximately(envelope.Right, 0.13f);
        AssertLimitApproximately(envelope.Forward, 0.165f);
        AssertLimitApproximately(envelope.Back, 0.10f);
    }

    /// <summary>
    /// At full crouch, single-sided authored limits remain active on their authored anchors.
    /// </summary>
    [Fact]
    public void ComputeHipLimitFrame_FullCrouch_KeepsSingleSidedVerticalLimitsAnchored()
    {
        HipLimitFrame frame = StandingPoseState.ComputeHipLimitFrame(
            hipLocalRest: new Vector3(0f, 0.95f, 0f),
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            restHeadY: 1.6f,
            currentHeadY: 1.6f - FullCrouchDepthMetres,
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio,
            fullCrouchReferenceForwardShiftRatio: FullCrouchReferenceForwardShiftRatio,
            uprightEnvelope: _uprightEnvelope,
            crouchedEnvelope: _crouchedEnvelope);

        AssertApproximately(frame.ReferenceHipLocalPosition, new Vector3(0f, 0.72f, -0.064f));
        Assert.True(frame.AbsoluteBounds.HasValue);
        HipLimitBounds bounds = frame.AbsoluteBounds.Value;
        AssertBoundApproximately(bounds.Up, 1.19f);
        AssertBoundApproximately(bounds.Down, 0.672f);

        Assert.True(frame.OffsetEnvelope.HasValue);
        HipLimitEnvelope envelope = frame.OffsetEnvelope.Value;
        Assert.Null(envelope.Up);
        AssertLimitApproximately(envelope.Down, _crouchedEnvelope.Down);
        AssertLimitApproximately(envelope.Left, _crouchedEnvelope.Left);
        AssertLimitApproximately(envelope.Right, _crouchedEnvelope.Right);
        AssertLimitApproximately(envelope.Forward, _crouchedEnvelope.Forward);
        AssertLimitApproximately(envelope.Back, _crouchedEnvelope.Back);
    }

    /// <summary>
    /// The full-crouch seam must not rely on an upright-only upward limit disappearing at the end
    /// of the standing continuum.
    /// </summary>
    [Fact]
    public void ApplyHipLimitFrame_FullCrouchSeam_PreservesUprightOnlyUpClamp()
    {
        HipReconciliationProfileResult profileResult = new()
        {
            DesiredHipLocalPosition = new Vector3(0f, 1.25f, 0f),
        };

        HipReconciliationTickResult nearFullCrouch = PoseState.ApplyHipLimitFrame(
            profileResult,
            StandingPoseState.ComputeHipLimitFrame(
                hipLocalRest: new Vector3(0f, 0.95f, 0f),
                hipRestUpLocal: Vector3.Up,
                hipRestForwardLocal: Vector3.Forward,
                restHeadY: 1.6f,
                currentHeadY: 1.6f - (FullCrouchDepthMetres * 0.999f),
                restHeadHeight: RestHeadHeight,
                fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio,
                fullCrouchReferenceForwardShiftRatio: FullCrouchReferenceForwardShiftRatio,
                uprightEnvelope: _uprightEnvelope,
                crouchedEnvelope: _crouchedEnvelope),
            restHeadHeight: RestHeadHeight,
            skeletonGlobalTransform: Transform3D.Identity);

        HipReconciliationTickResult fullCrouch = PoseState.ApplyHipLimitFrame(
            profileResult,
            StandingPoseState.ComputeHipLimitFrame(
                hipLocalRest: new Vector3(0f, 0.95f, 0f),
                hipRestUpLocal: Vector3.Up,
                hipRestForwardLocal: Vector3.Forward,
                restHeadY: 1.6f,
                currentHeadY: 1.6f - FullCrouchDepthMetres,
                restHeadHeight: RestHeadHeight,
                fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio,
                fullCrouchReferenceForwardShiftRatio: FullCrouchReferenceForwardShiftRatio,
                uprightEnvelope: _uprightEnvelope,
                crouchedEnvelope: _crouchedEnvelope),
            restHeadHeight: RestHeadHeight,
            skeletonGlobalTransform: Transform3D.Identity);

        AssertApproximately(nearFullCrouch.AppliedHipLocalPosition, new Vector3(0f, 1.19f, 0f), 1e-4f);
        AssertApproximately(fullCrouch.AppliedHipLocalPosition, new Vector3(0f, 1.19f, 0f), 1e-4f);
        AssertApproximately(nearFullCrouch.AppliedHipLocalPosition, fullCrouch.AppliedHipLocalPosition, 1e-4f);
    }

    /// <summary>
    /// If the rest hip height is already below the authored full-crouch target, no extra downward
    /// shift is applied.
    /// </summary>
    [Fact]
    public void ComputeHipLimitFrame_FullCrouchHeightAboveRestHeight_DoesNotShiftFurtherDown()
    {
        HipLimitFrame frame = StandingPoseState.ComputeHipLimitFrame(
            hipLocalRest: new Vector3(0f, 0.30f, 0f),
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            restHeadY: 1.6f,
            currentHeadY: 1.6f - FullCrouchDepthMetres,
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: 0.25f,
            fullCrouchReferenceForwardShiftRatio: 0f,
            uprightEnvelope: _uprightEnvelope,
            crouchedEnvelope: _crouchedEnvelope);

        AssertApproximately(frame.ReferenceHipLocalPosition, new Vector3(0f, 0.30f, 0f));
    }

    /// <summary>
    /// Standing-family limit framing does not depend on the animated hip pose.
    /// </summary>
    [Fact]
    public void ComputeHipLimitFrame_IsIndependentOfAnimatedHipPose()
    {
        HipLimitFrame first = StandingPoseState.ComputeHipLimitFrame(
            hipLocalRest: new Vector3(0f, 0.95f, 0f),
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            restHeadY: 1.6f,
            currentHeadY: 1.2f,
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio,
            fullCrouchReferenceForwardShiftRatio: FullCrouchReferenceForwardShiftRatio,
            uprightEnvelope: _uprightEnvelope,
            crouchedEnvelope: _crouchedEnvelope);

        HipLimitFrame second = StandingPoseState.ComputeHipLimitFrame(
            hipLocalRest: new Vector3(0f, 0.95f, 0f),
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            restHeadY: 1.6f,
            currentHeadY: 1.2f,
            restHeadHeight: RestHeadHeight,
            fullCrouchReferenceHipHeightRatio: FullCrouchReferenceHipHeightRatio,
            fullCrouchReferenceForwardShiftRatio: FullCrouchReferenceForwardShiftRatio,
            uprightEnvelope: _uprightEnvelope,
            crouchedEnvelope: _crouchedEnvelope);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Full-crouch standing rotation compensation is attenuated so crouched lean-back behaviour can
    /// use a lighter rotational drive than the upright end of the continuum.
    /// </summary>
    [Theory]
    [InlineData(0.0f, 0.2f, 1.0f)]
    [InlineData(0.5f, 0.2f, 0.6f)]
    [InlineData(1.0f, 0.2f, 0.2f)]
    [InlineData(1.5f, 0.2f, 0.2f)]
    [InlineData(-0.5f, 0.2f, 1.0f)]
    public void ComputeRotationCompensationScale_LerpsTowardsCrouchedScale(
        float poseBlend,
        float fullCrouchRotationCompensationScale,
        float expectedScale)
    {
        float scale = StandingPoseState.ComputeRotationCompensationScale(
            poseBlend,
            fullCrouchRotationCompensationScale);

        AssertApproximately(scale, expectedScale);
    }

    private static void AssertApproximately(float actual, float expected, float epsilon = 1e-5f)
        => Assert.InRange(actual, expected - epsilon, expected + epsilon);

    private static void AssertLimitApproximately(float? actual, float? expected, float epsilon = 1e-5f)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (actual.HasValue && expected.HasValue)
        {
            AssertApproximately(actual.Value, expected.Value, epsilon);
        }
    }

    private static void AssertBoundApproximately(float? actual, float expected, float epsilon = 1e-5f)
    {
        Assert.True(actual.HasValue);
        AssertApproximately(actual.Value, expected, epsilon);
    }

    private static void AssertApproximately(Vector3 actual, Vector3 expected, float epsilon = 1e-5f)
    {
        AssertApproximately(actual.X, expected.X, epsilon);
        AssertApproximately(actual.Y, expected.Y, epsilon);
        AssertApproximately(actual.Z, expected.Z, epsilon);
    }
}
