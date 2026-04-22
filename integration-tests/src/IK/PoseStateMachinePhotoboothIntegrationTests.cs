using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Non-visual integration coverage for IK-004 marker-driven pose-state-machine verification.
/// </summary>
public sealed class PoseStateMachinePhotoboothIntegrationTests
{
    private const string VerificationScenePath = "res://tests/characters/ik/pose_state_machine_test.tscn";
    private const string DriverPath = "PoseStateMachineDriver";
    private const string ScenarioMarkersRootPath = "Markers/PoseStateMachine/Scenarios";
    private const string HeadRestMarkerPath = "Markers/PoseStateMachine/RestHeadTarget";
    private const string LeftHandRestMarkerPath = "Markers/PoseStateMachine/HandTargetRestLeft";
    private const string RightHandRestMarkerPath = "Markers/PoseStateMachine/HandTargetRestRight";
    private const string SkeletonPath = "Subject/Female/Female_export/GeneralSkeleton";
    private const string AnimationTreePath = "Subject/Female/AnimationTree";
    private const string SubjectRootPath = "Subject/Female";
    private const string LeftFootTargetPath = "Subject/Female/IKTargets/LeftFoot";
    private const string RightFootTargetPath = "Subject/Female/IKTargets/RightFoot";
    private const string FootTargetSyncControllerPath = "Subject/Female/Female_export/GeneralSkeleton/FootTargetSyncController";
    private const string LeftLegIKControllerPath = "Subject/Female/Female_export/GeneralSkeleton/LeftLegIKController";
    private const string RightLegIKControllerPath = "Subject/Female/Female_export/GeneralSkeleton/RightLegIKController";
    private const string LeftLegTwoBoneIKControllerPath = "Subject/Female/Female_export/GeneralSkeleton/LeftLegTwoBoneIKController";
    private const string RightLegTwoBoneIKControllerPath = "Subject/Female/Female_export/GeneralSkeleton/RightLegTwoBoneIKController";
    private const string CopyLeftFootRotationPath = "Subject/Female/Female_export/GeneralSkeleton/CopyLeftFootRotation";
    private const string CopyRightFootRotationPath = "Subject/Female/Female_export/GeneralSkeleton/CopyRightFootRotation";
    private static readonly StringName _standingCrouchingSeekParameter =
        new("parameters/StandingCrouching/TimeSeek/seek_request");

    private const float MinimumMidwaySeek = 0.2f;
    private const float MinimumFullSeek = 0.6f;
    private const float MaximumReturnedStandingSeek = 0.1f;
    private const float MinimumFullCrouchHipDropMetres = 0.08f;
    private const float MinimumFullCrouchKneeFlexionIncreaseRadians = 0.08f;
    private const float MinimumFullCrouchKneeFlexionAbsoluteRadians = 0.15f;
    private const float FootTargetPositionToleranceMetres = 0.03f;
    private const float FootTargetRotationToleranceRadians = 0.06f;

    /// <summary>
    /// Verifies marker scenarios drive standing/crouching transitions, AnimationTree seek values,
    /// hip descent, and a crouch-specific knee-flexion sanity check.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineMarkerDriver_StandingToCrouchingScenarios_DriveStateAndBlendOutputs()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");

        Node driver = Assert.IsType<Node>(sceneRoot.GetNodeOrNull(DriverPath), exactMatch: false);
        Skeleton3D skeleton = Assert.IsType<Skeleton3D>(sceneRoot.GetNodeOrNull(SkeletonPath), exactMatch: false);
        AnimationTree animationTree = Assert.IsType<AnimationTree>(sceneRoot.GetNodeOrNull(AnimationTreePath), exactMatch: false);
        Assert.True((bool)driver.Call("IsAnimationTreeBound"), "Expected marker driver to bind AnimationTree.");
        _ = animationTree;

