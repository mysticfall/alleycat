using AlleyCat.Character;
using Godot;

namespace AlleyCat.Scene;

/// <summary>
/// Builds scene contexts from the live Godot scene tree.
/// </summary>
/// <param name="treeOwner">Node used to resolve the current scene tree.</param>
public sealed class SceneContextProvider(Node treeOwner) : ISceneContextProvider
{
    private static readonly StringName _actorsGroup = "Actors";

    /// <inheritdoc />
    public ISceneContext GetCurrent()
    {
        Godot.Collections.Array<Node> actorNodes = GetActorNodes();
        var characters = new ICharacter[actorNodes.Count];

        for (int i = 0; i < actorNodes.Count; i++)
        {
            Node actorNode = actorNodes[i];
            characters[i] = actorNode as ICharacter
                ?? throw new InvalidOperationException(
                    $"Scene authoring error: node '{actorNode.Name}' ({actorNode.GetPath()}) is in the Actors group but does not implement {typeof(ICharacter).FullName}.");
        }

        return new SceneContext(characters);
    }

    private Godot.Collections.Array<Node> GetActorNodes()
    {
        SceneTree sceneTree = treeOwner.GetTree()
            ?? throw new InvalidOperationException(
                $"Cannot resolve scene context because node '{treeOwner.Name}' is not inside a SceneTree.");

        return sceneTree.GetNodesInGroup(_actorsGroup);
    }
}
