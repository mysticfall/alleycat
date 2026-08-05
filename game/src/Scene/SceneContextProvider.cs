using System.Collections.ObjectModel;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using Godot;

namespace AlleyCat.Scene;

/// <summary>
/// Builds scene contexts from the live Godot scene tree.
/// </summary>
/// <param name="treeOwner">Node used to resolve the current scene tree.</param>
/// <param name="contentResolver">CORE content resolver used to expose active content context.</param>
public sealed class SceneContextProvider(Node treeOwner, IContentResolver? contentResolver = null) : ISceneContextProvider
{
    private static readonly IReadOnlyDictionary<string, SceneGroupDefinition> _sceneGroupsByType =
        new ReadOnlyDictionary<string, SceneGroupDefinition>(new Dictionary<string, SceneGroupDefinition>(StringComparer.Ordinal)
        {
            ["char"] = new("Actors", static node => node as ICharacter),
        });

    /// <inheritdoc />
    public ISceneContext GetCurrent()
    {
        SceneTree sceneTree = GetSceneTree();
        var membershipByType = new Dictionary<string, IIdentifiable[]>(_sceneGroupsByType.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, SceneGroupDefinition> entry in _sceneGroupsByType)
        {
            Godot.Collections.Array<Node> nodes = sceneTree.GetNodesInGroup(entry.Value.Group);
            var identifiables = new IIdentifiable[nodes.Count];
            for (int index = 0; index < nodes.Count; index++)
            {
                Node node = nodes[index];
                identifiables[index] = entry.Value.ResolveIdentifiable(node)
                    ?? throw new InvalidOperationException(
                        $"Scene authoring error: node '{node.Name}' ({node.GetPath()}) is in the {entry.Value.Group} group but does not implement {typeof(ICharacter).FullName}.");
            }

            membershipByType.Add(entry.Key, identifiables);
        }

        return new SceneContext(membershipByType, (contentResolver ?? new ContentResolver()).GetCurrentContentContext());
    }

    private SceneTree GetSceneTree()
    {
        SceneTree sceneTree = treeOwner.GetTree()
            ?? throw new InvalidOperationException(
                $"Cannot resolve scene context because node '{treeOwner.Name}' is not inside a SceneTree.");

        return sceneTree;
    }

    private sealed record SceneGroupDefinition(StringName Group, Func<Node, IIdentifiable?> ResolveIdentifiable);
}
