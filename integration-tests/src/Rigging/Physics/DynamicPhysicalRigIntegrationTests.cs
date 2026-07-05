using System.Reflection;
using AlleyCat.Body;
using AlleyCat.Core.Installer;
using AlleyCat.IK;
using AlleyCat.Interaction.Physical;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Rigging.Physics;

/// <summary>
/// Integration coverage for physics-timed IK target following and generated proxy collision rigging.
/// </summary>
public sealed class DynamicPhysicalRigIntegrationTests
{
    private const string CollidersScenePath = "res://assets/characters/reference/female/reference_female_colliders.tscn";
    private const string ColliderSourceScenePath = "res://assets/characters/reference/female/reference_female.colliders.blend";
    private const string ColliderProfilePath = "res://assets/characters/reference/female/body_collider_profile.tres";
    private const string ColliderProfileUID = "uid://dpisik0mj8f6a";
    private const string ReferenceFemaleNpcScenePath = "res://assets/characters/reference/ally.tscn";
    private const string ReferencePlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string TestBallScenePath = "res://assets/items/test_ball.tscn";
    private const string TestStickScenePath = "res://assets/items/test_stick.tscn";
    private const float PositionToleranceMetres = 0.001f;
    private static readonly Transform3D _rotatedSourceAttachmentTransform = new(
        Basis.FromEuler(new Vector3(0.17f, -0.29f, 0.13f)),
        new Vector3(-0.08f, 0.04f, 0.11f));
    private static readonly Transform3D _rotatedSourceBodyTransform = new(
        Basis.FromEuler(new Vector3(-0.07f, 0.23f, 0.31f)),
        new Vector3(0.05f, -0.02f, 0.03f));
    private static readonly Transform3D _rotatedSourceShapeTransform = new(
        Basis.FromEuler(new Vector3(0.41f, 0.19f, -0.27f)),
        new Vector3(0.02f, 0.06f, -0.04f));

    /// <summary>
    /// Verifies runtime setup builds generated proxy bodies that inherit generated attachment transforms directly.
    /// </summary>
    [Headless]
    [Fact(Skip = "Pose override fixture must be reauthored after the template-only/actual runtime scene split.")]
    public async Task DynamicPhysicalRig_RuntimeSetup_BuildsExpectedInheritedProxyTopology()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            int sourceShapeCount = CountSourceShapes(fixture.Rig.ColliderProfile?.SourceScene);
            int generatedBodyCount = CountGeneratedProxyBodies(fixture.Rig);
            int generatedReceiverCount = CountGeneratedPhysicalInteractionReceivers(fixture.Rig);

            Assert.Equal(sourceShapeCount, fixture.Rig.GeneratedProxyCount);
            Assert.Equal(sourceShapeCount, generatedBodyCount);
            Assert.Equal(generatedBodyCount, generatedReceiverCount);
            Assert.Equal(0, fixture.Rig.SkippedSourceShapeCount);
            Assert.False(fixture.Rig.IsPhysicsProcessing(), "Generated proxy bodies inherit BoneAttachment3D transforms without per-frame processing.");
            AssertGeneratedProxyBodiesUseInheritedAttachmentTopology(fixture.Rig);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies generated proxy bodies expose the BODY-008 physical interaction receiver contract and metadata.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_GeneratedProxyBodies_ImplementPhysicalInteractionReceiverWithMetadata()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            PhysicalBodyPart3D headProxy = Assert.IsType<PhysicalBodyPart3D>(FindGeneratedProxyBody(fixture.Rig, "Head"), exactMatch: false);
            IPhysicalInteractionReceiver receiver = headProxy;
            TestImpactPhysicalInteractionSource source = new(Vector3.Forward);

            IImpactPhysicalInteraction interaction = Assert.IsAssignableFrom<IImpactPhysicalInteraction>(
                receiver.InteractWith(source)
                ?? throw new Xunit.Sdk.XunitException("Expected source to produce an interaction."));

            Assert.Equal("Head", headProxy.BoneName.ToString());
            Assert.True(headProxy.BoneIndex >= 0);
            Assert.Contains("Head", headProxy.Tags);
            Assert.NotEmpty(headProxy.SourceShapeId);
            Assert.Same(fixture.Rig, headProxy.OwningRig);
            Assert.Same(source, interaction.Source);
            Assert.Equal(headProxy.GlobalPosition, interaction.ContactPoint);
            Assert.Equal(Vector3.Forward, interaction.Velocity);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies generated body-part receiver signals are connected and forwarded by the owning rig.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_GeneratedProxyReceivesInteraction_ForwardsBodyPartSignal()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            PhysicalBodyPart3D headProxy = Assert.IsType<PhysicalBodyPart3D>(
                FindGeneratedProxyBody(fixture.Rig, "Head"),
                exactMatch: false);
            TestImpactPhysicalInteractionSource source = new(Vector3.Forward);
            int forwardedCount = 0;
            PhysicalInteractionReceipt? forwardedReceipt = null;
            int forwardedBoneIndex = -1;
            string[] forwardedTags = [];
            PhysicalBodyPart3D? forwardedBodyPart = null;
            fixture.Rig.PhysicalInteractionReceived += (interaction, boneIndex, tags, bodyPart) =>
            {
                forwardedCount += 1;
                forwardedReceipt = interaction;
                forwardedBoneIndex = boneIndex;
                forwardedTags = tags;
                forwardedBodyPart = bodyPart;
            };

            IPhysicalInteraction interaction = headProxy.InteractWith(source)
                ?? throw new Xunit.Sdk.XunitException("Expected source to produce an interaction.");

            Assert.Equal(1, forwardedCount);
            Assert.NotNull(forwardedReceipt);
            Assert.Same(interaction, forwardedReceipt.Interaction);
            Assert.Equal(headProxy.BoneIndex, forwardedBoneIndex);
            Assert.Equal(headProxy.Tags, [.. forwardedTags]);
            Assert.Same(headProxy, forwardedBodyPart);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies generated body-part receiver signals carry the delivered interaction, bone index, and tag snapshot.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PhysicalBodyPart3D_InteractWith_EmitsSignalWithInteractionBoneAndTags()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            PhysicalBodyPart3D headProxy = Assert.IsType<PhysicalBodyPart3D>(
                FindGeneratedProxyBody(fixture.Rig, "Head"),
                exactMatch: false);
            TestImpactPhysicalInteractionSource source = new(Vector3.Forward);
            int emittedCount = 0;
            PhysicalInteractionReceipt? emittedReceipt = null;
            int emittedBoneIndex = -1;
            string[] emittedTags = [];
            headProxy.PhysicalInteractionReceived += (receipt, boneIndex, tags) =>
            {
                emittedCount += 1;
                emittedReceipt = receipt;
                emittedBoneIndex = boneIndex;
                emittedTags = tags;
            };

            IPhysicalInteraction interaction = headProxy.InteractWith(source)
                ?? throw new Xunit.Sdk.XunitException("Expected source to produce an interaction.");

            Assert.Equal(1, emittedCount);
            Assert.NotNull(emittedReceipt);
            Assert.Same(interaction, emittedReceipt.Interaction);
            Assert.Equal(headProxy.BoneIndex, emittedBoneIndex);
            Assert.Equal(headProxy.Tags, [.. emittedTags]);
            Assert.NotSame(headProxy.Tags, emittedTags);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private sealed class TestImpactPhysicalInteractionSource(Vector3 velocity) : IImpactPhysicalInteractionSource
    {
        public IReadOnlySet<string> Tags { get; } = new SortedSet<string>(["TestSource"], StringComparer.Ordinal);

        public Vector3 Velocity => velocity;

    }

    /// <summary>
    /// Verifies generated proxy bodies preserve the authored source collider pose while following target bone attachments.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_ProxyBodies_PreserveAuthoredSourceShapePoseUnderTargetAttachment()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            const string rotatedBoneName = "LeftHand";
            BodyColliderShapeDescriptor descriptor = Assert.Single(fixture.Rig.ColliderProfile!.QueryShapeDescriptorsForBone(rotatedBoneName));
            AnimatableBody3D handProxy = FindGeneratedProxyBody(fixture.Rig, rotatedBoneName);
            BoneAttachment3D handAttachment = FindGeneratedAttachment(fixture.Rig.TargetSkeleton!, rotatedBoneName);
            CollisionShape3D proxyShape = Assert.IsAssignableFrom<CollisionShape3D>(handProxy.GetChild(0));
            Transform3D attachmentGlobalTransform = ResolveNodeGlobalTransform(handAttachment);
            Transform3D proxyGlobalTransform = ResolveNodeGlobalTransform(handProxy);
            Transform3D shapeGlobalTransform = ResolveNodeGlobalTransform(proxyShape);

            Assert.False(handProxy.TopLevel, "Generated authored proxies should inherit generated BoneAttachment3D transforms.");
            AssertTransformApproximately(Transform3D.Identity, handProxy.Transform, PositionToleranceMetres);
            AssertTransformApproximately(attachmentGlobalTransform, proxyGlobalTransform, PositionToleranceMetres);
            AssertTransformApproximately(descriptor.LocalTransform, proxyShape.Transform, PositionToleranceMetres);
            AssertTransformApproximately(attachmentGlobalTransform * descriptor.LocalTransform, shapeGlobalTransform, PositionToleranceMetres);
            AssertTransformNotApproximately(proxyShape.Transform, handProxy.Transform, PositionToleranceMetres);
            Assert.False(handProxy.SyncToPhysics, "Generated proxies inherit attachment transforms and do not need AnimatableBody3D physics sync.");
            Assert.Equal(fixture.Rig.ProxyCollisionLayer, handProxy.CollisionLayer);
            Assert.Equal(fixture.Rig.ProxyCollisionMask, handProxy.CollisionMask);

