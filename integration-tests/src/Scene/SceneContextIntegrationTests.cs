using AlleyCat.Character;
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
            Id = "live-first",
        };
        var secondCharacter = new CharacterHub
        {
            Name = "LiveSecondActor",
            Id = "live-second",
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
            firstCharacter.Id = "first-mutated";

            ISceneContext updatedContext = provider.GetCurrent();

            Assert.Equal(baselineActorCount + 1, initialContext.Characters.Count);
            ICharacter initialCharacter = Assert.Single(initialContext.Characters, character => ReferenceEquals(character, firstCharacter));
            Assert.Equal("first-mutated", initialCharacter.Id);
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
            Id = "wrapped-first",
        };
        var secondCharacter = new CharacterHub
        {
            Name = "WrappedSecondActor",
            Id = "wrapped-second",
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
