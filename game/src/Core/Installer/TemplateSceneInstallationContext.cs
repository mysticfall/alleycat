using Godot;

namespace AlleyCat.Core.Installer;

/// <summary>
/// Carries template-specific dependencies for installers that copy content from an instantiated template root.
/// </summary>
/// <param name="targetRoot">The scene or entity root that installers should modify.</param>
/// <param name="metadataNamespace">The metadata namespace used for idempotency markers.</param>
/// <param name="templateRoot">The instantiated template root used as the source for template installers.</param>
public class TemplateSceneInstallationContext(
    Node targetRoot,
    string metadataNamespace,
    Node templateRoot)
    : SceneInstallationContext(targetRoot, metadataNamespace)
{
    /// <summary>
    /// Gets the instantiated template root provided by the top-level installer.
    /// </summary>
    public Node TemplateRoot { get; } = templateRoot ?? throw new ArgumentNullException(nameof(templateRoot));

    /// <inheritdoc />
    public override TemplateSceneInstallationContext WithTargetRoot(Node targetRoot)
        => new(targetRoot, MetadataNamespace, TemplateRoot);
}
