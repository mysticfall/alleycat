using AlleyCat.Body;
using AlleyCat.Body.Hands;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Body.Hands;

/// <summary>
/// Integration coverage for BODY-001 Hands reference tree resources and hand-pose controller behaviour.
/// </summary>
public sealed class HandPoseBlendTreeIntegrationTests
{
    private const string PoseStateMachineTreePath = "res://assets/characters/reference/female/animation_tree_root_player.tres";
    private const string NpcAnimationTreeRootPath = "res://assets/characters/reference/female/animation_tree_root_npc.tres";
    private const string AnimationTreeScenePath = "res://assets/characters/reference/female/animation_tree_player.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string StandingPoseStatePath = "res://assets/characters/ik/pose/standing_pose_state.tres";
    private const string ResetAnimationPath = "res://assets/characters/reference/female/animations/Reset.tres";
    private const string GrabBallAnimationPath = "res://assets/characters/reference/female/animations/Grab-ball-40.tres";
    private const string PoseStateMachineTreeUID = "uid://bge48ng374i85";
    private const string NpcAnimationTreeRootUID = "uid://c485owf86etdu";
    private const string AnimationTreeSceneUID = "uid://djeh2d5hkxyoj";

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
        AssertConnection(root, "output", 0, HandPoseAnimationTreePaths.RightHandBlendNode);
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
        Assert.DoesNotContain("TimeSeek", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("seek_request", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("AllFours", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("VRIK", contents, StringComparison.Ordinal);

        _ = Assert.IsType<AnimationNodeStateMachine>(root.GetNode(HandPoseAnimationTreePaths.UpstreamNode), exactMatch: false);
        AssertHandBlendFilters(root, LimbSide.Left, HandPoseAnimationTreePaths.LeftHandBlendNode);
        AssertHandBlendFilters(root, LimbSide.Right, HandPoseAnimationTreePaths.RightHandBlendNode);
        AssertConnection(root, HandPoseAnimationTreePaths.LeftHandBlendNode, 0, HandPoseAnimationTreePaths.UpstreamNode);
        AssertConnection(root, HandPoseAnimationTreePaths.LeftHandBlendNode, 1, HandPoseAnimationTreePaths.LeftHandPoseNode);
        AssertConnection(root, HandPoseAnimationTreePaths.RightHandBlendNode, 0, HandPoseAnimationTreePaths.LeftHandBlendNode);
        AssertConnection(root, HandPoseAnimationTreePaths.RightHandBlendNode, 1, HandPoseAnimationTreePaths.RightHandPoseNode);
        AssertConnection(root, "output", 0, HandPoseAnimationTreePaths.RightHandBlendNode);
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
            AnimationTreeScenePath,
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
        Resource reset = ResourceLoader.Load(ResetAnimationPath);
        Resource grabBall = ResourceLoader.Load(GrabBallAnimationPath);
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
        Assert.InRange(weightedHalfwayBlend, 0.18f, 0.22f);
        controller.Update(0.1);
        Assert.InRange(tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle(), 0.39f, 0.41f);

        controller.ClearHandPose(LimbSide.Left, immediate: true);

        Assert.Null(controller.CurrentLeftHandPose);
        Assert.Equal(0f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Left)).AsSingle());
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
        _ = Assert.IsType<PackedScene>(ResourceLoader.Load(AnimationTreeSceneUID), exactMatch: false);
    }

    private static void AssertHandBlendFilters(AnimationNodeBlendTree root, LimbSide side, string nodeName)
    {
        AnimationNodeBlend2 blend = Assert.IsType<AnimationNodeBlend2>(root.GetNode(nodeName), exactMatch: false);
        Assert.True(blend.FilterEnabled);

        foreach (NodePath filterPath in HandPoseAnimationTreePaths.GetFingerFilterPaths(side))
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
