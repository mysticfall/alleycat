using Godot;

namespace AlleyCat.Core.Installer;

/// <summary>
/// Helper methods for storing installer idempotency metadata on Godot nodes.
/// </summary>
public static class SceneInstallationMetadata
{
    /// <summary>
    /// The default metadata namespace used by scene installers.
    /// </summary>
    public const string DefaultNamespace = "alleycat.scene_installer";

    /// <summary>
    /// Returns true when the node has already been marked as installed by the specified installer.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <param name="context">The installation context that supplies the metadata namespace.</param>
    /// <param name="installer">The installer whose effective identifier is used.</param>
    /// <returns>True when the idempotency marker exists.</returns>
    public static bool HasInstalled(Node node, SceneInstallationContext context, ISceneInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(installer);

        return node.HasMeta(MakeKey(context.MetadataNamespace, GetEffectiveInstallerKey(installer)));
    }

    /// <summary>
    /// Marks a node as installed by the specified installer.
    /// </summary>
    /// <param name="node">The node to mark.</param>
    /// <param name="context">The installation context that supplies the metadata namespace.</param>
    /// <param name="installer">The installer whose effective identifier is used.</param>
    public static void MarkInstalled(Node node, SceneInstallationContext context, ISceneInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(installer);

        node.SetMeta(MakeKey(context.MetadataNamespace, GetEffectiveInstallerKey(installer)), true);
    }

    /// <summary>
    /// Returns true when the node contains any installer-owned metadata marker in the supplied namespace.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <param name="metadataNamespace">The metadata namespace to match.</param>
    /// <returns>True when the node was materialised by an installer in the namespace.</returns>
    public static bool HasAnyInstalledMarker(Node node, string metadataNamespace = DefaultNamespace)
    {
        ArgumentNullException.ThrowIfNull(node);

        string prefix = $"{SanitiseKeyPart(metadataNamespace)}_";
        foreach (StringName metaKey in node.GetMetaList())
        {
            string key = metaKey.ToString();
            if (key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith("_installed", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the installer identifier used for diagnostics and idempotency markers.
    /// </summary>
    /// <param name="installer">The installer to identify.</param>
    /// <returns>The installer node path/name, or the installer's full type name when it is not a node.</returns>
    public static string GetEffectiveInstallerKey(ISceneInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);

        if (installer is Node node)
        {
            string path = node.IsInsideTree() ? node.GetPath().ToString() : node.Name.ToString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return installer.GetType().FullName ?? installer.GetType().Name;
    }

    private static StringName MakeKey(string metadataNamespace, string installerId)
    {
        return string.IsNullOrWhiteSpace(metadataNamespace)
            ? throw new ArgumentException("Metadata namespace must not be blank.", nameof(metadataNamespace))
            : string.IsNullOrWhiteSpace(installerId)
                ? throw new ArgumentException("Installer ID must not be blank.", nameof(installerId))
                : new StringName($"{SanitiseKeyPart(metadataNamespace)}_{SanitiseKeyPart(installerId)}_installed");
    }

    private static string SanitiseKeyPart(string value)
    {
        char[] characters = value.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(characters[index]))
            {
                characters[index] = '_';
            }
        }

        return new string(characters);
    }
}
