using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Rigging.Installation;

/// <summary>
/// Installs template skeleton children under the root-resolved target skeleton without per-child skeleton paths.
/// </summary>
[Tool]
[GlobalClass]
public partial class RigTemplateSkeletonSubtreeInstaller : RigTemplateSubtreeInstaller
{
    /// <summary>
    /// Creates an installer that copies the template skeleton's authored children into the target skeleton.
    /// </summary>
    public RigTemplateSkeletonSubtreeInstaller()
    {
        TargetSkeleton = true;
        InstallMode = TemplateInstallMode.SelectedNodeChildren;
    }

    /// <inheritdoc />
    protected override NodePath ResolveSourcePath(RigInstallationContext context)
        => context.TemplateRoot.GetPathTo(context.TemplateSkeleton);
}
