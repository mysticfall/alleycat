namespace AlleyCat.Core.Installer;

/// <summary>
/// Describes which part of an instantiated scene template should be installed into the target parent.
/// </summary>
public enum TemplateInstallMode
{
    /// <summary>
    /// Install the instantiated template root as a direct child of the target parent.
    /// </summary>
    TemplateRoot,

    /// <summary>
    /// Install the direct children of the instantiated template root as direct children of the target parent.
    /// </summary>
    TemplateRootChildren,

    /// <summary>
    /// Install the node resolved by the installer's source path as a direct child of the target parent.
    /// </summary>
    SelectedNode,

    /// <summary>
    /// Install the direct children of the node resolved by the installer's source path as direct children of the target parent.
    /// </summary>
    SelectedNodeChildren,
}
