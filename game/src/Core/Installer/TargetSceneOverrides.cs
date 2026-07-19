using Godot;

namespace AlleyCat.Core.Installer;

/// <summary>
/// Indexes properties explicitly serialised by the local layer of an installation target scene.
/// </summary>
public sealed class TargetSceneOverrides
{
    private readonly Node _sceneRoot;
    private readonly IReadOnlySet<AuthoredProperty> _properties;

    private TargetSceneOverrides(Node sceneRoot, IReadOnlySet<AuthoredProperty> properties)
    {
        _sceneRoot = sceneRoot;
        _properties = properties;
    }

    /// <summary>
    /// Discovers locally authored properties without traversing the scene's inherited base state.
    /// </summary>
    public static TargetSceneOverrides Discover(Node targetRoot)
    {
        ArgumentNullException.ThrowIfNull(targetRoot);

        string scenePath = targetRoot.SceneFilePath;
        if (string.IsNullOrWhiteSpace(scenePath))
        {
            return Empty(targetRoot);
        }

        try
        {
            if (!ResourceLoader.Exists(scenePath, nameof(PackedScene)))
            {
                return Empty(targetRoot);
            }

            PackedScene? packedScene = ResourceLoader.Load<PackedScene>(scenePath);
            if (packedScene is null)
            {
                return Empty(targetRoot);
            }

            SceneState state = packedScene.GetState();
            var properties = new HashSet<AuthoredProperty>();
            for (int nodeIndex = 0; nodeIndex < state.GetNodeCount(); nodeIndex++)
            {
                string nodePath = NormaliseNodePath(state.GetNodePath(nodeIndex).ToString(), targetRoot.Name.ToString());
                for (int propertyIndex = 0; propertyIndex < state.GetNodePropertyCount(nodeIndex); propertyIndex++)
                {
                    string propertyName = NormalisePropertyName(state.GetNodePropertyName(nodeIndex, propertyIndex).ToString());
                    if (!string.IsNullOrEmpty(propertyName))
                    {
                        _ = properties.Add(new AuthoredProperty(nodePath, propertyName));
                    }
                }
            }

            return new TargetSceneOverrides(targetRoot, properties);
        }
        catch (Exception)
        {
            return Empty(targetRoot);
        }
    }

    /// <summary>
    /// Returns whether the exact node property was explicitly authored by the target scene's local layer.
    /// </summary>
    public bool IsAuthored(Node node, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!IsSameOrDescendant(_sceneRoot, node))
        {
            return false;
        }

        string relativePath = ReferenceEquals(_sceneRoot, node)
            ? string.Empty
            : NormaliseNodePath(_sceneRoot.GetPathTo(node).ToString(), _sceneRoot.Name.ToString());
        return _properties.Contains(new AuthoredProperty(relativePath, NormalisePropertyName(propertyName)));
    }

    private static TargetSceneOverrides Empty(Node targetRoot)
        => new(targetRoot, new HashSet<AuthoredProperty>());

    private static string NormaliseNodePath(string path, string rootName)
    {
        string normalised = path.Trim().Replace('\\', '/').Trim('/');
        while (normalised.StartsWith("./", StringComparison.Ordinal))
        {
            normalised = normalised[2..];
        }

        if (normalised == "." || string.Equals(normalised, rootName, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string rootPrefix = rootName + "/";
        return normalised.StartsWith(rootPrefix, StringComparison.Ordinal)
            ? normalised[rootPrefix.Length..]
            : normalised;
    }

    private static string NormalisePropertyName(string propertyName)
        => propertyName.Trim();

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

    private readonly record struct AuthoredProperty(string NodePath, string PropertyName);
}
