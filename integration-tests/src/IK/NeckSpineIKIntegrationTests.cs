using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Non-visual integration coverage for IK-001 neck-spine CCDIK authoring.
/// </summary>
public sealed class NeckSpineIKIntegrationTests
{
    private const string ReferenceFemaleBaseScenePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string ReferenceMaleBaseScenePath =
        "res://assets/characters/templates/reference_male/reference_male_base.tscn";
    private const string IkNodeName = "NeckSpineIK";

    /// <summary>
    /// The supported base templates author independently resolved, constrained local neck-spine chains.
    /// </summary>
    [Fact]
    public async Task ReferenceBaseTemplates_AuthorLocallyResolvedNeckSpineIK()
    {
        await AssertLocalNeckSpineIKConfigurationAsync(ReferenceFemaleBaseScenePath, "Female/GeneralSkeleton");
        await AssertLocalNeckSpineIKConfigurationAsync(ReferenceMaleBaseScenePath, "Male/GeneralSkeleton");
    }

    private static IReadOnlyList<int> ResolveIkChainBoneIndices(Skeleton3D skeleton, Node ikNode)
    {
        int endBoneIndex = ResolveIkBoneIndex(skeleton, ikNode, "settings/0/end_bone", "settings/0/end_bone_name");
        int rootBoneIndex = ResolveIkBoneIndex(skeleton, ikNode, "settings/0/root_bone", "settings/0/root_bone_name");

        if (endBoneIndex < 0)
        {
            return [];
        }

        int maxChainLength = Math.Max(1, (int)ikNode.Get("settings/0/joint_count")) + 1;
        var chain = new List<int>(capacity: maxChainLength);

        int currentBoneIndex = endBoneIndex;
        while (currentBoneIndex >= 0 && chain.Count < maxChainLength)
        {
            chain.Add(currentBoneIndex);
            if (currentBoneIndex == rootBoneIndex)
            {
                break;
            }

            currentBoneIndex = skeleton.GetBoneParent(currentBoneIndex);
        }

        return chain;
    }

    private static int ResolveIkBoneIndex(
        Skeleton3D skeleton,
        Node ikNode,
        StringName indexPropertyName,
        StringName namePropertyName)
    {
        int configuredIndex = (int)ikNode.Get(indexPropertyName);
        if (configuredIndex >= 0)
        {
            return configuredIndex;
        }

        string configuredName = ((StringName)ikNode.Get(namePropertyName)).ToString();
        return string.IsNullOrWhiteSpace(configuredName)
            ? -1
            : skeleton.FindBone(configuredName);
    }

    private static async Task AssertLocalNeckSpineIKConfigurationAsync(string scenePath, NodePath skeletonPath)
    {
        SceneTree sceneTree = GetSceneTree();
        Node templateRoot = LoadPackedScene(scenePath).Instantiate();
        sceneTree.Root.AddChild(templateRoot);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            Skeleton3D skeleton = Assert.IsType<Skeleton3D>(templateRoot.GetNodeOrNull(skeletonPath), exactMatch: false);
            Node ikNode = skeleton.GetNodeOrNull(IkNodeName)
                ?? throw new Xunit.Sdk.XunitException($"Expected '{scenePath}' to author a local NeckSpineIK node.");
            Assert.True(ikNode.IsClass("CCDIK3D"), $"Expected '{scenePath}' NeckSpineIK node to be a CCDIK3D.");
            var targetPath = (NodePath)ikNode.Get("settings/0/target_node");

            Assert.False(targetPath.IsEmpty, $"Expected '{scenePath}' to author a local NeckSpineIK target binding.");
            Node3D configuredTarget = Assert.IsType<Node3D>(ikNode.GetNodeOrNull(targetPath), exactMatch: false);
            Assert.Equal("HeadSolve", configuredTarget.Name.ToString());
            Assert.Equal(1, (int)ikNode.Get("setting_count"));
            Assert.Equal("Spine", ((StringName)ikNode.Get("settings/0/root_bone_name")).ToString());
            Assert.Equal(skeleton.FindBone("Spine"), (int)ikNode.Get("settings/0/root_bone"));
            Assert.Equal("Head", ((StringName)ikNode.Get("settings/0/end_bone_name")).ToString());
            Assert.Equal(skeleton.FindBone("Head"), (int)ikNode.Get("settings/0/end_bone"));
            Assert.Equal(5, (int)ikNode.Get("settings/0/joint_count"));

            IReadOnlyList<int> chainBoneIndices = ResolveIkChainBoneIndices(skeleton, ikNode);
            Assert.Equal(5, chainBoneIndices.Count);
            Assert.Equal(skeleton.FindBone("Head"), chainBoneIndices[0]);
            Assert.Equal(skeleton.FindBone("Spine"), chainBoneIndices[^1]);

            AssertJointConstraints(ikNode);
        }
        finally
        {
            templateRoot.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static void AssertJointConstraints(Node ikNode)
    {
        ReadOnlySpan<int> expectedRotationAxes = [0, 0, 3, 3, 0];

        for (int jointIndex = 0; jointIndex < expectedRotationAxes.Length; jointIndex++)
        {
            Assert.Equal(expectedRotationAxes[jointIndex], (int)ikNode.Get($"settings/0/joints/{jointIndex}/rotation_axis"));

            GodotObject? limitation = ikNode.Get($"settings/0/joints/{jointIndex}/limitation").AsGodotObject();
            if (jointIndex < 4)
            {
                Assert.NotNull(limitation);
            }
            else
            {
                Assert.Null(limitation);
            }
        }
    }
}
