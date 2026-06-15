using AlleyCat.Character.Installer;
using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Body;

/// <summary>
/// Installs a visible dynamic physical rig template under the resolved skeleton and applies reference-specific rig configuration.
/// </summary>
[Tool]
[GlobalClass]
public partial class DynamicPhysicalRigTemplateInstaller : CharacterTemplateSubtreeInstaller
{
    /// <summary>
    /// Creates an installer that consumes the root-resolved skeleton context by default.
    /// </summary>
    public DynamicPhysicalRigTemplateInstaller()
    {
        TargetSkeleton = true;
        InstallMode = TemplateInstallMode.SelectedNode;
    }

    /// <summary>
    /// Gets or sets the installed rig node name under the skeleton.
    /// </summary>
    [ExportGroup("Dynamic Physical Rig")]
    [Export]
    public string RigName { get; set; } = "DynamicPhysicalRig";

    /// <summary>
    /// Gets or sets the optional collider profile assigned to the installed rig.
    /// </summary>
    [Export]
    public BodyColliderProfile? ColliderProfile
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets whether generated runtime rig creation is enabled on the installed rig.
    /// </summary>
    [Export]
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public override SceneInstallationResult Install(CharacterInstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        SceneInstallationResult templateResult = base.Install(context);
        if (!templateResult.Succeeded)
        {
            return templateResult;
        }

        try
        {
            DynamicPhysicalRig? rig = ResolveInstalledRig(context.Skeleton)
                ?? throw new InvalidOperationException(
                    $"Dynamic physical rig template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(this)}' "
                    + $"could not find installed rig '{RigName}' under skeleton '{context.Skeleton.GetPath()}'.");

            rig.ColliderProfile = ColliderProfile ?? rig.ColliderProfile;
            rig.Enabled = Enabled;
            rig.RegenerateNow();
            SceneInstallationMetadata.MarkInstalled(rig, context, this);

            return SceneInstallationResult.Successful();
        }
        catch (InvalidOperationException ex)
        {
            return SceneInstallationResult.Failed(ex.Message);
        }
    }

    private DynamicPhysicalRig? ResolveInstalledRig(Skeleton3D skeleton)
    {
        DynamicPhysicalRig? directRig = skeleton.GetNodeOrNull<DynamicPhysicalRig>(RigName);
        if (directRig is not null)
        {
            return directRig;
        }

        DynamicPhysicalRig? descendantRig = FindSingleDescendantRig(skeleton, RigName);
        if (descendantRig is null)
        {
            return null;
        }

        Node? sourceParent = descendantRig.GetParent();
        sourceParent?.RemoveChild(descendantRig);
        skeleton.AddChild(descendantRig);
        return descendantRig;
    }

    /// <inheritdoc />
    protected override NodePath ResolveSourcePath(CharacterInstallationContext context)
    {
        if (!string.IsNullOrWhiteSpace(SourcePath.ToString()))
        {
            return SourcePath;
        }

        DynamicPhysicalRig? templateRig = FindSingleDescendantRig(context.TemplateSkeleton, RigName)
            ?? throw new InvalidOperationException(
                $"Dynamic physical rig template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(this)}' "
                + $"could not find a single template rig '{RigName}' under template skeleton '{context.TemplateSkeleton.GetPath()}'.");
        return context.TemplateRoot.GetPathTo(templateRig);
    }

    private static DynamicPhysicalRig? FindSingleDescendantRig(Node node, string rigName)
    {
        DynamicPhysicalRig? match = null;
        foreach (Node child in node.GetChildren())
        {
            if (child is DynamicPhysicalRig rig && child.Name == rigName)
            {
                if (match is not null)
                {
                    return null;
                }

                match = rig;
            }

            DynamicPhysicalRig? childMatch = FindSingleDescendantRig(child, rigName);
            if (childMatch is null)
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = childMatch;
        }

        return match;
    }
}
