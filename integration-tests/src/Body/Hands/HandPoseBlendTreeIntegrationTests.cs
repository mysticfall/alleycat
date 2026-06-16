using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Body.Hands;

/// <summary>
/// Integration coverage for BODY-001 Hands reference tree resources and hand-pose controller behaviour.
/// </summary>
public sealed class HandPoseBlendTreeIntegrationTests
{
    private const string PoseStateMachineTreePath = "res://assets/characters/templates/animation/animation_tree_root_player.tres";
    private const string NpcAnimationTreeRootPath = "res://assets/characters/templates/animation/animation_tree_root_npc.tres";
    private const string PlayerTemplateScenePath = "res://assets/characters/templates/reference_female/reference_female_player.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string StandingPoseStatePath = "res://assets/characters/ik/pose/standing_pose_state.tres";
    private const string ResetAnimationPath = "res://assets/characters/reference/female/animations/Reset.tres";
    private const string GrabBallAnimationPath = "res://assets/characters/reference/female/animations/Grab-ball-40.tres";
    private const string PoseStateMachineTreeUID = "uid://bge48ng374i85";
    private const string NpcAnimationTreeRootUID = "uid://c485owf86etdu";
    private static readonly NodePath _referenceFemaleSkeletonFilterRoot = new("%GeneralSkeleton");

    /// <summary>
    /// Verifies the reference female tree has one active upstream state machine and finger-only hand blends.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemalePoseTree_WrapsStateMachineAndConfiguresFingerFilteredHandBlends()
    {
        AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(
            ResourceLoader.Load(PoseStateMachineTreePath),
            exactMatch: false);

        _ = Assert.IsType<AnimationNodeStateMachine>(root.GetNode(HandPoseAnimationTreePaths.UpstreamNode), exactMatch: false);
        Assert.Null(root.GetNode("PostPipeline"));
        Assert.Equal(1, CountNodesNamed(root, HandPoseAnimationTreePaths.UpstreamNode));

        AssertHandBlendFilters(root, LimbSide.Left, HandPoseAnimationTreePaths.LeftHandBlendNode);
        AssertHandBlendFilters(root, LimbSide.Right, HandPoseAnimationTreePaths.RightHandBlendNode);
        AssertConnection(root, HandPoseAnimationTreePaths.LeftHandBlendNode, 0, HandPoseAnimationTreePaths.UpstreamNode);
        AssertConnection(root, HandPoseAnimationTreePaths.LeftHandBlendNode, 1, HandPoseAnimationTreePaths.LeftHandPoseNode);
        AssertConnection(root, HandPoseAnimationTreePaths.RightHandBlendNode, 0, HandPoseAnimationTreePaths.LeftHandBlendNode);
        AssertConnection(root, HandPoseAnimationTreePaths.RightHandBlendNode, 1, HandPoseAnimationTreePaths.RightHandPoseNode);
        AssertConnection(root, EyesAnimationTreePaths.HorizontalLookBlendNode, 0, HandPoseAnimationTreePaths.RightHandBlendNode);
    }

