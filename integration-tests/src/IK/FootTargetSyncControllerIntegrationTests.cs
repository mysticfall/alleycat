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

    private const string SkeletonPath = "Female_export/GeneralSkeleton";
    private const string LeftFootTargetPath = "IKTargets/LeftFoot";
    private const string RightFootTargetPath = "IKTargets/RightFoot";

    private const string FootTargetSyncControllerPath = "Female_export/GeneralSkeleton/FootTargetSyncController";
    private const string HipReconciliationModifierPath = "Female_export/GeneralSkeleton/HipReconciliationModifier";
    private const string LeftLegIKControllerPath = "Female_export/GeneralSkeleton/LeftLegIKController";
    private const string RightLegIKControllerPath = "Female_export/GeneralSkeleton/RightLegIKController";
    private const string LeftLegTwoBoneIKControllerPath = "Female_export/GeneralSkeleton/LeftLegTwoBoneIKController";
    private const string RightLegTwoBoneIKControllerPath = "Female_export/GeneralSkeleton/RightLegTwoBoneIKController";
    private const string CopyLeftFootRotationPath = "Female_export/GeneralSkeleton/CopyLeftFootRotation";
    private const string CopyRightFootRotationPath = "Female_export/GeneralSkeleton/CopyRightFootRotation";

    private const float PositionToleranceMetres = 0.01f;
    private const float MinimumFootShiftEffectMetres = 0.04f;

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
    /// Verifies a hip-shift-style foot mutator only affects targets when ordered before sync.
    /// </summary>
    [Headless]
    [Fact]
    public async Task FootTargetSyncController_FootShiftMutatorOrder_ControlsWhetherTargetsDrift()
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

        int leftFootIndex = RequireBoneIndex(skeleton, "LeftFoot");
        int rightFootIndex = RequireBoneIndex(skeleton, "RightFoot");

        FootShiftMutator shiftMutator = new()
        {
            Name = "FootShiftMutator",
            LeftFootBasePose = skeleton.GetBonePosePosition(leftFootIndex),
            RightFootBasePose = skeleton.GetBonePosePosition(rightFootIndex),
            ShiftDelta = new Vector3(0.0f, 0.10f, 0.0f),
            Active = true,
        };
        skeleton.AddChild(shiftMutator);

        skeleton.MoveChild(shiftMutator, skeleton.GetChildCount() - 1);
        Assert.True(
            shiftMutator.GetIndex() > syncController.GetIndex(),
            "Foot shift mutator should execute after FootTargetSyncController in post-sync case.");
        await WaitForFramesAsync(sceneTree, 3);

        Vector3 baselineLeftTarget = leftFootTarget.GlobalPosition;
        Vector3 baselineRightTarget = rightFootTarget.GlobalPosition;

        skeleton.MoveChild(shiftMutator, syncController.GetIndex());
        Assert.True(
            shiftMutator.GetIndex() < syncController.GetIndex(),
            "Foot shift mutator should execute before FootTargetSyncController in pre-sync case.");
        await WaitForFramesAsync(sceneTree, 4);

        Vector3 shiftedLeftTarget = leftFootTarget.GlobalPosition;
        Vector3 shiftedRightTarget = rightFootTarget.GlobalPosition;

        Assert.True(
            shiftedLeftTarget.DistanceTo(baselineLeftTarget) >= MinimumFootShiftEffectMetres,
            "Left target should drift when a foot-shift mutator executes before sync (proxy for after-hip sampling regression).");
        Assert.True(
            shiftedRightTarget.DistanceTo(baselineRightTarget) >= MinimumFootShiftEffectMetres,
            "Right target should drift when a foot-shift mutator executes before sync (proxy for after-hip sampling regression).");

        skeleton.MoveChild(shiftMutator, skeleton.GetChildCount() - 1);
        await WaitForFramesAsync(sceneTree, 4);

        Assert.True(
            leftFootTarget.GlobalPosition.DistanceTo(baselineLeftTarget) <= PositionToleranceMetres,
            "Left target should remain stable when foot-shift mutator executes after sync.");
        Assert.True(
            rightFootTarget.GlobalPosition.DistanceTo(baselineRightTarget) <= PositionToleranceMetres,
            "Right target should remain stable when foot-shift mutator executes after sync.");

        shiftMutator.QueueFree();
    }

    private static int RequireBoneIndex(Skeleton3D skeleton, string boneName)
    {
        int boneIndex = skeleton.FindBone(boneName);
        Assert.True(boneIndex >= 0, $"Expected skeleton bone '{boneName}' to exist.");
        return boneIndex;
    }

    private sealed partial class FootShiftMutator : SkeletonModifier3D
    {
        public Vector3 LeftFootBasePose
        {
            get;
            set;
        }

        public Vector3 RightFootBasePose
        {
            get;
            set;
        }

        public Vector3 ShiftDelta
        {
            get;
            set;
        }

        private int _leftFootBoneIndex = -1;
        private int _rightFootBoneIndex = -1;

        public override void _ProcessModificationWithDelta(double delta)
        {
            _ = delta;

            Skeleton3D? skeleton = GetSkeleton();
            if (skeleton is null)
            {
                return;
            }

            if (_leftFootBoneIndex < 0 || _rightFootBoneIndex < 0)
            {
                _leftFootBoneIndex = skeleton.FindBone("LeftFoot");
                _rightFootBoneIndex = skeleton.FindBone("RightFoot");
            }

            if (_leftFootBoneIndex < 0 || _rightFootBoneIndex < 0)
            {
                return;
            }

            skeleton.SetBonePosePosition(_leftFootBoneIndex, LeftFootBasePose + ShiftDelta);
            skeleton.SetBonePosePosition(_rightFootBoneIndex, RightFootBasePose + ShiftDelta);
        }
    }
}
