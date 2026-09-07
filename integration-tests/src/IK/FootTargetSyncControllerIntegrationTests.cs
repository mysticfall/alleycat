using AlleyCat.IK;
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
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";

    private const string SkeletonPath = "Female/GeneralSkeleton";
    private const string LeftFootTargetPath = "IKTargets/LeftFoot";
    private const string RightFootTargetPath = "IKTargets/RightFoot";

    private const string FootTargetSyncControllerPath = "Female/GeneralSkeleton/FootTargetSyncController";
    private const string CharacterIKBeginStagePath = "Female/GeneralSkeleton/CharacterIKBeginStage";
    private const string CharacterIKFootProviderStagePath = "Female/GeneralSkeleton/CharacterIKFootProviderStage";
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
        CharacterIK characterIK = Assert.IsAssignableFrom<CharacterIK>(sceneRoot.GetNodeOrNull("VRIK"));
        characterIK._PhysicsProcess(1.0d / 60.0d);
        await WaitForNextFrameAsync(sceneTree);
        SkeletonModifier3D beginStage = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(CharacterIKBeginStagePath),
            exactMatch: false);
        SkeletonModifier3D footProviderStage = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(CharacterIKFootProviderStagePath),
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
            syncController.GetIndex() < beginStage.GetIndex()
            && beginStage.GetIndex() < footProviderStage.GetIndex()
            && footProviderStage.GetIndex() < hipReconciliationModifier.GetIndex(),
            "CharacterIK begin/provider stages must execute after foot synchronisation and before hip reconciliation.");

        Assert.True(
            footProviderStage.GetIndex() < leftLegController.GetIndex()
            && hipReconciliationModifier.GetIndex() < leftLegController.GetIndex(),
            "HipReconciliationModifier must execute before LeftLegIKController in the shipped player rig solve chain.");
        Assert.True(
            footProviderStage.GetIndex() < rightLegController.GetIndex()
            && hipReconciliationModifier.GetIndex() < rightLegController.GetIndex(),
            "HipReconciliationModifier must execute before RightLegIKController in the shipped player rig solve chain.");
        Assert.True(
            footProviderStage.GetIndex() < leftLegTwoBone.GetIndex()
            && hipReconciliationModifier.GetIndex() < leftLegTwoBone.GetIndex(),
            "HipReconciliationModifier must execute before LeftLegTwoBoneIKController in the shipped player rig solve chain.");
        Assert.True(
            footProviderStage.GetIndex() < rightLegTwoBone.GetIndex()
            && hipReconciliationModifier.GetIndex() < rightLegTwoBone.GetIndex(),
            "HipReconciliationModifier must execute before RightLegTwoBoneIKController in the shipped player rig solve chain.");
        Assert.True(
            footProviderStage.GetIndex() < copyLeftFootRotation.GetIndex()
            && hipReconciliationModifier.GetIndex() < copyLeftFootRotation.GetIndex(),
            "HipReconciliationModifier must execute before CopyLeftFootRotation in the shipped player rig solve chain.");
        Assert.True(
            footProviderStage.GetIndex() < copyRightFootRotation.GetIndex()
            && hipReconciliationModifier.GetIndex() < copyRightFootRotation.GetIndex(),
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

        FootTargetSyncController syncController = Assert.IsType<FootTargetSyncController>(
            sceneRoot.GetNodeOrNull(FootTargetSyncControllerPath),
            exactMatch: false);
        syncController.CapturePreIKDiagnostics = true;
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

        PreIKGraphPoseDiagnostic diagnostic = syncController.LastPreIKDiagnostic;
        Assert.True(diagnostic.IsAvailable);
        Assert.Equal(syncController.GetIndex(), diagnostic.ModifierIndex);
        Assert.Equal(skeleton.GetBoneGlobalPose(RequireBoneIndex(skeleton, "Root")), diagnostic.RootSkeletonLocal);
        Assert.Equal(skeleton.GetBoneGlobalPose(RequireBoneIndex(skeleton, "Hips")), diagnostic.HipsSkeletonLocal);

        int leftFootIndex = RequireBoneIndex(skeleton, "LeftFoot");
        int rightFootIndex = RequireBoneIndex(skeleton, "RightFoot");

        AssertFootTargetMatchesBonePose(skeleton, leftFootIndex, leftFootTarget, "left");
        AssertFootTargetMatchesBonePose(skeleton, rightFootIndex, rightFootTarget, "right");
    }

    /// <summary>Verifies normal target synchronisation does not require diagnostic toe bones.</summary>
    [Headless]
    [Fact]
    public async Task FootTargetSyncController_WithoutDiagnostics_SynchronisesWithoutToeBones()
    {
        SceneTree sceneTree = GetSceneTree();
        SyncFixture fixture = await CreateSyncFixtureAsync(sceneTree, includeDiagnosticBones: false);

        try
        {
            fixture.Controller._ProcessModificationWithDelta(1.0d / 60.0d);

            AssertFootTargetMatchesBonePose(fixture.Skeleton, fixture.LeftFootIndex, fixture.LeftTarget, "left");
            AssertFootTargetMatchesBonePose(fixture.Skeleton, fixture.RightFootIndex, fixture.RightTarget, "right");
            Assert.False(fixture.Controller.LastPreIKDiagnostic.IsAvailable);
        }
        finally
        {
            fixture.Root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Verifies unavailable diagnostic dependencies fail closed without suppressing normal synchronisation.</summary>
    [Headless]
    [Fact]
    public async Task FootTargetSyncController_WithMissingDiagnosticDependencies_ClearsDiagnosticAndSynchronisesTargets()
    {
        SceneTree sceneTree = GetSceneTree();
        SyncFixture fixture = await CreateSyncFixtureAsync(sceneTree, includeDiagnosticBones: false);

        try
        {
            fixture.Controller.CapturePreIKDiagnostics = true;
            fixture.Controller._ProcessModificationWithDelta(1.0d / 60.0d);

            Assert.False(fixture.Controller.LastPreIKDiagnostic.IsAvailable);
            AssertFootTargetMatchesBonePose(fixture.Skeleton, fixture.LeftFootIndex, fixture.LeftTarget, "left");
            AssertFootTargetMatchesBonePose(fixture.Skeleton, fixture.RightFootIndex, fixture.RightTarget, "right");
        }
        finally
        {
            fixture.Root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Verifies enabling diagnostics after normal caching resolves the additional dependencies on demand.</summary>
    [Headless]
    [Fact]
    public async Task FootTargetSyncController_EnablingDiagnosticsAfterNormalCache_ResolvesDiagnosticDependencies()
    {
        SceneTree sceneTree = GetSceneTree();
        SyncFixture fixture = await CreateSyncFixtureAsync(sceneTree, includeDiagnosticBones: true);

        try
        {
            fixture.Controller._ProcessModificationWithDelta(1.0d / 60.0d);
            Assert.False(fixture.Controller.LastPreIKDiagnostic.IsAvailable);

            fixture.Controller.CapturePreIKDiagnostics = true;
            fixture.Controller._ProcessModificationWithDelta(1.0d / 60.0d);

            PreIKGraphPoseDiagnostic diagnostic = fixture.Controller.LastPreIKDiagnostic;
            Assert.True(diagnostic.IsAvailable);
            Assert.Equal(fixture.Controller.GetIndex(), diagnostic.ModifierIndex);
            Assert.Equal(fixture.Skeleton.GetBoneGlobalPose(fixture.LeftFootIndex), diagnostic.LeftFootSkeletonLocal);
            Assert.Equal(fixture.Skeleton.GetBoneGlobalPose(fixture.RightFootIndex), diagnostic.RightFootSkeletonLocal);
        }
        finally
        {
            fixture.Root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
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

    private static async Task<SyncFixture> CreateSyncFixtureAsync(SceneTree sceneTree, bool includeDiagnosticBones)
    {
        var root = new Node3D { Name = "FootTargetSyncTestRoot" };
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        var leftTarget = new Node3D { Name = "LeftTarget" };
        var rightTarget = new Node3D { Name = "RightTarget" };
        var controller = new FootTargetSyncController
        {
            Name = "FootTargetSyncController",
            LeftFootTarget = leftTarget,
            RightFootTarget = rightTarget,
        };

        root.AddChild(skeleton);
        root.AddChild(leftTarget);
        root.AddChild(rightTarget);
        skeleton.AddChild(controller);

        int hipsIndex = -1;
        if (includeDiagnosticBones)
        {
            int rootIndex = AddBone(skeleton, "Root", -1, Vector3.Zero);
            hipsIndex = AddBone(skeleton, "Hips", rootIndex, new Vector3(0f, 0.9f, 0f));
        }

        int leftFootIndex = AddBone(skeleton, "LeftFoot", hipsIndex, new Vector3(-0.15f, -0.9f, 0.05f));
        int rightFootIndex = AddBone(skeleton, "RightFoot", hipsIndex, new Vector3(0.15f, -0.9f, 0.05f));
        if (includeDiagnosticBones)
        {
            _ = AddBone(skeleton, "LeftToes", leftFootIndex, new Vector3(0f, 0f, -0.2f));
            _ = AddBone(skeleton, "RightToes", rightFootIndex, new Vector3(0f, 0f, -0.2f));
        }

        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        await WaitForFramesAsync(sceneTree, 2);
        return new(root, skeleton, controller, leftTarget, rightTarget, leftFootIndex, rightFootIndex);
    }

    private static int AddBone(Skeleton3D skeleton, string name, int parentIndex, Vector3 restPosition)
    {
        int index = skeleton.AddBone(name);
        skeleton.SetBoneRest(index, new Transform3D(Basis.Identity, restPosition));
        if (parentIndex >= 0)
        {
            skeleton.SetBoneParent(index, parentIndex);
        }

        return index;
    }

    private sealed record SyncFixture(
        Node3D Root,
        Skeleton3D Skeleton,
        FootTargetSyncController Controller,
        Node3D LeftTarget,
        Node3D RightTarget,
        int LeftFootIndex,
        int RightFootIndex);
}