    /// <summary>
    /// Verifies the NPC-safe animation root keeps locomotion generic while exposing hand-pose overlays.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleNpcTree_ProvidesHandBlendsWithoutPlayerPoseStates()
    {
        AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(
            ResourceLoader.Load(NpcAnimationTreeRootPath),
            exactMatch: false);
        string contents = Godot.FileAccess.GetFileAsString(NpcAnimationTreeRootPath);

        Assert.Contains("states/Idle/node", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("StandingCrouching", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("AnimationNodeTimeSeek_kcdmd", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("AnimationNodeTimeSeek_phcyr", contents, StringComparison.Ordinal);
        Assert.Contains(EyesAnimationTreePaths.HorizontalLookSeekNode, contents, StringComparison.Ordinal);
        Assert.Contains(EyesAnimationTreePaths.VerticalLookSeekNode, contents, StringComparison.Ordinal);
        Assert.DoesNotContain("AllFours", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("VRIK", contents, StringComparison.Ordinal);

        _ = Assert.IsType<AnimationNodeStateMachine>(root.GetNode(HandPoseAnimationTreePaths.UpstreamNode), exactMatch: false);
        AssertHandBlendFilters(root, LimbSide.Left, HandPoseAnimationTreePaths.LeftHandBlendNode);
        AssertHandBlendFilters(root, LimbSide.Right, HandPoseAnimationTreePaths.RightHandBlendNode);
        AssertConnection(root, HandPoseAnimationTreePaths.LeftHandBlendNode, 0, HandPoseAnimationTreePaths.UpstreamNode);
        AssertConnection(root, HandPoseAnimationTreePaths.LeftHandBlendNode, 1, HandPoseAnimationTreePaths.LeftHandPoseNode);
        AssertConnection(root, HandPoseAnimationTreePaths.RightHandBlendNode, 0, HandPoseAnimationTreePaths.LeftHandBlendNode);
        AssertConnection(root, HandPoseAnimationTreePaths.RightHandBlendNode, 1, HandPoseAnimationTreePaths.RightHandPoseNode);
        AssertConnection(root, EyesAnimationTreePaths.HorizontalLookBlendNode, 0, HandPoseAnimationTreePaths.RightHandBlendNode);
    }

    /// <summary>
    /// Verifies shipped resources only use the wrapped States parameter paths.
    /// </summary>
    [Headless]
    [Fact]
    public void ShippedAnimationResources_DoNotContainStaleRootStateMachinePaths()
    {
        string[] paths =
        [
            PoseStateMachineTreePath,
            NpcAnimationTreeRootPath,
            PlayerTemplateScenePath,
            PlayerScenePath,
            StandingPoseStatePath,
        ];

        foreach (string path in paths)
        {
            string contents = Godot.FileAccess.GetFileAsString(path);
            Assert.DoesNotContain("UpstreamPoseStateMachine", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("parameters/playback", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("parameters/Walking/blend_position", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("parameters/StandingCrouching/TimeSeek/seek_request", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("parameters/AllFoursTransitioning/TimeSeek/seek_request", contents, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Verifies the controller initialises reset fallback state and smoothly advances hand blend amount.
    /// </summary>
    [Headless]
    [Fact]
    public void HandPoseController_UsesResetFallbackAndSmoothlyTransitionsBlendAmount()
    {
        Animation reset = Assert.IsType<Animation>(ResourceLoader.Load(ResetAnimationPath), exactMatch: false);
        Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);
        Assert.NotNull(reset);
        Assert.NotNull(grabBall);

        AnimationTree tree = new()
        {
            TreeRoot = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(PoseStateMachineTreePath), exactMatch: false),
        };

        HandPoseController controller = new(tree)
        {
            TransitionDuration = 0.2f,
        };

        controller.SetHandPose(LimbSide.Left, grabBall, weight: 1f, immediate: false);

        Assert.Equal(0f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Left)).AsSingle());
        controller.Update(0.1);
        float halfwayBlend = tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Left)).AsSingle();

        Assert.InRange(halfwayBlend, 0.45f, 0.55f);
        controller.Update(0.1);

        Assert.Equal(1f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Left)).AsSingle());
        Assert.Same(grabBall, controller.CurrentLeftHandPose);

        controller.SetHandPose(LimbSide.Right, grabBall, weight: 0.4f, immediate: false);
        controller.Update(0.1);
        float weightedHalfwayBlend = tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle();
        Assert.InRange(weightedHalfwayBlend, 0.19f, 0.21f);
        controller.Update(0.1);
        Assert.InRange(tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle(), 0.39f, 0.41f);
        Assert.Same(grabBall, controller.CurrentRightHandPose);

        controller.ClearHandPose(LimbSide.Left, immediate: true);

        Assert.Null(controller.CurrentLeftHandPose);
        Assert.Equal(0f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Left)).AsSingle());
    }

    /// <summary>
    /// Verifies clearing and switching poses settle at fixed-duration speed instead of asymptotically lingering.
    /// </summary>
    [Headless]
    [Fact]
    public void HandPoseController_ClearsAndSwitchesPosesWithinTransitionDuration()
    {
        Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);
        Animation alternateGrab = new()
        {
            ResourceName = "AlternateGrab",
        };

        AnimationTree tree = new()
        {
            TreeRoot = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(PoseStateMachineTreePath), exactMatch: false),
        };

        HandPoseController controller = new(tree)
        {
            TransitionDuration = 0.2f,
        };

        controller.SetHandPose(LimbSide.Left, grabBall, weight: 1f, immediate: true);
        controller.ClearHandPose(LimbSide.Left);

        AdvanceController(controller, 10, 0.02);

