using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for crouching-to-kneeling transition gating semantics.
/// </summary>
public sealed class CrouchingToKneelingPoseTransitionTests
{
    private const float FullCrouchDepthRatio = 0.4f;
    private const float MinimumCrouchDepthBlend = 0.92f;
    private const float FullCrouchForwardOffsetRatio = 0.053f;
    private const float MinimumForwardOffsetFromFullCrouchRatio = 0.027f;

    /// <summary>
    /// Kneeling transition must be blocked until the crouch-depth gate is satisfied.
    /// </summary>
    [Fact]
    public void Evaluate_MidCrouchWithLargeForwardLean_DoesNotTransition()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        Transform3D camera = CreateTransform(0f, 1.22f, 0.16f);

        bool shouldTransition = CrouchingToKneelingPoseTransition.Evaluate(
            rest,
            camera,
            restHeadHeight: 1.5f,
            fullCrouchDepthRatio: FullCrouchDepthRatio,
            minimumCrouchDepthBlend: MinimumCrouchDepthBlend,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio,
            minimumForwardOffsetFromFullCrouchRatio: MinimumForwardOffsetFromFullCrouchRatio);

        Assert.False(shouldTransition);
    }

    /// <summary>
    /// Forward travel equal to the full-crouch baseline must not trigger kneeling by itself.
    /// </summary>
    [Fact]
    public void Evaluate_NearlyFullCrouchWithoutAdditionalForwardOffset_DoesNotTransition()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        Transform3D camera = CreateTransform(0f, 0.91f, 0.08f);

        bool shouldTransition = CrouchingToKneelingPoseTransition.Evaluate(
            rest,
            camera,
            restHeadHeight: 1.5f,
            fullCrouchDepthRatio: FullCrouchDepthRatio,
            minimumCrouchDepthBlend: MinimumCrouchDepthBlend,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio,
            minimumForwardOffsetFromFullCrouchRatio: MinimumForwardOffsetFromFullCrouchRatio);

        Assert.False(shouldTransition);
    }

    /// <summary>
    /// Near-full crouch plus extra forward travel beyond the full-crouch baseline triggers kneel.
    /// </summary>
    [Fact]
    public void Evaluate_NearlyFullCrouchWithAdditionalForwardOffset_Transitions()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        Transform3D camera = CreateTransform(0f, 0.91f, 0.16f);

        bool shouldTransition = CrouchingToKneelingPoseTransition.Evaluate(
            rest,
            camera,
            restHeadHeight: 1.5f,
            fullCrouchDepthRatio: FullCrouchDepthRatio,
            minimumCrouchDepthBlend: MinimumCrouchDepthBlend,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio,
            minimumForwardOffsetFromFullCrouchRatio: MinimumForwardOffsetFromFullCrouchRatio);

        Assert.True(shouldTransition);
    }

    /// <summary>
    /// Kneel seek blend is computed from forward offset relative to full-crouch baseline.
    /// </summary>
    [Fact]
    public void ComputeKneelSeekBlend_UsesFullCrouchForwardBaseline()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        Transform3D camera = CreateTransform(0f, 0.91f, 0.16f);

        float forwardFromFullCrouchRatio = KneelingPoseMetrics.ComputeForwardOffsetFromFullCrouchRatio(
            rest,
            camera,
            restHeadHeight: 1.5f,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio);

        Assert.InRange(forwardFromFullCrouchRatio, 0.053f, 0.054f);

        float kneelBlend = KneelingPoseMetrics.ComputeKneelSeekBlend(
            forwardFromFullCrouchRatio,
            maximumKneelForwardRangeRatio: 0.093f);

        Assert.InRange(kneelBlend, 0.55f, 0.58f);
    }

    /// <summary>
    /// Forward trigger threshold uses rest-head-height normalised ratios rather than absolute metres.
    /// </summary>
    [Fact]
    public void Evaluate_UsesForwardRatioAcrossDifferentRestHeights()
    {
        Transform3D restShort = CreateTransform(0f, 1.50f, 0f);
        Transform3D cameraShort = CreateTransform(0f, 1.22f, 0.16f);

        Transform3D restTall = CreateTransform(0f, 1.80f, 0f);
        Transform3D cameraTall = CreateTransform(0f, 1.52f, 0.192f);

        bool shortCharacterTransitions = CrouchingToKneelingPoseTransition.Evaluate(
            restShort,
            cameraShort,
            restHeadHeight: 1.5f,
            fullCrouchDepthRatio: FullCrouchDepthRatio,
            minimumCrouchDepthBlend: 0f,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio,
            minimumForwardOffsetFromFullCrouchRatio: MinimumForwardOffsetFromFullCrouchRatio);

        bool tallCharacterTransitions = CrouchingToKneelingPoseTransition.Evaluate(
            restTall,
            cameraTall,
            restHeadHeight: 1.8f,
            fullCrouchDepthRatio: FullCrouchDepthRatio,
            minimumCrouchDepthBlend: 0f,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio,
            minimumForwardOffsetFromFullCrouchRatio: MinimumForwardOffsetFromFullCrouchRatio);

        Assert.True(shortCharacterTransitions);
        Assert.True(tallCharacterTransitions);
    }

    /// <summary>
    /// Crouch gate uses rest-height ratios so scaled avatars preserve near-full crouch behaviour.
    /// </summary>
    [Fact]
    public void Evaluate_UsesCrouchDepthRatioAcrossDifferentRestHeights()
    {
        Transform3D restShort = CreateTransform(0f, 1.50f, 0f);
        Transform3D cameraShort = CreateTransform(0f, 0.93f, 0.16f);

        Transform3D restTall = CreateTransform(0f, 1.80f, 0f);
        Transform3D cameraTall = CreateTransform(0f, 1.116f, 0.192f);

        bool shortCharacterTransitions = CrouchingToKneelingPoseTransition.Evaluate(
            restShort,
            cameraShort,
            restHeadHeight: 1.5f,
            fullCrouchDepthRatio: FullCrouchDepthRatio,
            minimumCrouchDepthBlend: MinimumCrouchDepthBlend,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio,
            minimumForwardOffsetFromFullCrouchRatio: MinimumForwardOffsetFromFullCrouchRatio);

        bool tallCharacterTransitions = CrouchingToKneelingPoseTransition.Evaluate(
            restTall,
            cameraTall,
            restHeadHeight: 1.8f,
            fullCrouchDepthRatio: FullCrouchDepthRatio,
            minimumCrouchDepthBlend: MinimumCrouchDepthBlend,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio,
            minimumForwardOffsetFromFullCrouchRatio: MinimumForwardOffsetFromFullCrouchRatio);

        Assert.True(shortCharacterTransitions);
        Assert.True(tallCharacterTransitions);
    }

    /// <summary>
    /// Kneeling returns to crouching when forward kneel offset falls back near full-crouch baseline.
    /// </summary>
    [Fact]
    public void KneelingToCrouchingEvaluate_ForwardOffsetNearBaseline_Transitions()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        Transform3D camera = CreateTransform(0f, 0.93f, 0.10f);

        bool shouldTransition = KneelingToCrouchingPoseTransition.Evaluate(
            rest,
            camera,
            restHeadHeight: 1.5f,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio,
            maximumForwardOffsetFromFullCrouchRatio: 0.02f);

        Assert.True(shouldTransition);
    }

    /// <summary>
    /// Kneeling remains active while forward kneel offset is still significantly ahead of crouch baseline.
    /// </summary>
    [Fact]
    public void KneelingToCrouchingEvaluate_ForwardOffsetStillDeepKneel_DoesNotTransition()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        Transform3D camera = CreateTransform(0f, 0.93f, 0.18f);

        bool shouldTransition = KneelingToCrouchingPoseTransition.Evaluate(
            rest,
            camera,
            restHeadHeight: 1.5f,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio,
            maximumForwardOffsetFromFullCrouchRatio: 0.02f);

        Assert.False(shouldTransition);
    }

    private static Transform3D CreateTransform(float x, float y, float z)
        => new(new Basis(new Vector3(-1f, 0f, 0f), Vector3.Up, new Vector3(0f, 0f, -1f)), new Vector3(x, y, z));
}
