using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Character.Installer;

/// <summary>
/// Character-specialised installer that copies selected subtrees from the role-provided template context.
/// </summary>
[Tool]
[GlobalClass]
public partial class CharacterTemplateSubtreeInstaller : CharacterSceneInstaller
{
    /// <summary>
    /// Gets or sets which portion of the character template root should be installed.
    /// </summary>
    [ExportGroup("Template Source")]
    [Export]
    public TemplateInstallMode InstallMode { get; set; } = TemplateInstallMode.TemplateRoot;

    /// <summary>
    /// Gets or sets an optional path within the character template root used by selected-subtree install modes.
    /// </summary>
    [Export]
    public NodePath SourcePath { get; set; } = new();

    /// <summary>
    /// Gets or sets an optional path from the resolved installation target to the parent receiving template content.
    /// </summary>
    [ExportGroup("Target")]
    [Export]
    public NodePath TargetParentPath { get; set; } = new();

    /// <summary>
    /// Gets or sets whether selected template content is installed under the resolved character skeleton.
    /// </summary>
    public bool TargetSkeleton
    {
        get; set;
    }

    /// <inheritdoc />
    public override SceneInstallationResult Install(CharacterInstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            CharacterInstallationContext delegatedContext = TargetSkeleton ? context.WithSkeletonTarget() : context;
            Node baseTarget = delegatedContext.TargetRoot;
            SceneInstallationResult result = TemplateSceneInstallation.Install(
                this,
                delegatedContext,
                ResolveTargetParent(delegatedContext),
                InstallMode,
                ResolveSourcePath(context));
            if (result.Succeeded && TargetSkeleton)
            {
                CharacterTemplateInstallation.RebindDirectBoneAttachments(context.Skeleton, this);
            }

            if (result.Succeeded)
            {
                CharacterTemplateInstallation.RebaseTemplateReferences(baseTarget, context, this, failOnUnresolved: false);
            }

            return result;
        }
        catch (InvalidOperationException ex)
        {
            return SceneInstallationResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Resolves the source path used by selected-subtree install modes.
    /// </summary>
    protected virtual NodePath ResolveSourcePath(CharacterInstallationContext context)
    {
        _ = context;
        return SourcePath;
    }

    /// <summary>
    /// Resolves the target parent that receives installed template content.
    /// </summary>
    protected virtual Node? ResolveTargetParent(CharacterInstallationContext context)
    {
        if (string.IsNullOrWhiteSpace(TargetParentPath.ToString()))
        {
            return context.TargetRoot;
        }

        Node? targetParent = context.TargetRoot.GetNodeOrNull(TargetParentPath);
        return targetParent is not null
            ? targetParent
            : throw new InvalidOperationException(
                $"Character template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(this)}' target '{context.TargetRoot.Name}' "
                + $"could not resolve target parent path '{TargetParentPath}'.");
    }
}
