using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// Real player-scene coverage for all-fours locomotion wiring.
/// </summary>
public sealed partial class PlayerLocomotionPlayerSceneIntegrationTests
{
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string AnimationTreeScenePath = "res://assets/characters/reference/female/animation_tree_player.tscn";
    private const string VrikNodePath = "VRIK";
    private const string PoseStateMachineNodePath = "VRIK/PoseStateMachine";
    private const string LocomotionNodePath = "Locomotion";
    private const string AnimationTreeNodePath = "AnimationTree";

    /// <summary>
    /// Verifies the shipped player scene wires locomotion to the pose state machine and authored all-fours animation states.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_WiresLocomotionToPoseStateMachineAndAnimationTree()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(PlayerScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected player scene to become current scene.");

        Node vrikNode = Assert.IsAssignableFrom<Node>(sceneRoot.GetNodeOrNull(VrikNodePath));
        Node poseStateMachineNode = Assert.IsAssignableFrom<Node>(sceneRoot.GetNodeOrNull(PoseStateMachineNodePath));
        Node locomotionNode = Assert.IsAssignableFrom<Node>(sceneRoot.GetNodeOrNull(LocomotionNodePath));
        AnimationTree animationTree = Assert.IsAssignableFrom<AnimationTree>(sceneRoot.GetNodeOrNull(AnimationTreeNodePath));

        Assert.Equal("AlleyCat.IK.PlayerVRIK", vrikNode.GetType().FullName);
        Assert.Equal("AlleyCat.IK.Pose.PoseStateMachine", poseStateMachineNode.GetType().FullName);
        Assert.Equal("AlleyCat.Control.PlayerLocomotion", locomotionNode.GetType().FullName);

        Godot.Collections.Array permissionSources = locomotionNode.Get("PermissionSourceNodes").AsGodotArray();
        Variant permissionSource = Assert.Single(permissionSources);
        Assert.Same(poseStateMachineNode, permissionSource.Obj);

        Variant animationTreeReference = poseStateMachineNode.Get("AnimationTree");
        Assert.True(animationTreeReference.Obj is AnimationTree);
        Assert.Same(animationTree, animationTreeReference.Obj);

        PackedScene animationTreeScene = LoadPackedScene(AnimationTreeScenePath);
        AnimationTree authoredAnimationTree = Assert.IsAssignableFrom<AnimationTree>(animationTreeScene.Instantiate());

        try
        {
            AnimationNodeStateMachine stateMachine = Assert.IsType<AnimationNodeStateMachine>(authoredAnimationTree.TreeRoot, exactMatch: false);
            Assert.NotNull(stateMachine.GetNode("AllFours"));
            Assert.NotNull(stateMachine.GetNode("AllFoursForward"));
            Assert.NotNull(stateMachine.GetNode("AllFoursTransitioning"));
        }
        finally
        {
            authoredAnimationTree.QueueFree();
        }
    }
}
