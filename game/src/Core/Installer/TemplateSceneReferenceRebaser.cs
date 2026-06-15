using System.Reflection;
using Godot;

namespace AlleyCat.Core.Installer;

/// <summary>
/// Rehomes inspector-authored node references copied from template scenes into an installation target.
/// </summary>
public static class TemplateSceneReferenceRebaser
{
    /// <summary>
    /// Copies exported CLR properties from one node to another, rebasing template-local node references to the target scene.
    /// </summary>
    public static void CopyExportedPropertyValues(
        Node source,
        Node destination,
        Node templateRoot,
        Node targetRoot,
        ISceneInstaller installer,
        IReadOnlyDictionary<Node, Node>? sourceNodeMap = null,
        bool failOnUnresolved = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        Type destinationType = destination.GetType();
        foreach (PropertyInfo property in GetExportedWritableProperties(source.GetType()))
        {
            PropertyInfo? destinationProperty = destinationType.GetProperty(property.Name, BindingFlags.Instance | BindingFlags.Public);
            if (destinationProperty is null || !destinationProperty.CanWrite || !destinationProperty.PropertyType.IsAssignableFrom(property.PropertyType))
            {
                continue;
            }

            object? value = property.GetValue(source);
            object? rebasedValue = RebaseValue(
                value,
                property.PropertyType,
                templateRoot,
                targetRoot,
                installer,
                source,
                sourceNodeMap,
                failOnUnresolved);
            destinationProperty.SetValue(destination, rebasedValue);
        }
    }

    /// <summary>
    /// Rehomes exported node references under the supplied root from the template instance to the installation target.
    /// </summary>
    public static void RebaseExportedNodeReferences(
        Node root,
        Node templateRoot,
        Node targetRoot,
        ISceneInstaller installer,
        IReadOnlyDictionary<Node, Node>? sourceNodeMap = null,
        bool failOnUnresolved = false)
    {
        ArgumentNullException.ThrowIfNull(root);

        RebaseExportedNodeReferencesOnNode(root, templateRoot, targetRoot, installer, sourceNodeMap, failOnUnresolved);
        foreach (Node child in root.GetChildren())
        {
            RebaseExportedNodeReferences(child, templateRoot, targetRoot, installer, sourceNodeMap, failOnUnresolved);
        }
    }

    private static void RebaseExportedNodeReferencesOnNode(
        Node node,
        Node templateRoot,
        Node targetRoot,
        ISceneInstaller installer,
        IReadOnlyDictionary<Node, Node>? sourceNodeMap,
        bool failOnUnresolved)
    {
        foreach (PropertyInfo property in GetExportedWritableProperties(node.GetType()))
        {
            if (!CanContainNodeReference(property.PropertyType))
            {
                continue;
            }

            object? value = property.GetValue(node);
            object? rebasedValue = RebaseValue(
                value,
                property.PropertyType,
                templateRoot,
                targetRoot,
                installer,
                node,
                sourceNodeMap,
                failOnUnresolved);
            if (!ReferenceEquals(value, rebasedValue))
            {
                property.SetValue(node, rebasedValue);
            }
        }
    }

    private static IEnumerable<PropertyInfo> GetExportedWritableProperties(Type type)
        => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.CanWrite && property.GetCustomAttribute<ExportAttribute>() is not null);

    private static bool CanContainNodeReference(Type type)
        => typeof(Node).IsAssignableFrom(type)
            || (type.IsArray && typeof(Node).IsAssignableFrom(type.GetElementType()));

    private static object? RebaseValue(
        object? value,
        Type valueType,
        Node templateRoot,
        Node targetRoot,
        ISceneInstaller installer,
        Node owner,
        IReadOnlyDictionary<Node, Node>? sourceNodeMap,
        bool failOnUnresolved)
    {
        if (value is null)
        {
            return null;
        }

        if (value is Node node)
        {
            return RebaseNode(node, templateRoot, targetRoot, installer, owner, sourceNodeMap, failOnUnresolved);
        }

        if (valueType.IsArray && value is Array sourceArray)
        {
            Type elementType = valueType.GetElementType() ?? typeof(Node);
            var rebasedArray = Array.CreateInstance(elementType, sourceArray.Length);
            for (int index = 0; index < sourceArray.Length; index++)
            {
                object? element = sourceArray.GetValue(index);
                rebasedArray.SetValue(element is Node elementNode
                    ? RebaseNode(elementNode, templateRoot, targetRoot, installer, owner, sourceNodeMap, failOnUnresolved)
                    : element, index);
            }

            return rebasedArray;
        }

        return value;
    }

    private static Node RebaseNode(
        Node node,
        Node templateRoot,
        Node targetRoot,
        ISceneInstaller installer,
        Node owner,
        IReadOnlyDictionary<Node, Node>? sourceNodeMap,
        bool failOnUnresolved)
    {
        if (!IsSameOrDescendant(templateRoot, node))
        {
            return node;
        }

        Node? mapped = TryMapSourceNode(node, sourceNodeMap);
        if (mapped is not null)
        {
            return mapped;
        }

        if (ReferenceEquals(node, templateRoot))
        {
            return targetRoot;
        }

        NodePath relativePath = templateRoot.GetPathTo(node);
        Node? rebased = targetRoot.GetNodeOrNull(relativePath);
        return rebased ?? (failOnUnresolved
            ? throw new InvalidOperationException(
                $"Template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' could not rebase "
                + $"exported node reference '{relativePath}' for '{owner.GetPath()}' into target root '{targetRoot.GetPath()}'.")
            : node);
    }

    private static Node? TryMapSourceNode(Node node, IReadOnlyDictionary<Node, Node>? sourceNodeMap)
    {
        if (sourceNodeMap is null || sourceNodeMap.Count == 0)
        {
            return null;
        }

        for (Node? current = node; current is not null; current = current.GetParent())
        {
            if (!sourceNodeMap.TryGetValue(current, out Node? mappedRoot))
            {
                continue;
            }

            return ReferenceEquals(current, node)
                ? mappedRoot
                : mappedRoot.GetNodeOrNull(current.GetPathTo(node));
        }

        return null;
    }

    private static bool IsSameOrDescendant(Node root, Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }
}
