using Godot;

namespace AlleyCat.Core.Installer;

/// <summary>
/// Shared implementation for installers that copy authored template-context content into a target scene.
/// </summary>
public static class TemplateSceneInstallation
{
    /// <summary>
    /// Installs template content under the supplied parent while preserving bake ownership and idempotency.
    /// </summary>
    /// <param name="installer">The installer requesting the operation.</param>
    /// <param name="context">The scene installation context.</param>
    /// <param name="targetParent">The parent that receives template content.</param>
    /// <param name="installMode">Which part of the template instance should be installed.</param>
    /// <param name="sourcePath">Optional path within the template source for selected-subtree install modes.</param>
    /// <returns>The installation result.</returns>
    public static SceneInstallationResult Install(
        ISceneInstaller installer,
        TemplateSceneInstallationContext context,
        Node? targetParent,
        TemplateInstallMode installMode,
        NodePath? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            if (targetParent is null || !GodotObject.IsInstanceValid(targetParent))
            {
                return SceneInstallationResult.Failed(
                    $"Template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' could not resolve a valid target parent.");
            }

            Node templateRoot = context.TemplateRoot;
            switch (installMode)
            {
                case TemplateInstallMode.TemplateRoot:
                    InstallNodes(targetParent, [(templateRoot, DuplicateInstallCandidate(templateRoot))], context, installer);
                    break;
                case TemplateInstallMode.TemplateRootChildren:
                    InstallRootChildren(targetParent, templateRoot, context, installer);
                    break;
                case TemplateInstallMode.SelectedNode:
                    InstallSelectedNode(targetParent, templateRoot, context, installer, sourcePath);
                    break;
                case TemplateInstallMode.SelectedNodeChildren:
                    InstallSelectedNodeChildren(targetParent, templateRoot, context, installer, sourcePath);
                    break;
                default:
                    return SceneInstallationResult.Failed(
                        $"Template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' has unsupported install mode '{installMode}'.");
            }

            return SceneInstallationResult.Successful();
        }
        catch (InvalidOperationException ex)
        {
            return SceneInstallationResult.Failed(ex.Message);
        }
    }

    private static void InstallRootChildren(
        Node targetParent,
        Node templateRoot,
        TemplateSceneInstallationContext context,
        ISceneInstaller installer)
    {
        List<(Node Source, Node Candidate)> templateChildren = [];
        foreach (Node child in templateRoot.GetChildren())
        {
            if (HasBaselineEquivalent(child, context))
            {
                continue;
            }

            templateChildren.Add((child, DuplicateInstallCandidate(child)));
        }

        InstallNodes(targetParent, templateChildren, context, installer);
    }

    private static void InstallSelectedNode(
        Node targetParent,
        Node templateRoot,
        TemplateSceneInstallationContext context,
        ISceneInstaller installer,
        NodePath? sourcePath)
    {
        Node selectedNode = ResolveSelectedSource(templateRoot, installer, sourcePath);
        InstallNodes(targetParent, [(selectedNode, DuplicateInstallCandidate(selectedNode))], context, installer);
    }

    private static void InstallSelectedNodeChildren(
        Node targetParent,
        Node templateRoot,
        TemplateSceneInstallationContext context,
        ISceneInstaller installer,
        NodePath? sourcePath)
    {
        Node selectedNode = ResolveSelectedSource(templateRoot, installer, sourcePath);
        List<(Node Source, Node Candidate)> selectedChildren = [];
        foreach (Node child in selectedNode.GetChildren())
        {
            if (HasBaselineEquivalent(child, context))
            {
                continue;
            }

            selectedChildren.Add((child, DuplicateInstallCandidate(child)));
        }

        InstallNodes(targetParent, selectedChildren, context, installer);
    }

    private static bool HasBaselineEquivalent(Node templateNode, TemplateSceneInstallationContext context)
    {
        Node? baselineRoot = context.TemplateBaselineRoot;
        if (baselineRoot is null || !GodotObject.IsInstanceValid(baselineRoot))
        {
            return false;
        }

        NodePath relativePath = context.TemplateRoot.GetPathTo(templateNode);
        Node? baselineNode = string.IsNullOrWhiteSpace(relativePath.ToString())
            ? baselineRoot
            : baselineRoot.GetNodeOrNull(relativePath);
        return baselineNode is not null
            && baselineNode.Name == templateNode.Name
            && NodesHaveCompatibleTypes(templateNode, baselineNode);
    }

    private static bool NodesHaveCompatibleTypes(Node templateNode, Node baselineNode)
    {
        Type templateType = templateNode.GetType();
        Type baselineType = baselineNode.GetType();
        return templateType.IsAssignableFrom(baselineType) || baselineType.IsAssignableFrom(templateType);
    }

    private static Node ResolveSelectedSource(Node templateRoot, ISceneInstaller installer, NodePath? sourcePath)
    {
        _ = sourcePath is null || string.IsNullOrWhiteSpace(sourcePath.ToString())
            ? throw new InvalidOperationException(
                $"Template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' requires a source path for selected-subtree install modes.")
            : sourcePath;
        return templateRoot.GetNodeOrNull(sourcePath)
            ?? throw new InvalidOperationException(
                $"Template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' could not resolve source path '{sourcePath}' "
                + $"within template root '{templateRoot.Name}'.");
    }

    private static Node DuplicateInstallCandidate(Node source)
    {
        Node duplicate = source.Duplicate();
        ApplyReusableNodeState(duplicate, source);
        ClearOwnerRecursively(duplicate);
        return duplicate;
    }

    private static void InstallNodes(
        Node targetParent,
        IReadOnlyList<(Node Source, Node Candidate)> sourceCandidates,
        TemplateSceneInstallationContext context,
        ISceneInstaller installer)
    {
        List<InstallationPlan> plans = [];
        var sourceNodeMap = new Dictionary<Node, Node>();
        foreach ((Node source, Node candidate) in sourceCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Name.ToString()))
            {
                throw new InvalidOperationException(
                    $"Template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' cannot install a template node with a blank name under '{targetParent.GetPath()}'.");
            }

            Node? existing = targetParent.GetNodeOrNull(candidate.Name.ToString());
            if (existing is not null)
            {
                EnsureExistingNodeType(existing, candidate, installer);
            }

            Node destination = existing ?? candidate;
            MapReusableSourceSubtree(source, destination, sourceNodeMap);
            plans.Add(new InstallationPlan(source, candidate, existing));
        }

        foreach (InstallationPlan plan in plans)
        {
            if (plan.Existing is not null)
            {
                ReconcileMissingDescendants(plan.Existing, plan.Source, context, installer, sourceNodeMap);
                ApplyReusableNodeState(plan.Existing, plan.Source, context, installer, sourceNodeMap);
                DisposeUnparentedCandidate(plan.Candidate);
                continue;
            }

            PrepareCandidateForInstall(plan.Source, plan.Candidate, context, installer, sourceNodeMap);
        }

        foreach (InstallationPlan plan in plans)
        {
            if (plan.Existing is not null)
            {
                continue;
            }

            targetParent.AddChild(plan.Candidate);
            AssignPersistedOwnerIfNeeded(plan.Candidate, targetParent);
            MarkInstalledRecursively(plan.Candidate, context, installer);
        }
    }

    private static void PrepareCandidateForInstall(
        Node source,
        Node candidate,
        TemplateSceneInstallationContext context,
        ISceneInstaller installer,
        IReadOnlyDictionary<Node, Node> sourceNodeMap)
    {
        Node? sourceParent = candidate.GetParent();
        sourceParent?.RemoveChild(candidate);
        ClearOwnerRecursively(candidate);
        DeactivateSkeletonModifiersRecursively(candidate);
        CopyExportedPropertyValuesRecursively(source, candidate, context, installer, sourceNodeMap);
        TemplateSceneReferenceRebaser.RebaseExportedNodeReferences(
            candidate,
            context.TemplateRoot,
            context.TargetRoot,
            installer,
            sourceNodeMap,
            failOnUnresolved: false);
    }

    private static void CopyExportedPropertyValuesRecursively(
        Node source,
        Node destination,
        TemplateSceneInstallationContext context,
        ISceneInstaller installer,
        IReadOnlyDictionary<Node, Node> sourceNodeMap)
    {
        TemplateSceneReferenceRebaser.CopyExportedPropertyValues(
            source,
            destination,
            context.TemplateRoot,
            context.TargetRoot,
            installer,
            sourceNodeMap,
            failOnUnresolved: false);

        int childCount = Math.Min(source.GetChildCount(), destination.GetChildCount());
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            CopyExportedPropertyValuesRecursively(
                source.GetChild(childIndex),
                destination.GetChild(childIndex),
                context,
                installer,
                sourceNodeMap);
        }
    }

    private static void MapSourceSubtree(Node source, Node destination, IDictionary<Node, Node> sourceNodeMap)
    {
        sourceNodeMap[source] = destination;

        int childCount = Math.Min(source.GetChildCount(), destination.GetChildCount());
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            MapSourceSubtree(source.GetChild(childIndex), destination.GetChild(childIndex), sourceNodeMap);
        }
    }

    private static void MapReusableSourceSubtree(Node source, Node destination, IDictionary<Node, Node> sourceNodeMap)
    {
        sourceNodeMap[source] = destination;

        HashSet<Node> usedDestinationChildren = [];
        foreach (Node sourceChild in source.GetChildren())
        {
            Node? destinationChild = FindReusableChild(sourceChild, destination, usedDestinationChildren);
            if (destinationChild is null)
            {
                continue;
            }

            _ = usedDestinationChildren.Add(destinationChild);
            MapReusableSourceSubtree(sourceChild, destinationChild, sourceNodeMap);
        }
    }

    private static void ReconcileMissingDescendants(
        Node existing,
        Node source,
        TemplateSceneInstallationContext context,
        ISceneInstaller installer,
        Dictionary<Node, Node> sourceNodeMap)
    {
        HashSet<Node> usedDestinationChildren = [];
        foreach (Node sourceChild in source.GetChildren())
        {
            Node? existingChild = TryGetMappedDestination(sourceChild, sourceNodeMap)
                ?? FindReusableChild(sourceChild, existing, usedDestinationChildren);
            if (existingChild is not null)
            {
                _ = usedDestinationChildren.Add(existingChild);
                sourceNodeMap[sourceChild] = existingChild;
                ReconcileMissingDescendants(existingChild, sourceChild, context, installer, sourceNodeMap);
                continue;
            }

            Node candidate = DuplicateInstallCandidate(sourceChild);
            MapSourceSubtree(sourceChild, candidate, sourceNodeMap);
            PrepareCandidateForInstall(sourceChild, candidate, context, installer, sourceNodeMap);
            existing.AddChild(candidate);
            AssignPersistedOwnerIfNeeded(candidate, existing);
            MarkInstalledRecursively(candidate, context, installer);
        }
    }

    private static Node? TryGetMappedDestination(Node source, IReadOnlyDictionary<Node, Node> sourceNodeMap)
        => sourceNodeMap.TryGetValue(source, out Node? destination) && GodotObject.IsInstanceValid(destination)
            ? destination
            : null;

    private static Node? FindReusableChild(Node sourceChild, Node destinationParent, ISet<Node> usedDestinationChildren)
    {
        foreach (Node destinationChild in destinationParent.GetChildren())
        {
            if (usedDestinationChildren.Contains(destinationChild)
                || sourceChild.Name != destinationChild.Name
                || !sourceChild.GetType().IsAssignableFrom(destinationChild.GetType()))
            {
                continue;
            }

            return destinationChild;
        }

        return null;
    }

    private static void DeactivateSkeletonModifiersRecursively(Node node)
    {
        if (node is SkeletonModifier3D modifier)
        {
            modifier.Active = false;
            modifier.Influence = 0.0f;
        }

        foreach (Node child in node.GetChildren())
        {
            DeactivateSkeletonModifiersRecursively(child);
        }
    }

    private static void DisposeUnparentedCandidate(Node candidate)
    {
        if (candidate.GetParent() is null)
        {
            DeactivateSkeletonModifiersRecursively(candidate);
            candidate.Free();
        }
    }

    private static void ApplyReusableNodeState(Node duplicate, Node source)
    {
        if (duplicate is Node3D duplicateNode3D && source is Node3D sourceNode3D)
        {
            duplicateNode3D.Transform = sourceNode3D.Transform;
        }
    }

    private static void ApplyReusableNodeState(
        Node existing,
        Node source,
        TemplateSceneInstallationContext context,
        ISceneInstaller installer,
        IReadOnlyDictionary<Node, Node> sourceNodeMap)
    {
        if (existing is Node3D existingNode3D && source is Node3D sourceNode3D)
        {
            existingNode3D.Transform = sourceNode3D.Transform;
        }

        TemplateSceneReferenceRebaser.CopyExportedPropertyValues(
            source,
            existing,
            context.TemplateRoot,
            context.TargetRoot,
            installer,
            sourceNodeMap,
            failOnUnresolved: false);

        HashSet<Node> usedExistingChildren = [];
        foreach (Node sourceChild in source.GetChildren())
        {
            Node? existingChild = TryGetMappedDestination(sourceChild, sourceNodeMap)
                ?? FindReusableChild(sourceChild, existing, usedExistingChildren);
            if (existingChild is null)
            {
                continue;
            }

            _ = usedExistingChildren.Add(existingChild);
            ApplyReusableNodeState(existingChild, sourceChild, context, installer, sourceNodeMap);
        }
    }

    private sealed record InstallationPlan(Node Source, Node Candidate, Node? Existing);

    private static void AssignPersistedOwnerIfNeeded(Node node, Node targetParent)
    {
        if (!Engine.IsEditorHint())
        {
            return;
        }

        Node? owner = targetParent.Owner ?? targetParent.GetTree()?.EditedSceneRoot;
        if (owner is null)
        {
            return;
        }

        AssignOwnerRecursively(node, owner);
    }

    private static void AssignOwnerRecursively(Node node, Node owner)
    {
        node.Owner = owner;
        foreach (Node child in node.GetChildren())
        {
            AssignOwnerRecursively(child, owner);
        }
    }

    private static void ClearOwnerRecursively(Node node)
    {
        node.Owner = null;
        foreach (Node child in node.GetChildren())
        {
            ClearOwnerRecursively(child);
        }
    }

    private static void EnsureExistingNodeType(Node existing, Node candidate, ISceneInstaller installer)
    {
        Type candidateType = candidate.GetType();
        Type existingType = existing.GetType();
        if (candidateType.IsAssignableFrom(existingType))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' found existing node "
            + $"'{existing.GetPath()}' named '{existing.Name}' with type {existingType.Name}, expected {candidateType.Name}.");
    }

    private static void MarkInstalledRecursively(Node node, SceneInstallationContext context, ISceneInstaller installer)
    {
        SceneInstallationMetadata.MarkInstalled(node, context, installer);
        foreach (Node child in node.GetChildren())
        {
            MarkInstalledRecursively(child, context, installer);
        }
    }
}
