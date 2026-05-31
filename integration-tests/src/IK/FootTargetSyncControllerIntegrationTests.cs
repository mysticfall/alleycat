using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for FootTargetSyncController ordering and hip-shift risk.
/// </summary>
public sealed partial class FootTargetSyncControllerIntegrationTests
{
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";

    private const string SkeletonPath = "Female/GeneralSkeleton";
    private const string LeftFootTargetPath = "IKTargets/LeftFoot";
    private const string RightFootTargetPath = "IKTargets/RightFoot";

    private const string FootTargetSyncControllerPath = "Female/GeneralSkeleton/FootTargetSyncController";
    private const string HipReconciliationModifierPath = "Female/GeneralSkeleton/HipReconciliationModifier";
    private const string LeftLegIKControllerPath = "Female/GeneralSkeleton/LeftLegIKController";
    private const string RightLegIKControllerPath = "Female/GeneralSkeleton/RightLegIKController";
    private const string LeftLegTwoBoneIKControllerPath = "Female/GeneralSkeleton/LeftLegTwoBoneIKController";
    private const string RightLegTwoBoneIKControllerPath = "Female/GeneralSkeleton/RightLegTwoBoneIKController";
    private const string CopyLeftFootRotationPath = "Female/GeneralSkeleton/CopyLeftFootRotation";
    private const string CopyRightFootRotationPath = "Female/GeneralSkeleton/CopyRightFootRotation";

    private const float PositionToleranceMetres = 0.01f;

    /// <summary>
    /// Verifies scene-authored player pipeline ordering for foot sync, hip reconciliation, and leg solve chain.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerRigModifierPipeline_EnforcesFootSyncHipReconciliationAndLegSolveOrder()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(PlayerScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected player scene to become current scene.");

