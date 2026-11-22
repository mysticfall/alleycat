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
    public override SceneInstallationResult Install(RigInstallationContext context)
    {
        SceneInstallationResult result = base.Install(context);
        if (!result.Succeeded)
        {
            return result;
        }

        try
        {
            // Relative native settings need the installed parent chain. The role's IK subsystem
            // activates newly copied modifiers only after this subtree installer returns.
            foreach (Node source in context.TemplateSkeleton.GetChildren())
            {
                Node? installed = context.Skeleton.GetNodeOrNull(new NodePath(source.Name.ToString()));
                switch (source, installed)
                {
                    case (TwoBoneIK3D template, TwoBoneIK3D solver):
                        for (int i = 0; i < Math.Min(template.SettingCount, solver.SettingCount); i++)
                        {
                            RebaseSetting(context, template, solver, i, "target_node",
                                template.GetTargetNode(i), solver.GetTargetNode(i), solver.SetTargetNode);
                            RebaseSetting(context, template, solver, i, "pole_node",
                                template.GetPoleNode(i), solver.GetPoleNode(i), solver.SetPoleNode);
                        }

                        break;
                    case (IterateIK3D template, IterateIK3D solver):
                        for (int i = 0; i < Math.Min(template.SettingCount, solver.SettingCount); i++)
                        {
                            RebaseSetting(context, template, solver, i, "target_node",
                                template.GetTargetNode(i), solver.GetTargetNode(i), solver.SetTargetNode);
                        }

                        break;
                    case (CopyTransformModifier3D template, CopyTransformModifier3D modifier):
                        for (int i = 0; i < Math.Min(template.SettingCount, modifier.SettingCount); i++)
                        {
                            if (template.GetReferenceType(i) == BoneConstraint3D.ReferenceType.Node
                                && modifier.GetReferenceType(i) == BoneConstraint3D.ReferenceType.Node)
                            {
                                RebaseSetting(context, template, modifier, i, "reference_node",
                                    template.GetReferenceNode(i), modifier.GetReferenceNode(i), modifier.SetReferenceNode);
                            }
                        }

                        break;
                    default:
                        break;
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            return SceneInstallationResult.Failed(ex.Message);
        }

        return result;
    }

    private static void RebaseSetting(
        RigInstallationContext context,
        SkeletonModifier3D template,
        SkeletonModifier3D installed,
        int index,
        string setting,
        NodePath templatePath,
        NodePath installedPath,
        Action<int, NodePath> setPath)
    {
        string property = $"settings/{index}/{setting}";
        // A different path may already be rebased or explicitly wired by the target.
        if (templatePath.IsEmpty
            || !string.Equals(templatePath.ToString(), installedPath.ToString(), StringComparison.Ordinal)
            || context.TargetSceneOverrides.IsAuthored(installed, property))
        {
            return;
        }

        if (template.GetNodeOrNull<Node3D>(templatePath) is not Node3D templateTarget
            || TemplateSceneTargetResolver.ResolveEquivalent(templateTarget, context.TemplateRoot, context.TargetRoot) is not Node3D target)
        {
            throw new InvalidOperationException(
                $"Could not rebase '{installed.Name}' setting '{property}' from template path '{templatePath}' to an installed Node3D.");
        }

        NodePath path = installed.GetPathTo(target);
        if (!string.Equals(path.ToString(), installedPath.ToString(), StringComparison.Ordinal))
        {
            setPath(index, path);
        }
    }

    /// <inheritdoc />
    protected override NodePath ResolveSourcePath(RigInstallationContext context)
        => context.TemplateRoot.GetPathTo(context.TemplateSkeleton);
}
