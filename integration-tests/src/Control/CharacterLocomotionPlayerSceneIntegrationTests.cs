using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Control;

/// <summary>Production player-scene composition coverage with no authored animation-content oracle.</summary>
public sealed partial class CharacterLocomotionPlayerSceneIntegrationTests
{
    private const string BrokenCharacterSceneFileName = "reference_female" + "_character.tscn";
    private const string BrokenVisualSceneFileName = "reference_female" + "_visual.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";
    private const string ReferenceFemaleScenePath = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string PlayerInstallerScenePath = "res://assets/characters/templates/installers/player_installer.tscn";
    private const string CharacterLocomotionScriptPath = "res://src/Control/Locomotion/CharacterLocomotion.cs";

    [Headless]
    [Fact]
    public void PlayerScene_InstallsTemplatePlayerAndOverridesVRIKLocomotionPermissionSource()
    {
        string playerSceneText = ReadResourceText(PlayerScenePath);
        Assert.Contains("[node name=\"Player\"", playerSceneText);
        Assert.Contains(PlayerInstallerScenePath, playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("RigRoleTemplateSceneInstaller.cs", playerSceneText, StringComparison.Ordinal);
        Assert.Contains(ReferenceFemaleScenePath, playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain(BrokenCharacterSceneFileName, playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain(BrokenVisualSceneFileName, playerSceneText, StringComparison.Ordinal);

        PackedScene playerScene = LoadPackedScene(PlayerScenePath);
        Node sceneRoot = playerScene.Instantiate();
        try
        {
            object result = InvokeLoadedInstaller(sceneRoot.GetNode("PlayerCharacterInstaller"), sceneRoot);
            AssertLoadedInstallSucceeded(result);

            Node locomotion = AssertCharacterLocomotionNode(sceneRoot.GetNodeOrNull("Locomotion"));
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

    [Headless]
    [Fact]
    public async Task PlayerScene_RuntimeInstantiation_ResolvesRequiredCompositionNodes()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);
        Assert.Equal(Error.Ok, sceneTree.ChangeSceneToPacked(LoadPackedScene(PlayerScenePath)));
        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected player scene to become current scene.");
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("PlayerController"), exactMatch: false);
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("Locomotion"), exactMatch: false);
        _ = Assert.IsType<AnimationTree>(sceneRoot.GetNodeOrNull("AnimationTree"), exactMatch: false);
        _ = Assert.IsType<Node>(sceneRoot.GetNodeOrNull("VRIK/PoseStateMachine"), exactMatch: false);
        _ = Assert.IsType<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull("Female/GeneralSkeleton/HipReconciliationModifier"), exactMatch: false);
    }

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
        bool succeeded = (bool)(result.GetType().GetProperty(nameof(SceneInstallationResult.Succeeded))?.GetValue(result) ?? false);
        object? errors = result.GetType().GetProperty(nameof(SceneInstallationResult.Errors))?.GetValue(result);
        string errorText = errors is IEnumerable<string> typedErrors ? string.Join('\n', typedErrors) : errors?.ToString() ?? string.Empty;
        Assert.True(succeeded, errorText);
    }

    private static Node AssertCharacterLocomotionNode(Node? node)
    {
        Node locomotion = Assert.IsType<Node>(node, exactMatch: false);
        Script script = Assert.IsType<Script>(locomotion.GetScript().AsGodotObject(), exactMatch: false);
        Assert.Equal(CharacterLocomotionScriptPath, script.ResourcePath);
        return locomotion;
    }

    private static Node?[] ReadPermissionSourceNodes(Node node)
        => node.GetType().GetProperty(nameof(LocomotionBase.PermissionSourceNodes))?.GetValue(node) as Node?[]
            ?? throw new Xunit.Sdk.XunitException($"Expected node '{node.GetPath()}' to expose permission source nodes.");

    private static string ReadResourceText(string path)
    {
        string text = Godot.FileAccess.GetFileAsString(path);
        return !string.IsNullOrEmpty(text)
            ? text
            : throw new Xunit.Sdk.XunitException($"Expected text resource '{path}' to be readable.");
    }
}