        SkeletonModifier3D syncController = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(FootTargetSyncControllerPath),
            exactMatch: false);
        SkeletonModifier3D hipReconciliationModifier = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(HipReconciliationModifierPath),
            exactMatch: false);
        SkeletonModifier3D leftLegController = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(LeftLegIKControllerPath),
            exactMatch: false);
        SkeletonModifier3D rightLegController = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(RightLegIKControllerPath),
            exactMatch: false);
        SkeletonModifier3D leftLegTwoBone = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(LeftLegTwoBoneIKControllerPath),
            exactMatch: false);
        SkeletonModifier3D rightLegTwoBone = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(RightLegTwoBoneIKControllerPath),
            exactMatch: false);
        SkeletonModifier3D copyLeftFootRotation = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(CopyLeftFootRotationPath),
            exactMatch: false);
        SkeletonModifier3D copyRightFootRotation = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(CopyRightFootRotationPath),
            exactMatch: false);

        Assert.Same(syncController.GetParent(), hipReconciliationModifier.GetParent());
        Assert.Same(hipReconciliationModifier.GetParent(), leftLegController.GetParent());
        Assert.Same(hipReconciliationModifier.GetParent(), rightLegController.GetParent());
        Assert.Same(hipReconciliationModifier.GetParent(), leftLegTwoBone.GetParent());
        Assert.Same(hipReconciliationModifier.GetParent(), rightLegTwoBone.GetParent());
        Assert.Same(hipReconciliationModifier.GetParent(), copyLeftFootRotation.GetParent());
        Assert.Same(hipReconciliationModifier.GetParent(), copyRightFootRotation.GetParent());

        Assert.True(
            syncController.GetIndex() < hipReconciliationModifier.GetIndex(),
            "FootTargetSyncController must execute before HipReconciliationModifier so targets sample pre-reconciliation animated feet.");

        Assert.True(
            hipReconciliationModifier.GetIndex() < leftLegController.GetIndex(),
            "HipReconciliationModifier must execute before LeftLegIKController in the shipped player rig solve chain.");
        Assert.True(
            hipReconciliationModifier.GetIndex() < rightLegController.GetIndex(),
            "HipReconciliationModifier must execute before RightLegIKController in the shipped player rig solve chain.");
        Assert.True(
            hipReconciliationModifier.GetIndex() < leftLegTwoBone.GetIndex(),
            "HipReconciliationModifier must execute before LeftLegTwoBoneIKController in the shipped player rig solve chain.");
        Assert.True(
            hipReconciliationModifier.GetIndex() < rightLegTwoBone.GetIndex(),
            "HipReconciliationModifier must execute before RightLegTwoBoneIKController in the shipped player rig solve chain.");
        Assert.True(
            hipReconciliationModifier.GetIndex() < copyLeftFootRotation.GetIndex(),
            "HipReconciliationModifier must execute before CopyLeftFootRotation in the shipped player rig solve chain.");
        Assert.True(
            hipReconciliationModifier.GetIndex() < copyRightFootRotation.GetIndex(),
            "HipReconciliationModifier must execute before CopyRightFootRotation in the shipped player rig solve chain.");
    }

    /// <summary>
    /// Verifies foot targets are synchronised from animated foot bones before downstream modifiers run.
    /// </summary>
    [Headless]
    [Fact]
    public async Task FootTargetSyncController_SamplesAnimatedFootPoseBeforeDownstreamModifiers()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(PlayerScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected player scene to become current scene.");

        Skeleton3D skeleton = Assert.IsType<Skeleton3D>(sceneRoot.GetNodeOrNull(SkeletonPath), exactMatch: false);
        Node3D leftFootTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftFootTargetPath), exactMatch: false);
        Node3D rightFootTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(RightFootTargetPath), exactMatch: false);

        SkeletonModifier3D syncController = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(FootTargetSyncControllerPath),
            exactMatch: false);
        SkeletonModifier3D hipReconciliationModifier = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(HipReconciliationModifierPath),
            exactMatch: false);

        SkeletonModifier3D leftLegController = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(LeftLegIKControllerPath),
            exactMatch: false);
        SkeletonModifier3D rightLegController = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(RightLegIKControllerPath),
            exactMatch: false);
        SkeletonModifier3D leftLegTwoBone = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(LeftLegTwoBoneIKControllerPath),
            exactMatch: false);
        SkeletonModifier3D rightLegTwoBone = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(RightLegTwoBoneIKControllerPath),
            exactMatch: false);
        SkeletonModifier3D copyLeftFootRotation = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(CopyLeftFootRotationPath),
            exactMatch: false);
        SkeletonModifier3D copyRightFootRotation = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(CopyRightFootRotationPath),
            exactMatch: false);

        Assert.True(
            syncController.GetIndex() < hipReconciliationModifier.GetIndex(),
            "FootTargetSyncController must execute before HipReconciliationModifier for pre-reconciliation sampling.");

        leftLegController.Active = false;
        rightLegController.Active = false;
        leftLegTwoBone.Active = false;
        rightLegTwoBone.Active = false;
        copyLeftFootRotation.Active = false;
        copyRightFootRotation.Active = false;
        hipReconciliationModifier.Active = false;

        await WaitForFramesAsync(sceneTree, 4);

        int leftFootIndex = RequireBoneIndex(skeleton, "LeftFoot");
        int rightFootIndex = RequireBoneIndex(skeleton, "RightFoot");

        AssertFootTargetMatchesBonePose(skeleton, leftFootIndex, leftFootTarget, "left");
        AssertFootTargetMatchesBonePose(skeleton, rightFootIndex, rightFootTarget, "right");
    }

    private static void AssertFootTargetMatchesBonePose(
        Skeleton3D skeleton,
        int footBoneIndex,
        Node3D footTarget,
        string side)
    {
        Transform3D footGlobalPose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(footBoneIndex);

        Assert.True(
            footTarget.GlobalPosition.DistanceTo(footGlobalPose.Origin) <= PositionToleranceMetres,
            $"Expected {side} foot target to match the animated {side} foot bone position sampled by FootTargetSyncController.");

        Assert.True(
            footTarget.GlobalBasis.Orthonormalized().IsEqualApprox(footGlobalPose.Basis.Orthonormalized()),
            $"Expected {side} foot target basis to match the animated {side} foot bone basis sampled by FootTargetSyncController.");
    }

    private static int RequireBoneIndex(Skeleton3D skeleton, string boneName)
    {
        int boneIndex = skeleton.FindBone(boneName);
        Assert.True(boneIndex >= 0, $"Expected skeleton bone '{boneName}' to exist.");
        return boneIndex;
    }
}
