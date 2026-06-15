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
    /// Verifies finger filters are rooted in caller-supplied skeleton binding paths rather than character-specific names.
    /// </summary>
    [Fact]
    public void GetFingerFilterPaths_UsesConfiguredSkeletonFilterRoot()
    {
        IReadOnlyList<string> paths = HandPoseAnimationTreePaths.GetFingerFilterPathStrings(
            LimbSide.Left,
            "%PortableRigSkeleton");

        Assert.Contains("%PortableRigSkeleton:LeftIndexProximal", paths);
        Assert.Contains("%PortableRigSkeleton:LeftThumbDistal", paths);
        Assert.DoesNotContain(paths, path => path.Contains("GeneralSkeleton", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies filter logic excludes hand and arm bones.
    /// </summary>
    [Theory]
    [InlineData(LimbSide.Left, "%PortableRigSkeleton:LeftHand")]
    [InlineData(LimbSide.Left, "%PortableRigSkeleton:LeftLowerArm")]
    [InlineData(LimbSide.Right, "%PortableRigSkeleton:RightHand")]
    [InlineData(LimbSide.Right, "%PortableRigSkeleton:RightUpperArm")]
    public void IsFingerFilterPath_ExcludesHandsAndArmBones(LimbSide side, string path)
        => Assert.False(HandPoseAnimationTreePaths.IsFingerFilterPath(side, path));

    /// <summary>
    /// Verifies filter logic includes finger descendants.
    /// </summary>
    [Theory]
    [InlineData(LimbSide.Left, "%PortableRigSkeleton:LeftIndexProximal")]
    [InlineData(LimbSide.Left, "%PortableRigSkeleton:LeftThumbMetacarpal")]
    [InlineData(LimbSide.Right, "%PortableRigSkeleton:RightRingDistal")]
    [InlineData(LimbSide.Right, "%PortableRigSkeleton:RightLittleIntermediate")]
    public void IsFingerFilterPath_IncludesFingerBones(LimbSide side, string path)
        => Assert.True(HandPoseAnimationTreePaths.IsFingerFilterPath(side, path));
}
