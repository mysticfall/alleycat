using AlleyCat.Body;
using AlleyCat.Body.Hands;
using Xunit;

namespace AlleyCat.Tests.Body.Hands;

/// <summary>
/// Unit coverage for BODY-001 Hands hand-pose AnimationTree path helpers.
/// </summary>
public sealed class HandPoseAnimationTreePathsTests
{
    /// <summary>
    /// Verifies final hand blend parameters are rooted in the functional blend tree.
    /// </summary>
    [Theory]
    [InlineData(LimbSide.Left, "LeftHandBlend")]
    [InlineData(LimbSide.Right, "RightHandBlend")]
    public void GetHandBlendParameter_ReturnsFunctionalRootParameter(LimbSide side, string expectedNodeName)
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
    [InlineData(LimbSide.Left, "%GeneralSkeleton:LeftHand")]
    [InlineData(LimbSide.Left, "%GeneralSkeleton:LeftLowerArm")]
    [InlineData(LimbSide.Right, "%GeneralSkeleton:RightHand")]
    [InlineData(LimbSide.Right, "%GeneralSkeleton:RightUpperArm")]
    public void IsFingerFilterPath_ExcludesHandsAndArmBones(LimbSide side, string path)
        => Assert.False(HandPoseAnimationTreePaths.IsFingerFilterPath(side, path));

    /// <summary>
    /// Verifies filter logic includes finger descendants.
    /// </summary>
    [Theory]
    [InlineData(LimbSide.Left, "%GeneralSkeleton:LeftIndexProximal")]
    [InlineData(LimbSide.Left, "%GeneralSkeleton:LeftThumbMetacarpal")]
    [InlineData(LimbSide.Right, "%GeneralSkeleton:RightRingDistal")]
    [InlineData(LimbSide.Right, "%GeneralSkeleton:RightLittleIntermediate")]
    public void IsFingerFilterPath_IncludesFingerBones(LimbSide side, string path)
        => Assert.True(HandPoseAnimationTreePaths.IsFingerFilterPath(side, path));
}
