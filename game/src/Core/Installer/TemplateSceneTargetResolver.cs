using Godot;

namespace AlleyCat.Core.Installer;

/// <summary>
/// Resolves nodes within an installation target that correspond to nodes authored in a template scene.
/// </summary>
internal static class TemplateSceneTargetResolver
{
    /// <summary>
    /// Resolves the target node equivalent to the supplied template node. Equivalent nodes are found by the
    /// template-relative path first; when that path is absent, a unique skeleton-bearing target root child (for
    /// single-segment paths) or a unique suffix match across target root children (for deeper paths) is accepted
    /// instead. Returns <c>null</c> when no unique equivalent exists.
    /// </summary>
    /// <param name="templateNode">The template node to resolve an equivalent for.</param>
    /// <param name="templateRoot">The root of the instantiated template the node belongs to.</param>
    /// <param name="targetRoot">The root of the installation target to resolve within.</param>
    /// <returns>The equivalent target node, or <c>null</c> when no unique equivalent exists.</returns>
    internal static Node? ResolveEquivalent(Node templateNode, Node templateRoot, Node targetRoot)
    {
        ArgumentNullException.ThrowIfNull(templateNode);
        ArgumentNullException.ThrowIfNull(templateRoot);
        ArgumentNullException.ThrowIfNull(targetRoot);

        if (ReferenceEquals(templateNode, templateRoot))
        {
            return targetRoot;
        }

        NodePath relativePath = templateRoot.GetPathTo(templateNode);
        return targetRoot.GetNodeOrNull(relativePath)
            ?? ResolveViaEquivalentTargetRootChild(targetRoot, relativePath);
    }

    private static Node? ResolveViaEquivalentTargetRootChild(Node targetRoot, NodePath relativePath)
    {
        string pathText = relativePath.ToString();
        int separatorIndex = pathText.IndexOf('/');
        if (separatorIndex < 0)
        {
            return ResolveUniqueSkeletonBearingRootChild(targetRoot);
        }

        if (separatorIndex == pathText.Length - 1)
        {
            return null;
        }

        var suffixPath = new NodePath(pathText[(separatorIndex + 1)..]);
        Node? uniqueCandidate = null;
        foreach (Node targetChild in targetRoot.GetChildren())
        {
            Node? candidate = targetChild.GetNodeOrNull(suffixPath);
            if (candidate is null)
            {
                continue;
            }

            if (uniqueCandidate is not null)
            {
                return null;
            }

            uniqueCandidate = candidate;
        }

        return uniqueCandidate;
    }

    private static Node? ResolveUniqueSkeletonBearingRootChild(Node targetRoot)
    {
        Node? uniqueCandidate = null;
        foreach (Node targetChild in targetRoot.GetChildren())
        {
            if (!HasSkeletonDescendant(targetChild))
            {
                continue;
            }

            if (uniqueCandidate is not null)
            {
                return null;
            }

            uniqueCandidate = targetChild;
        }

        return uniqueCandidate;
    }

    private static bool HasSkeletonDescendant(Node node)
    {
        if (node is Skeleton3D)
        {
            return true;
        }

        foreach (Node child in node.GetChildren())
        {
            if (HasSkeletonDescendant(child))
            {
                return true;
            }
        }

        return false;
    }
}
