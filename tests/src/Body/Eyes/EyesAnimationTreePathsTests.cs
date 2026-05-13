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
    public void IsEyeBlendShapeFilterPath_IncludesOnlyReferenceEyeBlendShapes()
    {
        Assert.True(EyesAnimationTreePaths.IsEyeBlendShapeFilterPath("GeneralSkeleton/Female_body_export:eyeLookInRight"));
        Assert.True(EyesAnimationTreePaths.IsEyeBlendShapeFilterPath("GeneralSkeleton/Female_body_export:eyeBlinkLeft"));
        Assert.False(EyesAnimationTreePaths.IsEyeBlendShapeFilterPath("%GeneralSkeleton:Head"));
    }

    /// <summary>
    /// Verifies eye blend filters are split by the operation each blend node applies.
    /// </summary>
    [Fact]
    public void EyeBlendShapeFilterPaths_AreSeparatedByLookAxisAndBlink()
    {
        Assert.Contains(
            "GeneralSkeleton/Female_body_export:eyeLookInRight",
            EyesAnimationTreePaths.GetHorizontalLookBlendShapeFilterPathStrings());
        Assert.DoesNotContain(
            "GeneralSkeleton/Female_body_export:eyeLookUpRight",
            EyesAnimationTreePaths.GetHorizontalLookBlendShapeFilterPathStrings());
        Assert.DoesNotContain(
            "GeneralSkeleton/Female_body_export:eyeBlinkLeft",
            EyesAnimationTreePaths.GetHorizontalLookBlendShapeFilterPathStrings());

        Assert.Contains(
            "GeneralSkeleton/Female_body_export:eyeLookUpRight",
            EyesAnimationTreePaths.GetVerticalLookBlendShapeFilterPathStrings());
        Assert.DoesNotContain(
            "GeneralSkeleton/Female_body_export:eyeLookOutRight",
            EyesAnimationTreePaths.GetVerticalLookBlendShapeFilterPathStrings());
        Assert.DoesNotContain(
            "GeneralSkeleton/Female_body_export:eyeBlinkLeft",
            EyesAnimationTreePaths.GetVerticalLookBlendShapeFilterPathStrings());

        Assert.Contains(
            "GeneralSkeleton/Female_body_export:eyeBlinkLeft",
            EyesAnimationTreePaths.GetBlinkBlendShapeFilterPathStrings());
        Assert.DoesNotContain(
            "GeneralSkeleton/Female_body_export:eyeLookInRight",
            EyesAnimationTreePaths.GetBlinkBlendShapeFilterPathStrings());
    }
}
