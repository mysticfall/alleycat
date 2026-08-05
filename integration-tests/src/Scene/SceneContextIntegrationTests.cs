using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core.Content;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// Godot-runtime coverage for scene context membership retrieval.
/// </summary>
public sealed class SceneContextIntegrationTests
{
    /// <summary>
    /// Shared character bases provide Actors discovery and character-card context to inherited role templates.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterRoleTemplates_InheritActorsMembershipAndCharacterCardContext()
    {
        string[] scenePaths =
        [
            "res://assets/characters/templates/reference_female/reference_female_player.tscn",
            "res://assets/characters/templates/reference_female/reference_female_npc.tscn",
            "res://assets/characters/templates/reference_male/reference_male_npc.tscn",
        ];
        foreach (string scenePath in scenePaths)
        {
            PackedScene packedScene = ResourceLoader.Load<PackedScene>(scenePath);
            CharacterHub character = Assert.IsType<CharacterHub>(packedScene.Instantiate(), exactMatch: false);

            try
            {
                Assert.True(character.IsInGroup("Actors"), $"{scenePath} should inherit Actors membership.");
                ContextSource source = Assert.Single(character.ContextSources);
                _ = Assert.IsType<CharacterCardContextSource>(source, exactMatch: false);
            }
            finally
            {
                character.QueueFree();
            }
        }
    }

    /// <summary>
    /// The scene context provider discovers character nodes from the live SceneTree group membership.
    /// </summary>
    [Headless]
    [Fact]
    public async Task GetCurrent_WhenCharactersAreInLiveActorsGroup_DiscoversCurrentSceneTreeCharacters()
    {
        SceneTree sceneTree = GetSceneTree();
        int baselineActorCount = sceneTree.GetNodesInGroup("Actors").Count;
        var firstCharacter = new CharacterHub
        {
            Name = "LiveFirstActor",
            Id = "live_first",
        };
        var secondCharacter = new CharacterHub
        {
            Name = "LiveSecondActor",
            Id = "live_second",
        };
        Node contextRoot = new()
        {
            Name = "SceneContextLiveActorsRoot",
        };

        contextRoot.AddChild(firstCharacter);
        contextRoot.AddChild(secondCharacter);
        firstCharacter.AddToGroup("Actors");
        secondCharacter.AddToGroup("Actors");
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, contextRoot);
        await WaitForFramesAsync(sceneTree, 10);

