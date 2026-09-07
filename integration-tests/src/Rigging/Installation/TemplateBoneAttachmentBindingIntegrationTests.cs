using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Rigging.Installation;

/// <summary>
/// Load-time integrity of the authored <see cref="BoneAttachment3D"/> bindings in the reference
/// character templates. The stored <c>bone_idx</c> is load-authoritative (the engine only falls
/// back to <c>bone_name</c> for indices of -1 or below), so regenerating a skeleton with inserted
/// helper bones silently re-binds attachments whose indices were not refreshed. This guard loads
/// each template scene directly, without any role installer rebind, and proves every attachment's
/// stored index still resolves to its own bone name. The textual counterpart is
/// <c>tools/check_character_template_bone_bindings.py</c>.
/// </summary>
public sealed class TemplateBoneAttachmentBindingIntegrationTests
{
    private const string FemaleTemplatePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";

    private const string MaleTemplatePath =
        "res://assets/characters/templates/reference_male/reference_male_base.tscn";

    private static readonly string[] _canonicalAttachmentNames = ["Head", "RightHand", "LeftHand"];

    /// <summary>
    /// Headless-safe: the assertions only read bone-table data after scene instantiation and need no renderer.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferenceTemplates_StoredBoneAttachmentIndices_ResolveToTheirOwnBoneNamesAfterLoad()
    {
        SceneTree sceneTree = GetSceneTree();
        foreach (string templatePath in new[] { FemaleTemplatePath, MaleTemplatePath })
        {
            using Node template = LoadPackedScene(templatePath).Instantiate();
            sceneTree.Root.AddChild(template);

            try
            {
                foreach (Skeleton3D skeleton in template.FindChildren("*", "Skeleton3D", true, false)
                             .OfType<Skeleton3D>())
                {
                    AssertStoredBindingsResolveToTheirOwnBoneNames(skeleton, templatePath);
                }
            }
            finally
            {
                template.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    private static void AssertStoredBindingsResolveToTheirOwnBoneNames(Skeleton3D skeleton, string templatePath)
    {
        BoneAttachment3D[] attachments = [.. skeleton.GetChildren().OfType<BoneAttachment3D>()];

        Assert.True(
            attachments.Length >= _canonicalAttachmentNames.Length,
            $"Expected at least {_canonicalAttachmentNames.Length} bone attachments on the skeleton of "
            + $"'{templatePath}', found {attachments.Length}.");

        foreach (string canonicalName in _canonicalAttachmentNames)
        {
            Assert.Contains(attachments, attachment => attachment.Name.ToString() == canonicalName);
        }

        foreach (BoneAttachment3D attachment in attachments)
        {
            string boneName = attachment.BoneName.ToString();
            Assert.False(
                string.IsNullOrWhiteSpace(boneName),
                $"Bone attachment '{attachment.GetPath()}' must serialise bone_name explicitly.");

            Assert.True(
                attachment.BoneIdx >= 0,
                $"Bone attachment '{attachment.GetPath()}' must serialise a concrete bone_idx.");

            string resolvedName = skeleton.GetBoneName(attachment.BoneIdx).ToString();
            Assert.True(
                resolvedName == boneName,
                $"Bone attachment '{attachment.GetPath()}' stores bone_idx {attachment.BoneIdx} "
                + $"which resolves to '{resolvedName}' instead of its own bone '{boneName}'.");

            int declaredIndex = skeleton.FindBone(boneName);
            Assert.True(
                declaredIndex == attachment.BoneIdx,
                $"Bone attachment '{attachment.GetPath()}' declares bone '{boneName}' at index "
                + $"{declaredIndex}, but stores bone_idx {attachment.BoneIdx}.");
        }
    }
}
