using AlleyCat.Animation;
using Xunit;

namespace AlleyCat.Tests.Animation;

/// <summary>
/// Unit coverage for ANIM-001 hand-pose AnimationTree path helpers.
/// </summary>
public sealed class HandPoseAnimationTreePathsTests
{
    /// <summary>
    /// Verifies final hand blend parameters are rooted in the functional blend tree.
    /// </summary>
    [Theory]
    [InlineData(HandPoseSide.Left, "LeftHandBlend")]
    [InlineData(HandPoseSide.Right, "RightHandBlend")]
    public void GetHandBlendParameter_ReturnsFunctionalRootParameter(HandPoseSide side, string expectedNodeName)
        => Assert.Equal(
            $"parameters/{expectedNodeName}/blend_amount",
            HandPoseAnimationTreePaths.GetHandBlendParameterPath(side));

    /// <summary>
    /// Verifies former root state-machine parameters can be remapped under the wrapped upstream node.
    /// </summary>
    [Fact]
    public void GetNestedStateMachineParameter_PrefixesFormerRootStateMachinePath()
    {
        string nested = HandPoseAnimationTreePaths.GetNestedStateMachineParameterPath("parameters/Walking/blend_position");

        Assert.Equal("parameters/States/Walking/blend_position", nested);
    }

    /// <summary>
    /// Verifies filter logic excludes hand and arm bones.
    /// </summary>
    [Theory]
    [InlineData(HandPoseSide.Left, "%GeneralSkeleton:LeftHand")]
    [InlineData(HandPoseSide.Left, "%GeneralSkeleton:LeftLowerArm")]
    [InlineData(HandPoseSide.Right, "%GeneralSkeleton:RightHand")]
    [InlineData(HandPoseSide.Right, "%GeneralSkeleton:RightUpperArm")]
    public void IsFingerFilterPath_ExcludesHandsAndArmBones(HandPoseSide side, string path)
        => Assert.False(HandPoseAnimationTreePaths.IsFingerFilterPath(side, path));

    /// <summary>
    /// Verifies filter logic includes finger descendants.
    /// </summary>
    [Theory]
    [InlineData(HandPoseSide.Left, "%GeneralSkeleton:LeftIndexProximal")]
    [InlineData(HandPoseSide.Left, "%GeneralSkeleton:LeftThumbMetacarpal")]
    [InlineData(HandPoseSide.Right, "%GeneralSkeleton:RightRingDistal")]
    [InlineData(HandPoseSide.Right, "%GeneralSkeleton:RightLittleIntermediate")]
    public void IsFingerFilterPath_IncludesFingerBones(HandPoseSide side, string path)
        => Assert.True(HandPoseAnimationTreePaths.IsFingerFilterPath(side, path));
}
