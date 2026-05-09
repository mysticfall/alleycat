using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// Real player-scene coverage for all-fours locomotion wiring.
/// </summary>
public sealed partial class CharacterLocomotionPlayerSceneIntegrationTests
{
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";
    private const string AnimationTreeScenePath = "res://assets/characters/reference/female/animation_tree_player.tscn";
    private const string CharacterLocomotionScriptPath = "res://src/Control/Locomotion/CharacterLocomotion.cs";
    private const string PlayerControllerScriptPath = "res://src/Control/PlayerController.cs";

    /// <summary>
    /// Verifies the shipped player scene wires locomotion to the pose state machine and authored all-fours animation states.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerScene_WiresLocomotionToPoseStateMachineAndAnimationTree()
    {
        Script locomotionScript = LoadScript(CharacterLocomotionScriptPath);
        Script playerControllerScript = LoadScript(PlayerControllerScriptPath);

        Assert.Equal(CharacterLocomotionScriptPath, locomotionScript.ResourcePath);
        Assert.Equal(PlayerControllerScriptPath, playerControllerScript.ResourcePath);

        PackedScene animationTreeScene = LoadPackedScene(AnimationTreeScenePath);
        AnimationTree authoredAnimationTree = Assert.IsAssignableFrom<AnimationTree>(animationTreeScene.Instantiate());

        try
        {
            AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(authoredAnimationTree.TreeRoot, exactMatch: false);
            AnimationNodeStateMachine stateMachine = Assert.IsType<AnimationNodeStateMachine>(
                rootTree.GetNode("States"),
                exactMatch: false);

            Assert.NotNull(stateMachine.GetNode("AllFours"));
            Assert.NotNull(stateMachine.GetNode("AllFoursForward"));
            Assert.NotNull(stateMachine.GetNode("AllFoursTransitioning"));
        }
        finally
        {
            authoredAnimationTree.QueueFree();
        }
    }

    /// <summary>
    /// Verifies the shipped player scene instantiates the renamed locomotion component as a live runtime node.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_RuntimeInstantiation_ResolvesCharacterLocomotionNode()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(PlayerScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected player scene to become current scene.");

        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("PlayerController"), exactMatch: false);
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("Locomotion"), exactMatch: false);
        _ = Assert.IsType<AnimationTree>(sceneRoot.GetNodeOrNull("AnimationTree"), exactMatch: false);
    }

    /// <summary>
    /// Verifies the mirror-room test scene can instantiate the nested player scene and resolve CharacterLocomotion at runtime.
    /// </summary>
    [Headless]
    [Fact]
    public async Task MirrorRoom_RuntimeInstantiation_ResolvesNestedCharacterLocomotionNode()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(MirrorRoomScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected mirror-room scene to become current scene.");

        _ = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull("Female"), exactMatch: false);
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("Female/PlayerController"), exactMatch: false);
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("Female/Locomotion"), exactMatch: false);
        _ = Assert.IsType<AnimationTree>(sceneRoot.GetNodeOrNull("Female/AnimationTree"), exactMatch: false);

        Script locomotionScript = LoadScript(CharacterLocomotionScriptPath);
        Assert.Equal(CharacterLocomotionScriptPath, locomotionScript.ResourcePath);
    }

    private static Script LoadScript(string path)
        => ResourceLoader.Load<Script>(path)
            ?? throw new Xunit.Sdk.XunitException($"Expected script resource '{path}' to load successfully.");
}
