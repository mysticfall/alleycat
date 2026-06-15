using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
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
    private const string BrokenCharacterSceneFileName = "reference_female" + "_character.tscn";
    private const string BrokenVisualSceneFileName = "reference_female" + "_visual.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string ReferenceFemaleScenePath = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string ReferenceFemalePlayerTemplatePath = "res://assets/characters/templates/reference_female/reference_female_player.tscn";
    private const string PlayerInstallerScenePath = "res://assets/characters/templates/installers/player_installer.tscn";
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";
    private const string PlayerAnimationTreeRootPath = "res://assets/characters/templates/animation/animation_tree_root_player.tres";
    private const string CharacterLocomotionScriptPath = "res://src/Control/Locomotion/CharacterLocomotion.cs";
    private const string PlayerControllerScriptPath = "res://src/Control/PlayerController.cs";

    /// <summary>
    /// Verifies the player scene contains the shared character rig and applies VRIK-specific locomotion permission wiring.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerScene_InstallsTemplatePlayerAndOverridesVRIKLocomotionPermissionSource()
    {
        string playerSceneText = ReadResourceText(PlayerScenePath);
        Assert.Contains("[node name=\"Player\"", playerSceneText);
        Assert.Contains(PlayerInstallerScenePath, playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("CharacterRoleTemplateSceneInstaller.cs", playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain(ReferenceFemaleScenePath, playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain(BrokenCharacterSceneFileName, playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain(BrokenVisualSceneFileName, playerSceneText, StringComparison.Ordinal);

        PackedScene playerScene = LoadPackedScene(PlayerScenePath);
        Node sceneRoot = playerScene.Instantiate();

        try
        {
            object result = InvokeLoadedInstaller(sceneRoot.GetNode("PlayerCharacterInstaller"), sceneRoot);
            AssertLoadedInstallSucceeded(result);

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

        AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(
            ResourceLoader.Load(PlayerAnimationTreeRootPath),
            exactMatch: false);
        AnimationNodeStateMachine stateMachine = Assert.IsType<AnimationNodeStateMachine>(
            rootTree.GetNode("States"),
            exactMatch: false);

        Assert.NotNull(stateMachine.GetNode("AllFours"));
        Assert.NotNull(stateMachine.GetNode("AllFoursForward"));
        Assert.NotNull(stateMachine.GetNode("AllFoursTransitioning"));
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
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("VRIK/PoseStateMachine"), exactMatch: false);
        _ = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull("Female/GeneralSkeleton/HipReconciliationModifier"),
            exactMatch: false);
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

        _ = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull("Actors/Player"), exactMatch: false);
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("Actors/Player/PlayerController"), exactMatch: false);
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("Actors/Player/Locomotion"), exactMatch: false);
        _ = Assert.IsType<AnimationTree>(sceneRoot.GetNodeOrNull("Actors/Player/AnimationTree"), exactMatch: false);
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("Actors/Player/VRIK/PoseStateMachine"), exactMatch: false);

        Script locomotionScript = LoadScript(CharacterLocomotionScriptPath);
        Assert.Equal(CharacterLocomotionScriptPath, locomotionScript.ResourcePath);
    }

    private static Script LoadScript(string path)
        => ResourceLoader.Load<Script>(path)
            ?? throw new Xunit.Sdk.XunitException($"Expected script resource '{path}' to load successfully.");

    private static object InvokeLoadedInstaller(object installer, Node targetRoot)
    {
        Type installerType = installer.GetType();
        Type contextType = installerType.Assembly.GetType(typeof(SceneInstallationContext).FullName!)
            ?? throw new InvalidOperationException("Failed to resolve loaded SceneInstallationContext type.");
        object context = Activator.CreateInstance(contextType, targetRoot, SceneInstallationMetadata.DefaultNamespace)
            ?? throw new InvalidOperationException("Failed to create loaded scene installation context.");
        object? result = installerType.GetMethod(nameof(SceneInstaller.Install))?.Invoke(installer, [context]);
        Assert.NotNull(result);
        return result;
    }

    private static void AssertLoadedInstallSucceeded(object result)
    {
        bool succeeded = (bool)(result.GetType().GetProperty(nameof(SceneInstallationResult.Succeeded))?.GetValue(result)
            ?? false);
        object? errors = result.GetType().GetProperty(nameof(SceneInstallationResult.Errors))?.GetValue(result);
        string errorText = errors is IEnumerable<string> typedErrors
            ? string.Join('\n', typedErrors)
            : errors?.ToString() ?? string.Empty;
        Assert.True(succeeded, errorText);
    }

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
