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

    // The unified per-joint constraint set every supported base template serialises identically
    // (IK-001 TR 5-6). The engine's ChainIK3D joint list is serialised ROOT-to-END, so the joint indices
    // map to bones Spine, Chest, UpperChest, Neck, Head for this chain (asserted per joint below). The
    // shared limitation-frame convention uses right axis X with identity rotation offsets. JointLimitationCone3D
    // angle is the full cone aperture, so the enforced per-joint deviation cap is half the angle: with the
    // user-approved set the caps are Spine 8°, Chest 8°, UpperChest 14°, Neck 16°. These are safety
    // envelopes, not allocation weights or an attainment guarantee. The Head end-bone joint keeps a
    // null limitation and is inert because the chain does not extend the end bone.
    private static readonly int[] _unifiedRotationAxes = [0, 0, 3, 3, 0];
    private static readonly float[] _unifiedConeAngleRadians =
    [
        DegreesToRadians(16f),
        DegreesToRadians(16f),
        DegreesToRadians(28f),
        DegreesToRadians(32f),
    ];
    private static readonly string[] _unifiedJointBoneNames = ["Spine", "Chest", "UpperChest", "Neck", "Head"];
    private const int UnifiedRightAxis = 0;

    private readonly record struct SerialisedJointConstraint(
        int RotationAxis,
        bool HasLimitation,
        float ConeAngleRadians,
        int RightAxis,
        Quaternion RotationOffset);

    /// <summary>
    /// The supported base templates author independently resolved local neck-spine chains that serialise the
    /// identical unified constraint set.
    /// </summary>
    [Fact]
    public async Task ReferenceBaseTemplates_AuthorLocallyResolvedNeckSpineIK()
    {
        IReadOnlyList<SerialisedJointConstraint> femaleConstraints =
            await AssertLocalNeckSpineIKConfigurationAsync(ReferenceFemaleBaseScenePath, "Female/GeneralSkeleton");
        IReadOnlyList<SerialisedJointConstraint> maleConstraints =
            await AssertLocalNeckSpineIKConfigurationAsync(ReferenceMaleBaseScenePath, "Male/GeneralSkeleton");

        // IK-001 TR 5 / AC 12: only the skeleton bindings may vary between templates; the serialised
        // constraint sets — axes, cone angles, and limitation frames including right axes and rotation
        // offsets — must be identical across templates.
        Assert.Equal(femaleConstraints, maleConstraints);
    }

    /// <summary>
    /// The supported base templates author a NeckPivot marker at the neck articulation, parented to a Neck
    /// bone attachment the same way the Viewpoint marker hangs under the Head bone attachment.
    /// </summary>
    [Fact]
    public async Task ReferenceBaseTemplates_AuthorNeckPivotMarker()
    {
        await AssertNeckPivotAuthoredAsync(ReferenceFemaleBaseScenePath, "Female/GeneralSkeleton");
        await AssertNeckPivotAuthoredAsync(ReferenceMaleBaseScenePath, "Male/GeneralSkeleton");
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

    private static async Task<IReadOnlyList<SerialisedJointConstraint>> AssertLocalNeckSpineIKConfigurationAsync(
        string scenePath,
        NodePath skeletonPath)
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

            return CaptureAndAssertJointConstraints(ikNode, scenePath);
        }
        finally
        {
            templateRoot.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static IReadOnlyList<SerialisedJointConstraint> CaptureAndAssertJointConstraints(Node ikNode, string scenePath)
    {
        var captured = new List<SerialisedJointConstraint>(capacity: _unifiedRotationAxes.Length);

        for (int jointIndex = 0; jointIndex < _unifiedRotationAxes.Length; jointIndex++)
        {
            int rotationAxis = (int)ikNode.Get($"settings/0/joints/{jointIndex}/rotation_axis");
            Assert.Equal(
                _unifiedRotationAxes[jointIndex],
                rotationAxis);

            GodotObject? limitation = ikNode.Get($"settings/0/joints/{jointIndex}/limitation").AsGodotObject();
            string jointBoneName = ikNode.Call("get_joint_bone_name", 0, jointIndex).AsString();
            Assert.Equal(
                _unifiedJointBoneNames[jointIndex],
                jointBoneName,
                ignoreCase: true);
            bool jointHasLimitation = jointIndex < _unifiedConeAngleRadians.Length;
            if (!jointHasLimitation)
            {
                // The Head end-bone joint keeps a null limitation; its config is inert because the chain does
                // not extend the end bone (IK-001 TR 6 tuning set).
                Assert.Null(limitation);
                captured.Add(new SerialisedJointConstraint(rotationAxis, HasLimitation: false, 0f, 0, Quaternion.Identity));
                continue;
            }

            Assert.NotNull(limitation);
            float coneAngleRadians = limitation.Get("angle").AsSingle();
            Assert.Equal(
                _unifiedConeAngleRadians[jointIndex],
                coneAngleRadians,
                precision: 5);

            int rightAxis = (int)ikNode.Get($"settings/0/joints/{jointIndex}/limitation/right_axis");
            Assert.Equal(UnifiedRightAxis, rightAxis);

            Quaternion rotationOffset = ikNode.Get($"settings/0/joints/{jointIndex}/limitation/rotation_offset").AsQuaternion();
            Assert.Equal(0f, rotationOffset.X, precision: 5);
            Assert.Equal(0f, rotationOffset.Y, precision: 5);
            Assert.Equal(0f, rotationOffset.Z, precision: 5);
            Assert.Equal(1f, rotationOffset.W, precision: 5);

            captured.Add(new SerialisedJointConstraint(
                rotationAxis,
                HasLimitation: true,
                coneAngleRadians,
                rightAxis,
                rotationOffset));
        }

        Assert.True(
            captured.Count == 5,
            $"Expected '{scenePath}' to serialise five joint constraints, found {captured.Count}.");
        return captured;
    }

    private static async Task AssertNeckPivotAuthoredAsync(string scenePath, NodePath skeletonPath)
    {
        SceneTree sceneTree = GetSceneTree();
        Node templateRoot = LoadPackedScene(scenePath).Instantiate();
        sceneTree.Root.AddChild(templateRoot);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            Skeleton3D skeleton = Assert.IsType<Skeleton3D>(templateRoot.GetNodeOrNull(skeletonPath), exactMatch: false);
            int neckBoneIndex = skeleton.FindBone("Neck");
            Assert.True(neckBoneIndex >= 0, $"Expected '{scenePath}' skeleton to expose a Neck bone.");

            BoneAttachment3D headAttachment =
                Assert.IsType<BoneAttachment3D>(skeleton.GetNodeOrNull("Head"), exactMatch: false);
            BoneAttachment3D neckAttachment =
                Assert.IsType<BoneAttachment3D>(skeleton.GetNodeOrNull("Neck"), exactMatch: false);
            Assert.Equal("Neck", neckAttachment.BoneName.ToString());
            Assert.Equal(neckBoneIndex, neckAttachment.BoneIdx);

            // IK-001 TR 7 / AC 13: the pivot marker hangs under the Neck bone attachment exactly the way
            // the Viewpoint marker hangs under the Head bone attachment, at the articulation origin.
            Marker3D viewpoint = Assert.IsType<Marker3D>(headAttachment.GetNodeOrNull("Viewpoint"), exactMatch: false);
            Marker3D neckPivot = Assert.IsType<Marker3D>(neckAttachment.GetNodeOrNull("NeckPivot"), exactMatch: false);
            Assert.Same(headAttachment, viewpoint.GetParent());
            Assert.Same(neckAttachment, neckPivot.GetParent());
            Assert.True(neckPivot.UniqueNameInOwner, "Expected the NeckPivot marker to mirror the Viewpoint unique-name authoring.");
            Assert.Equal(Transform3D.Identity, neckPivot.Transform);
        }
        finally
        {
            templateRoot.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180f;
}
