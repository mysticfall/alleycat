using System.Reflection;
using AlleyCat.IK.Pose;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Non-visual integration coverage for IK-004 marker-driven pose-state-machine verification.
/// </summary>
public sealed class PoseStateMachineIntegrationTests
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
    private const string StandingPoseStateResourcePath = "res://assets/characters/ik/pose/standing_pose_state.tres";
    private const string KneelingPoseStateResourcePath = "res://assets/characters/ik/pose/kneeling_pose_state.tres";
    private const string StandingToKneelingTransitionResourcePath = "res://assets/characters/ik/pose/standing_to_kneeling_transition.tres";
    private const string KneelingToStandingTransitionResourcePath = "res://assets/characters/ik/pose/kneeling_to_standing_transition.tres";
    private const string PoseStateMachineTreeResourcePath = "res://assets/characters/reference/female/pose_state_machine_tree.tres";
    private static readonly StringName _standingCrouchingSeekParameter =
        new("parameters/StandingCrouching/TimeSeek/seek_request");

    private const float MinimumMidwaySeek = 0.2f;
    private const float MinimumFullSeek = 0.6f;
    private const float MaximumReturnedStandingSeek = 0.1f;
    private const float MinimumFullCrouchHipDropMetres = 0.08f;
    private const float MinimumFullCrouchKneeFlexionIncreaseRadians = 0.08f;
    private const float MinimumFullCrouchKneeFlexionAbsoluteRadians = 0.15f;
    private const float MinimumKneelingKneeFlexionIncreaseRadians = 0.05f;
    private const float FootTargetPositionToleranceMetres = 0.03f;
    private const float FootTargetRotationToleranceRadians = 0.06f;
    private const int TransitionAutoAdvanceWaitFrames = 320;

    /// <summary>
    /// Verifies marker scenarios drive the standing-continuum seek values,
    /// hip descent, and a crouch-specific knee-flexion sanity check.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineMarkerDriver_StandingContinuumScenarios_DriveStateAndBlendOutputs()
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

        Resource standingState = LoadRequiredResource(StandingPoseStateResourcePath);
        StringName standingAnimationStateName = GetRequiredStringNameProperty(standingState, "AnimationStateName");
        Assert.Equal("Standing", ((StringName)driver.Call("GetCurrentStateId")).ToString());
        Assert.Equal(standingAnimationStateName, ResolvePlayback(animationTree).GetCurrentNode());

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
            "Standing",
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
            "Standing",
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
    /// Verifies standing-continuum-to-kneeling honours the crouch gate plus armed-retreat trigger,
    /// uses trigger-driven kneel entry playback once entered, and can round-trip back to standing.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineMarkerDriver_StandingToKneeling_RespectsDepthGateAndForwardBaseline()
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
        int leftUpperLegIndex = RequireBoneIndex(skeleton, "LeftUpperLeg");
        int leftLowerLegIndex = RequireBoneIndex(skeleton, "LeftLowerLeg");
        int leftFootIndex = RequireBoneIndex(skeleton, "LeftFoot");

        TickScenario(sceneRoot, driver, "CrouchMidwayForward");
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        var crouchMidwayState = (StringName)driver.Call("GetCurrentStateId");
        Assert.Equal("Standing", crouchMidwayState.ToString());
        float crouchMidwayKneeFlexion = ComputeKneeFlexionRadians(
            skeleton,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        // Arm StandingToKneeling with a strong forward pose in the overlap region.
        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.32f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        var armedStandingState = (StringName)driver.Call("GetCurrentStateId");
        Assert.Equal("Standing", armedStandingState.ToString());

        // Retreat from the armed peak by enough to fire the armed-retreat trigger.
        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f));

        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        var kneelingState = (StringName)driver.Call("GetCurrentStateId");
        Assert.Equal("Kneeling", kneelingState.ToString());
        AssertPlaybackNodeIsOneOf(ResolvePlayback(animationTree), "KneelingEnter", "Kneeling");
        await AssertPlaybackConvergesToNodeWithoutInputAsync(
            sceneTree,
            ResolvePlayback(animationTree),
            KneelingPoseState.DefaultAnimationStateName,
            TransitionAutoAdvanceWaitFrames);

        float kneelingKneeFlexion = ComputeKneeFlexionRadians(
            skeleton,
            leftUpperLegIndex,
            leftLowerLegIndex,
            leftFootIndex);

        Assert.True(
            kneelingKneeFlexion >= crouchMidwayKneeFlexion + MinimumKneelingKneeFlexionIncreaseRadians,
            "Kneeling should increase left-knee flexion beyond crouch-midway, guarding against implausible shallow kneel poses.");

        // Holding the retreated pose must not bounce the state back to standing while the
        // cross-transition neutral-return gate holds.
        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        var stillKneelingState = (StringName)driver.Call("GetCurrentStateId");
        Assert.Equal("Kneeling", stillKneelingState.ToString());

        // Returning the head to the neutral forward baseline clears the cross-transition gate;
        // a subsequent forward-then-retreat cycle must then fire KneelingToStanding.
        TickScenario(sceneRoot, driver, "Standing");
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.32f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        var standingStateAfterKneel = (StringName)driver.Call("GetCurrentStateId");
        Assert.Equal(
            "Standing",
            standingStateAfterKneel.ToString());
        await AssertPlaybackConvergesToNodeAsync(
            sceneTree,
            sceneRoot,
            driver,
            ResolvePlayback(animationTree),
            StandingPoseState.DefaultAnimationStateName,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f),
            TransitionAutoAdvanceWaitFrames);
    }

    /// <summary>
    /// Verifies a kneel entry that fires while still inside the shared forward region does not
    /// immediately bounce back to standing on subsequent ticks.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineMarkerDriver_StandingToKneeling_OverlapRegionDoesNotImmediatelyReverse()
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

        // Arm StandingToKneeling with a strong forward pose beyond the arming threshold.
        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.32f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        // Retreat past the trigger-retreat ratio fires StandingToKneeling inside the overlap region.
        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        var kneelingState = (StringName)driver.Call("GetCurrentStateId");
        Assert.Equal("Kneeling", kneelingState.ToString());

        // Holding the retreated pose must not immediately reverse: the opposite-direction
        // transition is cross-locked until the forward-only neutral-return gate clears.
        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        var stillKneelingState = (StringName)driver.Call("GetCurrentStateId");
        Assert.Equal("Kneeling", stillKneelingState.ToString());
    }

    /// <summary>
    /// Verifies kneel exit honours the cross-transition lockout: after
    /// <see cref="StandingToKneelingPoseTransition"/> fires, the opposite-direction
    /// <see cref="KneelingToStandingPoseTransition"/> is gated until the head returns to the
    /// forward-only neutral baseline. Once the gate clears, a fresh forward-then-retreat cycle
    /// fires the exit transition.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineMarkerDriver_KneelingToStanding_ExitsAfterNeutralReturnAndForwardRetreat()
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
        _ = animationTree;

        // Arm and fire StandingToKneeling via a forward-then-retreat cycle in the overlap region.
        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.32f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        Assert.Equal("Kneeling", ((StringName)driver.Call("GetCurrentStateId")).ToString());
        AssertPlaybackNodeIsOneOf(ResolvePlayback(animationTree), "KneelingEnter", "Kneeling");
        await AssertPlaybackConvergesToNodeWithoutInputAsync(
            sceneTree,
            ResolvePlayback(animationTree),
            KneelingPoseState.DefaultAnimationStateName,
            TransitionAutoAdvanceWaitFrames);

        // Holding the retreated forward pose must keep the state in kneeling: the opposite-
        // direction transition is cross-locked until the forward-only neutral-return gate clears.
        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        Assert.Equal("Kneeling", ((StringName)driver.Call("GetCurrentStateId")).ToString());

        // Returning the head to the standing scenario brings the forward offset within the
        // neutral-return threshold, clearing the cross-transition gate.
        TickScenario(sceneRoot, driver, "Standing");
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        // A fresh forward-then-retreat cycle now fires KneelingToStanding.
        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.32f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        TickScenarioWithHeadOverride(
            sceneRoot,
            driver,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f));
        await WaitForFramesAsync(sceneTree, 2);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        var standingState = (StringName)driver.Call("GetCurrentStateId");
        Assert.Equal("Standing", standingState.ToString());
        await AssertPlaybackConvergesToNodeAsync(
            sceneTree,
            sceneRoot,
            driver,
            ResolvePlayback(animationTree),
            StandingPoseState.DefaultAnimationStateName,
            "KneelForward",
            CreateScenarioHeadTransform(sceneRoot, "KneelForward", z: 0.26f),
            TransitionAutoAdvanceWaitFrames);
    }

    /// <summary>
    /// Verifies transition-owned playback can move into the kneeling path while the standing state
    /// only writes crouch seek values when its steady-state AnimationTree node is actually active.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineMarkerDriver_KneelEntry_StandingStateRemainsPassiveOutsideItsActiveAnimationState()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");
        Node driver = Assert.IsType<Node>(sceneRoot.GetNodeOrNull(DriverPath), exactMatch: false);
        driver.Set("Active", false);

        AnimationTree animationTree = Assert.IsType<AnimationTree>(sceneRoot.GetNodeOrNull(AnimationTreePath), exactMatch: false);
        AnimationNodeStateMachinePlayback playback = ResolvePlayback(animationTree);
        AnimationNodeStateMachine stateMachine = Assert.IsType<AnimationNodeStateMachine>(ResourceLoader.Load(PoseStateMachineTreeResourcePath), exactMatch: false);
        Resource standingState = LoadRequiredResource(StandingPoseStateResourcePath);
        Resource standingToKneelingTransition = LoadRequiredResource(StandingToKneelingTransitionResourcePath);

        _ = Assert.IsType<AnimationNodeAnimation>(stateMachine.GetNode("KneelingEnter"), exactMatch: false);
        _ = Assert.IsType<AnimationNodeAnimation>(stateMachine.GetNode("Kneeling"), exactMatch: false);
        AnimationNodeAnimation kneelingExitNode = Assert.IsType<AnimationNodeAnimation>(
            stateMachine.GetNode("KneelingExit"),
            exactMatch: false);
        Assert.Equal("Kneel-enter", kneelingExitNode.Animation.ToString());
        Assert.Equal(1L, kneelingExitNode.Get("play_mode").AsInt64());

        StringName standingAnimationStateName = GetRequiredStringNameProperty(standingState, "AnimationStateName");
        StringName standingSeekRequestParameter = GetRequiredStringNameProperty(standingState, "SeekRequestParameter");

        playback.Start(standingAnimationStateName, true);
        await WaitForFramesAsync(sceneTree, 2);

        object kneelingContext = CreateRuntimeScenarioContext(
            standingToKneelingTransition,
            nameof(IPoseTransition.OnTransitionExit),
            0,
            sceneRoot,
            "KneelForward",
            1.0 / 60.0);
        object standingContext = CreateRuntimeScenarioContext(
            standingState,
            nameof(IPoseState.OnUpdate),
            0,
            sceneRoot,
            "CrouchFull",
            1e-6);

        InvokeMethod(standingState, nameof(IPoseState.OnUpdate), standingContext);
        float activeStandingSeek = ReadSeekRequest(animationTree, standingSeekRequestParameter);
        Assert.True(activeStandingSeek > 0f, "Standing state should write a positive crouch seek when its AnimationTree node is active.");

        InvokeMethod(standingToKneelingTransition, nameof(IPoseTransition.OnTransitionExit), kneelingContext);
        await WaitForFramesAsync(sceneTree, 2);

        AssertPlaybackNodeIsOneOf(playback, "KneelingEnter", "Kneeling");

        float inactiveSeekBeforeApply = ReadSeekRequest(animationTree, standingSeekRequestParameter);

        InvokeMethod(standingState, nameof(IPoseState.OnUpdate), standingContext);

        AssertPlaybackNodeIsOneOf(playback, "KneelingEnter", "Kneeling");

        float inactiveSeekAfterApply = ReadSeekRequest(animationTree, standingSeekRequestParameter);
        Assert.Equal(
            inactiveSeekBeforeApply,
            inactiveSeekAfterApply,
            precision: 5);
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

    private static void TickScenario(
        Node sceneRoot,
        Node driver,
        string scenarioName,
        int tickCount = -1,
        double delta = -1.0)
        => TickScenarioWithHeadOverride(sceneRoot, driver, scenarioName, headTargetOverride: null, tickCount, delta);

    private static void TickScenarioWithHeadOverride(
        Node sceneRoot,
        Node driver,
        string scenarioName,
        Transform3D? headTargetOverride,
        int tickCount = -1,
        double delta = -1.0)
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

        Transform3D headTargetTransform = headTargetOverride
            ?? ResolveScenarioMarkerTransform(
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
            nameof(PoseStateMachineMarkerDriver.TickPoseTargets),
            headTargetTransform,
            leftHandTargetTransform,
            rightHandTargetTransform,
            leftFootTargetTransform,
            rightFootTargetTransform,
            headRestMarker.GlobalTransform,
            tickCount,
            delta);
    }

    private static Transform3D CreateScenarioHeadTransform(Node sceneRoot, string scenarioName, float? y = null, float? z = null)
    {
        Node3D scenariosRoot = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(ScenarioMarkersRootPath), exactMatch: false);
        Node3D scenarioNode = Assert.IsType<Node3D>(
            scenariosRoot.GetNodeOrNull(new NodePath(scenarioName)),
            exactMatch: false);
        Transform3D baseTransform = ResolveScenarioMarkerTransform(
            scenarioNode,
            markerName: "Head",
            fallback: scenarioNode.GlobalTransform);
        Vector3 origin = baseTransform.Origin;
        origin = new Vector3(origin.X, y ?? origin.Y, z ?? origin.Z);
        return new Transform3D(baseTransform.Basis, origin);
    }

    private static AnimationNodeStateMachinePlayback ResolvePlayback(AnimationTree animationTree)
        => animationTree.Get(new StringName("parameters/playback")).As<AnimationNodeStateMachinePlayback>()
           ?? throw new Xunit.Sdk.XunitException("Expected AnimationTree playback object.");

    private static float ReadSeekRequest(AnimationTree animationTree, StringName seekRequestParameter)
        => animationTree.Get(seekRequestParameter).AsSingle();

    private static async Task AssertPlaybackConvergesToNodeWithoutInputAsync(
        SceneTree sceneTree,
        AnimationNodeStateMachinePlayback playback,
        StringName expectedNode,
        int maxWaitFrames)
    {
        for (int frame = 0; frame < maxWaitFrames; frame++)
        {
            if (playback.GetCurrentNode() == expectedNode)
            {
                return;
            }

            await WaitForFramesAsync(sceneTree, 1);
        }

        Assert.Equal(expectedNode.ToString(), playback.GetCurrentNode().ToString());
    }

    private static async Task AssertPlaybackConvergesToNodeAsync(
        SceneTree sceneTree,
        Node sceneRoot,
        Node driver,
        AnimationNodeStateMachinePlayback playback,
        StringName expectedNode,
        string scenarioName,
        Transform3D? headTargetOverride,
        int maxWaitFrames)
    {
        for (int frame = 0; frame < maxWaitFrames; frame++)
        {
            if (playback.GetCurrentNode() == expectedNode)
            {
                return;
            }

            if (headTargetOverride is null)
            {
                TickScenario(sceneRoot, driver, scenarioName);
            }
            else
            {
                TickScenarioWithHeadOverride(sceneRoot, driver, scenarioName, headTargetOverride);
            }

            await WaitForFramesAsync(sceneTree, 1);
        }

        Assert.Equal(expectedNode.ToString(), playback.GetCurrentNode().ToString());
    }

    private static object CreateRuntimeScenarioContext(
        GodotObject target,
        string methodName,
        int contextParameterIndex,
        Node sceneRoot,
        string scenarioName,
        double delta)
    {
        Node3D scenariosRoot = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(ScenarioMarkersRootPath), exactMatch: false);
        Node3D scenarioNode = Assert.IsType<Node3D>(
            scenariosRoot.GetNodeOrNull(new NodePath(scenarioName)),
            exactMatch: false);
        Node3D headRestMarker = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(HeadRestMarkerPath), exactMatch: false);

        Type contextType = ResolveMethod(target, methodName, parameterCount: -1)
            .GetParameters()[contextParameterIndex]
            .ParameterType;
        object? context = Activator.CreateInstance(contextType);
        Assert.NotNull(context);
        SetRequiredProperty(context, "HeadTargetRestTransform", headRestMarker.GlobalTransform);
        SetRequiredProperty(
            context,
            "HeadTargetTransform",
            ResolveScenarioMarkerTransform(
                scenarioNode,
                markerName: "Head",
                fallback: scenarioNode.GlobalTransform));
        AnimationTree? animationTree = sceneRoot.GetNodeOrNull<AnimationTree>(AnimationTreePath);
        if (animationTree is not null)
        {
            _ = TrySetProperty(context, nameof(PoseStateContext.AnimationTree), animationTree);
        }

        SetRequiredProperty(context, "RestHeadHeight", headRestMarker.GlobalTransform.Origin.Y);
        SetRequiredProperty(context, "Delta", delta);
        return context;
    }

    private static Resource LoadRequiredResource(string resourcePath)
    {
        Resource? resource = ResourceLoader.Load<Resource>(resourcePath);
        Assert.NotNull(resource);
        return resource;
    }

    private static StringName GetRequiredStringNameProperty(GodotObject target, StringName propertyName)
    {
        Variant property = target.Get(propertyName);
        StringName value = property.AsStringName();
        Assert.False(value.IsEmpty);
        return value;
    }

    private static float GetRequiredFloatProperty(GodotObject target, StringName propertyName)
        => target.Get(propertyName).AsSingle();

    private static void InvokeMethod(GodotObject target, string methodName, params object[] arguments)
    {
        MethodInfo method = ResolveMethod(target, methodName, arguments.Length);
        _ = method.Invoke(target, arguments);
    }

    private static MethodInfo ResolveMethod(GodotObject target, string methodName, int parameterCount)
        => target.GetType().GetMethods()
               .Single(method => method.Name == methodName && (parameterCount < 0 || method.GetParameters().Length == parameterCount));

    private static T GetRequiredPrivateField<T>(GodotObject target, string fieldName)
    {
        FieldInfo? field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        object? value = field.GetValue(target);
        return Assert.IsType<T>(value);
    }

    private static void SetRequiredProperty(object target, string propertyName, object value)
    {
        PropertyInfo? property = target.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        property.SetValue(target, value);
    }

    private static bool TrySetProperty(object target, string propertyName, object value)
    {
        PropertyInfo? property = target.GetType().GetProperty(propertyName);
        if (property is null)
        {
            return false;
        }

        property.SetValue(target, value);
        return true;
    }

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

    private static void AssertPlaybackNodeIsOneOf(
        AnimationNodeStateMachinePlayback playback,
        params string[] expectedNodes)
    {
        string currentNode = playback.GetCurrentNode().ToString();
        Assert.Contains(currentNode, expectedNodes);
    }

    private sealed record PoseSnapshot(
        StringName StateId,
        float SeekRequest,
        float HipsWorldY,
        float LeftKneeFlexionRadians);
}
