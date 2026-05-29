using AlleyCat.Body.Eyes;
using Xunit;

namespace AlleyCat.Tests.Body.Eyes;

/// <summary>
/// Unit coverage for BODY-004 Eyes AnimationTree path helpers.
/// </summary>
public sealed class EyesAnimationTreePathsTests
{
    /// <summary>
    /// Verifies eye parameter paths are rooted in stable blend-tree node names.
    /// </summary>
    [Fact]
    public void ParameterPaths_ReturnStableAnimationTreePaths()
    {
        Assert.Equal("parameters/EyesHorizontalLookSeek/seek_request", EyesAnimationTreePaths.GetHorizontalLookSeekParameterPath());
        Assert.Equal("parameters/EyesVerticalLookSeek/seek_request", EyesAnimationTreePaths.GetVerticalLookSeekParameterPath());
        Assert.Equal("parameters/EyesBlinkOneShot/request", EyesAnimationTreePaths.GetBlinkOneShotRequestParameterPath());
        Assert.Equal("parameters/EyesBlinkTimeScale/scale", EyesAnimationTreePaths.GetBlinkTimeScaleParameterPath());
        Assert.Equal("parameters/EyesHorizontalLookBlend/blend_amount", EyesAnimationTreePaths.GetHorizontalLookBlendParameterPath());
        Assert.Equal("parameters/EyesVerticalLookBlend/blend_amount", EyesAnimationTreePaths.GetVerticalLookBlendParameterPath());
    }

    /// <summary>
    /// Verifies the filter set includes eye blend shapes and excludes unrelated bones.
    /// </summary>
    [Fact]
    public void IsEyeBlendShapeFilterPath_IncludesOnlyConfiguredEyeBlendShapes()
    {
        Assert.True(EyesAnimationTreePaths.IsEyeBlendShapeFilterPath(
            "Rig/FaceMesh:eyeLookInRight",
            "Rig",
            ["FaceMesh"]));
        Assert.True(EyesAnimationTreePaths.IsEyeBlendShapeFilterPath(
            "Rig/FaceMesh:eyeBlinkLeft",
            "Rig",
            ["FaceMesh"]));
        Assert.False(EyesAnimationTreePaths.IsEyeBlendShapeFilterPath(
            "%GeneralSkeleton:Head",
            "Rig",
            ["FaceMesh"]));
    }

    /// <summary>
    /// Verifies eye blend filters are split by the operation each blend node applies.
    /// </summary>
    [Fact]
    public void EyeBlendShapeFilterPaths_AreSeparatedByLookAxisAndBlink()
    {
        Assert.Contains(
            "Rig/FaceMesh:eyeLookInRight",
            EyesAnimationTreePaths.BuildHorizontalLookBlendShapeFilterPathStrings("Rig", ["FaceMesh"]));
        Assert.DoesNotContain(
            "Rig/FaceMesh:eyeLookUpRight",
            EyesAnimationTreePaths.BuildHorizontalLookBlendShapeFilterPathStrings("Rig", ["FaceMesh"]));
        Assert.DoesNotContain(
            "Rig/FaceMesh:eyeBlinkLeft",
            EyesAnimationTreePaths.BuildHorizontalLookBlendShapeFilterPathStrings("Rig", ["FaceMesh"]));

        Assert.Contains(
            "Rig/FaceMesh:eyeLookUpRight",
            EyesAnimationTreePaths.BuildVerticalLookBlendShapeFilterPathStrings("Rig", ["FaceMesh"]));
        Assert.DoesNotContain(
            "Rig/FaceMesh:eyeLookOutRight",
            EyesAnimationTreePaths.BuildVerticalLookBlendShapeFilterPathStrings("Rig", ["FaceMesh"]));
        Assert.DoesNotContain(
            "Rig/FaceMesh:eyeBlinkLeft",
            EyesAnimationTreePaths.BuildVerticalLookBlendShapeFilterPathStrings("Rig", ["FaceMesh"]));

        Assert.Contains(
            "Rig/FaceMesh:eyeBlinkLeft",
            EyesAnimationTreePaths.BuildBlinkBlendShapeFilterPathStrings("Rig", ["FaceMesh"]));
        Assert.DoesNotContain(
            "Rig/FaceMesh:eyeLookInRight",
            EyesAnimationTreePaths.BuildBlinkBlendShapeFilterPathStrings("Rig", ["FaceMesh"]));
    }

    /// <summary>
    /// Verifies filter paths are generated for every configured mesh.
    /// </summary>
    [Fact]
    public void EyeBlendShapeFilterPaths_AreBuiltForEveryConfiguredMesh()
    {
        IReadOnlyList<string> paths = EyesAnimationTreePaths.BuildHorizontalLookBlendShapeFilterPathStrings(
            "GeneralSkeleton",
            ["Face", "Lashes"]);

        Assert.Contains("GeneralSkeleton/Face:eyeLookInRight", paths);
        Assert.Contains("GeneralSkeleton/Lashes:eyeLookInRight", paths);
        Assert.DoesNotContain("GeneralSkeleton/Body:eyeLookInRight", paths);
    }
}