        Assert.Null(controller.CurrentLeftHandPose);
        Assert.Equal(0f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Left)).AsSingle());

        controller.SetHandPose(LimbSide.Right, grabBall, weight: 1f, immediate: true);
        controller.SetHandPose(LimbSide.Right, alternateGrab, weight: 1f, immediate: false);

        AdvanceController(controller, 10, 0.02);

        Assert.Same(alternateGrab, controller.CurrentRightHandPose);
        Assert.Equal(0f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle());

        AdvanceController(controller, 10, 0.02);

        Assert.Equal(1f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle());

        controller.SetHandPose(LimbSide.Left, grabBall, weight: 0.01f, immediate: true);
        controller.ClearHandPose(LimbSide.Left);

        AdvanceController(controller, 10, 0.02);

        Assert.Null(controller.CurrentLeftHandPose);
        Assert.Equal(0f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Left)).AsSingle());

        controller.SetHandPose(LimbSide.Right, grabBall, weight: 0.01f, immediate: true);
        controller.SetHandPose(LimbSide.Right, alternateGrab, weight: 1f, immediate: false);

        AdvanceController(controller, 10, 0.02);

        Assert.Same(alternateGrab, controller.CurrentRightHandPose);
        Assert.Equal(0f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle());
    }

    /// <summary>
    /// Verifies a pose replacement activates as soon as fade-out reaches zero and uses remaining frame time to fade in.
    /// </summary>
    [Headless]
    [Fact]
    public void HandPoseController_SwitchingPoseActivatesPendingPoseInSameUpdateWhenFadeOutSettles()
    {
        Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);
        Animation alternateGrab = new()
        {
            ResourceName = "AlternateGrabSameUpdate",
        };

        AnimationTree tree = new()
        {
            TreeRoot = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(PoseStateMachineTreePath), exactMatch: false),
        };

        HandPoseController controller = new(tree)
        {
            TransitionDuration = 0.2f,
        };

        controller.SetHandPose(LimbSide.Right, grabBall, weight: 1f, immediate: true);
        controller.SetHandPose(LimbSide.Right, alternateGrab, weight: 1f, immediate: false);

        controller.Update(0.3);

        Assert.Same(alternateGrab, controller.CurrentRightHandPose);
        Assert.InRange(tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle(), 0.45f, 0.55f);
    }

    /// <summary>
    /// Verifies reselecting the current pose during replacement fade-out cancels the pending replacement.
    /// </summary>
    [Headless]
    [Fact]
    public void HandPoseController_ReselectingCurrentPoseCancelsPendingReplacement()
    {
        Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);
        Animation alternateGrab = new()
        {
            ResourceName = "AlternateGrabCancelledPending",
        };

        AnimationTree tree = new()
        {
            TreeRoot = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(PoseStateMachineTreePath), exactMatch: false),
        };

        HandPoseController controller = new(tree)
        {
            TransitionDuration = 0.2f,
        };

        controller.SetHandPose(LimbSide.Right, grabBall, weight: 1f, immediate: true);
        controller.SetHandPose(LimbSide.Right, alternateGrab, weight: 1f, immediate: false);
        controller.Update(0.1);

        controller.SetHandPose(LimbSide.Right, grabBall, weight: 1f, immediate: false);
        AdvanceController(controller, 10, 0.02);

        AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(tree.TreeRoot, exactMatch: false);
        AnimationNodeAnimation rightPoseNode = Assert.IsType<AnimationNodeAnimation>(
            rootTree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(LimbSide.Right)),
            exactMatch: false);

        Assert.Same(grabBall, controller.CurrentRightHandPose);
        Assert.NotSame(alternateGrab, controller.CurrentRightHandPose);
        Assert.Equal(new StringName("Grab-ball-40"), rightPoseNode.Animation);
        Assert.NotEqual(new StringName("AlternateGrabCancelledPending"), rightPoseNode.Animation);
        Assert.Equal(1f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle());
    }

    /// <summary>
    /// Verifies the player scene animation setup can register and use the ball grab hand-pose animation.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerScene_HandPoseControllerCanUseGrabBallAnimation()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(PlayerScenePath);
        Node root = scene.Instantiate();

        try
        {
            TestUtils.EnsureCharacterRuntimeInstalled(root);
            AnimationTree tree = root.GetNode<AnimationTree>("AnimationTree");
            AnimationPlayer player = root.GetNode<AnimationPlayer>("AnimationPlayer");
            Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);

            tree.Active = false;
            HandPoseController controller = new(tree);
            controller.SetHandPose(LimbSide.Right, grabBall, weight: 1f, immediate: true);

            AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(tree.TreeRoot, exactMatch: false);
            AnimationNodeAnimation rightPoseNode = Assert.IsType<AnimationNodeAnimation>(
                rootTree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(LimbSide.Right)),
                exactMatch: false);

            Assert.True(player.HasAnimation(new StringName("Grab-ball-40")));
            Assert.Equal(new StringName("Grab-ball-40"), rightPoseNode.Animation);
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Verifies the reference player tree applies the grab pose to effective finger bone output for both hands.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_GrabBallPoseEffectivelyMovesEachHandsOwnFingerBones()
    {
        await AssertGrabPoseMovesFingerBoneAsync(LimbSide.Left, "LeftIndexProximal");
        await AssertGrabPoseMovesFingerBoneAsync(LimbSide.Right, "RightIndexProximal");
    }

    /// <summary>
    /// Verifies per-hand behaviours sharing one AnimationTree do not reset the opposite hand channel during processing.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_RightHandBehaviourKeepsGrabBlendAfterLeftHandProcessesSharedTree()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PackedScene scene = ResourceLoader.Load<PackedScene>(PlayerScenePath);
        Node root = scene.Instantiate();
        sceneTree.Root.AddChild(root);

        try
        {
            TestUtils.EnsureCharacterRuntimeInstalled(root);
            root.GetNode<Node>("Hands").QueueFree();
            AnimationTree tree = root.GetNode<AnimationTree>("AnimationTree");
            AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(tree.TreeRoot, exactMatch: false);
            HandPoseBehaviour rightHand = new()
            {
                Name = "RegressionRightHand",
                AnimationTree = tree,
                Side = LimbSide.Right,
            };
            HandPoseBehaviour leftHand = new()
            {
                Name = "RegressionLeftHand",
                AnimationTree = tree,
                Side = LimbSide.Left,
            };
            Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);

            root.AddChild(rightHand);
            root.AddChild(leftHand);
            rightHand._Ready();
            leftHand._Ready();

            ResolvePlayback(tree).Start(new StringName("StandingCrouching"), true);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            tree.Advance(1.0 / 60.0);

            rightHand.SetPose(grabBall, weight: 1f, immediate: true);
            tree.Advance(1.0 / 60.0);
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            float rightBlend = tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle();
            float leftBlend = tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Left)).AsSingle();
            AnimationNodeAnimation rightPoseNode = Assert.IsType<AnimationNodeAnimation>(
                rootTree.GetNode(HandPoseAnimationTreePaths.RightHandPoseNode),
                exactMatch: false);

            Assert.InRange(rightBlend, 0.99f, 1.0f);
            Assert.Equal(0f, leftBlend);
            Assert.Equal(new StringName("Grab-ball-40"), rightPoseNode.Animation);

            rightHand.ClearPose(immediate: true);
            tree.Advance(1.0 / 60.0);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            leftHand.SetPose(grabBall, weight: 1f, immediate: true);
            tree.Advance(1.0 / 60.0);
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            leftBlend = tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Left)).AsSingle();
            rightBlend = tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle();
            AnimationNodeAnimation leftPoseNode = Assert.IsType<AnimationNodeAnimation>(
                rootTree.GetNode(HandPoseAnimationTreePaths.LeftHandPoseNode),
                exactMatch: false);

            Assert.InRange(leftBlend, 0.99f, 1.0f);
            Assert.Equal(0f, rightBlend);
            Assert.Equal(new StringName("Grab-ball-40"), leftPoseNode.Animation);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies affected resources retain stable UIDs and reload through UID references.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleAnimationResources_RetainStableUIDsAndReloadByUID()
    {
        Assert.Equal(ResourceUid.TextToId(PoseStateMachineTreeUID), ResourceLoader.GetResourceUid(PoseStateMachineTreePath));
        Assert.Equal(ResourceUid.TextToId(NpcAnimationTreeRootUID), ResourceLoader.GetResourceUid(NpcAnimationTreeRootPath));

        _ = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(PoseStateMachineTreeUID), exactMatch: false);
        _ = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(NpcAnimationTreeRootUID), exactMatch: false);
    }

    private static void AssertHandBlendFilters(AnimationNodeBlendTree root, LimbSide side, string nodeName)
    {
        AnimationNodeBlend2 blend = Assert.IsType<AnimationNodeBlend2>(root.GetNode(nodeName), exactMatch: false);
        Assert.True(blend.FilterEnabled);

        foreach (NodePath filterPath in HandPoseAnimationTreePaths.GetFingerFilterPaths(side, _referenceFemaleSkeletonFilterRoot))
        {
            Assert.True(blend.IsPathFiltered(filterPath), $"Expected {nodeName} to filter {filterPath}.");
        }

        string sideName = side == LimbSide.Left ? "Left" : "Right";
        Assert.False(blend.IsPathFiltered(new NodePath($"%GeneralSkeleton:{sideName}Hand")));
        Assert.False(blend.IsPathFiltered(new NodePath($"%GeneralSkeleton:{sideName}LowerArm")));
        Assert.False(blend.IsPathFiltered(new NodePath($"%GeneralSkeleton:{sideName}UpperArm")));
    }

    private static void AssertConnection(
        AnimationNodeBlendTree tree,
        string inputNode,
        int inputIndex,
        string outputNode)
    {
        Godot.Collections.Array connections = tree.Get("node_connections").AsGodotArray();
        for (int index = 0; index < connections.Count; index += 3)
        {
            if (connections[index].AsStringName() == new StringName(inputNode)
                && connections[index + 1].AsInt32() == inputIndex
                && connections[index + 2].AsStringName() == new StringName(outputNode))
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected connection {inputNode}[{inputIndex}] <- {outputNode} in {tree.ResourceName}.");
    }

    private static async Task AssertGrabPoseMovesFingerBoneAsync(LimbSide side, string fingerBoneName)
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PackedScene scene = ResourceLoader.Load<PackedScene>(PlayerScenePath);
        Node root = scene.Instantiate();
        sceneTree.Root.AddChild(root);

        try
        {
            TestUtils.EnsureCharacterRuntimeInstalled(root);
            AnimationPlayer player = root.GetNode<AnimationPlayer>("AnimationPlayer");
            AnimationTree tree = root.GetNode<AnimationTree>("AnimationTree");
            Skeleton3D skeleton = root.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);
            Animation reset = Assert.IsType<Animation>(ResourceLoader.Load(ResetAnimationPath), exactMatch: false);
            AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(tree.TreeRoot, exactMatch: false);
            AnimationNodeAnimation poseNode = Assert.IsType<AnimationNodeAnimation>(
                rootTree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(side)),
                exactMatch: false);
            NodePath fingerTrackPath = new($"%GeneralSkeleton:{fingerBoneName}");
            int resetTrackIndex = RequireRotationTrack(reset, fingerTrackPath);
            int grabTrackIndex = RequireRotationTrack(grabBall, fingerTrackPath);
            int fingerBoneIndex = skeleton.FindBone(fingerBoneName);
            Assert.True(fingerBoneIndex >= 0, $"Expected skeleton to contain {fingerBoneName}.");
            Assert.True(player.HasAnimation(new StringName(HandPoseAnimationTreePaths.ResetAnimationName)));
            Assert.True(player.HasAnimation(new StringName("Grab-ball-40")));

            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            tree.Active = true;
            ResolvePlayback(tree).Start(new StringName("StandingCrouching"), true);
            HandPoseController controller = new(tree);
            controller.ClearHandPose(side, immediate: true);

            controller.SetHandPose(side, grabBall, weight: 1f, immediate: true);
            float blend = tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(side)).AsSingle();
            Quaternion resetPose = reset.RotationTrackInterpolate(resetTrackIndex, 0.0, backward: false);
            Quaternion grabPose = grabBall.RotationTrackInterpolate(grabTrackIndex, 0.0, backward: false);
            float poseDelta = QuaternionAngleRadians(resetPose, grabPose);

            Assert.Equal(new StringName("Grab-ball-40"), poseNode.Animation);
            Assert.InRange(blend, 0.99f, 1.0f);
            Assert.True(
                poseDelta > 0.001f,
                $"Expected {side} grab pose to visibly affect {fingerBoneName}; observed basis delta {poseDelta:0.######}.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    private static int RequireRotationTrack(Animation animation, NodePath path)
    {
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            if (animation.TrackGetType(trackIndex) == Animation.TrackType.Rotation3D
                && animation.TrackGetPath(trackIndex) == path)
            {
                return trackIndex;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected animation '{animation.ResourcePath}' to contain rotation track '{path}'.");
    }

    private static float QuaternionAngleRadians(Quaternion from, Quaternion to)
        => Mathf.Abs(from.AngleTo(to));

    private static void AdvanceController(HandPoseController controller, int frameCount, double deltaSeconds)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            controller.Update(deltaSeconds);
        }
    }

    private static AnimationNodeStateMachinePlayback ResolvePlayback(AnimationTree animationTree)
        => animationTree.Get(HandPoseAnimationTreePaths.GetNestedStateMachinePlaybackParameter()).As<AnimationNodeStateMachinePlayback>()
           ?? throw new InvalidOperationException("AnimationTree is missing the hand-pose upstream state machine playback.");

    private static int CountNodesNamed(AnimationNodeBlendTree tree, string nodeName)
    {
        int count = 0;
        foreach (StringName currentNodeName in tree.GetNodeList())
        {
            if (currentNodeName == new StringName(nodeName))
            {
                count++;
            }

            if (tree.GetNode(currentNodeName) is AnimationNodeBlendTree childTree)
            {
                count += CountNodesNamed(childTree, nodeName);
            }
        }

        return count;
    }
}
