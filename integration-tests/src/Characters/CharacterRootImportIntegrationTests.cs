using AlleyCat.Character;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

using CharacterRoot = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Characters;

/// <summary>
/// Integration coverage for imported character roots using Character.cs as the custom root script.
/// </summary>
public sealed class CharacterRootImportIntegrationTests
{
    private const string CharacterScriptPath = "res://src/Character/Character.cs";
    private const string PlayerCharacterScriptPath = "res://src/Character/PlayerCharacter.cs";
    private const string CharacterImportScriptPath = "res://assets/characters/import/character_import.gd";
    private const string CharacterEyeImportScriptPath = "res://assets/characters/import/character_eye_animation_import.gd";
    private const string CharacterColliderImportScriptPath = "res://assets/characters/import/character_collider_profile_import.gd";
    private const string LegacyEyeImportScriptPath = "res://assets/characters/import/eye_animation_library_import.gd";
    private const string ReferenceFemaleBlendPath = "res://assets/characters/reference/female/reference_female.blend";
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";
    private const string AllyScenePath = "res://assets/characters/reference/ally_npc.tscn";

    /// <summary>
    /// The imported source scene uses Character.cs directly on its CharacterBody3D root.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleBlend_InstantiatesAsCharacterBodyRootWithCharacterScript()
    {
        PackedScene scene = LoadPackedScene(ReferenceFemaleBlendPath);
        Node root = scene.Instantiate();

        try
        {
            AssertCharacterRoot(root, "Female", typeof(CharacterRoot), CharacterScriptPath, typeof(ICharacter));
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>
    /// Verifies the modular character post-import scripts and the legacy eye wrapper remain loadable by Godot.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterPostImportScripts_LoadAsGodotScripts()
    {
        AssertScriptLoads(CharacterImportScriptPath);
        AssertScriptLoads(CharacterEyeImportScriptPath);
        AssertScriptLoads(CharacterColliderImportScriptPath);
        AssertScriptLoads(LegacyEyeImportScriptPath);
    }

    /// <summary>
    /// Shipped character scenes inherit the imported Character root and remain installable.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerScene_ResolvesRootAsCharacterBodyICharacterAndInstalls()
        => AssertShippedCharacterScene(
            PlayerScenePath,
            "Player",
            typeof(PlayerCharacter),
            PlayerCharacterScriptPath,
            typeof(IPlayer));

    /// <summary>
    /// The shipped ally scene inherits the imported Character root and remains installable.
    /// </summary>
    [Headless]
    [Fact]
    public void AllyScene_ResolvesRootAsCharacterBodyICharacterAndInstalls()
        => AssertShippedCharacterScene(
            AllyScenePath,
            "Ally",
            typeof(CharacterRoot),
            CharacterScriptPath,
            typeof(ICharacter));

    private static void AssertShippedCharacterScene(
        string scenePath,
        string expectedName,
        Type expectedType,
        string expectedScriptPath,
        Type expectedInterface)
    {
        PackedScene scene = LoadPackedScene(scenePath);
        Node root = scene.Instantiate();

        try
        {
            AssertCharacterRoot(root, expectedName, expectedType, expectedScriptPath, expectedInterface);
            Assert.NotNull(root.GetNodeOrNull("Female/GeneralSkeleton"));

            EnsureCharacterRuntimeInstalled(root);
        }
        finally
        {
            root.QueueFree();
        }
    }

    private static void AssertCharacterRoot(
        Node root,
        string expectedName,
        Type expectedType,
        string expectedScriptPath,
        Type expectedInterface)
    {
        Assert.Equal(expectedName, root.Name.ToString());
        _ = Assert.IsAssignableFrom<CharacterBody3D>(root);
        Assert.Equal(expectedType.FullName, root.GetType().FullName);
        Assert.Contains(root.GetType().GetInterfaces(), type => type.FullName == expectedInterface.FullName);

        Script script = Assert.IsType<Script>(root.GetScript().AsGodotObject(), exactMatch: false);
        Assert.Equal(expectedScriptPath, script.ResourcePath);
    }

    private static void AssertScriptLoads(string scriptPath)
    {
        Script script = Assert.IsType<Script>(ResourceLoader.Load(scriptPath), exactMatch: false);
        Assert.Equal(scriptPath, script.ResourcePath);
    }
}
