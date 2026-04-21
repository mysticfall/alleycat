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
    private const string SkeletonPath = "Subject/Female/Female_export/GeneralSkeleton";
    private const string AnimationTreePath = "Subject/Female/AnimationTree";

    private const float MinimumMidwaySeek = 0.2f;
    private const float MinimumFullSeek = 0.6f;
    private const float MaximumReturnedStandingSeek = 0.1f;
    private const float MinimumFullCrouchHipDropMetres = 0.08f;
    private const float MinimumFullCrouchKneeFlexionIncreaseRadians = 0.08f;
    private const float MinimumFullCrouchKneeFlexionAbsoluteRadians = 0.15f;

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
            driver,
            skeleton,
            "Standing",
            "Standing",
            hipsIndex,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        PoseSnapshot crouchMidway = await ApplyScenarioAndCaptureAsync(
            sceneTree,
            driver,
            skeleton,
            "CrouchMidway",
            "Crouching",
            hipsIndex,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        PoseSnapshot crouchFull = await ApplyScenarioAndCaptureAsync(
            sceneTree,
            driver,
            skeleton,
            "CrouchFull",
            "Crouching",
            hipsIndex,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        PoseSnapshot standingAgain = await ApplyScenarioAndCaptureAsync(
            sceneTree,
            driver,
            skeleton,
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

    private static async Task<PoseSnapshot> ApplyScenarioAndCaptureAsync(
        SceneTree sceneTree,
        Node driver,
        Skeleton3D skeleton,
        string scenarioName,
        string expectedStateId,
        int hipsIndex,
        int leftUpperLegIndex,
        int leftLowerLegIndex,
        int leftFootIndex)
    {
        bool applied = (bool)driver.Call("ApplyScenario", scenarioName);
        Assert.True(applied, $"Expected scenario '{scenarioName}' to apply successfully.");

        float seekRequest = driver.Call("GetStandingCrouchingSeekRequest").AsSingle();

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

    private sealed record PoseSnapshot(
        StringName StateId,
        float SeekRequest,
        float HipsWorldY,
        float LeftKneeFlexionRadians);
}
