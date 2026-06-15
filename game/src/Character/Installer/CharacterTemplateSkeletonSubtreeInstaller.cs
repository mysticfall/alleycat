using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Character.Installer;

/// <summary>
/// Installs template skeleton children under the root-resolved target skeleton without per-child skeleton paths.
/// </summary>
[Tool]
[GlobalClass]
public partial class CharacterTemplateSkeletonSubtreeInstaller : CharacterTemplateSubtreeInstaller
{
    /// <summary>
    /// Creates an installer that copies the template skeleton's authored children into the target skeleton.
    /// </summary>
    public CharacterTemplateSkeletonSubtreeInstaller()
    {
        TargetSkeleton = true;
        InstallMode = TemplateInstallMode.SelectedNodeChildren;
    }

    /// <inheritdoc />
    protected override NodePath ResolveSourcePath(CharacterInstallationContext context)
        => context.TemplateRoot.GetPathTo(context.TemplateSkeleton);
}
