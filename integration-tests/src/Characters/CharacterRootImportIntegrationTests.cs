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
    private const string ReferenceFemaleBlendPath = "res://assets/characters/reference/female/reference_female.blend";
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string AllyScenePath = "res://assets/characters/reference/ally.tscn";

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
            AssertCharacterRoot(root, "Female");
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>
    /// Shipped character scenes inherit the imported Character root and remain installable.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerScene_ResolvesRootAsCharacterBodyICharacterAndInstalls()
        => AssertShippedCharacterScene(PlayerScenePath, "Player");

    /// <summary>
    /// The shipped ally scene inherits the imported Character root and remains installable.
    /// </summary>
    [Headless]
    [Fact]
    public void AllyScene_ResolvesRootAsCharacterBodyICharacterAndInstalls()
        => AssertShippedCharacterScene(AllyScenePath, "Ally");

    private static void AssertShippedCharacterScene(string scenePath, string expectedName)
    {
        PackedScene scene = LoadPackedScene(scenePath);
        Node root = scene.Instantiate();

        try
        {
            AssertCharacterRoot(root, expectedName);
            Assert.NotNull(root.GetNodeOrNull("Female/GeneralSkeleton"));

            EnsureCharacterRuntimeInstalled(root);
        }
        finally
        {
            root.QueueFree();
        }
    }

    private static void AssertCharacterRoot(Node root, string expectedName)
    {
        Assert.Equal(expectedName, root.Name.ToString());
        _ = Assert.IsAssignableFrom<CharacterBody3D>(root);
        Assert.Equal(typeof(CharacterRoot).FullName, root.GetType().FullName);
        Assert.Contains(root.GetType().GetInterfaces(), type => type.FullName == typeof(ICharacter).FullName);

        Script script = Assert.IsType<Script>(root.GetScript().AsGodotObject(), exactMatch: false);
        Assert.Equal(CharacterScriptPath, script.ResourcePath);
    }
}
