using Godot;

namespace AlleyCat.Core.Installer;

/// <summary>
/// Carries template-specific dependencies for installers that copy content from an instantiated template root.
/// </summary>
/// <param name="targetRoot">The scene or entity root that installers should modify.</param>
/// <param name="metadataNamespace">The metadata namespace used for idempotency markers.</param>
/// <param name="templateRoot">The instantiated template root used as the source for template installers.</param>
/// <param name="templateBaselineRoot">The optional instantiated baseline scene used to ignore inherited template content.</param>
/// <param name="targetSceneOverrides">The target scene's locally authored property index, captured before installation.</param>
public class TemplateSceneInstallationContext(
    Node targetRoot,
    string metadataNamespace,
    Node templateRoot,
    Node? templateBaselineRoot = null,
    TargetSceneOverrides? targetSceneOverrides = null)
    : SceneInstallationContext(targetRoot, metadataNamespace)
{
    /// <summary>
    /// Gets the instantiated template root provided by the top-level installer.
    /// </summary>
    public Node TemplateRoot { get; } = templateRoot ?? throw new ArgumentNullException(nameof(templateRoot));

    /// <summary>
    /// Gets the optional instantiated baseline root for the template scene. When present, shared template installers
    /// install only nodes that are authored by the template above this baseline.
    /// </summary>
    public Node? TemplateBaselineRoot { get; } = templateBaselineRoot;

    /// <summary>
    /// Gets the properties explicitly authored by the original target scene layer.
    /// </summary>
    public TargetSceneOverrides TargetSceneOverrides { get; } = targetSceneOverrides ?? TargetSceneOverrides.Discover(targetRoot);

    /// <inheritdoc />
    public override TemplateSceneInstallationContext WithTargetRoot(Node targetRoot)
        => new(targetRoot, MetadataNamespace, TemplateRoot, TemplateBaselineRoot, TargetSceneOverrides);
}