            AssertProxyShapeDataPreservedWithDescriptorTransform(fixture.Rig.ColliderProfile?.SourceScene, handProxy, rotatedBoneName, descriptor.LocalTransform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies generated authored shape transforms carry non-identity collider rotations below identity proxy bodies.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_AuthoredProxyShapeTransform_PreservesRotatedGeneratedSourceShapeGlobalPose()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "RotatedProxyTopologyRoot",
            Transform = new Transform3D(Basis.FromEuler(new Vector3(0.0f, 0.19f, 0.0f)), new Vector3(0.23f, 0.41f, -0.17f)),
        };
        Skeleton3D skeleton = CreateSkeleton();
        skeleton.Transform = new Transform3D(Basis.FromEuler(new Vector3(0.11f, -0.23f, 0.07f)), new Vector3(-0.31f, 0.18f, 0.29f));
        PackedScene sourceScene = CreatePackedSourceSceneWithRotatedAttachmentAndShape("LeftHand");
        BodyColliderProfile colliderProfile = CreateColliderProfile(sourceScene);
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = colliderProfile,
        };

        root.AddChild(skeleton);
        skeleton.AddChild(rig);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            int boneIndex = skeleton.FindBone("LeftHand");
            Assert.True(boneIndex >= 0, "Expected test skeleton to contain LeftHand.");
            skeleton.SetBonePoseRotation(boneIndex, new Quaternion(Vector3.Up, 0.37f).Normalized());
            ForceBuildGeneratedRig(rig);
            await WaitForFramesAsync(sceneTree, 1);
            await WaitForPhysicsFramesAsync(sceneTree, 2);

            Assert.True(boneIndex >= 0, "Expected test skeleton to contain LeftHand.");
            BodyColliderShapeDescriptor descriptor = Assert.Single(colliderProfile.QueryShapeDescriptorsForBone("LeftHand"));
            AnimatableBody3D proxyBody = FindGeneratedProxyBody(rig, "LeftHand");
            BoneAttachment3D attachment = FindGeneratedAttachment(skeleton, "LeftHand");
            CollisionShape3D proxyShape = Assert.IsAssignableFrom<CollisionShape3D>(proxyBody.GetChild(0));

            Transform3D expectedShapeGlobalTransform = attachment.GlobalTransform * descriptor.LocalTransform;

            Assert.False(descriptor.LocalTransform.Basis.IsEqualApprox(Basis.Identity), "Test source collider must exercise a non-identity local rotation.");
            AssertTransformApproximately(
                descriptor.SourceShapeFrameTransform,
                ResolveSourceShapeSkeletonTransform(sourceScene, "LeftHand"),
                PositionToleranceMetres);
            Assert.False(proxyBody.TopLevel, "Generated authored proxy bodies should inherit their BoneAttachment3D parent.");
            AssertTransformApproximately(Transform3D.Identity, proxyBody.Transform, PositionToleranceMetres);
            AssertTransformApproximately(attachment.GlobalTransform, ResolveNodeGlobalTransform(proxyBody), PositionToleranceMetres);
            AssertTransformApproximately(descriptor.LocalTransform, proxyShape.Transform, PositionToleranceMetres);
            AssertTransformApproximately(expectedShapeGlobalTransform, ResolveNodeGlobalTransform(proxyShape), PositionToleranceMetres);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>
    /// Verifies source model-frame diagnostics do not drive generated authored proxy placement.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_SourceShapeFrameTransform_IsDiagnosticForAuthoredProxyPlacement()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "SourceFrameRebaseRoot",
            Transform = new Transform3D(Basis.FromEuler(new Vector3(0.08f, -0.14f, 0.21f)), new Vector3(0.18f, -0.06f, 0.27f)),
        };
        Skeleton3D skeleton = CreateSkeletonWithRotatedLeftHandRest();
        PackedScene sourceScene = CreatePackedSourceSceneWithRotatedAttachmentAndShape("LeftHand");
        BodyColliderProfile colliderProfile = CreateColliderProfile(sourceScene);
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = colliderProfile,
        };

        root.AddChild(skeleton);
        skeleton.AddChild(rig);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            ForceBuildGeneratedRig(rig);
            await WaitForFramesAsync(sceneTree, 1);

            int boneIndex = skeleton.FindBone("LeftHand");
            Assert.True(boneIndex >= 0, "Expected test skeleton to contain LeftHand.");
            BodyColliderShapeDescriptor descriptor = Assert.Single(colliderProfile.QueryShapeDescriptorsForBone("LeftHand"));
            AnimatableBody3D proxyBody = FindGeneratedProxyBody(rig, "LeftHand");
            BoneAttachment3D attachment = FindGeneratedAttachment(skeleton, "LeftHand");
            CollisionShape3D proxyShape = Assert.IsAssignableFrom<CollisionShape3D>(proxyBody.GetChild(0));
            Transform3D expectedSourceShapeFrame = _rotatedSourceAttachmentTransform
                                                   * _rotatedSourceBodyTransform
                                                   * _rotatedSourceShapeTransform;
            Transform3D rebasedSourceFrameTransform = skeleton.GetBoneGlobalRest(boneIndex).AffineInverse() * expectedSourceShapeFrame;

            Assert.False(
                descriptor.SourceShapeFrameTransform.Basis.IsEqualApprox(descriptor.LocalTransform.Basis),
                "Test fixture must exercise a source attachment basis that differs from the source model frame.");
            Assert.False(
                skeleton.GetBoneGlobalRest(boneIndex).Basis.IsEqualApprox(Basis.Identity),
                "Test fixture must exercise a rotated target bone rest basis.");
            AssertTransformApproximately(expectedSourceShapeFrame, descriptor.SourceShapeFrameTransform, PositionToleranceMetres);
            AssertTransformApproximately(Transform3D.Identity, proxyBody.Transform, PositionToleranceMetres);
            AssertTransformApproximately(attachment.GlobalTransform, ResolveNodeGlobalTransform(proxyBody), PositionToleranceMetres);
            AssertTransformApproximately(descriptor.LocalTransform, proxyShape.Transform, PositionToleranceMetres);
            AssertTransformApproximately(attachment.GlobalTransform * descriptor.LocalTransform, ResolveNodeGlobalTransform(proxyShape), PositionToleranceMetres);
            AssertTransformNotApproximately(rebasedSourceFrameTransform, proxyShape.Transform, PositionToleranceMetres);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>
    /// Verifies collider profiles expose reusable, non-duplicated shape descriptors by bone.
    /// </summary>
    [Headless]
    [Fact]
    public void BodyColliderProfile_QueryShapeDescriptors_ExposesOriginalShapeResourceAndFiltersByBone()
    {
        BoxShape3D sourceShapeResource = new()
        {
            Size = new Vector3(0.2f, 0.3f, 0.4f),
        };
        BodyColliderProfile colliderProfile = new()
        {
            SourceScene = CreatePackedSourceSceneWithShapeResource("Hips", "AuthoredShape", sourceShapeResource),
        };

        IReadOnlyList<BodyColliderShapeDescriptor> descriptors = colliderProfile.QueryShapeDescriptors();
        IReadOnlyList<BodyColliderShapeDescriptor> hipDescriptors = colliderProfile.QueryShapeDescriptorsForBone("Hips");

        BodyColliderShapeDescriptor descriptor = Assert.Single(descriptors);
        BodyColliderShapeDescriptor hipDescriptor = Assert.Single(hipDescriptors);
        Assert.Equal("AuthoredShape", descriptor.SourceShapeName);
        Assert.Equal("Hips", descriptor.SourceBoneName);
        Assert.Equal("Hips", descriptor.SourceIdentifier);
        Assert.Equal("SourceBody", descriptor.SourcePhysicsBodyName);
        Assert.True(ReferenceEquals(sourceShapeResource, descriptor.Shape), "Collider profile descriptors should expose the original Shape3D resource without duplicating it.");
        Assert.True(ReferenceEquals(sourceShapeResource, hipDescriptor.Shape), "Bone-filtered descriptors should expose the original Shape3D resource without duplicating it.");
        AssertTransformApproximately(Transform3D.Identity, descriptor.LocalTransform, PositionToleranceMetres);
    }

    /// <summary>
    /// Verifies the asset-backed collider profile references the canonical collider source and can query representative bones.
    /// </summary>
    [Headless]
    [Fact]
    public void BodyColliderProfile_AssetResource_LoadsByPathAndUIDAndQueriesReferenceColliders()
    {
        Resource profileResource = LoadResource(ColliderProfilePath);
        Resource profileResourceByUID = LoadResource(ColliderProfileUID);
        PackedScene sourceScene = ReadResourceProperty<PackedScene>(profileResource, "SourceScene");
        BodyColliderProfile queryProfile = new()
        {
            SourceScene = sourceScene,
        };

        IReadOnlyList<BodyColliderShapeDescriptor> descriptors = queryProfile.QueryShapeDescriptors();

        Assert.Equal(ColliderProfilePath, profileResource.ResourcePath);
        Assert.Equal(ColliderProfilePath, profileResourceByUID.ResourcePath);
        Assert.Equal(CollidersScenePath, sourceScene.ResourcePath);
        Assert.Equal(CountSourceShapes(sourceScene), descriptors.Count);
        Assert.Contains(descriptors, descriptor => descriptor.SourceBoneName == "Hips");
        Assert.Contains(descriptors, descriptor => descriptor.SourceBoneName == "LeftHand");
        Assert.Contains(descriptors, descriptor => descriptor.SourceBoneName == "breast_r");
    }

    /// <summary>
    /// Verifies generated collider wrapper/profile assets keep the post-import source chain reloadable.
    /// </summary>
    [Headless]
    [Fact]
    public void BodyColliderProfile_GeneratedWrapperReferencesImportedColliderBlend()
    {
        _ = LoadPackedScene(CollidersScenePath);
        BodyColliderProfile colliderProfile = Assert.IsType<BodyColliderProfile>(LoadResource(ColliderProfilePath), exactMatch: false);
        PackedScene profileSourceScene = Assert.IsType<PackedScene>(colliderProfile.SourceScene, exactMatch: false);
        string wrapperText = ReadTextResource(CollidersScenePath);

        Assert.Contains($"path=\"{ColliderSourceScenePath}\"", wrapperText, StringComparison.Ordinal);
        Assert.Equal(CollidersScenePath, profileSourceScene.ResourcePath);
    }

    /// <summary>
    /// Verifies the real reference female scene uses the shared collider profile.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleNpc_DynamicPhysicalRig_UsesSharedColliderProfile()
    {
        PackedScene referenceScene = LoadPackedScene(ReferenceFemaleNpcScenePath);
        Node sceneRoot = referenceScene.Instantiate();

        try
        {
            EnsureReferenceFemaleInventoryInstalled(sceneRoot);
            Node rig = Assert.IsType<Node>(
                sceneRoot.GetNodeOrNull("Female/GeneralSkeleton/DynamicPhysicalRig"),
                exactMatch: false);
            Resource colliderProfile = ReadResourceProperty<Resource>(rig, nameof(DynamicPhysicalRig.ColliderProfile));
            PackedScene profileSourceScene = ReadResourceProperty<PackedScene>(colliderProfile, "SourceScene");

            Assert.Equal(ColliderProfilePath, colliderProfile.ResourcePath);
            Assert.Equal(CollidersScenePath, profileSourceScene.ResourcePath);
            AssertNoDirectCollisionShape(sceneRoot, "IKTargets/Head");
            AssertNoDirectCollisionShape(sceneRoot, "IKTargets/RightHand");
            AssertNoDirectCollisionShape(sceneRoot, "IKTargets/LeftHand");
            AssertNoDirectCollisionShape(sceneRoot, "IKTargets/RightFoot");
            AssertNoDirectCollisionShape(sceneRoot, "IKTargets/LeftFoot");
        }
        finally
        {
            sceneRoot.Free();
        }
    }

    /// <summary>
    /// Verifies the reusable reference female scene lets the right-hand IK collision path act as an impact source.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleNpc_RightHandIKTarget_ProvidesReusableBallPushCollisionPath()
    {
        PackedScene referenceScene = LoadPackedScene(ReferenceFemaleNpcScenePath);
        Node sceneRoot = referenceScene.Instantiate();

        try
        {
            EnsureReferenceFemaleInventoryInstalled(sceneRoot);
            AnimatableBody3D rightHandTarget = Assert.IsType<AnimatableBody3D>(
                sceneRoot.GetNodeOrNull("IKTargets/RightHand"),
                exactMatch: false);
            Assert.Equal(8U, rightHandTarget.CollisionLayer);
            Assert.Equal(5U, rightHandTarget.CollisionMask);
            Assert.Null(sceneRoot.GetNodeOrNull("Female/GeneralSkeleton/RightHand/PhysicalImpactRelay"));
            Assert.DoesNotContain(rightHandTarget.GetChildren(), child => child is CollisionShape3D);
            Assert.Null(rightHandTarget.GetNodeOrNull("ImpactSource"));
        }
        finally
        {
            sceneRoot.Free();
        }
    }

    private static void EnsureReferenceFemaleInventoryInstalled(Node sceneRoot)
    {
        if (sceneRoot.HasNode("IKTargets/RightHand") && sceneRoot.HasNode("Female/GeneralSkeleton/DynamicPhysicalRig"))
        {
            return;
        }

        Node installer = sceneRoot.GetNodeOrNull("NPCCharacterInstaller")
            ?? sceneRoot.GetNode("BaseCharacterInstaller");
        Type installerType = installer.GetType();
        Type contextType = installerType.Assembly.GetType(typeof(SceneInstallationContext).FullName!)
            ?? throw new InvalidOperationException("Failed to resolve loaded SceneInstallationContext type.");
        object context = Activator.CreateInstance(contextType, sceneRoot, SceneInstallationMetadata.DefaultNamespace)
            ?? throw new InvalidOperationException("Failed to create loaded scene installation context.");
        object result = installerType.GetMethod(nameof(SceneInstaller.Install))?.Invoke(installer, [context])
            ?? throw new InvalidOperationException("Failed to invoke loaded scene installer.");
        bool succeeded = (bool)(result.GetType().GetProperty(nameof(SceneInstallationResult.Succeeded))?.GetValue(result) ?? false);
        object? errors = result.GetType().GetProperty(nameof(SceneInstallationResult.Errors))?.GetValue(result);
        string errorText = errors is IEnumerable<string> typedErrors
            ? string.Join('\n', typedErrors)
            : errors?.ToString() ?? string.Empty;
        Assert.True(succeeded, errorText);
    }

    /// <summary>
    /// Verifies reference player and dynamic prop assets keep impact wiring local to reusable item scenes.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPropAssets_PlayerDoesNotOwnDirectImpactRelayOrSourceResources()
    {
        PackedScene playerScene = LoadPackedScene(ReferencePlayerScenePath);
        Node player = playerScene.Instantiate();
        Node ball = LoadPackedScene(TestBallScenePath).Instantiate();
        Node stick = LoadPackedScene(TestStickScenePath).Instantiate();

        try
        {
            Assert.Null(player.GetNodeOrNull("IKTargets/RightHand/ImpactSource"));
            Assert.Null(player.GetNodeOrNull("Female/GeneralSkeleton/RightHand/PhysicalImpactRelay"));
            _ = AssertDynamicPropContract(ball, "test_ball.tscn");
            _ = AssertDynamicPropContract(stick, "test_stick.tscn");
            Assert.DoesNotContain(GetPackedSceneDependencyPaths(playerScene), path =>
                path.EndsWith("PhysicalInteractionCollisionRelay3D.cs", StringComparison.Ordinal)
                || path.EndsWith("PhysicalInteractionImpactSource3D.cs", StringComparison.Ordinal));
        }
        finally
        {
            player.Free();
            ball.Free();
            stick.Free();
        }
    }

    private static Node AssertDynamicPropContract(Node prop, string sceneName)
    {
        RigidBody3D propBody = Assert.IsType<RigidBody3D>(prop, exactMatch: false);
        Assert.True(propBody.IsInGroup("hand_dynamic_interaction_body"));
        Assert.Equal(2U, propBody.CollisionLayer);
        Assert.Equal(11U, propBody.CollisionMask);
        Node receiver = prop.GetNodeOrNull("ImpactInteractionReceiver")
            ?? throw new Xunit.Sdk.XunitException($"Expected {sceneName} to carry an impact receiver child.");
        Assert.Equal("uid://mwj3evwi2jvj", receiver.GetMeta("_custom_type_script").AsString());

        return receiver;
    }

    /// <summary>
    /// Verifies DynamicPhysicalRig fails clearly when the required collider profile is missing.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_MissingColliderProfile_FailsFast()
    {
        Skeleton3D skeleton = CreateSkeleton();
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
        };
        skeleton.AddChild(rig);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ForceBuildGeneratedRig(rig));

        Assert.Contains("collider profile", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rig.GeneratedProxyCount);
    }

    /// <summary>
    /// Verifies finger proxies are discovered from target skeleton bones and use generated capsule primitives.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_FingerBones_GeneratePrimitiveCapsuleProxiesWithoutSourceDescriptors()
    {
        Skeleton3D skeleton = CreateFingerSkeleton();
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = CreateColliderProfile(CreatePackedSourceSceneWithBoneAttachments(
                ("Hips", null),
                ("LeftHand", null),
                ("RightHand", null))),
        };
        skeleton.AddChild(rig);

        ForceBuildGeneratedRig(rig);

        AnimatableBody3D leftIndex = FindGeneratedProxyBody(rig, "LeftIndexProximal");
        AnimatableBody3D leftIndexTip = FindGeneratedProxyBody(rig, "LeftIndexDistal");
        AnimatableBody3D leftMiddle = FindGeneratedProxyBody(rig, "LeftMiddleProximal");
        AnimatableBody3D rightIndex = FindGeneratedProxyBody(rig, "RightIndexProximal");
        PhysicalBodyPart3D leftIndexReceiver = Assert.IsType<PhysicalBodyPart3D>(leftIndex, exactMatch: false);
        CollisionShape3D leftIndexShape = Assert.IsAssignableFrom<CollisionShape3D>(leftIndex.GetChild(0));
        CollisionShape3D leftIndexTipShape = Assert.IsAssignableFrom<CollisionShape3D>(leftIndexTip.GetChild(0));
        CollisionShape3D leftMiddleShape = Assert.IsAssignableFrom<CollisionShape3D>(leftMiddle.GetChild(0));
        CapsuleShape3D capsule = Assert.IsAssignableFrom<CapsuleShape3D>(leftIndexShape.Shape);
        CapsuleShape3D terminalCapsule = Assert.IsAssignableFrom<CapsuleShape3D>(leftIndexTipShape.Shape);
        CapsuleShape3D siblingInferredCapsule = Assert.IsAssignableFrom<CapsuleShape3D>(leftMiddleShape.Shape);
        Vector3 leftIndexSegmentOffset = ResolveRestOffsetToChild(skeleton, "LeftIndexProximal", "LeftIndexDistal");
        float leftIndexRestLength = leftIndexSegmentOffset.Length();
        Vector3 leftIndexDirection = leftIndexSegmentOffset.Normalized();
        Assert.False(leftIndex.TopLevel, "Generated finger proxies should inherit generated BoneAttachment3D transforms.");
        AssertTransformApproximately(Transform3D.Identity, leftIndex.Transform, PositionToleranceMetres);
        AssertTransformApproximately(leftIndexShape.Transform, ResolveNodeGlobalTransform(leftIndex).AffineInverse() * ResolveNodeGlobalTransform(leftIndexShape), PositionToleranceMetres);

        float capsuleCentreProjection = leftIndexShape.Transform.Origin.Dot(leftIndexDirection);
        float capsuleStartProjection = capsuleCentreProjection - (capsule.Height * 0.5f);
        float capsuleEndProjection = capsuleCentreProjection + (capsule.Height * 0.5f);
        float terminalSegmentLength = leftIndexRestLength;
        Vector3 terminalDirection = leftIndexSegmentOffset.Normalized();
        float terminalCentreProjection = leftIndexTipShape.Transform.Origin.Dot(terminalDirection);
        float terminalStartProjection = terminalCentreProjection - (terminalCapsule.Height * 0.5f);
        float terminalEndProjection = terminalCentreProjection + (terminalCapsule.Height * 0.5f);

        Assert.Equal(4, rig.GeneratedFingerProxyCount);
        Assert.Equal(7, rig.GeneratedProxyCount);
        Assert.Equal("LeftIndexProximal", leftIndexReceiver.BoneName.ToString());
        Assert.Contains("LeftIndexProximal", leftIndexReceiver.Tags);
        Assert.Equal("GeneratedFinger:LeftIndexProximal", leftIndexReceiver.SourceShapeId);
        Assert.Same(rig, leftIndexReceiver.OwningRig);
        _ = Assert.IsAssignableFrom<IPhysicalInteractionReceiver>(leftIndexReceiver);
        Assert.Equal(2, rig.AdjacentBoneExceptionPairCount);
        Assert.Equal(7, rig.FingerSideExceptionPairCount);
        _ = Assert.Single(rig.GetGeneratedProxyBodiesForBone("LeftIndexProximal"));
        Assert.Equal(3, rig.GetGeneratedFingerProxyCollisionShapesForHand("LeftHand").Count);
        _ = Assert.Single(rig.GetGeneratedFingerProxyCollisionShapesForHand("RightHand"));
        Assert.All(
            rig.GetGeneratedFingerProxyCollisionShapesForHand("LeftHand"),
            shape =>
            {
                Assert.NotNull(shape.Shape);
                Assert.True(GodotObject.IsInstanceValid(shape.SourceShape));
            });
        Assert.NotSame(capsule, terminalCapsule);
        Assert.True(capsule.Height >= capsule.Radius * 2.0f, "Finger capsule radius must remain valid for its height.");
        Assert.InRange(capsule.Height, leftIndexRestLength - PositionToleranceMetres, leftIndexRestLength + PositionToleranceMetres);
        Assert.InRange(capsuleCentreProjection, (leftIndexRestLength * 0.5f) - PositionToleranceMetres, (leftIndexRestLength * 0.5f) + PositionToleranceMetres);
        Assert.InRange(capsuleStartProjection, -PositionToleranceMetres, PositionToleranceMetres);
        Assert.InRange(capsuleEndProjection, leftIndexRestLength - PositionToleranceMetres, leftIndexRestLength + PositionToleranceMetres);
        Assert.True(terminalCapsule.Height >= terminalCapsule.Radius * 2.0f, "Terminal finger capsule radius must remain valid for its height.");
        Assert.InRange(terminalCapsule.Height, terminalSegmentLength - PositionToleranceMetres, terminalSegmentLength + PositionToleranceMetres);
        Assert.InRange(terminalCentreProjection, (terminalSegmentLength * 0.5f) - PositionToleranceMetres, (terminalSegmentLength * 0.5f) + PositionToleranceMetres);
        Assert.InRange(terminalStartProjection, -PositionToleranceMetres, PositionToleranceMetres);
        Assert.InRange(terminalEndProjection, terminalSegmentLength - PositionToleranceMetres, terminalSegmentLength + PositionToleranceMetres);
        Assert.InRange(siblingInferredCapsule.Height, leftIndexRestLength - PositionToleranceMetres, leftIndexRestLength + PositionToleranceMetres);
        AssertBodyHasCollisionException(leftIndex, FindGeneratedProxyBody(rig, "LeftHand"));
        AssertBodyHasCollisionException(leftIndex, leftIndexTip);
        AssertBodyHasCollisionException(leftIndex, leftMiddle);
        AssertBodyDoesNotHaveCollisionException(leftIndex, FindGeneratedProxyBody(rig, "RightHand"));
        AssertBodyDoesNotHaveCollisionException(leftIndex, rightIndex);
    }

    /// <summary>
    /// Verifies generated finger bodies ignore same-rig same-side hand/finger proxies without suppressing arm/body collisions.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_FingerSelfCollisionExceptions_IgnoreSameSideHandAndFingersOnlyWithinRig()
    {
        DynamicPhysicalRig rig = CreateBuiltFingerArmRig("FirstArmSkeleton");
        DynamicPhysicalRig secondRig = CreateBuiltFingerArmRig("SecondArmSkeleton");

        AnimatableBody3D leftIndex = FindGeneratedProxyBody(rig, "LeftIndexProximal");
        AnimatableBody3D leftHand = FindGeneratedProxyBody(rig, "LeftHand");
        AnimatableBody3D leftLowerArm = FindGeneratedProxyBody(rig, "LeftLowerArm");
        AnimatableBody3D leftWrist = FindGeneratedProxyBody(rig, "LeftWrist");
        AnimatableBody3D leftPalm = FindGeneratedProxyBody(rig, "LeftPalm");
        AnimatableBody3D leftMetacarpal = FindGeneratedProxyBody(rig, "LeftMetacarpal");
        AnimatableBody3D leftUpperArm = FindGeneratedProxyBody(rig, "LeftUpperArm");
        AnimatableBody3D leftShoulder = FindGeneratedProxyBody(rig, "LeftShoulder");
        AnimatableBody3D head = FindGeneratedProxyBody(rig, "Head");
        AnimatableBody3D hips = FindGeneratedProxyBody(rig, "Hips");
        AnimatableBody3D rightUpperArm = FindGeneratedProxyBody(rig, "RightUpperArm");
        AnimatableBody3D rightLowerArm = FindGeneratedProxyBody(rig, "RightLowerArm");
        AnimatableBody3D rightHand = FindGeneratedProxyBody(rig, "RightHand");
        AnimatableBody3D rightIndex = FindGeneratedProxyBody(rig, "RightIndexProximal");
        AnimatableBody3D secondHead = FindGeneratedProxyBody(secondRig, "Head");
        AnimatableBody3D secondLeftHand = FindGeneratedProxyBody(secondRig, "LeftHand");
        AnimatableBody3D secondLeftLowerArm = FindGeneratedProxyBody(secondRig, "LeftLowerArm");
        AnimatableBody3D secondLeftUpperArm = FindGeneratedProxyBody(secondRig, "LeftUpperArm");
        AnimatableBody3D secondLeftShoulder = FindGeneratedProxyBody(secondRig, "LeftShoulder");
        AnimatableBody3D secondLeftIndex = FindGeneratedProxyBody(secondRig, "LeftIndexProximal");
        AnimatableBody3D secondRightHand = FindGeneratedProxyBody(secondRig, "RightHand");
        AnimatableBody3D secondRightUpperArm = FindGeneratedProxyBody(secondRig, "RightUpperArm");
        AnimatableBody3D secondRightLowerArm = FindGeneratedProxyBody(secondRig, "RightLowerArm");
        AnimatableBody3D secondRightIndex = FindGeneratedProxyBody(secondRig, "RightIndexProximal");

        AssertBodiesHaveMutualCollisionExceptionButLayerReachable(leftIndex, leftHand);
        AssertBodiesAreCollisionReachable(leftIndex, leftLowerArm);
        AssertBodiesAreCollisionReachable(leftIndex, leftWrist);
        AssertBodiesAreCollisionReachable(leftIndex, leftPalm);
        AssertBodiesAreCollisionReachable(leftIndex, leftMetacarpal);
        AssertBodiesAreCollisionReachable(leftIndex, leftUpperArm);
        AssertBodiesAreCollisionReachable(leftIndex, leftShoulder);
        AssertBodiesAreCollisionReachable(leftIndex, hips);
        AssertBodiesAreCollisionReachable(leftIndex, head);
        AssertBodiesAreCollisionReachable(leftIndex, rightUpperArm);
        AssertBodiesAreCollisionReachable(leftIndex, rightLowerArm);
        AssertBodiesAreCollisionReachable(leftIndex, rightHand);
        AssertBodiesAreCollisionReachable(leftIndex, rightIndex);
        AssertBodiesAreCollisionReachable(leftIndex, secondHead);
        AssertBodiesAreCollisionReachable(leftIndex, secondLeftHand);
        AssertBodiesAreCollisionReachable(leftIndex, secondLeftLowerArm);
        AssertBodiesAreCollisionReachable(leftIndex, secondLeftUpperArm);
        AssertBodiesAreCollisionReachable(leftIndex, secondLeftShoulder);
        AssertBodiesAreCollisionReachable(leftIndex, secondLeftIndex);
        AssertBodiesAreCollisionReachable(leftIndex, secondRightHand);
        AssertBodiesAreCollisionReachable(leftIndex, secondRightUpperArm);
        AssertBodiesAreCollisionReachable(leftIndex, secondRightLowerArm);
        AssertBodiesAreCollisionReachable(leftIndex, secondRightIndex);
    }

    /// <summary>
    /// Verifies source-authored finger descriptors are not duplicated and are replaced with generated primitive finger geometry.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_DuplicateSourceFingerDescriptors_UseSingleGeneratedPrimitiveProxy()
    {
        Skeleton3D skeleton = CreateFingerSkeleton();
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = CreateColliderProfile(CreatePackedSourceSceneWithBoneAttachments(
                ("Hips", null),
                ("LeftHand", null),
                ("LeftIndexProximal", "LeftIndexProximalPrimaryAttachment"),
                ("LeftIndexProximal", "LeftIndexProximalDuplicateAttachment"))),
        };
        skeleton.AddChild(rig);

        ForceBuildGeneratedRig(rig);

        IReadOnlyList<PhysicsBody3D> leftIndexBodies = rig.GetGeneratedProxyBodiesForBone("LeftIndexProximal");
        AnimatableBody3D leftIndex = Assert.IsAssignableFrom<AnimatableBody3D>(Assert.Single(leftIndexBodies));
        CollisionShape3D leftIndexShape = Assert.IsAssignableFrom<CollisionShape3D>(leftIndex.GetChild(0));

        CapsuleShape3D sourceFingerCapsule = Assert.IsType<CapsuleShape3D>(leftIndexShape.Shape, exactMatch: false);
        Assert.True(sourceFingerCapsule.Height >= sourceFingerCapsule.Radius * 2.0f, "Source-backed finger proxies should also use valid generated capsule geometry.");
        Assert.Equal(4, rig.GeneratedFingerProxyCount);
        Assert.Equal(6, rig.GeneratedProxyCount);
    }

    /// <summary>
    /// Verifies same-side hand/finger exceptions are scoped to each generated rig instance.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_FingerSideCollisionExceptions_DoNotCrossRigInstances()
    {
        DynamicPhysicalRig firstRig = CreateBuiltFingerRig("FirstSkeleton");
        DynamicPhysicalRig secondRig = CreateBuiltFingerRig("SecondSkeleton");

        AnimatableBody3D firstLeftIndex = FindGeneratedProxyBody(firstRig, "LeftIndexProximal");
        AnimatableBody3D firstLeftMiddle = FindGeneratedProxyBody(firstRig, "LeftMiddleProximal");
        AnimatableBody3D firstLeftHand = FindGeneratedProxyBody(firstRig, "LeftHand");
        AnimatableBody3D secondLeftIndex = FindGeneratedProxyBody(secondRig, "LeftIndexProximal");
        AnimatableBody3D secondLeftMiddle = FindGeneratedProxyBody(secondRig, "LeftMiddleProximal");
        AnimatableBody3D secondLeftHand = FindGeneratedProxyBody(secondRig, "LeftHand");

        AssertBodyHasCollisionException(firstLeftIndex, firstLeftHand);
        AssertBodyHasCollisionException(firstLeftIndex, firstLeftMiddle);
        AssertBodyHasCollisionException(secondLeftIndex, secondLeftHand);
        AssertBodyHasCollisionException(secondLeftIndex, secondLeftMiddle);
        AssertBodyDoesNotHaveCollisionException(firstLeftIndex, secondLeftHand);
        AssertBodyDoesNotHaveCollisionException(firstLeftIndex, secondLeftIndex);
        AssertBodyDoesNotHaveCollisionException(firstLeftIndex, secondLeftMiddle);
        AssertBodyDoesNotHaveCollisionException(secondLeftIndex, firstLeftHand);
        AssertBodyDoesNotHaveCollisionException(secondLeftIndex, firstLeftIndex);
        AssertBodyDoesNotHaveCollisionException(secondLeftIndex, firstLeftMiddle);
    }

    /// <summary>
    /// Verifies generated proxy bodies inherit generated attachment pose changes through the scene tree.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_GeneratedProxyBodies_InheritGeneratedAttachmentPose()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            const string boneName = "LeftHand";
            AnimatableBody3D handProxy = FindGeneratedProxyBody(fixture.Rig, boneName);
            BoneAttachment3D handAttachment = FindGeneratedAttachment(fixture.Rig.TargetSkeleton!, boneName);
            Transform3D proxyGlobalTransform = ResolveNodeGlobalTransform(handProxy);
            await WaitForFramesAsync(sceneTree, 1);

            Assert.Same(handAttachment, handProxy.GetParent());
            Assert.False(handProxy.TopLevel, "Generated proxies must remain normal attachment children.");
            AssertTransformApproximately(Transform3D.Identity, handProxy.Transform, PositionToleranceMetres);
            AssertTransformApproximately(handAttachment.GlobalTransform, ResolveNodeGlobalTransform(handProxy), PositionToleranceMetres);
            AssertTransformApproximately(proxyGlobalTransform, ResolveNodeGlobalTransform(handProxy), PositionToleranceMetres);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies only hand motion uses the physics-timed rewrite while the deferred head path stays off that schedule.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HandsUsePhysicsProcess_WhileHeadStaysOffThatPath()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            CharacterBody3D head = fixture.HeadTarget;
            AnimatableBody3D rightHand = fixture.RightHandTarget;
            Transform3D initialHeadTransform = head.GlobalTransform;
            Transform3D initialTransform = rightHand.GlobalTransform;
            ulong initialPhysicsTickCount = fixture.PlayerVRIK.PhysicsActuatorTickCount;
            Transform3D movedControllerTransform = new(initialTransform.Basis, initialTransform.Origin + new Vector3(0.08f, 0.0f, 0.0f));
            fixture.RightHandPosition.GlobalTransform = movedControllerTransform;

            InvokeOnBeginStage(fixture.PlayerVRIK, 1.0d / 60.0d);
            AssertTransformApproximately(initialTransform, rightHand.GlobalTransform, PositionToleranceMetres);
            Assert.Equal(initialPhysicsTickCount, fixture.PlayerVRIK.PhysicsActuatorTickCount);

            InvokeUpdatePhysicalActuators(fixture.PlayerVRIK, 1.0d / 60.0d);

            AssertTransformApproximately(initialHeadTransform, head.GlobalTransform, PositionToleranceMetres);
            Assert.True(
                fixture.PlayerVRIK.PhysicsActuatorTickCount > initialPhysicsTickCount,
                "Physics actuator ticks should now be recorded only from the physics-timed path.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the AnimatableBody3D collision-actuator rewrite remains hand-only for the current subphase.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HandActuators_UseAnimatableCollisionActuator_WhileHeadRemainsBaseline()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            InvokeEnsureActuators(fixture.PlayerVRIK);

            IKTargetBodyActuator headActuator = GetPrivateField<IKTargetBodyActuator>(fixture.PlayerVRIK, "_headActuator");
            IKTargetAnimatableActuator rightHandActuator = GetPrivateField<IKTargetAnimatableActuator>(fixture.PlayerVRIK, "_rightHandActuator");
            IKTargetAnimatableActuator leftHandActuator = GetPrivateField<IKTargetAnimatableActuator>(fixture.PlayerVRIK, "_leftHandActuator");

            Assert.False(headActuator.UseDampedFollow);

            _ = Assert.IsType<AnimatableBody3D>(fixture.RightHandTarget);
            _ = Assert.IsType<AnimatableBody3D>(fixture.LeftHandTarget);

            Assert.Equal(fixture.PlayerVRIK.HandTargetMaximumSpeed, rightHandActuator.MaximumSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandTargetPositionResponsiveness, rightHandActuator.PositionResponsiveness);
            Assert.Equal(fixture.PlayerVRIK.HandTargetMaximumAcceleration, rightHandActuator.MaximumAcceleration);
            Assert.Equal(fixture.PlayerVRIK.HandTargetSettleDistance, rightHandActuator.SnapDistance);
            Assert.Equal(fixture.PlayerVRIK.HandTargetRotationResponsiveness, rightHandActuator.RotationResponsiveness);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicInteractionCollisionMask, rightHandActuator.DynamicBodyInteractionCollisionMask);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactApproachSpeedThreshold, rightHandActuator.DynamicImpactApproachSpeedThreshold);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactImpulsePerSpeed, rightHandActuator.DynamicImpactImpulsePerSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactImpulseCap, rightHandActuator.DynamicImpactImpulseCap);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedPushSpeedThreshold, rightHandActuator.DynamicSustainedPushSpeedThreshold);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedForcePerSpeed, rightHandActuator.DynamicSustainedForcePerSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedForceCap, rightHandActuator.DynamicSustainedForceCap);
            Assert.True(
                CountDynamicInteractionQueryShapes(rightHandActuator) > 0,
                "Right-hand actuator should receive profile-backed dynamic-interaction query shapes.");
            Assert.True(
                rightHandActuator.GeneratedMovementCollisionShapeCount > 0,
                "Right-hand actuator should generate profile-backed movement collision shapes for MoveAndCollide/TestMove.");

            Assert.Equal(fixture.PlayerVRIK.HandTargetMaximumSpeed, leftHandActuator.MaximumSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandTargetPositionResponsiveness, leftHandActuator.PositionResponsiveness);
            Assert.Equal(fixture.PlayerVRIK.HandTargetMaximumAcceleration, leftHandActuator.MaximumAcceleration);
            Assert.Equal(fixture.PlayerVRIK.HandTargetSettleDistance, leftHandActuator.SnapDistance);
            Assert.Equal(fixture.PlayerVRIK.HandTargetRotationResponsiveness, leftHandActuator.RotationResponsiveness);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicInteractionCollisionMask, leftHandActuator.DynamicBodyInteractionCollisionMask);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactApproachSpeedThreshold, leftHandActuator.DynamicImpactApproachSpeedThreshold);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactImpulsePerSpeed, leftHandActuator.DynamicImpactImpulsePerSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactImpulseCap, leftHandActuator.DynamicImpactImpulseCap);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedPushSpeedThreshold, leftHandActuator.DynamicSustainedPushSpeedThreshold);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedForcePerSpeed, leftHandActuator.DynamicSustainedForcePerSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedForceCap, leftHandActuator.DynamicSustainedForceCap);
            Assert.True(
                CountDynamicInteractionQueryShapes(leftHandActuator) > 0,
                "Left-hand actuator should receive profile-backed dynamic-interaction query shapes.");
            Assert.True(
                leftHandActuator.GeneratedMovementCollisionShapeCount > 0,
                "Left-hand actuator should generate profile-backed movement collision shapes for MoveAndCollide/TestMove.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies hand targets no longer require direct primitive target shapes.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HandTargets_DoNotRequireDirectPrimitiveShapes()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            InvokeEnsureActuators(fixture.PlayerVRIK);

            AssertNoDirectCollisionShape(fixture.RightHandTarget);
            AssertNoDirectCollisionShape(fixture.LeftHandTarget);
            Assert.True(CountGeneratedMovementCollisionShapes(fixture.RightHandTarget) > 0);
            Assert.True(CountGeneratedMovementCollisionShapes(fixture.LeftHandTarget) > 0);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the deferred head target no longer requires a direct primitive target shape.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HeadTarget_DoesNotRequireDirectPrimitiveShape()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            AssertNoDirectCollisionShape(fixture.HeadTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies deferred head collision work remains disabled for this hand-only subphase.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HeadTarget_DoesNotAddGeneratedProxyExceptionsInDeferredBaseline()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            CharacterBody3D head = fixture.HeadTarget;
            AnimatableBody3D headProxy = FindGeneratedProxyBody(fixture.Rig, "Head");
            AnimatableBody3D neckProxy = FindGeneratedProxyBody(fixture.Rig, "Neck");

            AssertBodyDoesNotHaveCollisionException(head, headProxy);
            AssertBodyDoesNotHaveCollisionException(head, neckProxy);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies each live hand target ignores its own generated hand and forearm proxies while retaining other body-proxy collisions.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HandTargets_IgnoreOwnHandAndForearmProxiesOnly()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            AnimatableBody3D rightHand = fixture.RightHandTarget;
            AnimatableBody3D rightHandProxy = FindGeneratedProxyBody(fixture.Rig, "RightHand");
            AnimatableBody3D rightLowerArmProxy = FindGeneratedProxyBody(fixture.Rig, "RightLowerArm");
            AnimatableBody3D rightUpperArmProxy = FindGeneratedProxyBody(fixture.Rig, "RightUpperArm");
            AnimatableBody3D chestProxy = FindGeneratedProxyBody(fixture.Rig, "Chest");

            AssertBodyHasCollisionException(rightHand, rightHandProxy);
            AssertBodyHasCollisionException(rightHand, rightLowerArmProxy);
            AssertBodyDoesNotHaveCollisionException(rightHand, rightUpperArmProxy);
            AssertBodyDoesNotHaveCollisionException(rightHand, chestProxy);

            Transform3D handProxyApproach = BuildApproachTransform(rightHandProxy.GlobalTransform, new Vector3(0.12f, 0.0f, 0.0f));
            bool ownHandCollisionBlocked = rightHand.TestMove(handProxyApproach, new Vector3(-0.08f, 0.0f, 0.0f));

            Assert.False(ownHandCollisionBlocked, "Right-hand motion tests should ignore the generated proxy for the same hand.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies live hand targets also ignore their own generated finger proxies so startup hand actuators do not push against their own fingers.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HandTargets_IgnoreOwnGeneratedFingerProxiesBidirectionally()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree, createSkeleton: CreateRuntimeFingerSkeleton);

        try
        {
            AnimatableBody3D rightHand = fixture.RightHandTarget;
            AnimatableBody3D rightIndex = FindGeneratedProxyBody(fixture.Rig, "RightIndexProximal");
            AnimatableBody3D leftIndex = FindGeneratedProxyBody(fixture.Rig, "LeftIndexProximal");

            AssertBodiesHaveMutualCollisionException(rightHand, rightIndex);
            AssertBodiesAreCollisionReachable(rightHand, leftIndex);
            AssertCollisionLayersCanInteract(rightHand, rightIndex);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies generated finger proxy shapes are mirrored under the live hand target before hand actuators move.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HandTargetActuators_SynchroniseFingerCollisionShapesBeforeMovement()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree, createSkeleton: CreateRuntimeFingerSkeleton);

        try
        {
            IReadOnlyList<GeneratedProxyCollisionShape> sourceShapes = fixture.Rig.GetGeneratedFingerProxyCollisionShapesForHand("RightHand");
            int proximalSourceIndex = ResolveSourceShapeIndex(sourceShapes, "RightIndexProximal");
            GeneratedProxyCollisionShape proximalSource = sourceShapes[proximalSourceIndex];
            BoneAttachment3D proximalAttachment = FindGeneratedAttachment(fixture.Rig.TargetSkeleton!, "RightIndexProximal");
            Transform3D movedAttachmentTransform = new(
                Basis.FromEuler(new Vector3(0.0f, 0.23f, 0.11f)) * proximalAttachment.Transform.Basis,
                proximalAttachment.Transform.Origin + new Vector3(0.03f, 0.01f, 0.02f));
            proximalAttachment.Transform = movedAttachmentTransform;

            InvokeUpdatePhysicalActuators(fixture.PlayerVRIK, 1.0d / 60.0d);

            IReadOnlyList<CollisionShape3D> mirroredShapes = GetGeneratedFingerMovementCollisionShapes(fixture.RightHandTarget, "Right");
            CollisionShape3D mirroredShape = mirroredShapes[proximalSourceIndex];
            Transform3D expectedMirrorTransform = ResolveNodeGlobalTransform(fixture.RightHandTarget).AffineInverse()
                                                  * ResolveNodeGlobalTransform(proximalSource.SourceShape);

            Assert.Equal(sourceShapes.Count, mirroredShapes.Count);
            Assert.True(ReferenceEquals(proximalSource.Shape, mirroredShape.Shape));
            AssertTransformApproximately(expectedMirrorTransform, mirroredShape.Transform, PositionToleranceMetres);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the live right-hand body preserves generated-proxy and external-body collision eligibility.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_RightHand_PreservesGeneratedProxyAndExternalBodyCollisionEligibility()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            AnimatableBody3D rightHand = fixture.RightHandTarget;
            AnimatableBody3D chestProxy = FindGeneratedProxyBody(fixture.Rig, "Chest");
            AnimatableBody3D upperArmProxy = FindGeneratedProxyBody(fixture.Rig, "RightUpperArm");
            AssertBodyDoesNotHaveCollisionException(rightHand, chestProxy);
            AssertBodyDoesNotHaveCollisionException(rightHand, upperArmProxy);
            AssertCollisionLayersCanInteract(rightHand, chestProxy);
            AssertCollisionLayersCanInteract(rightHand, upperArmProxy);

            Transform3D initialHandTransform = rightHand.GlobalTransform;
            Vector3 outwardDirection = (initialHandTransform.Origin - chestProxy.GlobalPosition).Normalized();
            StaticBody3D externalObstacle = CreateExternalObstacle(initialHandTransform.Origin + (outwardDirection * 0.16f));
            fixture.Root.AddChild(externalObstacle);
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            AssertCollisionLayersCanInteract(rightHand, externalObstacle);

            externalObstacle.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies reference breast colliders resolve directly through their source BoneAttachment3D bone names.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_ReferenceBreastColliders_ResolveThroughBoneAttachmentBoneName()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            AnimatableBody3D rightBreastProxy = FindGeneratedProxyBody(fixture.Rig, "breast_r");
            AnimatableBody3D leftBreastProxy = FindGeneratedProxyBody(fixture.Rig, "breast_l");

            Assert.Equal("breast_r", rightBreastProxy.GetMeta("alleycat_generated_physical_rig_source").AsString());
            Assert.Equal("breast_l", leftBreastProxy.GetMeta("alleycat_generated_physical_rig_source").AsString());
            Assert.Equal(0, fixture.Rig.SkippedSourceShapeCount);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies unresolved source BoneAttachment3D bone names skip only that shape while valid shapes still generate.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_UnresolvedSourceBoneName_SkipsShapeAndKeepsValidGeneratedProxy()
    {
        Skeleton3D skeleton = CreateSkeleton();
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = CreateColliderProfile(CreatePackedSourceSceneWithBoneAttachments(("Hips", null), ("UnmappedBone", null))),
        };
        skeleton.AddChild(rig);

        ForceBuildGeneratedRig(rig);

        AnimatableBody3D hipsProxy = FindGeneratedProxyBody(rig, "Hips");
        BoneAttachment3D hipsAttachment = Assert.IsType<BoneAttachment3D>(hipsProxy.GetParent());
        Assert.Equal("Hips", hipsAttachment.BoneName);
        Assert.Equal(1, rig.SkippedSourceShapeCount);
        Assert.Equal(1, rig.GeneratedProxyCount);
        Assert.Equal(1, CountGeneratedProxyBodies(rig));
    }

    /// <summary>
    /// Verifies source CollisionShape3D nodes without a Shape3D resource fail fast and clear any partially generated rig.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_MissingSourceShapeResource_FailsFastAndClearsPartialGeneratedRig()
    {
        Skeleton3D skeleton = CreateSkeleton();
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = CreateColliderProfile(CreatePackedSourceSceneWithMissingShapeResource()),
        };
        skeleton.AddChild(rig);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ForceBuildGeneratedRig(rig));

        Assert.Contains(nameof(Shape3D), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, rig.SkippedSourceShapeCount);
        Assert.Equal(0, rig.GeneratedProxyCount);
        Assert.Equal(0, CountGeneratedProxyBodies(rig));
    }

    /// <summary>
    /// Verifies the closest source BoneAttachment3D BoneName is used when intermediate node names differ.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_ClosestSourceBoneAttachmentBoneName_ResolvesTargetBone()
    {
        Skeleton3D skeleton = CreateSkeleton();
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = CreateColliderProfile(CreatePackedSourceSceneWithNestedBoneAttachment("Chest", "Hips", "DifferentIntermediateName", "DifferentPhysicsBodyName")),
        };
        skeleton.AddChild(rig);

        ForceBuildGeneratedRig(rig);

        AnimatableBody3D hipsProxy = FindGeneratedProxyBody(rig, "Hips");
        BoneAttachment3D hipsAttachment = Assert.IsType<BoneAttachment3D>(hipsProxy.GetParent());
        Assert.Equal("Hips", hipsAttachment.BoneName);
        Assert.Equal("Hips", hipsProxy.GetMeta("alleycat_generated_physical_rig_source").AsString());
        Assert.Equal(1, rig.GeneratedProxyCount);
        Assert.Equal(0, rig.SkippedSourceShapeCount);
    }

    /// <summary>
    /// Verifies source shapes without a BoneAttachment3D ancestor fail fast as required setup errors.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_MissingSourceBoneAttachment_FailsFast()
    {
        Skeleton3D skeleton = CreateSkeleton();
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = CreateColliderProfile(CreatePackedSourceSceneWithoutBoneAttachment()),
        };
        skeleton.AddChild(rig);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ForceBuildGeneratedRig(rig));
        Assert.Contains(nameof(BoneAttachment3D), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies empty source BoneAttachment3D BoneName values skip only that shape while valid shapes still generate.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRig_EmptySourceBoneName_SkipsShapeAndKeepsValidGeneratedProxy()
    {
        Skeleton3D skeleton = CreateSkeleton();
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = CreateColliderProfile(CreatePackedSourceSceneWithBoneAttachments(("Hips", null), ("", "BlankAttachment"))),
        };
        skeleton.AddChild(rig);

        ForceBuildGeneratedRig(rig);

        _ = FindGeneratedProxyBody(rig, "Hips");
        Assert.Equal(1, rig.SkippedSourceShapeCount);
        Assert.Equal(1, rig.GeneratedProxyCount);
        Assert.Equal(1, CountGeneratedProxyBodies(rig));
    }

    private static void AssertNoDirectCollisionShape(Node root, string targetPath)
    {
        Node target = root.GetNodeOrNull(targetPath)
                      ?? throw new Xunit.Sdk.XunitException($"Expected target node '{targetPath}' to exist.");
        AssertNoDirectCollisionShape(target);
    }

    private static void AssertNoDirectCollisionShape(Node target)
    {
        foreach (Node child in target.GetChildren())
        {
            Assert.False(
                child is CollisionShape3D && !child.HasMeta(IKTargetAnimatableActuator.GeneratedMovementCollisionShapeMetaKey),
                $"Expected '{target.GetPath()}' to rely on BodyColliderProfile data rather than direct primitive authored CollisionShape3D child '{child.Name}'.");
        }
    }

    private static int CountGeneratedMovementCollisionShapes(Node target)
    {
        int count = 0;
        foreach (Node child in target.GetChildren())
        {
            if (child is CollisionShape3D
                && child.HasMeta(IKTargetAnimatableActuator.GeneratedMovementCollisionShapeMetaKey))
            {
                count += 1;
            }
        }

        return count;
    }

    private static IReadOnlyList<CollisionShape3D> GetGeneratedFingerMovementCollisionShapes(Node target, string sideName)
    {
        List<CollisionShape3D> shapes = [];
        foreach (Node child in target.GetChildren())
        {
            if (child is CollisionShape3D collisionShape
                && child.HasMeta(IKTargetAnimatableActuator.GeneratedMovementCollisionShapeMetaKey)
                && child.Name.ToString().StartsWith($"Generated{sideName}FingerMovementCollisionShape_", StringComparison.Ordinal))
            {
                shapes.Add(collisionShape);
            }
        }

        return shapes;
    }

    private static int ResolveSourceShapeIndex(IReadOnlyList<GeneratedProxyCollisionShape> sourceShapes, string boneName)
    {
        for (int index = 0; index < sourceShapes.Count; index += 1)
        {
            if (TryResolveSourceBoneName(sourceShapes[index].SourceShape, out string sourceBoneName)
                && sourceBoneName == boneName)
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected generated source shape for bone '{boneName}'.");
    }

    private static bool TryResolveSourceBoneName(Node sourceShape, out string boneName)
    {
        for (Node? current = sourceShape.GetParent(); current is not null; current = current.GetParent())
        {
            if (current is BoneAttachment3D attachment)
            {
                boneName = attachment.BoneName;
                return true;
            }
        }

        boneName = string.Empty;
        return false;
    }

    private static T GetPrivateField<T>(PlayerVRIK playerVRIK, string fieldName)
    {
        FieldInfo field = GetNonPublicInstanceField(playerVRIK.GetType(), fieldName)
                          ?? throw new InvalidOperationException(
                              $"{playerVRIK.GetType().Name} field '{fieldName}' was not found in the inheritance chain.");

        return Assert.IsType<T>(field.GetValue(playerVRIK));
    }

    private static int CountDynamicInteractionQueryShapes(IKTargetAnimatableActuator actuator)
    {
        HandDynamicBodyInteractionController controller = actuator.DynamicBodyInteractionControllerForTesting;
        FieldInfo queryShapeSourcesField = GetNonPublicInstanceField(controller.GetType(), "_queryShapeSources")
                                           ?? throw new InvalidOperationException(
                                               "HandDynamicBodyInteractionController._queryShapeSources was not found.");
        Array queryShapeSources = Assert.IsAssignableFrom<Array>(queryShapeSourcesField.GetValue(controller));
        return queryShapeSources.Length;
    }

    private static void AssertBodyHasCollisionException(PhysicsBody3D source, PhysicsBody3D expected)
    {
        Godot.Collections.Array<PhysicsBody3D> exceptions = source.GetCollisionExceptions();
        Assert.Contains(exceptions, body => ReferenceEquals(body, expected));
    }

    private static void AssertBodiesHaveMutualCollisionException(PhysicsBody3D first, PhysicsBody3D second)
    {
        AssertBodyHasCollisionException(first, second);
        AssertBodyHasCollisionException(second, first);
    }

    private static void AssertBodiesHaveMutualCollisionExceptionButLayerReachable(PhysicsBody3D first, PhysicsBody3D second)
    {
        AssertBodiesHaveMutualCollisionException(first, second);
        AssertCollisionLayersCanInteract(first, second);
    }

    private static void AssertBodiesAreCollisionReachable(PhysicsBody3D first, PhysicsBody3D second)
    {
        AssertBodyDoesNotHaveCollisionException(first, second);
        AssertBodyDoesNotHaveCollisionException(second, first);
        AssertCollisionLayersCanInteract(first, second);
    }

    private static void AssertBodyDoesNotHaveCollisionException(PhysicsBody3D source, PhysicsBody3D other)
    {
        Godot.Collections.Array<PhysicsBody3D> exceptions = source.GetCollisionExceptions();
        Assert.DoesNotContain(exceptions, body => ReferenceEquals(body, other));
    }

    private static void AssertCollisionLayersCanInteract(PhysicsBody3D source, PhysicsBody3D other)
    {
        Assert.True((source.CollisionMask & other.CollisionLayer) != 0, $"{source.Name} mask should include {other.Name} layer.");
        Assert.True((other.CollisionMask & source.CollisionLayer) != 0, $"{other.Name} mask should include {source.Name} layer.");
    }

    private static Transform3D BuildApproachTransform(Transform3D targetTransform, Vector3 offset)
        => new(targetTransform.Basis, targetTransform.Origin + offset);

    private static StaticBody3D CreateExternalObstacle(Vector3 position)
    {
        StaticBody3D obstacle = new()
        {
            Name = "ExternalObstacle",
            GlobalPosition = position,
            CollisionLayer = 1,
            CollisionMask = 8,
        };

        CollisionShape3D shape = new()
        {
            Shape = new BoxShape3D
            {
                Size = new Vector3(0.12f, 0.12f, 0.12f),
            },
        };

        obstacle.AddChild(shape);
        return obstacle;
    }

    private static void AssertProxyShapeDataPreservedWithDescriptorTransform(
        PackedScene? sourceScene,
        AnimatableBody3D proxyBody,
        string boneName,
        Transform3D expectedLocalTransform)
    {
        Node sourceRoot = sourceScene?.Instantiate()
                          ?? throw new Xunit.Sdk.XunitException("BodyColliderProfile source scene should be configured.");

        try
        {
            CollisionShape3D sourceShape = FindSourceShapeForProfileBone(sourceRoot, boneName);
            CollisionShape3D proxyShape = Assert.IsAssignableFrom<CollisionShape3D>(proxyBody.GetChild(0));

            Assert.Equal(sourceShape.Name, proxyShape.Name);
            Assert.Equal(sourceShape.Disabled, proxyShape.Disabled);
            Assert.IsType(sourceShape.Shape.GetType(), proxyShape.Shape);
            AssertTransformApproximately(expectedLocalTransform, proxyShape.Transform, PositionToleranceMetres);
        }
        finally
        {
            sourceRoot.Free();
        }
    }

    private static CollisionShape3D FindSourceShapeForProfileBone(Node sourceRoot, string profileBoneName)
    {
        Stack<Node> pending = new([sourceRoot]);

        while (pending.Count > 0)
        {
            Node current = pending.Pop();
            if (current is CollisionShape3D shape)
            {
                BoneAttachment3D? sourceAttachment = FindClosestBoneAttachmentAncestor(shape);
                if (sourceAttachment is not null && sourceAttachment.BoneName == profileBoneName)
                {
                    return shape;
                }
            }

            foreach (Node child in current.GetChildren())
            {
                pending.Push(child);
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected source shape for mapped profile bone '{profileBoneName}'.");
    }

    private static Transform3D ResolveSourceShapeSkeletonTransform(PackedScene? sourceScene, string boneName)
    {
        Node sourceRoot = sourceScene?.Instantiate()
                          ?? throw new Xunit.Sdk.XunitException("BodyColliderProfile source scene should be configured.");

        try
        {
            CollisionShape3D sourceShape = FindSourceShapeForProfileBone(sourceRoot, boneName);
            BoneAttachment3D sourceAttachment = FindClosestBoneAttachmentAncestor(sourceShape)
                                                ?? throw new Xunit.Sdk.XunitException(
                                                    $"Expected source shape '{sourceShape.Name}' to have a BoneAttachment3D ancestor.");
            Node? sourceFrameRoot = FindAncestor<Skeleton3D>(sourceAttachment) ?? sourceAttachment.GetParent();
            return ComposeTransformRelativeToAncestor(sourceShape, sourceFrameRoot);
        }
        finally
        {
            sourceRoot.Free();
        }
    }

    private static Transform3D ComposeTransformRelativeToAncestor(Node3D node, Node? ancestor)
    {
        Transform3D transform = node.Transform;

        for (Node? current = node.GetParent(); current is not null && current != ancestor; current = current.GetParent())
        {
            if (current is Node3D current3D)
            {
                transform = current3D.Transform * transform;
            }
        }

        return transform;
    }

    private static T? FindAncestor<T>(Node start)
        where T : Node
    {
        for (Node? current = start.GetParent(); current is not null; current = current.GetParent())
        {
            if (current is T ancestor)
            {
                return ancestor;
            }
        }

        return null;
    }

    private static Transform3D ResolveNodeGlobalTransform(Node3D node)
        => node.TopLevel
            ? node.Transform
            : node.GetParent() is Node3D parent
                ? parent.GlobalTransform * node.Transform
                : node.GlobalTransform;

    private static BoneAttachment3D? FindClosestBoneAttachmentAncestor(Node start)
    {
        for (Node? current = start.GetParent(); current is not null; current = current.GetParent())
        {
            if (current is BoneAttachment3D attachment)
            {
                return attachment;
            }
        }

        return null;
    }

    private static BoneAttachment3D FindGeneratedAttachment(Skeleton3D skeleton, string boneName)
    {
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is BoneAttachment3D attachment && attachment.BoneName == boneName)
            {
                return attachment;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected generated attachment for bone '{boneName}'.");
    }

    private static AnimatableBody3D FindGeneratedProxyBody(DynamicPhysicalRig rig, string sourceBoneName)
    {
        foreach (Node child in rig.TargetSkeleton!.GetChildren())
        {
            if (child is not BoneAttachment3D attachment || attachment.BoneName != sourceBoneName)
            {
                continue;
            }

            if (attachment.GetNodeOrNull("ProxyBody") is not AnimatableBody3D proxyBody)
            {
                continue;
            }

            return proxyBody;
        }

        throw new Xunit.Sdk.XunitException($"Expected generated proxy body for source bone '{sourceBoneName}'.");
    }

    private static AnimatableBody3D FindGeneratedProxyBody(Skeleton3D skeleton, string sourceBoneName)
    {
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is not BoneAttachment3D attachment || attachment.BoneName != sourceBoneName)
            {
                continue;
            }

            if (attachment.GetNodeOrNull("ProxyBody") is not AnimatableBody3D proxyBody)
            {
                continue;
            }

            return proxyBody;
        }

        throw new Xunit.Sdk.XunitException($"Expected generated proxy body for source bone '{sourceBoneName}'.");
    }

    private static T ReadProperty<T>(object target, string propertyName)
    {
        object? value = target.GetType().GetProperty(propertyName)?.GetValue(target)
            ?? throw new Xunit.Sdk.XunitException($"Expected '{target.GetType().Name}' to expose property '{propertyName}'.");
        return Assert.IsType<T>(value, exactMatch: false);
    }

    private static int CountGeneratedProxyBodies(DynamicPhysicalRig rig)
    {
        int count = 0;

        foreach (Node child in rig.TargetSkeleton!.GetChildren())
        {
            if (child is not BoneAttachment3D attachment)
            {
                continue;
            }

            if (!attachment.Name.ToString().StartsWith("Collider_", StringComparison.Ordinal))
            {
                continue;
            }

            if (attachment.GetNodeOrNull("ProxyBody") is AnimatableBody3D)
            {
                count += 1;
            }
        }

        return count;
    }

    private static int CountGeneratedPhysicalInteractionReceivers(DynamicPhysicalRig rig)
    {
        int count = 0;

        foreach (Node child in rig.TargetSkeleton!.GetChildren())
        {
            if (child is not BoneAttachment3D attachment)
            {
                continue;
            }

            if (!attachment.Name.ToString().StartsWith("Collider_", StringComparison.Ordinal))
            {
                continue;
            }

            if (attachment.GetNodeOrNull("ProxyBody") is IPhysicalInteractionReceiver)
            {
                count += 1;
            }
        }

        return count;
    }

    private static void AssertGeneratedProxyBodiesUseInheritedAttachmentTopology(DynamicPhysicalRig rig)
    {
        foreach (Node child in rig.TargetSkeleton!.GetChildren())
        {
            if (child is not BoneAttachment3D attachment
                || !attachment.Name.ToString().StartsWith("Collider_", StringComparison.Ordinal))
            {
                continue;
            }

            AnimatableBody3D proxyBody = Assert.IsAssignableFrom<AnimatableBody3D>(attachment.GetNodeOrNull("ProxyBody"));
            CollisionShape3D proxyShape = Assert.IsAssignableFrom<CollisionShape3D>(proxyBody.GetChild(0));

            Assert.Same(attachment, proxyBody.GetParent());
            Assert.False(proxyBody.TopLevel, "Generated proxy bodies should inherit generated BoneAttachment3D transforms.");
            AssertTransformApproximately(Transform3D.Identity, proxyBody.Transform, PositionToleranceMetres);
            AssertTransformApproximately(
                attachment.GlobalTransform * proxyShape.Transform,
                ResolveNodeGlobalTransform(proxyShape),
                PositionToleranceMetres);
        }
    }

    private static Resource LoadResource(string resourcePath)
        => ResourceLoader.Load<Resource>(resourcePath)
           ?? throw new Xunit.Sdk.XunitException($"Expected resource '{resourcePath}' to load.");

    private static string ReadTextResource(string resourcePath)
    {
        string projectPath = ProjectSettings.GlobalizePath(resourcePath);
        return File.ReadAllText(projectPath);
    }

    private static BodyColliderProfile CreateColliderProfile(PackedScene sourceScene)
        => new()
        {
            SourceScene = sourceScene,
        };

    private static TResource ReadResourceProperty<TResource>(GodotObject owner, string propertyName)
        where TResource : Resource
    {
        Variant propertyValue = owner.Get(propertyName);
        Assert.Equal(Variant.Type.Object, propertyValue.VariantType);

        return Assert.IsType<TResource>(propertyValue.AsGodotObject(), exactMatch: false);
    }

    private static IReadOnlyList<string> GetPackedSceneDependencyPaths(PackedScene scene)
        => [.. ResourceLoader.GetDependencies(scene.ResourcePath)
            .Select(dependency => dependency.ToString())];

    private static int CountSourceShapes(PackedScene? sourceScene)
    {
        Node sourceRoot = sourceScene?.Instantiate()
                          ?? throw new Xunit.Sdk.XunitException("BodyColliderProfile source scene should be configured.");

        try
        {
            int count = 0;
            Stack<Node> pending = new([sourceRoot]);

            while (pending.Count > 0)
            {
                Node current = pending.Pop();
                if (current is CollisionShape3D)
                {
                    count += 1;
                }

                foreach (Node child in current.GetChildren())
                {
                    pending.Push(child);
                }
            }

            return count;
        }
        finally
        {
            sourceRoot.Free();
        }
    }

    private static void InvokeOnBeginStage(PlayerVRIK playerVRIK, double delta)
    {
        MethodInfo method = GetNonPublicInstanceMethod(playerVRIK.GetType(), "OnBeginStage")
                            ?? throw new InvalidOperationException(
                                $"{playerVRIK.GetType().Name}.OnBeginStage was not found in the inheritance chain.");

        _ = method.Invoke(playerVRIK, [delta]);
    }

    private static void InvokeUpdatePhysicalActuators(PlayerVRIK playerVRIK, double delta)
    {
        MethodInfo method = GetNonPublicInstanceMethod(playerVRIK.GetType(), "UpdatePhysicalActuators")
                            ?? throw new InvalidOperationException(
                                $"{playerVRIK.GetType().Name}.UpdatePhysicalActuators was not found in the inheritance chain.");

        _ = method.Invoke(playerVRIK, [delta]);
    }

    private static void InvokeEnsureActuators(object playerVRIK)
    {
        MethodInfo method = GetNonPublicInstanceMethod(playerVRIK.GetType(), "EnsureActuators")
                            ?? throw new InvalidOperationException(
                                $"{playerVRIK.GetType().Name}.EnsureActuators was not found in the inheritance chain.");

        _ = method.Invoke(playerVRIK, null);
    }

    private static FieldInfo? GetNonPublicInstanceField(Type type, string fieldName)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static MethodInfo? GetNonPublicInstanceMethod(Type type, string methodName)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            MethodInfo? method = current.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (method is not null)
            {
                return method;
            }
        }

        return null;
    }

    private static void ForceBuildGeneratedRig(DynamicPhysicalRig rig)
    {
        MethodInfo clearMethod = typeof(DynamicPhysicalRig).GetMethod(
                                     "ClearGeneratedRig",
                                     BindingFlags.Instance | BindingFlags.NonPublic)
                                 ?? throw new InvalidOperationException("DynamicPhysicalRig.ClearGeneratedRig was not found.");
        MethodInfo buildMethod = typeof(DynamicPhysicalRig).GetMethod(
                                     "BuildGeneratedRig",
                                     BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new InvalidOperationException("DynamicPhysicalRig.BuildGeneratedRig was not found.");

        InvokeReflectedMethod(clearMethod, rig, null);
        InvokeReflectedMethod(buildMethod, rig, null);
    }

    private static void InvokeReflectedMethod(MethodInfo method, object target, object?[]? args)
    {
        try
        {
            _ = method.Invoke(target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static void AssertTransformApproximately(Transform3D expected, Transform3D actual, float epsilon)
    {
        AssertVectorApproximately(expected.Origin, actual.Origin, epsilon);
        AssertBasisApproximately(expected.Basis, actual.Basis, epsilon);
    }

    private static void AssertTransformNotApproximately(Transform3D notExpected, Transform3D actual, float epsilon)
    {
        bool originMatches = notExpected.Origin.DistanceTo(actual.Origin) <= epsilon;
        bool basisMatches = notExpected.Basis.X.DistanceTo(actual.Basis.X) <= epsilon
                            && notExpected.Basis.Y.DistanceTo(actual.Basis.Y) <= epsilon
                            && notExpected.Basis.Z.DistanceTo(actual.Basis.Z) <= epsilon;

        Assert.False(originMatches && basisMatches, "Transforms unexpectedly matched within tolerance.");
    }

    private static void AssertBasisApproximately(Basis expected, Basis actual, float epsilon)
    {
        AssertVectorApproximately(expected.X, actual.X, epsilon);
        AssertVectorApproximately(expected.Y, actual.Y, epsilon);
        AssertVectorApproximately(expected.Z, actual.Z, epsilon);
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual, float epsilon)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
        Assert.InRange(actual.Z, expected.Z - epsilon, expected.Z + epsilon);
    }

    private static PackedScene CreatePackedSourceSceneWithBoneAttachments(params (string BoneName, string? AttachmentName)[] sourceAttachments)
    {
        Node root = new()
        {
            Name = "CollidersRoot",
        };

        for (int index = 0; index < sourceAttachments.Length; index += 1)
        {
            (string boneName, string? attachmentName) = sourceAttachments[index];
            BoneAttachment3D sourceBoneAttachment = new()
            {
                Name = attachmentName ?? $"{boneName}Attachment",
                BoneName = boneName,
            };
            AnimatableBody3D sourceBody = new()
            {
                Name = $"SourceBody{index}",
            };
            CollisionShape3D sourceShape = new()
            {
                Name = $"SourceShape{index}",
                Shape = new BoxShape3D
                {
                    Size = new Vector3(0.1f, 0.1f, 0.1f),
                },
            };

            root.AddChild(sourceBoneAttachment);
            sourceBoneAttachment.Owner = root;
            sourceBoneAttachment.AddChild(sourceBody);
            sourceBody.Owner = root;
            sourceBody.AddChild(sourceShape);
            sourceShape.Owner = root;
        }

        PackedScene sourceScene = new();
        Error packResult = sourceScene.Pack(root);
        root.Free();

        Assert.Equal(Error.Ok, packResult);
        return sourceScene;
    }

    private static DynamicPhysicalRig CreateBuiltFingerRig(string skeletonName)
    {
        Skeleton3D skeleton = CreateFingerSkeleton();
        skeleton.Name = skeletonName;
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = CreateColliderProfile(CreatePackedSourceSceneWithBoneAttachments(
                ("Hips", null),
                ("LeftHand", null),
                ("RightHand", null))),
        };
        skeleton.AddChild(rig);

        ForceBuildGeneratedRig(rig);
        return rig;
    }

    private static DynamicPhysicalRig CreateBuiltFingerArmRig(string skeletonName)
    {
        Skeleton3D skeleton = CreateFingerArmSkeleton();
        skeleton.Name = skeletonName;
        DynamicPhysicalRig rig = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = CreateColliderProfile(CreatePackedSourceSceneWithBoneAttachments(
                ("Hips", null),
                ("Head", null),
                ("LeftShoulder", null),
                ("LeftUpperArm", null),
                ("LeftLowerArm", null),
                ("LeftWrist", null),
                ("LeftHand", null),
                ("LeftPalm", null),
                ("LeftMetacarpal", null),
                ("RightUpperArm", null),
                ("RightLowerArm", null),
                ("RightHand", null))),
        };
        skeleton.AddChild(rig);

        ForceBuildGeneratedRig(rig);
        return rig;
    }

    private static PackedScene CreatePackedSourceSceneWithShapeResource(string boneName, string shapeName, Shape3D shapeResource)
    {
        Node root = new()
        {
            Name = "CollidersRoot",
        };
        BoneAttachment3D sourceBoneAttachment = new()
        {
            Name = $"{boneName}Attachment",
            BoneName = boneName,
        };
        AnimatableBody3D sourceBody = new()
        {
            Name = "SourceBody",
        };
        CollisionShape3D sourceShape = new()
        {
            Name = shapeName,
            Shape = shapeResource,
        };

        root.AddChild(sourceBoneAttachment);
        sourceBoneAttachment.Owner = root;
        sourceBoneAttachment.AddChild(sourceBody);
        sourceBody.Owner = root;
        sourceBody.AddChild(sourceShape);
        sourceShape.Owner = root;

        PackedScene sourceScene = new();
        Error packResult = sourceScene.Pack(root);
        root.Free();

        Assert.Equal(Error.Ok, packResult);
        return sourceScene;
    }

    private static PackedScene CreatePackedSourceSceneWithRotatedAttachmentAndShape(string boneName)
    {
        Node root = new()
        {
            Name = "CollidersRoot",
        };
        BoneAttachment3D sourceBoneAttachment = new()
        {
            Name = $"{boneName}Attachment",
            BoneName = boneName,
            Transform = _rotatedSourceAttachmentTransform,
        };
        AnimatableBody3D sourceBody = new()
        {
            Name = "SourceBody",
            Transform = _rotatedSourceBodyTransform,
        };
        CollisionShape3D sourceShape = new()
        {
            Name = "RotatedSourceShape",
            Shape = new BoxShape3D
            {
                Size = new Vector3(0.12f, 0.08f, 0.18f),
            },
            Transform = _rotatedSourceShapeTransform,
        };

        root.AddChild(sourceBoneAttachment);
        sourceBoneAttachment.Owner = root;
        sourceBoneAttachment.AddChild(sourceBody);
        sourceBody.Owner = root;
        sourceBody.AddChild(sourceShape);
        sourceShape.Owner = root;

        PackedScene sourceScene = new();
        Error packResult = sourceScene.Pack(root);
        root.Free();

        Assert.Equal(Error.Ok, packResult);
        return sourceScene;
    }

    private static PackedScene CreatePackedSourceSceneWithNestedBoneAttachment(
        string outerAttachmentBoneName,
        string innerAttachmentBoneName,
        string intermediateNodeName,
        string physicsBodyName)
    {
        Node root = new()
        {
            Name = "CollidersRoot",
        };
        BoneAttachment3D outerAttachment = new()
        {
            Name = "OuterAttachment",
            BoneName = outerAttachmentBoneName,
        };
        Node intermediate = new()
        {
            Name = intermediateNodeName,
        };
        BoneAttachment3D innerAttachment = new()
        {
            Name = "InnerAttachment",
            BoneName = innerAttachmentBoneName,
        };
        AnimatableBody3D sourceBody = new()
        {
            Name = physicsBodyName,
        };
        CollisionShape3D sourceShape = new()
        {
            Name = "SourceShape",
            Shape = new BoxShape3D
            {
                Size = new Vector3(0.1f, 0.1f, 0.1f),
            },
        };

        root.AddChild(outerAttachment);
        outerAttachment.Owner = root;
        outerAttachment.AddChild(intermediate);
        intermediate.Owner = root;
        intermediate.AddChild(innerAttachment);
        innerAttachment.Owner = root;
        innerAttachment.AddChild(sourceBody);
        sourceBody.Owner = root;
        sourceBody.AddChild(sourceShape);
        sourceShape.Owner = root;

        PackedScene sourceScene = new();
        Error packResult = sourceScene.Pack(root);
        root.Free();

        Assert.Equal(Error.Ok, packResult);
        return sourceScene;
    }

    private static PackedScene CreatePackedSourceSceneWithMissingShapeResource()
    {
        Node root = new()
        {
            Name = "CollidersRoot",
        };
        BoneAttachment3D validAttachment = new()
        {
            Name = "HipsAttachment",
            BoneName = "Hips",
        };
        AnimatableBody3D validSourceBody = new()
        {
            Name = "ValidSourceBody",
        };
        CollisionShape3D validSourceShape = new()
        {
            Name = "ValidSourceShape",
            Shape = new BoxShape3D
            {
                Size = new Vector3(0.1f, 0.1f, 0.1f),
            },
        };
        BoneAttachment3D invalidAttachment = new()
        {
            Name = "ChestAttachment",
            BoneName = "Chest",
        };
        AnimatableBody3D invalidSourceBody = new()
        {
            Name = "InvalidSourceBody",
        };
        CollisionShape3D invalidSourceShape = new()
        {
            Name = "MissingShapeResource",
            Shape = null,
        };

        root.AddChild(invalidAttachment);
        invalidAttachment.Owner = root;
        invalidAttachment.AddChild(invalidSourceBody);
        invalidSourceBody.Owner = root;
        invalidSourceBody.AddChild(invalidSourceShape);
        invalidSourceShape.Owner = root;
        root.AddChild(validAttachment);
        validAttachment.Owner = root;
        validAttachment.AddChild(validSourceBody);
        validSourceBody.Owner = root;
        validSourceBody.AddChild(validSourceShape);
        validSourceShape.Owner = root;

        PackedScene sourceScene = new();
        Error packResult = sourceScene.Pack(root);
        root.Free();

        Assert.Equal(Error.Ok, packResult);
        return sourceScene;
    }

    private static PackedScene CreatePackedSourceSceneWithoutBoneAttachment()
    {
        Node root = new()
        {
            Name = "CollidersRoot",
        };
        AnimatableBody3D sourceBody = new()
        {
            Name = "SourceBody",
        };
        CollisionShape3D sourceShape = new()
        {
            Name = "SourceShape",
            Shape = new BoxShape3D
            {
                Size = new Vector3(0.1f, 0.1f, 0.1f),
            },
        };

        root.AddChild(sourceBody);
        sourceBody.Owner = root;
        sourceBody.AddChild(sourceShape);
        sourceShape.Owner = root;

        PackedScene sourceScene = new();
        Error packResult = sourceScene.Pack(root);
        root.Free();

        Assert.Equal(Error.Ok, packResult);
        return sourceScene;
    }

    private static Skeleton3D CreateSkeleton()
    {
        Skeleton3D skeleton = new()
        {
            Name = "GeneralSkeleton",
        };

        Dictionary<string, int> boneIndices = [];

        foreach ((string name, _, _) in RuntimeFixture.BoneDefinitions)
        {
            boneIndices[name] = skeleton.AddBone(name);
        }

        foreach ((string name, string? parent, Vector3 position) in RuntimeFixture.BoneDefinitions)
        {
            int boneIndex = boneIndices[name];
            skeleton.SetBoneRest(boneIndex, new Transform3D(Basis.Identity, position));

            if (parent is not null)
            {
                skeleton.SetBoneParent(boneIndex, boneIndices[parent]);
            }
        }

        return skeleton;
    }

    private static Skeleton3D CreateSkeletonWithRotatedLeftHandRest()
    {
        Skeleton3D skeleton = CreateSkeleton();
        int leftHandIndex = skeleton.FindBone("LeftHand");
        Assert.True(leftHandIndex >= 0, "Expected test skeleton to contain LeftHand.");

        Transform3D existingRest = skeleton.GetBoneRest(leftHandIndex);
        skeleton.SetBoneRest(
            leftHandIndex,
            new Transform3D(
                Basis.FromEuler(new Vector3(-0.32f, 0.24f, 0.18f)),
                existingRest.Origin));

        return skeleton;
    }

    private static Skeleton3D CreateRuntimeFingerSkeleton()
    {
        Skeleton3D skeleton = new()
        {
            Name = "GeneralSkeleton",
        };

        List<(string Name, string? Parent, Vector3 LocalPosition)> boneDefinitions = [.. RuntimeFixture.BoneDefinitions];
        boneDefinitions.Add(("RightIndexProximal", "RightHand", new Vector3(0.02f, 0.0f, 0.05f)));
        boneDefinitions.Add(("RightIndexDistal", "RightIndexProximal", new Vector3(0.0f, 0.0f, 0.04f)));
        boneDefinitions.Add(("LeftIndexProximal", "LeftHand", new Vector3(-0.02f, 0.0f, 0.05f)));
        boneDefinitions.Add(("LeftIndexDistal", "LeftIndexProximal", new Vector3(0.0f, 0.0f, 0.04f)));

        AddBonesToSkeleton(skeleton, boneDefinitions);
        return skeleton;
    }

    private static Skeleton3D CreateFingerSkeleton()
    {
        Skeleton3D skeleton = new()
        {
            Name = "GeneralSkeleton",
        };

        (string Name, string? Parent, Vector3 LocalPosition)[] boneDefinitions =
        [
            ("Hips", null, Vector3.Zero),
            ("LeftHand", "Hips", new Vector3(-0.35f, 1.0f, 0.0f)),
            ("RightHand", "Hips", new Vector3(0.35f, 1.0f, 0.0f)),
            ("LeftIndexProximal", "LeftHand", new Vector3(-0.02f, 0.0f, 0.05f)),
            ("LeftIndexDistal", "LeftIndexProximal", new Vector3(0.0f, 0.0f, 0.04f)),
            ("LeftMiddleProximal", "LeftHand", new Vector3(0.0f, 0.0f, 0.055f)),
            ("RightIndexProximal", "RightHand", new Vector3(0.02f, 0.0f, 0.05f)),
            ("RightToes", "Hips", new Vector3(0.12f, -0.9f, 0.15f)),
        ];
        Dictionary<string, int> boneIndices = [];

        foreach ((string name, _, _) in boneDefinitions)
        {
            boneIndices[name] = skeleton.AddBone(name);
        }

        foreach ((string name, string? parent, Vector3 localPosition) in boneDefinitions)
        {
            int boneIndex = boneIndices[name];
            skeleton.SetBoneRest(boneIndex, new Transform3D(Basis.Identity, localPosition));

            if (parent is not null)
            {
                skeleton.SetBoneParent(boneIndex, boneIndices[parent]);
            }
        }

        return skeleton;
    }

    private static Skeleton3D CreateFingerArmSkeleton()
    {
        Skeleton3D skeleton = new()
        {
            Name = "GeneralSkeleton",
        };

        (string Name, string? Parent, Vector3 LocalPosition)[] boneDefinitions =
        [
            ("Hips", null, Vector3.Zero),
            ("Neck", "Hips", new Vector3(0.0f, 0.92f, 0.0f)),
            ("Head", "Neck", new Vector3(0.0f, 0.15f, 0.0f)),
            ("LeftShoulder", "Hips", new Vector3(-0.15f, 0.95f, 0.0f)),
            ("LeftUpperArm", "LeftShoulder", new Vector3(-0.18f, -0.02f, 0.0f)),
            ("LeftLowerArm", "LeftUpperArm", new Vector3(-0.18f, -0.03f, 0.0f)),
            ("LeftWrist", "LeftLowerArm", new Vector3(-0.07f, -0.01f, 0.0f)),
            ("LeftHand", "LeftWrist", new Vector3(-0.04f, -0.01f, 0.0f)),
            ("LeftPalm", "LeftHand", new Vector3(-0.01f, 0.0f, 0.02f)),
            ("LeftMetacarpal", "LeftPalm", new Vector3(-0.01f, 0.0f, 0.02f)),
            ("RightShoulder", "Hips", new Vector3(0.15f, 0.95f, 0.0f)),
            ("RightUpperArm", "RightShoulder", new Vector3(0.18f, -0.02f, 0.0f)),
            ("RightLowerArm", "RightUpperArm", new Vector3(0.18f, -0.03f, 0.0f)),
            ("RightHand", "RightLowerArm", new Vector3(0.11f, -0.02f, 0.0f)),
            ("LeftIndexProximal", "LeftHand", new Vector3(-0.02f, 0.0f, 0.05f)),
            ("LeftIndexDistal", "LeftIndexProximal", new Vector3(0.0f, 0.0f, 0.04f)),
            ("LeftMiddleProximal", "LeftHand", new Vector3(0.0f, 0.0f, 0.055f)),
            ("RightIndexProximal", "RightHand", new Vector3(0.02f, 0.0f, 0.05f)),
        ];

        AddBonesToSkeleton(skeleton, boneDefinitions);
        return skeleton;
    }

    private static void AddBonesToSkeleton(
        Skeleton3D skeleton,
        IReadOnlyList<(string Name, string? Parent, Vector3 LocalPosition)> boneDefinitions)
    {
        Dictionary<string, int> boneIndices = [];

        foreach ((string name, _, _) in boneDefinitions)
        {
            boneIndices[name] = skeleton.AddBone(name);
        }

        foreach ((string name, string? parent, Vector3 localPosition) in boneDefinitions)
        {
            int boneIndex = boneIndices[name];
            skeleton.SetBoneRest(boneIndex, new Transform3D(Basis.Identity, localPosition));

            if (parent is not null)
            {
                skeleton.SetBoneParent(boneIndex, boneIndices[parent]);
            }
        }
    }

    private static Vector3 ResolveRestOffsetToChild(Skeleton3D skeleton, string parentBoneName, string childBoneName)
    {
        int parentBoneIndex = skeleton.FindBone(parentBoneName);
        int childBoneIndex = skeleton.FindBone(childBoneName);
        Assert.True(parentBoneIndex >= 0, $"Expected skeleton to contain bone '{parentBoneName}'.");
        Assert.True(childBoneIndex >= 0, $"Expected skeleton to contain bone '{childBoneName}'.");

        Transform3D parentRest = skeleton.GetBoneGlobalRest(parentBoneIndex);
        return parentRest.AffineInverse() * skeleton.GetBoneGlobalRest(childBoneIndex).Origin;
    }

    private sealed class RuntimeFixture(
        Node3D root,
        CharacterBody3D player,
        PlayerVRIK playerVRIK,
        DynamicPhysicalRig rig,
        CharacterBody3D headTarget,
        AnimatableBody3D rightHandTarget,
        AnimatableBody3D leftHandTarget,
        Node3D rightHandPosition)
    {
        internal static readonly (string Name, string? Parent, Vector3 Position)[] BoneDefinitions =
        [
            ("Hips", null, new Vector3(0.0f, 1.00f, 0.00f)),
            ("Spine", "Hips", new Vector3(0.0f, 1.15f, 0.00f)),
            ("Chest", "Spine", new Vector3(0.0f, 1.30f, 0.02f)),
            ("UpperChest", "Chest", new Vector3(0.0f, 1.42f, 0.04f)),
            ("Neck", "UpperChest", new Vector3(0.0f, 1.52f, 0.04f)),
            ("Head", "Neck", new Vector3(0.0f, 1.62f, 0.05f)),
            ("RightShoulder", "UpperChest", new Vector3(0.18f, 1.42f, 0.03f)),
            ("RightUpperArm", "RightShoulder", new Vector3(0.38f, 1.40f, 0.03f)),
            ("RightLowerArm", "RightUpperArm", new Vector3(0.55f, 1.33f, 0.02f)),
            ("RightHand", "RightLowerArm", new Vector3(0.72f, 1.28f, 0.02f)),
            ("LeftShoulder", "UpperChest", new Vector3(-0.18f, 1.42f, 0.03f)),
            ("LeftUpperArm", "LeftShoulder", new Vector3(-0.38f, 1.40f, 0.03f)),
            ("LeftLowerArm", "LeftUpperArm", new Vector3(-0.55f, 1.33f, 0.02f)),
            ("LeftHand", "LeftLowerArm", new Vector3(-0.72f, 1.28f, 0.02f)),
            ("RightUpperLeg", "Hips", new Vector3(0.10f, 0.86f, 0.00f)),
            ("RightLowerLeg", "RightUpperLeg", new Vector3(0.10f, 0.50f, 0.00f)),
            ("RightFoot", "RightLowerLeg", new Vector3(0.10f, 0.10f, 0.08f)),
            ("RightToes", "RightFoot", new Vector3(0.10f, 0.04f, 0.20f)),
            ("LeftUpperLeg", "Hips", new Vector3(-0.10f, 0.86f, 0.00f)),
            ("LeftLowerLeg", "LeftUpperLeg", new Vector3(-0.10f, 0.50f, 0.00f)),
            ("LeftFoot", "LeftLowerLeg", new Vector3(-0.10f, 0.10f, 0.08f)),
            ("LeftToes", "LeftFoot", new Vector3(-0.10f, 0.04f, 0.20f)),
            ("breast_r", "Chest", new Vector3(0.10f, 1.34f, 0.10f)),
            ("breast_l", "Chest", new Vector3(-0.10f, 1.34f, 0.10f)),
        ];

        public Node3D Root { get; } = root;

        public CharacterBody3D Player { get; } = player;

        public PlayerVRIK PlayerVRIK { get; } = playerVRIK;

        public DynamicPhysicalRig Rig { get; } = rig;

        public CharacterBody3D HeadTarget { get; } = headTarget;

        public AnimatableBody3D RightHandTarget { get; } = rightHandTarget;

        public AnimatableBody3D LeftHandTarget { get; } = leftHandTarget;

        public Node3D RightHandPosition { get; } = rightHandPosition;

        public static async Task<RuntimeFixture> CreateAsync(
            SceneTree sceneTree,
            Action<PlayerVRIK>? configurePlayerVRIK = null,
            Func<Skeleton3D>? createSkeleton = null)
        {
            Node3D root = new()
            {
                Name = "DynamicPhysicalRigTestRoot",
            };

            CharacterBody3D player = new()
            {
                Name = "Player",
            };
            root.AddChild(player);

            Skeleton3D skeleton = createSkeleton?.Invoke() ?? CreateSkeleton();
            player.AddChild(skeleton);

            BoneAttachment3D headAttachment = new()
            {
                Name = "HeadAttachment",
                BoneName = "Head",
                BoneIdx = skeleton.FindBone("Head"),
            };
            skeleton.AddChild(headAttachment);

            Marker3D viewpoint = new()
            {
                Name = "Viewpoint",
                Transform = new Transform3D(Basis.Identity, new Vector3(0.0f, 0.03f, 0.09f)),
            };
            headAttachment.AddChild(viewpoint);

            DynamicPhysicalRig rig = new()
            {
                Name = "DynamicPhysicalRig",
                TargetSkeleton = skeleton,
                ColliderProfile = new BodyColliderProfile
                {
                    SourceScene = LoadPackedScene(CollidersScenePath),
                },
                GenerateInEditor = true,
            };
            skeleton.AddChild(rig);

            Node3D ikTargets = new()
            {
                Name = "IKTargets",
            };
            player.AddChild(ikTargets);

            CharacterBody3D headTarget = CreateCharacterIkTargetBody("Head", new Vector3(0.0f, 1.62f, 0.05f), 1, 1, shape: null);
            AnimatableBody3D rightHandTarget = CreateAnimatableIkTargetBody("RightHand", new Vector3(0.72f, 1.28f, 0.02f), 8, 5, shape: null);
            AnimatableBody3D leftHandTarget = CreateAnimatableIkTargetBody("LeftHand", new Vector3(-0.72f, 1.28f, 0.02f), 8, 5, shape: null);
            Node3D headSolveTarget = new Marker3D { Name = "HeadSolve", TopLevel = true, GlobalTransform = headTarget.GlobalTransform };

            ikTargets.AddChild(headTarget);
            ikTargets.AddChild(headSolveTarget);
            ikTargets.AddChild(rightHandTarget);
            ikTargets.AddChild(leftHandTarget);

            PlayerVRIK playerVRIK = new()
            {
                Name = "VRIK",
                Viewpoint = viewpoint,
                HeadIKTarget = headTarget,
                HeadIKSolveTarget = headSolveTarget,
                RightHandIKTarget = rightHandTarget,
                LeftHandIKTarget = leftHandTarget,
                PhysicalRig = rig,
            };
            configurePlayerVRIK?.Invoke(playerVRIK);
            player.AddChild(playerVRIK);

            Node3D originNode = new()
            {
                Name = "Origin",
            };
            Camera3D cameraNode = new()
            {
                Name = "Camera",
            };
            Node3D rightControllerNode = new()
            {
                Name = "RightController",
            };
            Node3D rightHandPosition = new()
            {
                Name = "RightHandPosition",
            };
            Node3D leftControllerNode = new()
            {
                Name = "LeftController",
            };
            Node3D leftHandPosition = new()
            {
                Name = "LeftHandPosition",
            };

            root.AddChild(originNode);
            originNode.AddChild(cameraNode);
            originNode.AddChild(rightControllerNode);
            rightControllerNode.AddChild(rightHandPosition);
            originNode.AddChild(leftControllerNode);
            leftControllerNode.AddChild(leftHandPosition);

            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 2);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            ForceBuildGeneratedRig(rig);
            await WaitForFramesAsync(sceneTree, 1);
            await WaitForPhysicsFramesAsync(sceneTree, 2);

            cameraNode.GlobalTransform = headTarget.GlobalTransform * viewpoint.Transform;
            rightHandPosition.GlobalTransform = rightHandTarget.GlobalTransform;
            leftHandPosition.GlobalTransform = leftHandTarget.GlobalTransform;

            bool bound = playerVRIK.BindToXRRuntime(
                new TestXROrigin(originNode),
                new TestXRCamera(cameraNode));

            Assert.True(bound);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            return new RuntimeFixture(root, player, playerVRIK, rig, headTarget, rightHandTarget, leftHandTarget, rightHandPosition);
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }

        private static CharacterBody3D CreateCharacterIkTargetBody(string name, Vector3 position, uint collisionLayer, uint collisionMask, Shape3D? shape)
        {
            CharacterBody3D body = new()
            {
                Name = name,
                TopLevel = true,
                CollisionLayer = collisionLayer,
                CollisionMask = collisionMask,
                MotionMode = CharacterBody3D.MotionModeEnum.Floating,
                GlobalPosition = position,
            };

            if (shape is null)
            {
                return body;
            }

            CollisionShape3D collisionShape = new()
            {
                Name = "CollisionShape3D",
                Shape = shape,
            };

            body.AddChild(collisionShape);
            return body;
        }

        private static AnimatableBody3D CreateAnimatableIkTargetBody(string name, Vector3 position, uint collisionLayer, uint collisionMask, Shape3D? shape)
        {
            AnimatableBody3D body = new()
            {
                Name = name,
                TopLevel = true,
                SyncToPhysics = false,
                CollisionLayer = collisionLayer,
                CollisionMask = collisionMask,
                GlobalPosition = position,
            };

            if (shape is null)
            {
                return body;
            }

            CollisionShape3D collisionShape = new()
            {
                Name = "CollisionShape3D",
                Shape = shape,
            };

            body.AddChild(collisionShape);
            return body;
        }
    }

    private sealed class TestXROrigin(Node3D originNode) : IXROrigin
    {
        public Node3D OriginNode => originNode;

        public float WorldScale { get; set; } = 1.0f;
    }

    private sealed class TestXRCamera(Camera3D cameraNode) : IXRCamera
    {
        public Camera3D CameraNode => cameraNode;
    }

}