        try
        {
            Assert.True(firstCharacter.IsInsideTree(), "First live actor should be inside the SceneTree before querying groups.");
            Assert.True(firstCharacter.IsInGroup("Actors"), "First live actor should report Actors membership before provider query.");
            Assert.Equal(baselineActorCount + 2, sceneTree.GetNodesInGroup("Actors").Count);
            var provider = new SceneContextProvider(contextRoot);

            ISceneContext context = provider.GetCurrent();

            Assert.Equal(baselineActorCount + 2, context.Characters.Count);
            Assert.Contains(firstCharacter, context.Characters);
            Assert.Contains(secondCharacter, context.Characters);
            Assert.Same(firstCharacter, context.Find("char:live_first"));
            Assert.Null(context.Find("loc:interrogation_room"));
        }
        finally
        {
            firstCharacter.RemoveFromGroup("Actors");
            secondCharacter.RemoveFromGroup("Actors");
            contextRoot.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// The scene context provider captures group membership as a fixed collection while exposing live character references.
    /// </summary>
    [Headless]
    [Fact]
    public async Task GetCurrent_ReturnsFixedActorsGroupMembershipSnapshot_WithLiveCharacterReferences()
    {
        SceneTree sceneTree = GetSceneTree();
        int baselineActorCount = sceneTree.GetNodesInGroup("Actors").Count;
        var firstCharacter = new CharacterHub
        {
            Name = "FirstActor",
            Id = "first",
        };
        var secondCharacter = new CharacterHub
        {
            Name = "SecondActor",
            Id = "second",
        };
        Node contextRoot = new()
        {
            Name = "SceneContextSnapshotRoot",
        };

        contextRoot.AddChild(firstCharacter);
        contextRoot.AddChild(secondCharacter);
        firstCharacter.AddToGroup("Actors");
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, contextRoot);
        await WaitForFramesAsync(sceneTree, 10);

        try
        {
            var provider = new SceneContextProvider(contextRoot);

            ISceneContext initialContext = provider.GetCurrent();

            secondCharacter.AddToGroup("Actors");
            firstCharacter.Id = "first_mutated";

            ISceneContext updatedContext = provider.GetCurrent();

            Assert.Equal(baselineActorCount + 1, initialContext.Characters.Count);
            ICharacter initialCharacter = Assert.Single(initialContext.Characters, character => ReferenceEquals(character, firstCharacter));
            Assert.Equal("char:first_mutated", initialCharacter.FullId);
            Assert.DoesNotContain(secondCharacter, initialContext.Characters);
            Assert.Equal(baselineActorCount + 2, updatedContext.Characters.Count);
            Assert.Contains(firstCharacter, updatedContext.Characters);
            Assert.Contains(secondCharacter, updatedContext.Characters);
        }
        finally
        {
            firstCharacter.RemoveFromGroup("Actors");
            secondCharacter.RemoveFromGroup("Actors");
            contextRoot.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Scene context exposes its defensive membership snapshot through a read-only wrapper, not the copied array itself.
    /// </summary>
    [Headless]
    [Fact]
    public void Constructor_ExposesReadOnlyMembershipWrapper_AndPreservesSnapshotMembership()
    {
        var firstCharacter = new CharacterHub
        {
            Name = "WrappedFirstActor",
            Id = "wrapped_first",
        };
        var secondCharacter = new CharacterHub
        {
            Name = "WrappedSecondActor",
            Id = "wrapped_second",
        };
        var sourceCharacters = new List<ICharacter>
        {
            firstCharacter,
        };

        var context = new SceneContext(sourceCharacters);

        sourceCharacters.Add(secondCharacter);

        Assert.IsNotType<ICharacter[]>(context.Characters);
        ICollection<ICharacter> mutableInterface = Assert.IsAssignableFrom<ICollection<ICharacter>>(context.Characters);
        Assert.True(mutableInterface.IsReadOnly);
        _ = Assert.Throws<NotSupportedException>(() => mutableInterface.Add(secondCharacter));
        ICharacter snapshotCharacter = Assert.Single(context.Characters);
        Assert.Same(firstCharacter, snapshotCharacter);
        Assert.DoesNotContain(secondCharacter, context.Characters);
        Assert.Equal(ContentContext.Default, context.Content);
    }

    /// <summary>
    /// Scene contexts expose the current CORE content context for convenience without adding domain-specific paths.
    /// </summary>
    [Headless]
    [Fact]
    public void Constructor_ExposesSuppliedContentContext()
    {
        var content = ContentContext.ForPack("story-pack");

        SceneContext context = new([], content);

        Assert.Equal("story-pack", context.Content.ContentID);
        Assert.Equal("res://content/story-pack/", context.Content.RootPath);
    }

    /// <summary>
    /// Scene membership rejects identities that cannot safely key character context.
    /// </summary>
    [Headless]
    [Fact]
    public void Constructor_WhenCharacterIDIsBlank_ThrowsUsefulError()
    {
        foreach (string invalidID in new[] { string.Empty, "   " })
        {
            var character = new CharacterHub { Name = "InvalidIdentityCharacter", Id = invalidID };

            ArgumentException exception = Assert.Throws<ArgumentException>(() => new SceneContext([character]));

            Assert.Contains("non-empty lower snake_case", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(typeof(CharacterHub).FullName!, exception.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Exact duplicate canonical character identities fail at the runtime scene boundary.
    /// </summary>
    [Headless]
    [Fact]
    public void Constructor_RejectsDuplicateCanonicalCharacterIdentity()
    {
        var first = new CharacterHub { Name = "First", Id = "ally" };
        var duplicate = new CharacterHub { Name = "Duplicate", Id = "ally" };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new SceneContext([first, duplicate]));

        Assert.Contains("char:ally", exception.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate exact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Actors group is strict and rejects non-character nodes as authoring errors.
    /// </summary>
    [Headless]
    [Fact]
    public void GetCurrent_WhenActorsGroupContainsNonCharacterNode_ThrowsAuthoringError()
    {
        SceneTree sceneTree = GetSceneTree();
        Node invalidActor = sceneTree.Root;
        invalidActor.AddToGroup("Actors");

        try
        {
            var provider = new SceneContextProvider(sceneTree.Root);
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(provider.GetCurrent);

            Assert.Contains("Actors", ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(ICharacter), ex.Message, StringComparison.Ordinal);
            Assert.Contains(invalidActor.Name, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            invalidActor.RemoveFromGroup("Actors");
        }
    }
}
