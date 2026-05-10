using System.Reflection;
using AlleyCat.Body;
using AlleyCat.IK;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Body;

/// <summary>
/// Integration coverage for physics-timed IK target following and generated proxy collision rigging.
/// </summary>
public sealed class DynamicPhysicalRigIntegrationTests
{
    private const string CollidersScenePath = "res://assets/characters/reference/female/colliders.tscn";
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";
    private const float PositionToleranceMetres = 0.001f;

    /// <summary>
    /// Verifies runtime setup builds the generated proxy count and enables manual physics synchronisation.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_RuntimeSetup_BuildsExpectedProxyCountWithManualPhysicsSync()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            int sourceShapeCount = CountSourceShapes(fixture.Rig.SourceScene);
            int generatedBodyCount = CountGeneratedProxyBodies(fixture.Rig);

            Assert.Equal(sourceShapeCount, fixture.Rig.GeneratedProxyCount);
            Assert.Equal(sourceShapeCount, generatedBodyCount);
            Assert.Equal(0, fixture.Rig.SkippedSourceShapeCount);
            Assert.True(fixture.Rig.IsPhysicsProcessing(), "Generated top-level proxy bodies require the rig's manual physics sync loop.");
            Assert.True(fixture.Rig.PhysicsProxySyncTickCount > 0, "Generated proxy bodies should be synchronised immediately and on physics ticks.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
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
            AnimatableBody3D handProxy = FindGeneratedProxyBody(fixture.Rig, rotatedBoneName);
            BoneAttachment3D handAttachment = FindGeneratedAttachment(fixture.Rig.TargetSkeleton!, rotatedBoneName);
            Transform3D expectedProxyLocalTransform = ResolveExpectedProxyLocalTransform(fixture.Rig.SourceScene, rotatedBoneName);
            CollisionShape3D proxyShape = Assert.IsAssignableFrom<CollisionShape3D>(handProxy.GetChild(0));
            Transform3D attachmentGlobalTransform = ResolveNodeGlobalTransform(handAttachment);
            Transform3D proxyGlobalTransform = ResolveNodeGlobalTransform(handProxy);

            Assert.True(handProxy.TopLevel, "Generated proxies should be top-level because manual sync writes global transforms.");
            AssertTransformApproximately(expectedProxyLocalTransform, attachmentGlobalTransform.AffineInverse() * proxyGlobalTransform, PositionToleranceMetres);
            AssertTransformApproximately(Transform3D.Identity, proxyShape.Transform, PositionToleranceMetres);
            Assert.False(handProxy.SyncToPhysics, "Generated proxies are manually synchronised during the rig physics tick.");
            Assert.Equal(fixture.Rig.ProxyCollisionLayer, handProxy.CollisionLayer);
            Assert.Equal(fixture.Rig.ProxyCollisionMask, handProxy.CollisionMask);

            AssertProxyShapeDataPreservedWithIdentityTransform(fixture.Rig.SourceScene, handProxy, rotatedBoneName);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies manual sync moves generated top-level proxy bodies with generated attachments after pose changes.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_SyncProxyBodiesToPhysics_FollowsGeneratedAttachmentPose()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            const string boneName = "LeftHand";
            AnimatableBody3D handProxy = FindGeneratedProxyBody(fixture.Rig, boneName);
            BoneAttachment3D handAttachment = FindGeneratedAttachment(fixture.Rig.TargetSkeleton!, boneName);
            Transform3D attachmentGlobalTransform = ResolveNodeGlobalTransform(handAttachment);
            Transform3D proxyGlobalTransform = ResolveNodeGlobalTransform(handProxy);
            Transform3D localProxyTransform = attachmentGlobalTransform.AffineInverse() * proxyGlobalTransform;
            Transform3D restProxyGlobalTransform = proxyGlobalTransform;
            Transform3D movedAttachmentTransform = new(
                Basis.FromEuler(new Vector3(0.0f, 0.31f, -0.17f)) * attachmentGlobalTransform.Basis,
                attachmentGlobalTransform.Origin + new Vector3(0.17f, 0.11f, -0.09f));
            ulong initialSyncTickCount = fixture.Rig.PhysicsProxySyncTickCount;

            handAttachment.Transform = movedAttachmentTransform;
            AssertTransformApproximately(restProxyGlobalTransform, ResolveNodeGlobalTransform(handProxy), PositionToleranceMetres);

            fixture.Rig.SyncProxyBodiesToPhysics();

            Assert.True(fixture.Rig.PhysicsProxySyncTickCount > initialSyncTickCount);
            AssertTransformApproximately(movedAttachmentTransform * localProxyTransform, ResolveNodeGlobalTransform(handProxy), PositionToleranceMetres);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the mirror-room NPC generated proxies follow the NPC skeleton pose overrides at runtime.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DynamicPhysicalRig_MirrorRoomFemale_ProxiesFollowNpcPoseOverrides()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(MirrorRoomScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 4);
        await WaitForPhysicsFramesAsync(sceneTree, 4);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected mirror-room scene to become current scene.");
        Node rig = Assert.IsType<Node>(
            sceneRoot.GetNodeOrNull("Actors/Female/Female_export/GeneralSkeleton/DynamicPhysicalRig"),
            exactMatch: false);
        Skeleton3D skeleton = Assert.IsAssignableFrom<Skeleton3D>(rig.GetParent());

        Assert.True(ReadProperty<int>(rig, nameof(DynamicPhysicalRig.GeneratedProxyCount)) > 0, "Mirror-room NPC rig should generate runtime proxy bodies.");
        Assert.True(rig.IsPhysicsProcessing(), "Mirror-room NPC rig should keep the manual physics sync loop enabled.");

        const string boneName = "LeftHand";
        int boneIndex = skeleton.FindBone(boneName);
        Assert.True(boneIndex >= 0, $"Expected NPC skeleton to contain bone '{boneName}'.");
        AnimatableBody3D proxy = FindGeneratedProxyBody(skeleton, boneName);
        BoneAttachment3D attachment = FindGeneratedAttachment(skeleton, boneName);
        Transform3D attachmentGlobalTransform = ResolveNodeGlobalTransform(attachment);
        Transform3D proxyGlobalTransform = ResolveNodeGlobalTransform(proxy);
        Transform3D localProxyTransform = ResolveExpectedProxyLocalTransform(
            LoadPackedScene(CollidersScenePath),
            boneName);
        Transform3D expectedAttachmentGlobalTransform = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(boneIndex);
        Transform3D restAttachmentGlobalTransform = skeleton.GlobalTransform * skeleton.GetBoneGlobalRest(boneIndex);

        AssertTransformApproximately(expectedAttachmentGlobalTransform, attachmentGlobalTransform, PositionToleranceMetres);
        AssertTransformApproximately(
            attachmentGlobalTransform * localProxyTransform,
            proxyGlobalTransform,
            PositionToleranceMetres);
        Assert.True(
            attachmentGlobalTransform.Origin.DistanceTo(restAttachmentGlobalTransform.Origin) > 0.01f,
            "Mirror-room NPC attachment should reflect the overridden runtime pose rather than the imported rest pose.");

        Quaternion initialRotation = skeleton.GetBonePoseRotation(boneIndex);
        skeleton.SetBonePoseRotation(boneIndex, (new Quaternion(Vector3.Up, 0.25f) * initialRotation).Normalized());
        await WaitForFramesAsync(sceneTree, 1);
        await WaitForPhysicsFramesAsync(sceneTree, 1);

        InvokeMethod(rig, nameof(DynamicPhysicalRig.SyncProxyBodiesToPhysics));

        Transform3D movedAttachmentGlobalTransform = ResolveNodeGlobalTransform(attachment);
        AssertTransformApproximately(
            movedAttachmentGlobalTransform * localProxyTransform,
            ResolveNodeGlobalTransform(proxy),
            PositionToleranceMetres);
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
            ulong initialPhysicsTickCount = fixture.PlayerVRIK.PhysicsFollowerTickCount;
            Transform3D movedControllerTransform = new(initialTransform.Basis, initialTransform.Origin + new Vector3(0.08f, 0.0f, 0.0f));
            fixture.RightHandPosition.GlobalTransform = movedControllerTransform;

            InvokeOnBeginStage(fixture.PlayerVRIK, 1.0d / 60.0d);
            AssertTransformApproximately(initialTransform, rightHand.GlobalTransform, PositionToleranceMetres);
            Assert.Equal(initialPhysicsTickCount, fixture.PlayerVRIK.PhysicsFollowerTickCount);

            InvokeUpdatePhysicalFollowers(fixture.PlayerVRIK, 1.0d / 60.0d);

            AssertTransformApproximately(initialHeadTransform, head.GlobalTransform, PositionToleranceMetres);
            Assert.True(
                fixture.PlayerVRIK.PhysicsFollowerTickCount > initialPhysicsTickCount,
                "Follower ticks should now be recorded only from the physics-timed path.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the AnimatableBody3D collision-follower rewrite remains hand-only for the current subphase.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HandFollowers_UseAnimatableCollisionFollower_WhileHeadRemainsBaseline()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            InvokeEnsureFollowers(fixture.PlayerVRIK);

            IKTargetBodyFollower headFollower = GetPrivateField<IKTargetBodyFollower>(fixture.PlayerVRIK, "_headFollower");
            IKTargetAnimatableFollower rightHandFollower = GetPrivateField<IKTargetAnimatableFollower>(fixture.PlayerVRIK, "_rightHandFollower");
            IKTargetAnimatableFollower leftHandFollower = GetPrivateField<IKTargetAnimatableFollower>(fixture.PlayerVRIK, "_leftHandFollower");

            Assert.False(headFollower.UseDampedFollow);

            _ = Assert.IsType<AnimatableBody3D>(fixture.RightHandTarget);
            _ = Assert.IsType<AnimatableBody3D>(fixture.LeftHandTarget);

            Assert.Equal(fixture.PlayerVRIK.HandTargetMaximumSpeed, rightHandFollower.MaximumSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandTargetPositionResponsiveness, rightHandFollower.PositionResponsiveness);
            Assert.Equal(fixture.PlayerVRIK.HandTargetMaximumAcceleration, rightHandFollower.MaximumAcceleration);
            Assert.Equal(fixture.PlayerVRIK.HandTargetSettleDistance, rightHandFollower.SnapDistance);
            Assert.Equal(fixture.PlayerVRIK.HandTargetRotationResponsiveness, rightHandFollower.RotationResponsiveness);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicInteractionCollisionMask, rightHandFollower.DynamicBodyInteractionCollisionMask);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactApproachSpeedThreshold, rightHandFollower.DynamicImpactApproachSpeedThreshold);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactImpulsePerSpeed, rightHandFollower.DynamicImpactImpulsePerSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactImpulseCap, rightHandFollower.DynamicImpactImpulseCap);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedPushSpeedThreshold, rightHandFollower.DynamicSustainedPushSpeedThreshold);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedForcePerSpeed, rightHandFollower.DynamicSustainedForcePerSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedForceCap, rightHandFollower.DynamicSustainedForceCap);

            Assert.Equal(fixture.PlayerVRIK.HandTargetMaximumSpeed, leftHandFollower.MaximumSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandTargetPositionResponsiveness, leftHandFollower.PositionResponsiveness);
            Assert.Equal(fixture.PlayerVRIK.HandTargetMaximumAcceleration, leftHandFollower.MaximumAcceleration);
            Assert.Equal(fixture.PlayerVRIK.HandTargetSettleDistance, leftHandFollower.SnapDistance);
            Assert.Equal(fixture.PlayerVRIK.HandTargetRotationResponsiveness, leftHandFollower.RotationResponsiveness);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicInteractionCollisionMask, leftHandFollower.DynamicBodyInteractionCollisionMask);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactApproachSpeedThreshold, leftHandFollower.DynamicImpactApproachSpeedThreshold);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactImpulsePerSpeed, leftHandFollower.DynamicImpactImpulsePerSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicImpactImpulseCap, leftHandFollower.DynamicImpactImpulseCap);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedPushSpeedThreshold, leftHandFollower.DynamicSustainedPushSpeedThreshold);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedForcePerSpeed, leftHandFollower.DynamicSustainedForcePerSpeed);
            Assert.Equal(fixture.PlayerVRIK.HandDynamicSustainedForceCap, leftHandFollower.DynamicSustainedForceCap);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies only authored hand targets keep the hand collision contract for the current subphase.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HandTargets_RetainHandCollisionLayers()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            AssertCollisionLayerContract(fixture.RightHandTarget, 8, 5);
            AssertCollisionLayerContract(fixture.LeftHandTarget, 8, 5);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the deferred head baseline keeps its pre-hand-rewrite collision contract.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIK_HeadTarget_RetainsDeferredBaselineCollisionLayers()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            AssertCollisionLayerContract(fixture.HeadTarget, 1, 1);
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
            SourceScene = CreatePackedSourceSceneWithBoneAttachments(("Hips", null), ("UnmappedBone", null)),
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
            SourceScene = CreatePackedSourceSceneWithMissingShapeResource(),
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
            SourceScene = CreatePackedSourceSceneWithNestedBoneAttachment("Chest", "Hips", "DifferentIntermediateName", "DifferentPhysicsBodyName"),
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
            SourceScene = CreatePackedSourceSceneWithoutBoneAttachment(),
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
            SourceScene = CreatePackedSourceSceneWithBoneAttachments(("Hips", null), ("", "BlankAttachment")),
        };
        skeleton.AddChild(rig);

        ForceBuildGeneratedRig(rig);

        _ = FindGeneratedProxyBody(rig, "Hips");
        Assert.Equal(1, rig.SkippedSourceShapeCount);
        Assert.Equal(1, rig.GeneratedProxyCount);
        Assert.Equal(1, CountGeneratedProxyBodies(rig));
    }

    private static void AssertCollisionLayerContract(PhysicsBody3D body, uint expectedLayer, uint expectedMask)
    {
        Assert.Equal(expectedLayer, body.CollisionLayer);
        Assert.Equal(expectedMask, body.CollisionMask);
    }

    private static T GetPrivateField<T>(PlayerVRIK playerVRIK, string fieldName)
    {
        FieldInfo field = GetNonPublicInstanceField(playerVRIK.GetType(), fieldName)
                          ?? throw new InvalidOperationException(
                              $"{playerVRIK.GetType().Name} field '{fieldName}' was not found in the inheritance chain.");

        return Assert.IsType<T>(field.GetValue(playerVRIK));
    }

    private static void AssertBodyHasCollisionException(PhysicsBody3D source, PhysicsBody3D expected)
    {
        Godot.Collections.Array<PhysicsBody3D> exceptions = source.GetCollisionExceptions();
        Assert.Contains(exceptions, body => ReferenceEquals(body, expected));
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

    private static void AssertProxyShapeDataPreservedWithIdentityTransform(PackedScene? sourceScene, AnimatableBody3D proxyBody, string boneName)
    {
        Node sourceRoot = sourceScene?.Instantiate()
                          ?? throw new Xunit.Sdk.XunitException("DynamicPhysicalRig source scene should be configured.");

        try
        {
            CollisionShape3D sourceShape = FindSourceShapeForProfileBone(sourceRoot, boneName);
            CollisionShape3D proxyShape = Assert.IsAssignableFrom<CollisionShape3D>(proxyBody.GetChild(0));

            Assert.Equal(sourceShape.Name, proxyShape.Name);
            Assert.Equal(sourceShape.Disabled, proxyShape.Disabled);
            Assert.IsType(sourceShape.Shape.GetType(), proxyShape.Shape);
            Assert.False(ReferenceEquals(sourceShape.Shape, proxyShape.Shape));
            AssertTransformApproximately(Transform3D.Identity, proxyShape.Transform, PositionToleranceMetres);
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

    private static Transform3D ResolveExpectedProxyLocalTransform(PackedScene? sourceScene, string boneName)
    {
        Node sourceRoot = sourceScene?.Instantiate()
                          ?? throw new Xunit.Sdk.XunitException("DynamicPhysicalRig source scene should be configured.");

        try
        {
            CollisionShape3D sourceShape = FindSourceShapeForProfileBone(sourceRoot, boneName);
            BoneAttachment3D sourceAttachment = FindClosestBoneAttachmentAncestor(sourceShape)
                                                ?? throw new Xunit.Sdk.XunitException(
                                                    $"Expected source shape '{sourceShape.Name}' to have a BoneAttachment3D ancestor.");
            Node? sourceFrameRoot = FindAncestor<Skeleton3D>(sourceAttachment) ?? sourceAttachment.GetParent();
            Transform3D sourceAttachmentSkeletonTransform = ComposeTransformRelativeToAncestor(sourceAttachment, sourceFrameRoot);
            Transform3D sourceShapeSkeletonTransform = ComposeTransformRelativeToAncestor(sourceShape, sourceFrameRoot);
            return sourceAttachmentSkeletonTransform.AffineInverse() * sourceShapeSkeletonTransform;
        }
        finally
        {
            sourceRoot.Free();
        }
    }

    private static Transform3D ResolveSourceShapeSkeletonTransform(PackedScene? sourceScene, string boneName)
    {
        Node sourceRoot = sourceScene?.Instantiate()
                          ?? throw new Xunit.Sdk.XunitException("DynamicPhysicalRig source scene should be configured.");

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

    private static void InvokeMethod(object target, string methodName)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
                            ?? throw new Xunit.Sdk.XunitException($"Expected '{target.GetType().Name}' to expose method '{methodName}'.");
        InvokeReflectedMethod(method, target, null);
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

    private static int CountSourceShapes(PackedScene? sourceScene)
    {
        Node sourceRoot = sourceScene?.Instantiate()
                          ?? throw new Xunit.Sdk.XunitException("DynamicPhysicalRig source scene should be configured.");

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

    private static void InvokeUpdatePhysicalFollowers(PlayerVRIK playerVRIK, double delta)
    {
        MethodInfo method = GetNonPublicInstanceMethod(playerVRIK.GetType(), "UpdatePhysicalFollowers")
                            ?? throw new InvalidOperationException(
                                $"{playerVRIK.GetType().Name}.UpdatePhysicalFollowers was not found in the inheritance chain.");

        _ = method.Invoke(playerVRIK, [delta]);
    }

    private static void InvokeEnsureFollowers(PlayerVRIK playerVRIK)
    {
        MethodInfo method = GetNonPublicInstanceMethod(playerVRIK.GetType(), "EnsureFollowers")
                            ?? throw new InvalidOperationException(
                                $"{playerVRIK.GetType().Name}.EnsureFollowers was not found in the inheritance chain.");

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

        public static async Task<RuntimeFixture> CreateAsync(SceneTree sceneTree, Action<PlayerVRIK>? configurePlayerVRIK = null)
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

            Skeleton3D skeleton = CreateSkeleton();
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
                SourceScene = LoadPackedScene(CollidersScenePath),
                GenerateInEditor = true,
            };
            skeleton.AddChild(rig);

            Node3D ikTargets = new()
            {
                Name = "IKTargets",
            };
            player.AddChild(ikTargets);

            CharacterBody3D headTarget = CreateCharacterIkTargetBody("Head", new Vector3(0.0f, 1.62f, 0.05f), 1, 1, new CapsuleShape3D { Radius = 0.08f, Height = 0.18f });
            AnimatableBody3D rightHandTarget = CreateAnimatableIkTargetBody("RightHand", new Vector3(0.72f, 1.28f, 0.02f), 8, 5, new BoxShape3D { Size = new Vector3(0.10f, 0.18f, 0.10f) });
            AnimatableBody3D leftHandTarget = CreateAnimatableIkTargetBody("LeftHand", new Vector3(-0.72f, 1.28f, 0.02f), 8, 5, new BoxShape3D { Size = new Vector3(0.10f, 0.18f, 0.10f) });
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
                Skeleton = skeleton,
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

            bool bound = playerVRIK.TryBind(
                new TestXROrigin(originNode),
                new TestXRCamera(cameraNode),
                new TestXRHandController(rightControllerNode, rightHandPosition),
                new TestXRHandController(leftControllerNode, leftHandPosition));

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

        private static CharacterBody3D CreateCharacterIkTargetBody(string name, Vector3 position, uint collisionLayer, uint collisionMask, Shape3D shape)
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

            CollisionShape3D collisionShape = new()
            {
                Name = "CollisionShape3D",
                Shape = shape,
            };

            body.AddChild(collisionShape);
            return body;
        }

        private static AnimatableBody3D CreateAnimatableIkTargetBody(string name, Vector3 position, uint collisionLayer, uint collisionMask, Shape3D shape)
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

    private sealed class TestXRHandController(Node3D controllerNode, Node3D handPositionNode) : IXRHandController
    {
        public event Action<string>? ActionButtonPressed
        {
            add
            {
            }

            remove
            {
            }
        }

        public event Action<string>? ActionButtonReleased
        {
            add
            {
            }

            remove
            {
            }
        }

        public event Action<string, float>? ActionFloatInputChanged
        {
            add
            {
            }

            remove
            {
            }
        }

        public event Action<string, Vector2>? ActionVector2InputChanged
        {
            add
            {
            }

            remove
            {
            }
        }

        public Node3D ControllerNode => controllerNode;

        public Node3D HandPositionNode => handPositionNode;
    }
}
