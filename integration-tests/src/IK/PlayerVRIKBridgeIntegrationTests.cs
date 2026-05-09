using System.Reflection;
using AlleyCat.IK;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for player XR↔IK bridge runtime behaviour.
/// </summary>
public sealed partial class PlayerVRIKBridgeIntegrationTests
{
    /// <summary>
    /// Verifies startup binder does not attempt VRIK binding after XR initialisation failure.
    /// </summary>
    [Fact]
    public async Task StartupBinder_WhenXRInitialisationFails_DoesNotBindPlayerVRIK()
    {
        SceneTree sceneTree = GetSceneTree();

        Node root = new()
        {
            Name = "BinderFailureFixture",
        };

        TestXRManager xrManager = new()
        {
            Name = "XR",
        };

        TestPlayerVRIKStartupBinder binder = new()
        {
            Name = "PlayerVRIKStartupBinder",
        };

        Node3D playerNode = new()
        {
            Name = "Player",
        };

        TestPlayerVRIK playerVRIK = new()
        {
            Name = "VRIK",
        };
        binder.PlayerVRIKToBind = playerVRIK;

        root.AddChild(xrManager);
        root.AddChild(binder);
        root.AddChild(playerNode);
        playerNode.AddChild(playerVRIK);

        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            binder.XRManagerPath = xrManager.GetPath();
            if (!binder.ReadyCalled)
            {
                binder._Ready();
            }

            xrManager.EmitInitialisedResult(succeeded: false);
            binder._Process(1.0d / 60.0d);
            await WaitForFramesAsync(sceneTree, 4);

            Assert.Equal(0, playerVRIK.BindCallCount);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies startup binder still binds when XR initialised successfully before binder subscription.
    /// </summary>
    [Fact]
    public async Task StartupBinder_WhenXRAlreadyInitialisedBeforeSubscription_BindsPlayerVRIK()
    {
        SceneTree sceneTree = GetSceneTree();

        Node root = new()
        {
            Name = "BinderLateSubscriptionFixture",
        };

        TestXRManager xrManager = new()
        {
            Name = "XR",
        };

        TestPlayerVRIKStartupBinder binder = new()
        {
            Name = "PlayerVRIKStartupBinder",
        };

        Node3D playerNode = new()
        {
            Name = "Player",
        };

        TestPlayerVRIK playerVRIK = new()
        {
            Name = "VRIK",
        };
        binder.PlayerVRIKToBind = playerVRIK;

        root.AddChild(xrManager);
        root.AddChild(playerNode);
        playerNode.AddChild(playerVRIK);

        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            xrManager.EmitInitialisedResult(succeeded: true);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.True(xrManager.InitialisationAttempted);
            Assert.True(xrManager.InitialisationSucceeded);
            binder.XRManagerPath = xrManager.GetPath();
            root.AddChild(binder);
            await WaitForNextFrameAsync(sceneTree);
            if (!binder.ReadyCalled)
            {
                binder._Ready();
            }

            binder._Process(1.0d / 60.0d);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.True(binder.ReadyCalled);
            Assert.Equal(1, playerVRIK.BindCallCount);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies calibration guard preserves existing scale when XR camera height is near zero.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKCalibration_WhenXRHeightIsNearZero_PreservesWorldScale()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 0.0f, initialWorldScale: 2.5f);

        try
        {
            bool bound = fixture.PlayerVRIK.TryBind(
                fixture.Origin,
                fixture.Camera,
                fixture.RightHandController,
                fixture.LeftHandController);

            Assert.True(bound);
            Assert.True(float.IsFinite(fixture.Origin.WorldScale));
            Assert.Equal(2.5f, fixture.Origin.WorldScale);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies hand IK target bodies follow XR controller hand nodes rather than their own current transforms.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKHandFollowTargets_WhenBound_UseControllerHandNodes()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            bool bound = fixture.PlayerVRIK.TryBind(
                fixture.Origin,
                fixture.Camera,
                fixture.RightHandController,
                fixture.LeftHandController);

            Assert.True(bound);

            Transform3D rightFollowTarget = InvokeBuildHandTargetTransform(fixture.PlayerVRIK, "BuildRightHandTargetTransform");
            Transform3D leftFollowTarget = InvokeBuildHandTargetTransform(fixture.PlayerVRIK, "BuildLeftHandTargetTransform");

            AssertTransformApproximately(fixture.RightHandController.HandPositionNode.GlobalTransform, rightFollowTarget);
            AssertTransformApproximately(fixture.LeftHandController.HandPositionNode.GlobalTransform, leftFollowTarget);
            Assert.Null(fixture.PlayerVRIK.RightHandIKTargetStateProvider);
            Assert.Null(fixture.PlayerVRIK.LeftHandIKTargetStateProvider);
            Assert.Equal(LimbSide.Right, fixture.RightHandFallbackProvider.Side);
            Assert.Equal(LimbSide.Left, fixture.LeftHandFallbackProvider.Side);
            Assert.Same(fixture.RightHandController.HandPositionNode, fixture.RightHandFallbackProvider.ResolvedSourceNode);
            Assert.Same(fixture.LeftHandController.HandPositionNode, fixture.LeftHandFallbackProvider.ResolvedSourceNode);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a scene-wired provider overrides the XR controller target and drives modifier influence.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKHandFollowTargets_WhenProviderAssigned_UseProviderTargetAndInfluence()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);
        TestIKTargetStateProvider provider = new()
        {
            Name = "RightHandProvider",
            TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(1.2f, 0.9f, -0.8f)),
                0.42f),
        };

        fixture.Root.AddChild(provider);

        try
        {
            bool bound = fixture.PlayerVRIK.TryBind(
                fixture.Origin,
                fixture.Camera,
                fixture.RightHandController,
                fixture.LeftHandController);

            Assert.True(bound);

            fixture.PlayerVRIK.RightHandIKTargetStateProvider = provider;

            Transform3D providerFollowTarget = InvokeBuildHandTargetTransform(
                fixture.PlayerVRIK,
                "BuildRightHandTargetTransform");

            AssertTransformApproximately(provider.TargetState.WorldTransform, providerFollowTarget);
            Assert.Equal(0.42f, fixture.RightHandIKModifier.Influence);
            Assert.True(fixture.RightHandIKModifier.Active);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies provider influence zero deactivates every same-limb arm and shoulder modifier.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKHandInfluence_WhenRightProviderInfluenceIsZero_DeactivatesRightArmGroup()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);
        TestIKTargetStateProvider provider = new()
        {
            Name = "RightHandZeroInfluenceProvider",
            TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(1.2f, 0.9f, -0.8f)),
                0.0f),
        };

        fixture.Root.AddChild(provider);

        try
        {
            bool bound = fixture.PlayerVRIK.TryBind(
                fixture.Origin,
                fixture.Camera,
                fixture.RightHandController,
                fixture.LeftHandController);

            Assert.True(bound);

            fixture.PlayerVRIK.RightHandIKTargetStateProvider = provider;

            Transform3D fallbackTarget = fixture.RightHandController.HandPositionNode.GlobalTransform;
            Transform3D resolvedTarget = InvokeBuildHandTargetTransform(
                fixture.PlayerVRIK,
                "BuildRightHandTargetTransform");

            AssertTransformApproximately(fallbackTarget, resolvedTarget);
            Assert.NotEqual(provider.TargetState.WorldTransform.Origin, resolvedTarget.Origin);
            Assert.All(fixture.PlayerVRIK.RightHandModifierGroup, modifier =>
            {
                Assert.False(modifier.Active);
                Assert.Equal(0.0f, modifier.Influence);
            });
            Assert.False(fixture.RightHandIKModifier.Active);
            Assert.Equal(0.0f, fixture.RightHandIKModifier.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a zero-influence hand provider disables modifiers without moving the runtime hand target to that
    /// disabled provider pose.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKHandProvider_WhenZeroInfluence_DoesNotDriveDisabledProviderPose()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "ZeroInfluenceHandProviderFixture");
        TestIKTargetStateProvider provider = new()
        {
            Name = "RightHandZeroInfluenceProvider",
            TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(2.0f, 2.0f, 2.0f)),
                0.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandIKTargetStateProvider = provider;

        try
        {
            Transform3D restTarget = fixture.RightHandIKTarget.Transform;
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);

            AssertTransformApproximately(restTarget, fixture.RightHandIKTarget.Transform);
            Assert.NotEqual(provider.TargetState.WorldTransform.Origin, fixture.RightHandIKTarget.GlobalPosition);
            Assert.False(fixture.RightHandModifier.Active);
            Assert.Equal(0.0f, fixture.RightHandModifier.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies resolving a hand provider only returns the desired follow pose and leaves collision-aware movement to the physics follower.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKHandProvider_WhenInfluenceIsPositive_DoesNotTeleportRuntimeHandTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "PositiveInfluenceHandProviderFixture");
        TestIKTargetStateProvider provider = new()
        {
            Name = "RightHandPositiveInfluenceProvider",
            TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(2.0f, 2.0f, 2.0f)),
                1.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandIKTargetStateProvider = provider;

        try
        {
            Transform3D authoredTarget = fixture.RightHandIKTarget.Transform;
            Transform3D resolvedTarget = InvokeBuildHandTargetTransform(fixture.VRIK, "BuildRightHandTargetTransform");

            AssertTransformApproximately(provider.TargetState.WorldTransform, resolvedTarget);
            AssertTransformApproximately(authoredTarget, fixture.RightHandIKTarget.Transform);
            Assert.NotEqual(provider.TargetState.WorldTransform.Origin, fixture.RightHandIKTarget.GlobalPosition);
            Assert.True(fixture.RightHandModifier.Active);
            Assert.Equal(1.0f, fixture.RightHandModifier.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a zero-influence fallback hand provider disables modifiers without moving the runtime hand target to that
    /// disabled fallback pose.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKHandFallbackProvider_WhenZeroInfluence_DoesNotDriveDisabledFallbackPose()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "ZeroInfluenceHandFallbackProviderFixture");
        TestIKTargetStateProvider fallbackProvider = new()
        {
            Name = "RightHandZeroInfluenceFallbackProvider",
            TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(2.0f, 2.0f, 2.0f)),
                0.0f),
        };
        fixture.Root.AddChild(fallbackProvider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandFallbackProvider = fallbackProvider;

        try
        {
            Transform3D restTarget = fixture.RightHandIKTarget.Transform;
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);

            AssertTransformApproximately(restTarget, fixture.RightHandIKTarget.Transform);
            Assert.NotEqual(fallbackProvider.TargetState.WorldTransform.Origin, fixture.RightHandIKTarget.GlobalPosition);
            Assert.False(fixture.RightHandModifier.Active);
            Assert.Equal(0.0f, fixture.RightHandModifier.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies unavailable controller sources preserve authored hand IK target bodies and modifier state.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKHandFollowTargets_WhenUnbound_PreserveTargetBodiesAndModifierState()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            fixture.PlayerVRIK._Ready();
            fixture.PlayerVRIK.RightHandFallbackProvider = null;
            fixture.PlayerVRIK.LeftHandFallbackProvider = null;
            fixture.RightHandIKTarget.Transform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Up, 0.35f),
                new Vector3(1.1f, 0.8f, -0.4f));
            fixture.LeftHandIKTarget.Transform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Right, -0.25f),
                new Vector3(-1.2f, 0.7f, -0.3f));
            await WaitForNextFrameAsync(sceneTree);
            Transform3D authoredRightTarget = fixture.RightHandIKTarget.GlobalTransform;
            Transform3D authoredLeftTarget = fixture.LeftHandIKTarget.GlobalTransform;
            fixture.RightHandIKModifier.Active = true;
            fixture.RightHandIKModifier.Influence = 0.73f;
            fixture.LeftHandIKModifier.Active = true;
            fixture.LeftHandIKModifier.Influence = 0.64f;

            Transform3D rightFollowTarget = InvokeBuildHandTargetTransform(fixture.PlayerVRIK, "BuildRightHandTargetTransform");
            Transform3D leftFollowTarget = InvokeBuildHandTargetTransform(fixture.PlayerVRIK, "BuildLeftHandTargetTransform");

            AssertTransformApproximately(authoredRightTarget, rightFollowTarget);
            AssertTransformApproximately(authoredLeftTarget, leftFollowTarget);
            Assert.True(fixture.RightHandIKModifier.Active);
            Assert.Equal(0.73f, fixture.RightHandIKModifier.Influence);
            Assert.True(fixture.LeftHandIKModifier.Active);
            Assert.Equal(0.64f, fixture.LeftHandIKModifier.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies end-stage origin compensation still aligns the physical and virtual head poses
    /// when the virtual head pose has diverged from the physical camera.
    /// </summary>
    [Fact]
    public async Task ComputeCompensatedOriginTransform_WhenVirtualHeadDiffers_RealignsPhysicalHeadPose()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            bool bound = fixture.PlayerVRIK.TryBind(
                fixture.Origin,
                fixture.Camera,
                fixture.RightHandController,
                fixture.LeftHandController);

            Assert.True(bound);

            fixture.Origin.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.3f, 0.2f, -0.4f));
            fixture.Skeleton.SetBonePosePosition(fixture.HeadBoneIndex, new Vector3(0.05f, -0.35f, 0.08f));

            Transform3D compensatedOrigin = fixture.PlayerVRIK.ComputeCompensatedOriginTransform(
                fixture.Skeleton,
                fixture.Camera.CameraNode,
                fixture.Origin);

            Transform3D physicalHeadPose = fixture.Camera.CameraNode.GlobalTransform * fixture.PlayerVRIK.Viewpoint!.Transform.Inverse();
            Transform3D localPose = fixture.Origin.GlobalTransform.Inverse() * physicalHeadPose;
            Transform3D virtualHeadPose = fixture.Skeleton.GlobalTransform * fixture.Skeleton.GetBoneGlobalPose(fixture.HeadBoneIndex);
            Transform3D recomposedHeadPose = compensatedOrigin * localPose;

            AssertTransformApproximately(virtualHeadPose, recomposedHeadPose);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies inactive begin-stage processing clears any stale limited head solve target.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKBeginStage_WhenInactive_ResetsHeadSolveTargetToHeadTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            bool bound = fixture.PlayerVRIK.TryBind(
                fixture.Origin,
                fixture.Camera,
                fixture.RightHandController,
                fixture.LeftHandController);

            Assert.True(bound);

            fixture.HeadIKTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.4f, 1.2f, -0.3f));
            fixture.HeadIKSolveTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(-1.5f, 0.1f, 2.2f));
            fixture.PlayerVRIK.Active = false;

            InvokeOnBeginStage(fixture.PlayerVRIK, 1.0d / 60.0d);

            AssertTransformApproximately(fixture.HeadIKTarget.GlobalTransform, fixture.HeadIKSolveTarget.GlobalTransform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies unbound begin-stage processing also clears any stale limited head solve target.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKBeginStage_WhenUnbound_ResetsHeadSolveTargetToHeadTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            fixture.HeadIKTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(-0.6f, 1.4f, 0.25f));
            fixture.HeadIKSolveTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(2.0f, -0.3f, 1.1f));

            InvokeOnBeginStage(fixture.PlayerVRIK, 1.0d / 60.0d);

            AssertTransformApproximately(fixture.HeadIKTarget.GlobalTransform, fixture.HeadIKSolveTarget.GlobalTransform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies non-player VRIK applies configured provider targets without any XR binding.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKBeginStage_WhenProviderConfigured_DrivesTargetWithoutXRBinding()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(sceneTree, "CharacterIKFixture");
        TestIKTargetStateProvider provider = new()
        {
            Name = "RightHandProvider",
            TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(0.25f, 0.1f, -0.35f)),
                1.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandIKTargetStateProvider = provider;

        try
        {
            fixture.VRIK._Ready();
            fixture.RightHandIKTarget.GlobalTransform = Transform3D.Identity;
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);

            AssertTransformApproximately(provider.TargetState.WorldTransform, fixture.RightHandIKTarget.Transform);
            Assert.True(fixture.RightHandModifier.Active);
            Assert.Equal(1.0f, fixture.RightHandModifier.Influence);
            Assert.Equal(1UL, fixture.VRIK.PhysicsFollowerTickCount);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a head provider supplies the head target transform and gates head modifier influence.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKHeadProvider_WhenAssigned_DrivesTargetAndInfluence()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(sceneTree, "HeadProviderFixture");
        TestIKTargetStateProvider provider = new()
        {
            Name = "HeadProvider",
            TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(0.001f, 0.001f, 0.001f)),
                0.37f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.HeadTargetProvider = provider;

        try
        {
            fixture.VRIK._Ready();
            fixture.HeadIKTarget.GlobalTransform = Transform3D.Identity;
            fixture.VRIKBeginStage._ProcessModificationWithDelta(1.0d / 60.0d);

            AssertTransformApproximately(provider.TargetState.WorldTransform, fixture.HeadIKTarget.Transform);
            AssertTransformApproximately(provider.TargetState.WorldTransform, fixture.HeadIKSolveTarget.Transform);
            AssertTransformApproximately(fixture.HeadIKTarget.GlobalTransform, fixture.HeadIKSolveTarget.GlobalTransform);
            Assert.True(fixture.HeadModifier.Active);
            Assert.Equal(0.37f, fixture.HeadModifier.Influence);

            provider.TargetState = new IKTargetState(provider.TargetState.WorldTransform, 0.0f);
            fixture.VRIKBeginStage._ProcessModificationWithDelta(1.0d / 60.0d);

            AssertTransformApproximately(provider.TargetState.WorldTransform, fixture.HeadIKTarget.Transform);
            AssertTransformApproximately(provider.TargetState.WorldTransform, fixture.HeadIKSolveTarget.Transform);
            Assert.False(fixture.HeadModifier.Active);
            Assert.Equal(0.0f, fixture.HeadModifier.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies foot providers drive foot target/influence while preserving target physics-sync ownership.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKFootProvider_WhenAssigned_RunsAfterFootSyncAndBeforeLegConsumers()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(sceneTree, "FootProviderFixture");
        TestIKTargetStateProvider provider = new()
        {
            Name = "RightFootProvider",
            TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(0.4f, 0.2f, -0.25f)),
                0.58f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightFootTargetProvider = provider;
        fixture.VRIK.RightFootIKTarget = fixture.RightFootIKTarget;
        fixture.FootSyncController.Active = true;
        FootTargetConsumer consumer = new()
        {
            Name = "RightFootConsumer",
            FootTarget = fixture.RightFootIKTarget,
        };
        fixture.Skeleton.AddChild(consumer);
        fixture.Skeleton.MoveChild(consumer, fixture.VRIKFootProviderStage.GetIndex() + 1);

        try
        {
            fixture.VRIK._Ready();
            fixture.FootSyncController._ProcessModificationWithDelta(1.0d / 60.0d);
            fixture.VRIKBeginStage._ProcessModificationWithDelta(1.0d / 60.0d);
            fixture.VRIKFootProviderStage._ProcessModificationWithDelta(1.0d / 60.0d);
            consumer._ProcessModificationWithDelta(1.0d / 60.0d);

            Assert.True(fixture.FootSyncController.GetIndex() < fixture.VRIKBeginStage.GetIndex());
            Assert.True(fixture.VRIKBeginStage.GetIndex() < fixture.VRIKFootProviderStage.GetIndex());
            Assert.True(fixture.VRIKFootProviderStage.GetIndex() < consumer.GetIndex());
            Assert.True(fixture.RightFootModifier.Active);
            Assert.Equal(0.58f, fixture.RightFootModifier.Influence);
            Assert.True(fixture.FootSyncController.Active);
            Assert.Equal(1.0f, fixture.FootSyncController.Influence);
            AssertTransformApproximately(provider.TargetState.WorldTransform, fixture.RightFootIKTarget.Transform);
            AssertTransformApproximately(provider.TargetState.WorldTransform, consumer.ConsumedLocalTransform);

            provider.TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(2.0f, 2.0f, 2.0f)),
                0.0f);
            fixture.RightFootIKTarget.GlobalTransform = provider.TargetState.WorldTransform;
            fixture.FootSyncController._ProcessModificationWithDelta(1.0d / 60.0d);
            fixture.VRIKBeginStage._ProcessModificationWithDelta(1.0d / 60.0d);
            fixture.VRIKFootProviderStage._ProcessModificationWithDelta(1.0d / 60.0d);
            consumer._ProcessModificationWithDelta(1.0d / 60.0d);

            AssertVectorApproximately(Vector3.Zero, fixture.RightFootIKTarget.GlobalPosition);
            AssertTransformApproximately(fixture.RightFootIKTarget.Transform, consumer.ConsumedLocalTransform);
            Assert.False(fixture.RightFootModifier.Active);
            Assert.Equal(0.0f, fixture.RightFootModifier.Influence);
            Assert.True(fixture.FootSyncController.Active);
            Assert.Equal(1.0f, fixture.FootSyncController.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies authored foot sync remains the target source when no provider is configured.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKFootProvider_WhenAbsent_PreservesFootSyncFallback()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(sceneTree, "FootSyncFallbackFixture");

        try
        {
            fixture.VRIK.RightFootIKTarget = fixture.RightFootIKTarget;
            fixture.RightFootIKTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(5.0f, 5.0f, 5.0f));
            fixture.FootSyncController._ProcessModificationWithDelta(1.0d / 60.0d);
            fixture.VRIKBeginStage._ProcessModificationWithDelta(1.0d / 60.0d);
            fixture.VRIKFootProviderStage._ProcessModificationWithDelta(1.0d / 60.0d);

            AssertVectorApproximately(Vector3.Zero, fixture.RightFootIKTarget.GlobalPosition);
            Assert.True(fixture.FootSyncController.Active);
            Assert.Equal(1.0f, fixture.FootSyncController.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static async Task<VrikFixture> CreateVrikFixtureAsync(SceneTree sceneTree, float xrCameraHeight, float initialWorldScale)
    {
        Node3D root = new()
        {
            Name = "PlayerVrikFixture",
        };

        Node3D player = new()
        {
            Name = "Player",
        };
        root.AddChild(player);

        Node3D femaleExport = new()
        {
            Name = "Female_export",
        };
        player.AddChild(femaleExport);

        Skeleton3D skeleton = new()
        {
            Name = "GeneralSkeleton",
        };
        femaleExport.AddChild(skeleton);

        int headBoneIndex = skeleton.AddBone("Head");
        skeleton.SetBoneRest(headBoneIndex, new Transform3D(Basis.Identity, new Vector3(0.0f, 1.55f, 0.0f)));
        skeleton.SetBonePosePosition(headBoneIndex, new Vector3(0.0f, -0.15f, 0.0f));

        SkeletonModifier3D rightArmIKController = new()
        {
            Name = "RightArmIKController",
            Influence = 1.0f,
            Active = true,
        };
        skeleton.AddChild(rightArmIKController);

        TwoBoneIK3D rightHandIKModifier = new()
        {
            Name = "RightArmTwoBoneIKController",
            Influence = 1.0f,
        };
        skeleton.AddChild(rightHandIKModifier);

        TwoBoneIK3D leftHandIKModifier = new()
        {
            Name = "LeftArmTwoBoneIKController",
            Influence = 1.0f,
        };
        skeleton.AddChild(leftHandIKModifier);

        SkeletonModifier3D rightHandCopyRotation = new()
        {
            Name = "RightHandCopyRotation",
            Influence = 1.0f,
            Active = true,
        };
        skeleton.AddChild(rightHandCopyRotation);

        Node3D headAttachment = new()
        {
            Name = "Head",
        };
        skeleton.AddChild(headAttachment);

        Marker3D viewpoint = new()
        {
            Name = "Viewpoint",
            Transform = new Transform3D(Basis.Identity, new Vector3(0.0f, 0.1f, 0.0f)),
        };
        headAttachment.AddChild(viewpoint);

        Node3D ikTargets = new()
        {
            Name = "IKTargets",
        };
        player.AddChild(ikTargets);

        CharacterBody3D headIKTarget = new()
        {
            Name = "Head",
        };
        ikTargets.AddChild(headIKTarget);

        Node3D headIKSolveTarget = new()
        {
            Name = "HeadSolve",
        };
        ikTargets.AddChild(headIKSolveTarget);

        AnimatableBody3D rightHandIKTarget = new()
        {
            Name = "RightHand",
            Position = new Vector3(4.0f, 4.0f, 4.0f),
            SyncToPhysics = false,
        };
        ikTargets.AddChild(rightHandIKTarget);

        AnimatableBody3D leftHandIKTarget = new()
        {
            Name = "LeftHand",
            Position = new Vector3(-4.0f, -4.0f, -4.0f),
            SyncToPhysics = false,
        };
        ikTargets.AddChild(leftHandIKTarget);

        PlayerVRIK playerVRIK = new()
        {
            Name = "VRIK",
            Viewpoint = viewpoint,
            HeadIKTarget = headIKTarget,
            HeadIKSolveTarget = headIKSolveTarget,
            RightHandIKTarget = rightHandIKTarget,
            LeftHandIKTarget = leftHandIKTarget,
            RightHandModifierGroup = [rightArmIKController, rightHandIKModifier, rightHandCopyRotation],
            LeftHandModifierGroup = [leftHandIKModifier],
            Skeleton = skeleton,
        };
        player.AddChild(playerVRIK);

        TestXROrigin origin = new(initialWorldScale)
        {
            Name = "Origin",
        };
        root.AddChild(origin);

        Camera3D cameraNode = new()
        {
            Name = "MainCamera",
            Position = new Vector3(0.0f, xrCameraHeight, 0.0f),
        };
        origin.AddChild(cameraNode);

        Node3D rightControllerNode = new()
        {
            Name = "RightController",
        };
        origin.AddChild(rightControllerNode);

        Node3D rightHandPosition = new()
        {
            Name = "RightHandPosition",
            Position = new Vector3(0.3f, 1.1f, -0.4f),
        };
        rightControllerNode.AddChild(rightHandPosition);

        Node3D leftControllerNode = new()
        {
            Name = "LeftController",
        };
        origin.AddChild(leftControllerNode);

        Node3D leftHandPosition = new()
        {
            Name = "LeftHandPosition",
            Position = new Vector3(-0.35f, 1.05f, -0.45f),
        };
        leftControllerNode.AddChild(leftHandPosition);

        TestXRCamera camera = new(cameraNode);
        TestXRHandController rightHandController = new(rightControllerNode, rightHandPosition);
        TestXRHandController leftHandController = new(leftControllerNode, leftHandPosition);

        XRControllerTargetProvider rightHandFallbackProvider = new()
        {
            Name = "RightHandFallbackProvider",
            Side = LimbSide.Right,
        };
        playerVRIK.AddChild(rightHandFallbackProvider);
        playerVRIK.RightHandFallbackProvider = rightHandFallbackProvider;

        XRControllerTargetProvider leftHandFallbackProvider = new()
        {
            Name = "LeftHandFallbackProvider",
            Side = LimbSide.Left,
        };
        playerVRIK.AddChild(leftHandFallbackProvider);
        playerVRIK.LeftHandFallbackProvider = leftHandFallbackProvider;

        sceneTree.Root.AddChild(root);
        await WaitForFramesAsync(sceneTree, 2);

        return new VrikFixture(
            root,
            playerVRIK,
            skeleton,
            headBoneIndex,
            headIKTarget,
            headIKSolveTarget,
            rightHandIKTarget,
            leftHandIKTarget,
            rightHandIKModifier,
            leftHandIKModifier,
            rightHandFallbackProvider,
            leftHandFallbackProvider,
            origin,
            camera,
            rightHandController,
            leftHandController);
    }

    private static async Task<CharacterIKFixture<TCharacterIK>> CreateCharacterIKFixtureAsync<TCharacterIK>(
        SceneTree sceneTree,
        string name)
        where TCharacterIK : CharacterIK, new()
    {
        Node3D root = new()
        {
            Name = name,
        };

        Node3D femaleExport = new()
        {
            Name = "Female_export",
        };
        root.AddChild(femaleExport);

        Skeleton3D skeleton = new()
        {
            Name = "GeneralSkeleton",
        };
        femaleExport.AddChild(skeleton);

        int headBoneIndex = skeleton.AddBone("Head");
        skeleton.SetBoneRest(headBoneIndex, new Transform3D(Basis.Identity, new Vector3(0.0f, 1.55f, 0.0f)));
        int leftFootBoneIndex = skeleton.AddBone("LeftFoot");
        skeleton.SetBoneRest(leftFootBoneIndex, new Transform3D(Basis.Identity, new Vector3(-0.15f, 0.05f, 0.02f)));
        skeleton.SetBonePosePosition(leftFootBoneIndex, new Vector3(-0.05f, 0.02f, 0.01f));
        int rightFootBoneIndex = skeleton.AddBone("RightFoot");
        skeleton.SetBoneRest(rightFootBoneIndex, new Transform3D(Basis.Identity, new Vector3(0.15f, 0.05f, 0.02f)));
        skeleton.SetBonePosePosition(rightFootBoneIndex, new Vector3(0.05f, 0.02f, 0.01f));

        SkeletonModifier3D headModifier = new()
        {
            Name = "HeadModifier",
            Active = true,
            Influence = 1.0f,
        };
        skeleton.AddChild(headModifier);

        SkeletonModifier3D rightFootModifier = new()
        {
            Name = "RightFootModifier",
            Active = true,
            Influence = 1.0f,
        };
        skeleton.AddChild(rightFootModifier);

        SkeletonModifier3D rightHandModifier = new()
        {
            Name = "RightHandModifier",
            Active = true,
            Influence = 1.0f,
        };
        skeleton.AddChild(rightHandModifier);

        Node3D headAttachment = new()
        {
            Name = "Head",
        };
        skeleton.AddChild(headAttachment);

        Marker3D viewpoint = new()
        {
            Name = "Viewpoint",
            Transform = new Transform3D(Basis.Identity, new Vector3(0.0f, 0.1f, 0.0f)),
        };
        headAttachment.AddChild(viewpoint);

        Node3D ikTargets = new()
        {
            Name = "IKTargets",
        };

        CharacterBody3D headIKTarget = new()
        {
            Name = "Head",
        };
        ikTargets.AddChild(headIKTarget);

        Node3D headIKSolveTarget = new()
        {
            Name = "HeadSolve",
        };
        ikTargets.AddChild(headIKSolveTarget);

        AnimatableBody3D rightHandIKTarget = new()
        {
            Name = "RightHand",
            SyncToPhysics = false,
        };
        ikTargets.AddChild(rightHandIKTarget);

        AnimatableBody3D leftHandIKTarget = new()
        {
            Name = "LeftHand",
            SyncToPhysics = false,
        };
        ikTargets.AddChild(leftHandIKTarget);

        Node3D rightFootIKTarget = new()
        {
            Name = "RightFoot",
        };
        ikTargets.AddChild(rightFootIKTarget);

        Node3D leftFootIKTarget = new()
        {
            Name = "LeftFoot",
        };
        ikTargets.AddChild(leftFootIKTarget);

        FootTargetSyncController footSyncController = new()
        {
            Name = "FootTargetSyncController",
            LeftFootTarget = leftFootIKTarget,
            RightFootTarget = rightFootIKTarget,
            Active = true,
            Influence = 1.0f,
        };
        skeleton.AddChild(footSyncController);

        TCharacterIK characterIK = new()
        {
            Name = "CharacterIK",
            Viewpoint = viewpoint,
            HeadIKTarget = headIKTarget,
            HeadIKSolveTarget = headIKSolveTarget,
            RightHandIKTarget = rightHandIKTarget,
            LeftHandIKTarget = leftHandIKTarget,
            RightFootIKTarget = rightFootIKTarget,
            Skeleton = skeleton,
            HeadModifierGroup = [headModifier],
            RightHandModifierGroup = [rightHandModifier],
            RightFootModifierGroup = [rightFootModifier, footSyncController],
            HeadTargetMaximumSpeed = 1000.0f,
            HandTargetMaximumSpeed = 1000.0f,
            HandTargetMaximumAcceleration = 1000.0f,
            HandTargetSettleDistance = 10.0f,
        };
        characterIK.AddChild(ikTargets);
        root.AddChild(characterIK);

        sceneTree.Root.AddChild(root);
        await WaitForFramesAsync(sceneTree, 2);

        if (skeleton.GetNodeOrNull("CharacterIKBeginStage") is null)
        {
            characterIK._Ready();
            await WaitForNextFrameAsync(sceneTree);
        }

        SkeletonModifier3D vrikBeginStage = Assert.IsType<SkeletonModifier3D>(
            skeleton.GetNodeOrNull("CharacterIKBeginStage"),
            exactMatch: false);
        SkeletonModifier3D vrikFootProviderStage = Assert.IsType<SkeletonModifier3D>(
            skeleton.GetNodeOrNull("CharacterIKFootProviderStage"),
            exactMatch: false);

        return new CharacterIKFixture<TCharacterIK>(
            root,
            characterIK,
            skeleton,
            rightFootBoneIndex,
            headIKTarget,
            headIKSolveTarget,
            rightHandIKTarget,
            rightFootIKTarget,
            headModifier,
            rightHandModifier,
            rightFootModifier,
            footSyncController,
            vrikBeginStage,
            vrikFootProviderStage);
    }

    private static void InvokeOnBeginStage(CharacterIK characterIK, double delta)
    {
        MethodInfo method = typeof(CharacterIK).GetMethod(
                                "OnBeginStage",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("CharacterIK.OnBeginStage was not found.");

        _ = method.Invoke(characterIK, [delta]);
    }

    private static Transform3D InvokeBuildHandTargetTransform(CharacterIK characterIK, string methodName)
    {
        MethodInfo method = typeof(CharacterIK).GetMethod(
                                methodName,
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException($"CharacterIK.{methodName} was not found.");

        return (Transform3D)(method.Invoke(characterIK, [])
                             ?? throw new InvalidOperationException($"CharacterIK.{methodName} returned null."));
    }

    private sealed class VrikFixture(
        Node3D root,
        PlayerVRIK playerVRIK,
        Skeleton3D skeleton,
        int headBoneIndex,
        CharacterBody3D headIKTarget,
        Node3D headIKSolveTarget,
        AnimatableBody3D rightHandIKTarget,
        AnimatableBody3D leftHandIKTarget,
        TwoBoneIK3D rightHandIKModifier,
        TwoBoneIK3D leftHandIKModifier,
        XRControllerTargetProvider rightHandFallbackProvider,
        XRControllerTargetProvider leftHandFallbackProvider,
        TestXROrigin origin,
        TestXRCamera camera,
        TestXRHandController rightHandController,
        TestXRHandController leftHandController)
    {
        public Node3D Root { get; } = root;

        public PlayerVRIK PlayerVRIK { get; } = playerVRIK;

        public Skeleton3D Skeleton { get; } = skeleton;

        public int HeadBoneIndex { get; } = headBoneIndex;

        public CharacterBody3D HeadIKTarget { get; } = headIKTarget;

        public Node3D HeadIKSolveTarget { get; } = headIKSolveTarget;

        public AnimatableBody3D RightHandIKTarget { get; } = rightHandIKTarget;

        public AnimatableBody3D LeftHandIKTarget { get; } = leftHandIKTarget;

        public TwoBoneIK3D RightHandIKModifier { get; } = rightHandIKModifier;

        public TwoBoneIK3D LeftHandIKModifier { get; } = leftHandIKModifier;

        public XRControllerTargetProvider RightHandFallbackProvider { get; } = rightHandFallbackProvider;

        public XRControllerTargetProvider LeftHandFallbackProvider { get; } = leftHandFallbackProvider;

        public TestXROrigin Origin { get; } = origin;

        public TestXRCamera Camera { get; } = camera;

        public TestXRHandController RightHandController { get; } = rightHandController;

        public TestXRHandController LeftHandController { get; } = leftHandController;

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    private sealed class CharacterIKFixture<TCharacterIK>(
        Node3D root,
        TCharacterIK characterIK,
        Skeleton3D skeleton,
        int rightFootBoneIndex,
        CharacterBody3D headIKTarget,
        Node3D headIKSolveTarget,
        AnimatableBody3D rightHandIKTarget,
        Node3D rightFootIKTarget,
        SkeletonModifier3D headModifier,
        SkeletonModifier3D rightHandModifier,
        SkeletonModifier3D rightFootModifier,
        FootTargetSyncController footSyncController,
        SkeletonModifier3D vrikBeginStage,
        SkeletonModifier3D vrikFootProviderStage)
        where TCharacterIK : CharacterIK
    {
        public Node3D Root { get; } = root;

        public TCharacterIK VRIK { get; } = characterIK;

        public Skeleton3D Skeleton { get; } = skeleton;

        public int RightFootBoneIndex { get; } = rightFootBoneIndex;

        public CharacterBody3D HeadIKTarget { get; } = headIKTarget;

        public Node3D HeadIKSolveTarget { get; } = headIKSolveTarget;

        public AnimatableBody3D RightHandIKTarget { get; } = rightHandIKTarget;

        public Node3D RightFootIKTarget { get; } = rightFootIKTarget;

        public SkeletonModifier3D HeadModifier { get; } = headModifier;

        public SkeletonModifier3D RightHandModifier { get; } = rightHandModifier;

        public SkeletonModifier3D RightFootModifier { get; } = rightFootModifier;

        public FootTargetSyncController FootSyncController { get; } = footSyncController;

        public SkeletonModifier3D VRIKBeginStage { get; } = vrikBeginStage;

        public SkeletonModifier3D VRIKFootProviderStage { get; } = vrikFootProviderStage;

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    private static void AssertTransformApproximately(Transform3D expected, Transform3D actual, float epsilon = 1e-4f)
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

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual, float epsilon = 1e-4f)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
        Assert.InRange(actual.Z, expected.Z - epsilon, expected.Z + epsilon);
    }

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
        }

        public void EmitInitialisedResult(bool succeeded)
        {
            InitialisationAttempted = true;
            InitialisationSucceeded = succeeded;
            _ = EmitSignal("Initialised", succeeded);
        }
    }

    private sealed partial class TestPlayerVRIKStartupBinder : PlayerVRIKStartupBinder
    {
        public bool ReadyCalled
        {
            get;
            private set;
        }

        public PlayerVRIK? PlayerVRIKToBind
        {
            get;
            set;
        }

        public override void _Ready()
        {
            ReadyCalled = true;
            base._Ready();
        }

        protected override PlayerVRIK? ResolvePlayerVRIK()
            => PlayerVRIKToBind;
    }

    private sealed partial class TestPlayerVRIK : PlayerVRIK
    {
        public int BindCallCount
        {
            get;
            private set;
        }

        public override void _Ready()
        {
        }

        public override bool TryBind(IXRRuntime runtime)
        {
            BindCallCount++;
            return true;
        }
    }

    private sealed partial class TestIKTargetStateProvider : IKTargetStateProvider
    {
        public IKTargetState TargetState
        {
            get;
            set;
        }

        public override IKTargetState GetTargetState()
            => TargetState;
    }

    private sealed partial class FootTargetConsumer : SkeletonModifier3D
    {
        public Node3D? FootTarget
        {
            get;
            set;
        }

        public Transform3D ConsumedTransform
        {
            get;
            private set;
        } = Transform3D.Identity;

        public Transform3D ConsumedLocalTransform
        {
            get;
            private set;
        } = Transform3D.Identity;

        public override void _ProcessModificationWithDelta(double delta)
        {
            _ = delta;
            if (FootTarget is not null)
            {
                ConsumedTransform = FootTarget.GlobalTransform;
                ConsumedLocalTransform = FootTarget.Transform;
            }
        }
    }

    private sealed partial class TestXROrigin(float worldScale) : Node3D, IXROrigin
    {
        public Node3D OriginNode => this;

        public float WorldScale
        {
            get;
            set;
        } = worldScale;
    }

    private sealed class TestXRCamera(Camera3D cameraNode) : IXRCamera
    {
        public Camera3D CameraNode => cameraNode;
    }

    private sealed class TestXRHandController(Node3D controllerNode, Node3D handPositionNode) : IXRHandController
    {
#pragma warning disable CS0067
        public event Action<string>? ActionButtonPressed;

        public event Action<string>? ActionButtonReleased;

        public event Action<string, float>? ActionFloatInputChanged;

        public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

        public Node3D ControllerNode => controllerNode;

        public Node3D HandPositionNode => handPositionNode;
    }
}
