using Godot;

namespace AlleyCat.Common;

public static class NodeExtensions
{
    public static IEnumerable<Node> GetDescendants(this Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;

            foreach (var descendant in child.GetDescendants())
                yield return descendant;
        }
    }
}