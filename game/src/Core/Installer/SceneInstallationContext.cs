using Godot;

namespace AlleyCat.Core.Installer;

/// <summary>
/// Carries the explicit target for a scene installer invocation.
/// </summary>
/// <param name="targetRoot">The scene or entity root that installers should modify using relative knowledge.</param>
/// <param name="metadataNamespace">The metadata namespace used for idempotency markers.</param>
public class SceneInstallationContext(
    Node targetRoot,
    string metadataNamespace = SceneInstallationMetadata.DefaultNamespace)
{
    /// <summary>
    /// Gets the scene or entity root that installers should modify.
    /// </summary>
    public Node TargetRoot
    {
        get;
    } = targetRoot ?? throw new ArgumentNullException(nameof(targetRoot));

    /// <summary>
    /// Gets the metadata namespace used for idempotency markers.
    /// </summary>
    public string MetadataNamespace
    {
        get;
    } = string.IsNullOrWhiteSpace(metadataNamespace)
        ? throw new ArgumentException("Metadata namespace must not be blank.", nameof(metadataNamespace))
        : metadataNamespace;

    /// <summary>
    /// Creates a copy of the context with a different target root.
    /// </summary>
    /// <param name="targetRoot">The replacement target root.</param>
    /// <returns>A context using the same metadata namespace for the replacement target.</returns>
    public virtual SceneInstallationContext WithTargetRoot(Node targetRoot)
        => new(targetRoot, MetadataNamespace);

}
