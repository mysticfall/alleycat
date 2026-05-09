using AlleyCat.Control.Locomotion;
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
    private const string BaseScenePath = "res://assets/characters/reference/female/reference_female_base.tscn";
    private const string BaseSceneUid = "uid://e765iqdvmnjd";
    private const string ReferenceFemaleScenePath = "res://assets/characters/reference/female/reference_female.tscn";
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";
    private const string AnimationTreeScenePath = "res://assets/characters/reference/female/animation_tree_player.tscn";
    private const string CharacterLocomotionScriptPath = "res://src/Control/Locomotion/CharacterLocomotion.cs";
    private const string PlayerControllerScriptPath = "res://src/Control/PlayerController.cs";

    /// <summary>
    /// Verifies the shared reference base scene owns non-player locomotion and physical-rig wiring only.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleBaseScene_InstantiatesSharedCharacterRigWithoutPlayerOnlyNodes()
    {
        Resource baseSceneByUid = ResourceLoader.Load(BaseSceneUid)
            ?? throw new Xunit.Sdk.XunitException($"Expected '{BaseSceneUid}' to resolve successfully.");
        Assert.Equal(BaseScenePath, baseSceneByUid.ResourcePath);

        string baseSceneText = ReadResourceText(BaseScenePath);
        Assert.Contains($"path=\"{ReferenceFemaleScenePath}\"", baseSceneText);
        Assert.Contains("[node name=\"Female\"", baseSceneText);
        Assert.Contains("instance=ExtResource(\"1_yqj8o\")", baseSceneText);

        PackedScene baseScene = LoadPackedScene(BaseScenePath);
        Node sceneRoot = baseScene.Instantiate();

        try
        {
            _ = Assert.IsType<Node>(
                sceneRoot.GetNodeOrNull("Female_export/GeneralSkeleton/DynamicPhysicalRig"),
                exactMatch: false);
            Node locomotion = AssertCharacterLocomotionNode(sceneRoot.GetNodeOrNull("Locomotion"));
            _ = Assert.IsType<AnimationTree>(sceneRoot.GetNodeOrNull("AnimationTree"), exactMatch: false);

            Assert.Equal(TurnMode.Smooth.ToString(), ReadProperty(locomotion, nameof(CharacterLocomotion.TurnMode))?.ToString());
            Assert.Empty(ReadPermissionSourceNodes(locomotion));
            Assert.Null(sceneRoot.GetNodeOrNull("VRIK"));
            Assert.Null(sceneRoot.GetNodeOrNull("PlayerController"));
            Assert.Null(sceneRoot.GetNodeOrNull("OpenAITranscriber"));
            Assert.Null(sceneRoot.GetNodeOrNull("Female_export/GeneralSkeleton/HipReconciliationModifier"));
        }
        finally
        {
            sceneRoot.QueueFree();
        }
    }

    /// <summary>
    /// Verifies the player scene inherits the shared base and applies VRIK-specific locomotion permission wiring.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerScene_InheritsBaseSceneAndOverridesVRIKLocomotionPermissionSource()
    {
        string playerSceneText = ReadResourceText(PlayerScenePath);
        Assert.Contains($"uid=\"{BaseSceneUid}\" path=\"{BaseScenePath}\"", playerSceneText);
        Assert.Contains("[node name=\"Player\"", playerSceneText);
        Assert.Contains("instance=ExtResource(\"1_edvvb\")", playerSceneText);

        PackedScene playerScene = LoadPackedScene(PlayerScenePath);
        Node sceneRoot = playerScene.Instantiate();

        try
        {
            Node locomotion = AssertCharacterLocomotionNode(sceneRoot.GetNodeOrNull("Locomotion"));

            Assert.Equal(TurnMode.Smooth.ToString(), ReadProperty(locomotion, nameof(CharacterLocomotion.TurnMode))?.ToString());
            Node?[] permissionSourceNodes = ReadPermissionSourceNodes(locomotion);
            Node permissionSourceNode = Assert.Single(permissionSourceNodes)
                ?? throw new Xunit.Sdk.XunitException("Expected player locomotion permission source to resolve to a node.");

            Assert.Equal("../VRIK/PoseStateMachine", locomotion.GetPathTo(permissionSourceNode).ToString());
        }
        finally
        {
            sceneRoot.QueueFree();
        }
    }

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

    private static Node AssertCharacterLocomotionNode(Node? node)
    {
        Node locomotion = Assert.IsType<Node>(node, exactMatch: false);
        Script script = Assert.IsType<Script>(locomotion.GetScript().AsGodotObject(), exactMatch: false);
        Assert.Equal(CharacterLocomotionScriptPath, script.ResourcePath);
        return locomotion;
    }

    private static object? ReadProperty(Node node, string propertyName)
        => node.GetType().GetProperty(propertyName)?.GetValue(node)
            ?? throw new Xunit.Sdk.XunitException($"Expected node '{node.GetPath()}' to expose property '{propertyName}'.");

    private static Node?[] ReadPermissionSourceNodes(Node node)
        => ReadProperty(node, nameof(LocomotionBase.PermissionSourceNodes)) as Node?[]
            ?? throw new Xunit.Sdk.XunitException(
                $"Expected node '{node.GetPath()}' to expose {nameof(LocomotionBase.PermissionSourceNodes)} as a node array.");

    private static string ReadResourceText(string path)
    {
        string text = Godot.FileAccess.GetFileAsString(path);
        return !string.IsNullOrEmpty(text)
            ? text
            : throw new Xunit.Sdk.XunitException($"Expected text resource '{path}' to be readable.");
    }
}
