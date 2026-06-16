using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Rigging.Installation;

/// <summary>
/// Rig-specific helpers for content copied from role-provided template context.
/// </summary>
public static class RigTemplateInstallation
{
    /// <summary>
    /// Rebinds direct bone attachments installed under a skeleton to their same-named bones.
    /// </summary>
    public static void RebindDirectBoneAttachments(Skeleton3D skeleton, ISceneInstaller installer)
    {
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is not BoneAttachment3D attachment)
            {
                continue;
            }

            string boneName = attachment.BoneName.ToString();
            if (string.IsNullOrWhiteSpace(boneName))
            {
                boneName = attachment.Name.ToString();
                attachment.BoneName = boneName;
            }

            int boneIndex = skeleton.FindBone(boneName);
            if (boneIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Rig template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' installed "
                    + $"bone attachment '{attachment.GetPath()}' for missing bone '{boneName}' on skeleton '{skeleton.GetPath()}'.");
            }

            attachment.BoneIdx = boneIndex;
        }
    }

    /// <summary>
    /// Rehomes inspector-authored node references copied from the role template to the target character scene.
    /// </summary>
    public static void RebaseTemplateReferences(
        Node root,
        RigInstallationContext context,
        ISceneInstaller installer,
        bool failOnUnresolved = true)
        => TemplateSceneReferenceRebaser.RebaseExportedNodeReferences(
            root,
            context.TemplateRoot,
            context.TargetRoot,
            installer,
            failOnUnresolved: failOnUnresolved);

    /// <summary>
    /// Applies template_bindings metadata under the supplied root using paths relative to the original target root.
    /// </summary>
    public static void ApplyTemplateBindings(Node bindingRoot, SceneInstallationContext context, ISceneInstaller installer)
    {
        ApplyTemplateBindingsToNode(bindingRoot, context, installer);
        foreach (Node child in bindingRoot.GetChildren())
        {
            ApplyTemplateBindings(child, context, installer);
        }
    }

    private static void ApplyTemplateBindingsToNode(Node node, SceneInstallationContext context, ISceneInstaller installer)
    {
        if (!node.HasMeta("template_bindings"))
        {
            return;
        }

        Variant bindingsVariant = node.GetMeta("template_bindings");
        if (bindingsVariant.VariantType != Variant.Type.Dictionary)
        {
            throw new InvalidOperationException(
                $"Rig template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' found invalid "
                + $"template_bindings metadata on '{node.GetPath()}'.");
        }

        Godot.Collections.Dictionary bindings = bindingsVariant.AsGodotDictionary();
        foreach (Variant propertyVariant in bindings.Keys)
        {
            Variant pathVariant = bindings[propertyVariant];
            string propertyName = propertyVariant.AsString();
            var targetPath = new NodePath(pathVariant.AsString());
            Node target = context.TargetRoot.GetNodeOrNull(targetPath)
                ?? throw new InvalidOperationException(
                    $"Rig template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(installer)}' could not resolve "
                    + $"binding target '{targetPath}' for property '{propertyName}' on '{node.GetPath()}'.");

            node.Set(propertyName, target);
        }
    }
}
