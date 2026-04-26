using AlleyCat.IK.Pose;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for the standing-crouching blend math in
/// <see cref="StandingPoseState"/>.
/// </summary>
public sealed class StandingPoseStateTests
{
    private const float RestHeadHeight = 1.6f;
    private const float FullCrouchDepthRatio = 0.375f;
    private const float FullCrouchDepthMetres = RestHeadHeight * FullCrouchDepthRatio;

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
            fullCrouchDepthRatio: FullCrouchDepthRatio);

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
            fullCrouchDepthRatio: FullCrouchDepthRatio);

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
            fullCrouchDepthRatio: FullCrouchDepthRatio);

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
            fullCrouchDepthRatio: FullCrouchDepthRatio);

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
            fullCrouchDepthRatio: FullCrouchDepthRatio);

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
        float fullCrouchDepthRatio)
    {
        float blend = StandingPoseState.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.4f,
            restHeadHeight: restHeadHeight,
            fullCrouchDepthRatio: fullCrouchDepthRatio);

        Assert.False(float.IsNaN(blend));
        Assert.InRange(blend, 0f, 1f);
    }
}