        int hipsIndex = RequireBoneIndex(skeleton, "Hips");
        int leftUpperLegIndex = RequireBoneIndex(skeleton, "LeftUpperLeg");
        int leftLowerLegIndex = RequireBoneIndex(skeleton, "LeftLowerLeg");
        int leftFootIndex = RequireBoneIndex(skeleton, "LeftFoot");

        PoseSnapshot standing = await ApplyScenarioAndCaptureAsync(
            sceneTree,
            sceneRoot,
            driver,
            skeleton,
            _standingCrouchingSeekParameter,
            "Standing",
            "Standing",
            hipsIndex,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        PoseSnapshot crouchMidway = await ApplyScenarioAndCaptureAsync(
            sceneTree,
            sceneRoot,
            driver,
            skeleton,
            _standingCrouchingSeekParameter,
            "CrouchMidway",
            "Crouching",
            hipsIndex,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        PoseSnapshot crouchFull = await ApplyScenarioAndCaptureAsync(
            sceneTree,
            sceneRoot,
            driver,
            skeleton,
            _standingCrouchingSeekParameter,
            "CrouchFull",
            "Crouching",
            hipsIndex,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        PoseSnapshot standingAgain = await ApplyScenarioAndCaptureAsync(
            sceneTree,
            sceneRoot,
            driver,
            skeleton,
            _standingCrouchingSeekParameter,
            "Standing",
            "Standing",
            hipsIndex,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        Assert.True(
            crouchMidway.SeekRequest > standing.SeekRequest,
            "Crouch-midway seek value should exceed standing seek value.");
        Assert.True(
            crouchMidway.SeekRequest >= MinimumMidwaySeek,
            $"Crouch-midway seek should be at least {MinimumMidwaySeek:F2}.");
        Assert.True(
            crouchFull.SeekRequest > crouchMidway.SeekRequest,
            "Crouch-full seek value should exceed crouch-midway seek value.");
        Assert.True(
            crouchFull.SeekRequest >= MinimumFullSeek,
            $"Crouch-full seek should be at least {MinimumFullSeek:F2}.");

        Assert.True(
            standingAgain.SeekRequest <= MaximumReturnedStandingSeek,
            $"Returned standing seek should drop near idle (<= {MaximumReturnedStandingSeek:F2}).");

        float fullCrouchHipDrop = standing.HipsWorldY - crouchFull.HipsWorldY;
        Assert.True(
            fullCrouchHipDrop >= MinimumFullCrouchHipDropMetres,
            "Full crouch should lower the hips compared to standing.");

        // Anatomical sanity guard: crouching should visibly increase knee flexion.
        float kneeFlexionIncrease = crouchFull.LeftKneeFlexionRadians - standing.LeftKneeFlexionRadians;
        Assert.True(
            kneeFlexionIncrease >= MinimumFullCrouchKneeFlexionIncreaseRadians,
            "Full crouch should increase left-knee flexion versus standing.");
        Assert.True(
            crouchFull.LeftKneeFlexionRadians >= MinimumFullCrouchKneeFlexionAbsoluteRadians,
            "Full crouch should maintain a minimally bent left-knee posture.");
    }

    /// <summary>
    /// Verifies crouch animation sampling synchronises both foot IK targets before leg IK solve.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineMarkerDriver_CrouchFull_SynchronisesFootTargetsFromAnimatedFeetBeforeLegSolve()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");

        Node driver = Assert.IsType<Node>(sceneRoot.GetNodeOrNull(DriverPath), exactMatch: false);
        _ = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(SubjectRootPath), exactMatch: false);
        Skeleton3D skeleton = Assert.IsType<Skeleton3D>(sceneRoot.GetNodeOrNull(SkeletonPath), exactMatch: false);
        Node3D leftFootTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftFootTargetPath), exactMatch: false);
        Node3D rightFootTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(RightFootTargetPath), exactMatch: false);
        SkeletonModifier3D syncController = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(FootTargetSyncControllerPath),
            exactMatch: false);
        SkeletonModifier3D leftLegController = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(LeftLegIKControllerPath),
            exactMatch: false);
        SkeletonModifier3D rightLegController = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull(RightLegIKControllerPath),
            exactMatch: false);

        TickScenario(sceneRoot, driver, "CrouchFull");

        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        Assert.True(
            syncController.GetIndex() < leftLegController.GetIndex()
            && syncController.GetIndex() < rightLegController.GetIndex(),
            "Foot target sync stage must execute before both leg IK controllers.");

        int leftFootIndex = RequireBoneIndex(skeleton, "LeftFoot");
        int rightFootIndex = RequireBoneIndex(skeleton, "RightFoot");

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

        leftLegController.Active = false;
        rightLegController.Active = false;
        leftLegTwoBone.Active = false;
        rightLegTwoBone.Active = false;
        copyLeftFootRotation.Active = false;
        copyRightFootRotation.Active = false;

        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        Transform3D expectedLeftFootPose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(leftFootIndex);
        Transform3D expectedRightFootPose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(rightFootIndex);

        AssertTargetMatchesFootPose(
            leftFootTarget,
            expectedLeftFootPose,
            "LeftFoot target should follow crouch animation foot pose before leg solve.");
        AssertTargetMatchesFootPose(
            rightFootTarget,
            expectedRightFootPose,
            "RightFoot target should follow crouch animation foot pose before leg solve.");
    }

    private static async Task<PoseSnapshot> ApplyScenarioAndCaptureAsync(
        SceneTree sceneTree,
        Node sceneRoot,
        Node driver,
        Skeleton3D skeleton,
        StringName seekRequestParameter,
        string scenarioName,
        string expectedStateId,
        int hipsIndex,
        int leftUpperLegIndex,
        int leftLowerLegIndex,
        int leftFootIndex)
    {
        TickScenario(sceneRoot, driver, scenarioName);

        AnimationTree animationTree = Assert.IsType<AnimationTree>(sceneRoot.GetNodeOrNull(AnimationTreePath), exactMatch: false);
        float seekRequest = ReadSeekRequest(animationTree, seekRequestParameter);

        await WaitForFramesAsync(sceneTree, 4);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        var currentStateId = (StringName)driver.Call("GetCurrentStateId");
        Assert.Equal(expectedStateId, currentStateId.ToString());

        float hipsWorldY = ResolveBoneWorldPosition(skeleton, hipsIndex).Y;
        float leftKneeFlexionRadians = ComputeKneeFlexionRadians(
            skeleton,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        return new PoseSnapshot(
            currentStateId,
            seekRequest,
            hipsWorldY,
            leftKneeFlexionRadians);
    }

    private static void TickScenario(Node sceneRoot, Node driver, string scenarioName)
    {
        Node3D scenariosRoot = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(ScenarioMarkersRootPath), exactMatch: false);
        Node3D scenarioNode = Assert.IsType<Node3D>(
            scenariosRoot.GetNodeOrNull(new NodePath(scenarioName)),
            exactMatch: false);

        Node3D headRestMarker = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(HeadRestMarkerPath), exactMatch: false);
        Node3D leftHandRestMarker = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftHandRestMarkerPath), exactMatch: false);
        Node3D rightHandRestMarker = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(RightHandRestMarkerPath), exactMatch: false);
        Node3D leftFootTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftFootTargetPath), exactMatch: false);
        Node3D rightFootTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(RightFootTargetPath), exactMatch: false);

        Transform3D headTargetTransform = ResolveScenarioMarkerTransform(
            scenarioNode,
            markerName: "Head",
            fallback: scenarioNode.GlobalTransform);
        Transform3D leftHandTargetTransform = ResolveScenarioMarkerTransform(
            scenarioNode,
            markerName: "LeftHand",
            fallback: leftHandRestMarker.GlobalTransform);
        Transform3D rightHandTargetTransform = ResolveScenarioMarkerTransform(
            scenarioNode,
            markerName: "RightHand",
            fallback: rightHandRestMarker.GlobalTransform);
        Transform3D leftFootTargetTransform = ResolveScenarioMarkerTransform(
            scenarioNode,
            markerName: "LeftFoot",
            fallback: leftFootTarget.GlobalTransform);
        Transform3D rightFootTargetTransform = ResolveScenarioMarkerTransform(
            scenarioNode,
            markerName: "RightFoot",
            fallback: rightFootTarget.GlobalTransform);

        _ = driver.Call(
            "TickPoseTargets",
            headTargetTransform,
            leftHandTargetTransform,
            rightHandTargetTransform,
            leftFootTargetTransform,
            rightFootTargetTransform,
            headRestMarker.GlobalTransform,
            -1,
            -1.0);
    }

    private static float ReadSeekRequest(AnimationTree animationTree, StringName seekRequestParameter)
        => animationTree.Get(seekRequestParameter).AsSingle();

    private static Transform3D ResolveScenarioMarkerTransform(
        Node3D scenarioNode,
        string markerName,
        Transform3D fallback)
    {
        Node3D? marker = scenarioNode.GetNodeOrNull<Node3D>(new NodePath(markerName));
        return marker?.GlobalTransform ?? fallback;
    }

    private static int RequireBoneIndex(Skeleton3D skeleton, string boneName)
    {
        int boneIndex = skeleton.FindBone(boneName);
        Assert.True(boneIndex >= 0, $"Expected skeleton bone '{boneName}' to exist.");
        return boneIndex;
    }

    private static Vector3 ResolveBoneWorldPosition(Skeleton3D skeleton, int boneIndex)
        => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private static float ComputeKneeFlexionRadians(
        Skeleton3D skeleton,
        int upperLegIndex,
        int lowerLegIndex,
        int footIndex)
    {
        Vector3 upperLegWorld = ResolveBoneWorldPosition(skeleton, upperLegIndex);
        Vector3 lowerLegWorld = ResolveBoneWorldPosition(skeleton, lowerLegIndex);
        Vector3 footWorld = ResolveBoneWorldPosition(skeleton, footIndex);

        Vector3 thighDirection = (lowerLegWorld - upperLegWorld).Normalized();
        Vector3 shinDirection = (footWorld - lowerLegWorld).Normalized();
        float clampedDot = Mathf.Clamp(thighDirection.Dot(shinDirection), -1.0f, 1.0f);

        return Mathf.Acos(clampedDot);
    }

    private static void AssertTargetMatchesFootPose(Node3D footTarget, Transform3D expectedFootPose, string message)
    {
        float positionDelta = footTarget.GlobalPosition.DistanceTo(expectedFootPose.Origin);
        Quaternion expectedRotation = expectedFootPose.Basis.Orthonormalized().GetRotationQuaternion();
        Quaternion actualRotation = footTarget.GlobalTransform.Basis.Orthonormalized().GetRotationQuaternion();
        float rotationDelta = QuaternionAngleRadians(expectedRotation, actualRotation);

        Assert.True(positionDelta <= FootTargetPositionToleranceMetres, $"{message} Position delta: {positionDelta:F4} m.");
        Assert.True(
            rotationDelta <= FootTargetRotationToleranceRadians,
            $"{message} Rotation delta: {rotationDelta:F4} rad.");
    }

    private static float QuaternionAngleRadians(Quaternion from, Quaternion to)
    {
        float dot = Mathf.Abs(from.Dot(to));
        dot = Mathf.Clamp(dot, -1.0f, 1.0f);
        return 2.0f * Mathf.Acos(dot);
    }

    private sealed record PoseSnapshot(
        StringName StateId,
        float SeekRequest,
        float HipsWorldY,
        float LeftKneeFlexionRadians);
}
