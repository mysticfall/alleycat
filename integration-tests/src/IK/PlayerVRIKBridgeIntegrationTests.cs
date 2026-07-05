using System.Reflection;
using AlleyCat.IK;
using AlleyCat.IK.Pose;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using DynamicPhysicalRig = AlleyCat.Rigging.Physics.DynamicPhysicalRig;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for player XR↔IK bridge runtime behaviour.
/// </summary>
public sealed partial class PlayerVRIKBridgeIntegrationTests
{
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string FemaleReferenceNPCScenePath = "res://assets/characters/reference/ally.tscn";
    private const string ReferencePlayerFixtureScenePath = "res://assets/testing/reference_player_fixture/reference_player_fixture.tscn";
    private const string PlayerVRIKScriptPath = "res://src/IK/PlayerVRIK.cs";
    private const string CharacterIKScriptPath = "res://src/IK/CharacterIK.cs";
    private const string DynamicPhysicalRigScriptPath = "res://src/Rigging/Physics/DynamicPhysicalRig.cs";
    private static NodePath ExpectedPlayerPhysicalRigPath => new("../Female/GeneralSkeleton/DynamicPhysicalRig");
    private static NodePath ExpectedFemaleReferenceNPCPhysicalRigPath => new("../Female/GeneralSkeleton/DynamicPhysicalRig");

    /// <summary>
    /// Verifies the actual player scene explicitly wires PlayerVRIK to the generated physical rig dependency.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_PlayerVRIKPhysicalRig_ResolvesToGeneratedDynamicPhysicalRig()
    {
        SceneTree sceneTree = GetSceneTree();
        Node player = LoadPackedScene(PlayerScenePath).Instantiate();
        sceneTree.Root.AddChild(player);
        await WaitForFramesAsync(sceneTree, 6);
        EnsureRuntimeRoleInstalled(player);

        try
        {
            Node playerVRIK = player.GetNode<Node>("VRIK");
            Node physicalRig = player.GetNode<Node>("Female/GeneralSkeleton/DynamicPhysicalRig");
            GodotObject configuredPhysicalRig = GetScriptProperty<GodotObject>(playerVRIK, "PhysicalRig")
                ?? throw new Xunit.Sdk.XunitException("Expected player VRIK PhysicalRig to resolve to a node.");

            Assert.Equal(physicalRig.GetInstanceId(), configuredPhysicalRig.GetInstanceId());
            Assert.Equal(ExpectedPlayerPhysicalRigPath, playerVRIK.GetPathTo(physicalRig));
            Assert.Equal(PlayerVRIKScriptPath, AssertNodeScriptPath(playerVRIK));
            Assert.Equal(DynamicPhysicalRigScriptPath, AssertNodeScriptPath(physicalRig));
        }
        finally
        {
            player.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the female reference NPC scene explicitly wires CharacterIK to the generated physical rig dependency.
    /// </summary>
    [Headless]
    [Fact]
    public async Task FemaleReferenceNPCScene_CharacterIKPhysicalRig_ResolvesToGeneratedDynamicPhysicalRig()
    {
        SceneTree sceneTree = GetSceneTree();
        Node npc = LoadPackedScene(FemaleReferenceNPCScenePath).Instantiate();
        sceneTree.Root.AddChild(npc);
        await WaitForFramesAsync(sceneTree, 6);
        EnsureRuntimeRoleInstalled(npc);

        try
        {
            Node characterIK = npc.GetNode<Node>("CharacterIK");
            Node physicalRig = npc.GetNode<Node>("Female/GeneralSkeleton/DynamicPhysicalRig");
            GodotObject configuredPhysicalRig = GetScriptProperty<GodotObject>(characterIK, "PhysicalRig")
                ?? throw new Xunit.Sdk.XunitException("Expected female reference NPC CharacterIK PhysicalRig to resolve to a node.");

            Assert.Equal(physicalRig.GetInstanceId(), configuredPhysicalRig.GetInstanceId());
            Assert.Equal(ExpectedFemaleReferenceNPCPhysicalRigPath, characterIK.GetPathTo(physicalRig));
            Assert.Equal(CharacterIKScriptPath, AssertNodeScriptPath(characterIK));
            Assert.Equal(DynamicPhysicalRigScriptPath, AssertNodeScriptPath(physicalRig));
        }
        finally
        {
            npc.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the actual player scene's role installer binds PlayerVRIK to the installed head viewpoint marker.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_PlayerVRIKViewpoint_ResolvesToInstalledHeadViewpoint()
    {
        SceneTree sceneTree = GetSceneTree();
        Node player = LoadPackedScene(PlayerScenePath).Instantiate();
        sceneTree.Root.AddChild(player);
        await WaitForFramesAsync(sceneTree, 8);
        await WaitForPhysicsFramesAsync(sceneTree, 2);
        EnsureRuntimeRoleInstalled(player);

        try
        {
            Node playerVRIK = player.GetNode<Node>("VRIK");
            Marker3D viewpoint = player.GetNode<Marker3D>("Female/GeneralSkeleton/Head/Viewpoint");
            GodotObject configuredViewpoint = GetGodotNodeProperty(playerVRIK, nameof(CharacterIK.Viewpoint), "viewpoint")
                ?? throw new Xunit.Sdk.XunitException("Expected player VRIK Viewpoint to resolve to a marker.");

            Assert.Equal(viewpoint.GetInstanceId(), configuredViewpoint.GetInstanceId());
            Assert.Equal(new NodePath("../Female/GeneralSkeleton/Head/Viewpoint"), playerVRIK.GetPathTo(viewpoint));
        }
        finally
        {
            player.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the actual NPC scene's role installer binds CharacterIK to the installed head viewpoint marker.
    /// </summary>
    [Headless]
    [Fact]
    public async Task FemaleReferenceNPCScene_CharacterIKViewpoint_ResolvesToInstalledHeadViewpoint()
    {
        SceneTree sceneTree = GetSceneTree();
        Node npc = LoadPackedScene(FemaleReferenceNPCScenePath).Instantiate();
        sceneTree.Root.AddChild(npc);
        await WaitForFramesAsync(sceneTree, 8);
        await WaitForPhysicsFramesAsync(sceneTree, 2);
        EnsureRuntimeRoleInstalled(npc);

        try
        {
            Node characterIK = npc.GetNode<Node>("CharacterIK");
            Marker3D viewpoint = npc.GetNode<Marker3D>("Female/GeneralSkeleton/Head/Viewpoint");
            GodotObject configuredViewpoint = GetGodotNodeProperty(characterIK, nameof(CharacterIK.Viewpoint), "viewpoint")
                ?? throw new Xunit.Sdk.XunitException("Expected NPC CharacterIK Viewpoint to resolve to a marker.");

            Assert.Equal(viewpoint.GetInstanceId(), configuredViewpoint.GetInstanceId());
            Assert.Equal(new NodePath("../Female/GeneralSkeleton/Head/Viewpoint"), characterIK.GetPathTo(viewpoint));
        }
        finally
        {
            npc.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a reference-player fixture resolves player/NPC IK viewpoints and NPC eye-tracking bindings.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferencePlayerFixture_RuntimeInstallers_BindIKViewpointsAndNpcEyeTracking()
    {
        SceneTree sceneTree = GetSceneTree();
        Node fixture = LoadPackedScene(ReferencePlayerFixtureScenePath).Instantiate();
        sceneTree.Root.AddChild(fixture);
        await WaitForFramesAsync(sceneTree, 10);
        await WaitForPhysicsFramesAsync(sceneTree, 2);
        EnsureRuntimeRoleInstalled(fixture.GetNode("Actors/Player"));
        EnsureRuntimeRoleInstalled(GetMirrorRoomAllyActor(fixture));

        try
        {
            Node player = fixture.GetNode("Actors/Player");
            Node playerVRIK = player.GetNode<Node>("VRIK");
            Marker3D playerViewpoint = player.GetNode<Marker3D>("Female/GeneralSkeleton/Head/Viewpoint");
            Node npc = GetMirrorRoomAllyActor(fixture);
            Node npcIK = npc.GetNode<Node>("CharacterIK");
            Marker3D npcViewpoint = npc.GetNode<Marker3D>("Female/GeneralSkeleton/Head/Viewpoint");
            Node npcEyes = npc.GetNode<Node>("Eyes");
            GodotObject playerConfiguredViewpoint = GetGodotNodeProperty(playerVRIK, nameof(CharacterIK.Viewpoint), "viewpoint")
                ?? throw new Xunit.Sdk.XunitException("Expected reference-player fixture player VRIK Viewpoint to resolve to a marker.");
            GodotObject npcConfiguredViewpoint = GetGodotNodeProperty(npcIK, nameof(CharacterIK.Viewpoint), "viewpoint")
                ?? throw new Xunit.Sdk.XunitException("Expected reference-player fixture NPC CharacterIK Viewpoint to resolve to a marker.");
            GodotObject npcEyeOrigin = GetGodotNodeProperty(npcEyes, "EyeOrigin", "eye_origin")
                ?? throw new Xunit.Sdk.XunitException("Expected reference-player fixture NPC Eyes EyeOrigin to resolve to a marker.");

            Assert.Equal(playerViewpoint.GetInstanceId(), playerConfiguredViewpoint.GetInstanceId());
            Assert.Equal(npcViewpoint.GetInstanceId(), npcConfiguredViewpoint.GetInstanceId());
            Assert.Equal(npcViewpoint.GetInstanceId(), npcEyeOrigin.GetInstanceId());
        }
        finally
        {
            fixture.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies reusable CharacterIK authoring fails explicitly when no viewpoint is bound.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterIK_MissingViewpoint_FailsFastWithAuthoringMessage()
    {
        using CharacterIK characterIK = new()
        {
            Name = "CharacterIK",
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(characterIK._Ready);

        Assert.Contains(nameof(CharacterIK.Viewpoint), exception.Message, StringComparison.Ordinal);
        Assert.Contains("install a character module", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies reusable CharacterIK authoring fails explicitly when no physical rig is bound.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterIK_MissingPhysicalRig_FailsFastWithAuthoringMessage()
    {
        using CharacterIK characterIK = CreateCharacterIKWithRequiredTargets();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(characterIK._Ready);

        Assert.Contains(nameof(CharacterIK.PhysicalRig), exception.Message, StringComparison.Ordinal);
        Assert.Contains("install a character module", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the female reference NPC CharacterIK consumes the same staged hand grab providers as the hand behaviours.
    /// </summary>
    [Headless]
    [Fact]
    public async Task FemaleReferenceNPCScene_CharacterIKHandTargetProviders_ResolveToHandGrabProviders()
    {
        SceneTree sceneTree = GetSceneTree();
        Node npc = LoadPackedScene(FemaleReferenceNPCScenePath).Instantiate();
        sceneTree.Root.AddChild(npc);

        try
        {
            await WaitForFramesAsync(sceneTree, 8);
            EnsureRuntimeRoleInstalled(npc);
            Node characterIK = npc.GetNode<Node>("CharacterIK");
            Node rightProvider = npc.GetNode<Node>("CharacterIK/RightHandGrabProvider");
            Node leftProvider = npc.GetNode<Node>("CharacterIK/LeftHandGrabProvider");
            GodotObject configuredRightProvider = characterIK.Get("RightHandIKTargetIntentProvider").AsGodotObject()
                ?? throw new Xunit.Sdk.XunitException("Expected female reference NPC CharacterIK right hand provider to resolve to a node.");
            GodotObject configuredLeftProvider = characterIK.Get("LeftHandIKTargetIntentProvider").AsGodotObject()
                ?? throw new Xunit.Sdk.XunitException("Expected female reference NPC CharacterIK left hand provider to resolve to a node.");

            Assert.Equal(rightProvider.GetInstanceId(), configuredRightProvider.GetInstanceId());
            Assert.Equal(leftProvider.GetInstanceId(), configuredLeftProvider.GetInstanceId());
            Assert.Equal(new NodePath("RightHandGrabProvider"), characterIK.GetPathTo(rightProvider));
            Assert.Equal(new NodePath("LeftHandGrabProvider"), characterIK.GetPathTo(leftProvider));
            Assert.Equal(typeof(HandGrabTargetProvider).FullName, configuredRightProvider.GetType().FullName);
            Assert.Equal(typeof(HandGrabTargetProvider).FullName, configuredLeftProvider.GetType().FullName);
        }
        finally
        {
            npc.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the real player auto-install path keeps grab providers staged while routing them to XR controller defaults.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_HandGrabProviders_DefaultToXRControllerSourcesAndEmitMovedHandIntent()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame root = new()
        {
            Name = "PlayerHandMovementFixture",
        };

        TestXRManager xrManager = new()
        {
            Name = "XR",
        };
        root.AddChild(xrManager);

        TestXROrigin origin = new(1.0f)
        {
            Name = "Origin",
        };
        root.AddChild(origin);

        Camera3D cameraNode = new()
        {
            Name = "Camera",
            Position = new Vector3(0.0f, 1.6f, 0.0f),
        };
        origin.AddChild(cameraNode);

        Node3D rightController = new()
        {
            Name = "RightController",
        };
        origin.AddChild(rightController);
        Node3D rightSource = new()
        {
            Name = "RightHandPosition",
        };
        rightController.AddChild(rightSource);

        Node3D leftController = new()
        {
            Name = "LeftController",
        };
        origin.AddChild(leftController);
        Node3D leftSource = new()
        {
            Name = "LeftHandPosition",
        };
        leftController.AddChild(leftSource);

        xrManager.SetRuntime(new TestXRRuntime(
            origin,
            new TestXRCamera(cameraNode),
            new TestXRHandController(rightController, rightSource),
            new TestXRHandController(leftController, leftSource)));

        Node player = LoadPackedScene(PlayerScenePath).Instantiate();
        root.AddChild(player);

        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 10);
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            EnsureRuntimeRoleInstalled(player);

            Node playerVRIK = player.GetNode<Node>("VRIK");
            Node rightGrabProvider = player.GetNode<Node>("VRIK/RightHandGrabProvider");
            Node leftGrabProvider = player.GetNode<Node>("VRIK/LeftHandGrabProvider");
            Node rightFallback = player.GetNode<Node>("VRIK/RightHandFallbackIntentProvider");
            Node leftFallback = player.GetNode<Node>("VRIK/LeftHandFallbackIntentProvider");
            AnimatableBody3D rightHandTarget = player.GetNode<AnimatableBody3D>("IKTargets/RightHand");
            AnimatableBody3D leftHandTarget = player.GetNode<AnimatableBody3D>("IKTargets/LeftHand");
            GodotObject configuredRightProvider = GetGodotNodeProperty(playerVRIK, "RightHandIKTargetIntentProvider", "right_hand_iktarget_intent_provider")
                ?? throw new Xunit.Sdk.XunitException("Expected player VRIK right hand provider to resolve to a node.");
            GodotObject configuredLeftProvider = GetGodotNodeProperty(playerVRIK, "LeftHandIKTargetIntentProvider", "left_hand_iktarget_intent_provider")
                ?? throw new Xunit.Sdk.XunitException("Expected player VRIK left hand provider to resolve to a node.");
            GodotObject configuredRightDefault = GetGodotNodeProperty(rightGrabProvider, nameof(HandGrabTargetProvider.DefaultProvider), "default_provider")
                ?? throw new Xunit.Sdk.XunitException("Expected right grab provider default to resolve to a node.");
            GodotObject configuredLeftDefault = GetGodotNodeProperty(leftGrabProvider, nameof(HandGrabTargetProvider.DefaultProvider), "default_provider")
                ?? throw new Xunit.Sdk.XunitException("Expected left grab provider default to resolve to a node.");

            Assert.Equal(rightGrabProvider.GetInstanceId(), configuredRightProvider.GetInstanceId());
            Assert.Equal(leftGrabProvider.GetInstanceId(), configuredLeftProvider.GetInstanceId());
            Assert.Equal(rightFallback.GetInstanceId(), configuredRightDefault.GetInstanceId());
            Assert.Equal(leftFallback.GetInstanceId(), configuredLeftDefault.GetInstanceId());
            Assert.Equal((long)LimbSide.Right, rightFallback.Get("Side").AsInt64());
            Assert.Equal((long)LimbSide.Left, leftFallback.Get("Side").AsInt64());

            SetScriptProperty(rightFallback, nameof(XRControllerTargetProvider.ResolvedSourceNode), rightSource);
            SetScriptProperty(leftFallback, nameof(XRControllerTargetProvider.ResolvedSourceNode), leftSource);
            SetScriptField(playerVRIK, "_isBound", true);

            Transform3D rightTargetTransform = new(Basis.Identity, rightHandTarget.GlobalPosition + new Vector3(0.01f, 0.0f, 0.0f));
            Transform3D leftTargetTransform = new(Basis.Identity, leftHandTarget.GlobalPosition + new Vector3(-0.01f, 0.0f, 0.0f));
            rightSource.Position = rightTargetTransform.Origin;
            leftSource.Position = leftTargetTransform.Origin;
            rightSource.ForceUpdateTransform();
            leftSource.ForceUpdateTransform();
            rightTargetTransform = rightSource.GlobalTransform;
            leftTargetTransform = leftSource.GlobalTransform;

            object rightIntent = InvokeScriptMethod(rightGrabProvider, nameof(HandGrabTargetProvider.GetTargetIntent));
            object leftIntent = InvokeScriptMethod(leftGrabProvider, nameof(HandGrabTargetProvider.GetTargetIntent));
            InvokeScriptVoidMethod(playerVRIK, nameof(PlayerVRIK._PhysicsProcess), 1.0d / 60.0d);
            await WaitForPhysicsFramesAsync(sceneTree, 2);

            GodotObject configuredRightSource = GetGodotNodeProperty(rightFallback, nameof(XRControllerTargetProvider.ResolvedSourceNode), "resolved_source_node")
                ?? throw new Xunit.Sdk.XunitException("Expected right XR fallback source to resolve to a node.");
            GodotObject configuredLeftSource = GetGodotNodeProperty(leftFallback, nameof(XRControllerTargetProvider.ResolvedSourceNode), "resolved_source_node")
                ?? throw new Xunit.Sdk.XunitException("Expected left XR fallback source to resolve to a node.");

            Assert.Equal(rightSource.GetInstanceId(), configuredRightSource.GetInstanceId());
            Assert.Equal(leftSource.GetInstanceId(), configuredLeftSource.GetInstanceId());
            AssertIntentApproximately(rightTargetTransform, 1.0f, rightIntent);
            AssertIntentApproximately(leftTargetTransform, 1.0f, leftIntent);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies startup binder does not attempt VRIK binding after XR initialisation failure.
    /// </summary>
    [Fact]
    public async Task StartupBinder_WhenXRInitialisationFails_DoesNotBindPlayerVRIK()
    {
        SceneTree sceneTree = GetSceneTree();

        Game root = new()
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

        root._EnterTree();
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
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

        Game root = new()
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

        root._EnterTree();
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            xrManager.EmitInitialisedResult(succeeded: true);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.True(xrManager.InitialisationAttempted);
            Assert.True(xrManager.InitialisationSucceeded);
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
            fixture.PlayerVRIK._Ready();
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

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
            fixture.PlayerVRIK._Ready();
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);

            Transform3D rightFollowTarget = InvokeBuildHandTargetTransform(fixture.PlayerVRIK, "BuildRightHandTargetTransform");
            Transform3D leftFollowTarget = InvokeBuildHandTargetTransform(fixture.PlayerVRIK, "BuildLeftHandTargetTransform");

            AssertTransformApproximately(fixture.RightHandController.HandPositionNode.GlobalTransform, rightFollowTarget);
            AssertTransformApproximately(fixture.LeftHandController.HandPositionNode.GlobalTransform, leftFollowTarget);
            Assert.Null(fixture.PlayerVRIK.RightHandIKTargetIntentProvider);
            Assert.Null(fixture.PlayerVRIK.LeftHandIKTargetIntentProvider);
            Assert.Equal(LimbSide.Right, fixture.RightHandFallbackIntentProvider.Side);
            Assert.Equal(LimbSide.Left, fixture.LeftHandFallbackIntentProvider.Side);
            Assert.Same(fixture.RightHandController.HandPositionNode, fixture.RightHandFallbackIntentProvider.ResolvedSourceNode);
            Assert.Same(fixture.LeftHandController.HandPositionNode, fixture.LeftHandFallbackIntentProvider.ResolvedSourceNode);
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
        TestIKTargetIntentProvider provider = new()
        {
            Name = "RightHandProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(1.2f, 0.9f, -0.8f)),
                0.42f),
        };

        fixture.Root.AddChild(provider);

        try
        {
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);

            fixture.PlayerVRIK.RightHandIKTargetIntentProvider = provider;

            Transform3D providerFollowTarget = InvokeBuildHandTargetTransform(
                fixture.PlayerVRIK,
                "BuildRightHandTargetTransform");

            AssertTransformApproximately(provider.TargetIntent.WorldTransform, providerFollowTarget);
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
        TestIKTargetIntentProvider provider = new()
        {
            Name = "RightHandZeroInfluenceProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(1.2f, 0.9f, -0.8f)),
                0.0f),
        };

        fixture.Root.AddChild(provider);

        try
        {
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);

            fixture.PlayerVRIK.RightHandIKTargetIntentProvider = provider;

            Transform3D currentTarget = fixture.RightHandIKTarget.GlobalTransform;
            Transform3D resolvedTarget = InvokeBuildHandTargetTransform(
                fixture.PlayerVRIK,
                "BuildRightHandTargetTransform");

            AssertTransformApproximately(currentTarget, resolvedTarget);
            Assert.NotEqual(provider.TargetIntent.WorldTransform.Origin, resolvedTarget.Origin);
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
    /// Verifies bound player head IK does not implicitly follow the camera when no explicit provider or fallback is assigned.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIKHeadFollow_WhenNoProviderAndCameraDefaultDiffers_DoesNotMoveTargetAndDisablesTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            fixture.PlayerVRIK._Ready();
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);
            fixture.PlayerVRIK.HeadTargetIntentProvider = null;
            fixture.PlayerVRIK.HeadFallbackIntentProvider = null;
            fixture.HeadIKTarget.Position = new Vector3(-0.4f, 1.1f, 0.25f);
            fixture.Camera.CameraNode.Position = new Vector3(0.6f, 1.8f, -0.7f);
            await WaitForNextFrameAsync(sceneTree);
            Transform3D initialTarget = fixture.HeadIKTarget.GlobalTransform;
            Transform3D cameraDefault = fixture.Camera.CameraNode.GlobalTransform * fixture.PlayerVRIK.Viewpoint!.Transform.Inverse();

            await ProcessBeginStageFramesAsync(sceneTree, fixture.PlayerVRIK, 2);

            Assert.NotEqual(cameraDefault.Origin, initialTarget.Origin);
            AssertTransformApproximately(initialTarget, fixture.HeadIKTarget.GlobalTransform);
            AssertTargetDisabled(fixture.HeadIKTarget);
            AssertTargetDisabled(fixture.HeadIKSolveTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the bound XR head fallback actively drives player head IK from the camera-derived head pose.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIKHeadFollowTargets_WhenBound_UsesXRCameraProvider()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);

            fixture.HeadIKTarget.GlobalTransform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Up, 0.3f),
                new Vector3(-0.6f, 0.9f, 0.4f));
            fixture.HeadIKSolveTarget.GlobalTransform = Transform3D.Identity;
            fixture.Origin.GlobalTransform = Transform3D.Identity;
            fixture.Camera.CameraNode.Transform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Right, -0.22f).Rotated(Vector3.Forward, 0.14f),
                new Vector3(0.25f, 1.7f, -0.35f));
            fixture.Camera.CameraNode.ForceUpdateTransform();
            await WaitForNextFrameAsync(sceneTree);

            Transform3D expectedHeadPose = fixture.Camera.CameraNode.GlobalTransform
                                           * fixture.PlayerVRIK.Viewpoint!.Transform.Inverse();
            IKTargetIntent fallbackIntent = fixture.HeadFallbackIntentProvider.GetTargetIntent();

            Assert.Equal(1.0f, fallbackIntent.DesiredInfluence);
            AssertTransformApproximately(expectedHeadPose, fallbackIntent.WorldTransform);
            Assert.NotEqual(expectedHeadPose.Origin, fixture.HeadIKTarget.GlobalPosition);

            await ProcessBeginStageFramesAsync(sceneTree, fixture.PlayerVRIK, 2);

            Transform3D expectedFollowPose = fixture.Camera.CameraNode.GlobalTransform
                                            * fixture.PlayerVRIK.Viewpoint!.Transform.Inverse();
            Transform3D resolvedHeadPose = InvokeBuildHeadTargetTransform(fixture.PlayerVRIK);
            fixture.HeadIKSolveTarget.Transform = fixture.HeadIKTarget.Transform;

            Assert.True(fixture.HeadModifier.Active);
            Assert.Equal(1.0f, fixture.HeadModifier.Influence);
            AssertTargetProcessEnabled(fixture.HeadIKTarget);
            AssertTargetProcessEnabled(fixture.HeadIKSolveTarget);
            AssertTransformApproximately(expectedFollowPose, resolvedHeadPose);
            AssertTransformApproximately(expectedFollowPose, fixture.HeadIKTarget.Transform);
            AssertTransformApproximately(expectedFollowPose, fixture.HeadIKSolveTarget.Transform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies pure headset tilt is consumed by the active XR head fallback without drifting the compensated XR origin.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIKOriginCompensation_WhenXRHeadTiltsWithFallback_KeepsOriginStable()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);

            Transform3D unchangedHeadTarget = new(
                Basis.Identity.Rotated(Vector3.Up, 0.4f),
                new Vector3(-0.35f, 1.05f, 0.3f));
            fixture.HeadIKTarget.GlobalTransform = unchangedHeadTarget;
            fixture.HeadIKSolveTarget.GlobalTransform = unchangedHeadTarget;
            fixture.Origin.GlobalTransform = Transform3D.Identity;
            fixture.Camera.CameraNode.Transform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Right, -0.31f).Rotated(Vector3.Forward, 0.27f),
                new Vector3(0.0f, 1.6f, 0.0f));
            fixture.Camera.CameraNode.ForceUpdateTransform();
            await WaitForNextFrameAsync(sceneTree);

            Transform3D expectedHeadPose = fixture.Camera.CameraNode.GlobalTransform
                                           * fixture.PlayerVRIK.Viewpoint!.Transform.Inverse();
            Assert.NotEqual(unchangedHeadTarget.Origin, expectedHeadPose.Origin);

            await ProcessBeginStageFramesAsync(sceneTree, fixture.PlayerVRIK, 2);

            Transform3D expectedFollowPose = fixture.Camera.CameraNode.GlobalTransform
                                            * fixture.PlayerVRIK.Viewpoint!.Transform.Inverse();
            Transform3D resolvedHeadPose = InvokeBuildHeadTargetTransform(fixture.PlayerVRIK);
            fixture.HeadIKSolveTarget.Transform = fixture.HeadIKTarget.Transform;

            AssertTransformApproximately(expectedFollowPose, resolvedHeadPose);
            AssertTransformApproximately(expectedFollowPose, fixture.HeadIKTarget.Transform);
            AssertTransformApproximately(expectedFollowPose, fixture.HeadIKSolveTarget.Transform);

            SetHeadBoneGlobalPose(fixture.Skeleton, fixture.HeadBoneIndex, expectedFollowPose);
            Transform3D originBeforeCompensation = fixture.Origin.GlobalTransform;

            InvokeOnEndStage(fixture.PlayerVRIK, 1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            AssertTransformApproximately(originBeforeCompensation, fixture.Origin.GlobalTransform, 1e-5f);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a zero-influence head provider disables head IK without moving the physical head target.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKHeadProvider_WhenZeroInfluence_DoesNotMoveTargetAndDisablesTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "ZeroInfluenceHeadProviderFixture");
        TestIKTargetIntentProvider provider = new()
        {
            Name = "HeadZeroInfluenceProvider",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(2.0f, 2.0f, 2.0f)), 0.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.HeadTargetIntentProvider = provider;

        try
        {
            fixture.HeadIKTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.1f, 0.2f, 0.3f));
            Transform3D initialTarget = fixture.HeadIKTarget.GlobalTransform;

            InvokeOnBeginStage(fixture.VRIK, 1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            AssertTransformApproximately(initialTarget, fixture.HeadIKTarget.GlobalTransform);
            Assert.NotEqual(provider.TargetIntent.WorldTransform.Origin, fixture.HeadIKTarget.GlobalPosition);
            Assert.False(fixture.HeadModifier.Active);
            Assert.Equal(0.0f, fixture.HeadModifier.Influence);
            AssertTargetDisabled(fixture.HeadIKTarget);
            AssertTargetDisabled(fixture.HeadIKSolveTarget);
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
        TestIKTargetIntentProvider provider = new()
        {
            Name = "RightHandZeroInfluenceProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(2.0f, 2.0f, 2.0f)),
                0.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandIKTargetIntentProvider = provider;

        try
        {
            Transform3D restTarget = fixture.RightHandIKTarget.Transform;
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            AssertTransformApproximately(restTarget, fixture.RightHandIKTarget.Transform);
            Assert.NotEqual(provider.TargetIntent.WorldTransform.Origin, fixture.RightHandIKTarget.GlobalPosition);
            Assert.False(fixture.RightHandModifier.Active);
            Assert.Equal(0.0f, fixture.RightHandModifier.Influence);
            AssertTargetDisabled(fixture.RightHandIKTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies resolving a hand provider only returns the desired follow pose and leaves collision-aware movement to the physics actuator.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKHandProvider_WhenInfluenceIsPositive_DoesNotTeleportRuntimeHandTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "PositiveInfluenceHandProviderFixture");
        TestIKTargetIntentProvider provider = new()
        {
            Name = "RightHandPositiveInfluenceProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(2.0f, 2.0f, 2.0f)),
                1.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandIKTargetIntentProvider = provider;

        try
        {
            Transform3D authoredTarget = fixture.RightHandIKTarget.Transform;
            Transform3D resolvedTarget = InvokeBuildHandTargetTransform(fixture.VRIK, "BuildRightHandTargetTransform");

            AssertTransformApproximately(provider.TargetIntent.WorldTransform, resolvedTarget);
            AssertTransformApproximately(authoredTarget, fixture.RightHandIKTarget.Transform);
            Assert.NotEqual(provider.TargetIntent.WorldTransform.Origin, fixture.RightHandIKTarget.GlobalPosition);
            Assert.True(fixture.RightHandModifier.Active);
            Assert.Equal(1.0f, fixture.RightHandModifier.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the BODY-005 right-hand target pipeline preserves provider intent through the actual CharacterIK
    /// physics actuator path when no constraining contributors are configured.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKRightHandPipeline_WhenNoContributors_PreservesProviderIntentAndActuatorOutput()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "RightHandPipelineNoContributorFixture");
        TestIKTargetIntentProvider provider = new()
        {
            Name = "RightHandPipelineProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity.Rotated(Vector3.Up, 0.25f), new Vector3(0.22f, 0.14f, -0.31f)),
                1.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandIKTargetIntentProvider = provider;
        fixture.VRIK.RightHandIKTargetContributors = [];

        try
        {
            CharacterIKRightHandPipelineObservation observation = await RunRightHandPipelineActuatorComparisonAsync(
                sceneTree,
                fixture,
                provider.TargetIntent.WorldTransform);

            AssertRightHandPipelinePreservesProviderIntent(fixture.VRIK, provider.TargetIntent.WorldTransform);
            AssertTransformApproximately(observation.ExpectedRealisedTarget, fixture.RightHandIKTarget.GlobalTransform);
            AssertTransformApproximately(
                observation.ExpectedRealisedTarget,
                fixture.VRIK.RightHandTargetPipelineDebugState.RealisedTarget);
            Assert.Equal(observation.ExpectedFeedback.Reason, fixture.VRIK.RightHandTargetPipelineDebugState.Feedback.Reason);
            Assert.Equal(1UL, fixture.VRIK.PhysicsActuatorTickCount);
            AssertTargetEnabled(fixture.RightHandIKTarget, 16u, 32u);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the BODY-005 left-hand target pipeline preserves provider intent through the actual CharacterIK
    /// physics actuator path when no constraining contributors are configured.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKLeftHandPipeline_WhenNoContributors_PreservesProviderIntentAndActuatorOutput()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "LeftHandPipelineNoContributorFixture");
        TestIKTargetIntentProvider provider = new()
        {
            Name = "LeftHandPipelineProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity.Rotated(Vector3.Up, -0.2f), new Vector3(-0.24f, 0.13f, -0.28f)),
                1.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.LeftHandIKTargetIntentProvider = provider;
        fixture.VRIK.LeftHandIKTargetContributors = [];

        try
        {
            CharacterIKRightHandPipelineObservation observation = await RunLeftHandPipelineActuatorComparisonAsync(
                sceneTree,
                fixture,
                provider.TargetIntent.WorldTransform);

            AssertLeftHandPipelinePreservesProviderIntent(fixture.VRIK, provider.TargetIntent.WorldTransform);
            AssertTransformApproximately(observation.ExpectedRealisedTarget, fixture.LeftHandIKTarget.GlobalTransform);
            AssertTransformApproximately(
                observation.ExpectedRealisedTarget,
                fixture.VRIK.LeftHandTargetPipelineDebugState.RealisedTarget);
            Assert.Equal(observation.ExpectedFeedback.Reason, fixture.VRIK.LeftHandTargetPipelineDebugState.Feedback.Reason);
            Assert.Equal(1UL, fixture.VRIK.PhysicsActuatorTickCount);
            AssertTargetEnabled(fixture.LeftHandIKTarget, 64u, 128u);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the head target path publishes a pipeline debug result while preserving head solve target output.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKHeadPipeline_WhenProviderConfigured_PublishesDebugStateAndSolveTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "HeadPipelineFixture");
        TestIKTargetIntentProvider provider = new()
        {
            Name = "HeadPipelineProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity.Rotated(Vector3.Up, 0.15f), new Vector3(0.02f, 1.71f, -0.08f)),
                1.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.HeadTargetIntentProvider = provider;

        try
        {
            InvokeOnBeginStage(fixture.VRIK, 1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            IKTargetPipelineResult debugState = fixture.VRIK.HeadTargetPipelineDebugState;
            AssertTransformApproximately(provider.TargetIntent.WorldTransform, debugState.SourceTarget);
            AssertTransformApproximately(provider.TargetIntent.WorldTransform, debugState.RequestedTarget);
            AssertTransformApproximately(fixture.HeadIKTarget.GlobalTransform, debugState.RealisedTarget);
            AssertTransformApproximately(fixture.HeadIKTarget.GlobalTransform, fixture.HeadIKSolveTarget.GlobalTransform);
            Assert.True(fixture.HeadModifier.Active);
            Assert.Equal(1.0f, fixture.HeadModifier.Influence);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a no-op contributor is behaviourally identical to the no-contributor CharacterIK right-hand path.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKRightHandPipeline_WhenNoOpContributorConfigured_MatchesNoContributorOutput()
    {
        SceneTree sceneTree = GetSceneTree();
        Transform3D sourceTarget = new(Basis.Identity, new Vector3(0.18f, 0.12f, -0.27f));
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "RightHandPipelineNoOpContributorFixture");
        TestIKTargetIntentProvider provider = new()
        {
            Name = "RightHandPipelineProvider",
            TargetIntent = new IKTargetIntent(sourceTarget, 1.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandIKTargetIntentProvider = provider;

        try
        {
            CharacterIKRightHandPipelineObservation noContributorObservation = await RunRightHandPipelineActuatorComparisonAsync(
                sceneTree,
                fixture,
                sourceTarget,
                []);
            CharacterIKRightHandPipelineObservation noOpContributorObservation = await RunRightHandPipelineActuatorComparisonAsync(
                sceneTree,
                fixture,
                sourceTarget,
                [new NoOpIKTargetContributor()]);

            AssertTransformApproximately(noContributorObservation.SourceTarget, noOpContributorObservation.SourceTarget);
            AssertTransformApproximately(noContributorObservation.RequestedTarget, noOpContributorObservation.RequestedTarget);
            AssertTransformApproximately(noContributorObservation.RealisedTarget, noOpContributorObservation.RealisedTarget);
            AssertTransformApproximately(noContributorObservation.ExpectedRealisedTarget, noOpContributorObservation.ExpectedRealisedTarget);
            Assert.Equal(noContributorObservation.Feedback.Reason, noOpContributorObservation.Feedback.Reason);
            Assert.Equal(noContributorObservation.ExpectedFeedback.Reason, noOpContributorObservation.ExpectedFeedback.Reason);
            AssertRightHandPipelinePreservesProviderIntent(fixture.VRIK, sourceTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a no-op contributor is behaviourally identical to the no-contributor CharacterIK left-hand path.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKLeftHandPipeline_WhenNoOpContributorConfigured_MatchesNoContributorOutput()
    {
        SceneTree sceneTree = GetSceneTree();
        Transform3D sourceTarget = new(Basis.Identity, new Vector3(-0.19f, 0.11f, -0.26f));
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "LeftHandPipelineNoOpContributorFixture");
        TestIKTargetIntentProvider provider = new()
        {
            Name = "LeftHandPipelineProvider",
            TargetIntent = new IKTargetIntent(sourceTarget, 1.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.LeftHandIKTargetIntentProvider = provider;

        try
        {
            CharacterIKRightHandPipelineObservation noContributorObservation = await RunLeftHandPipelineActuatorComparisonAsync(
                sceneTree,
                fixture,
                sourceTarget,
                []);
            fixture.VRIK.LeftHandIKTargetContributors = [new NoOpIKTargetContributor()];
            CharacterIKRightHandPipelineObservation noOpContributorObservation = await RunLeftHandPipelineActuatorComparisonAsync(
                sceneTree,
                fixture,
                sourceTarget);

            AssertTransformApproximately(noContributorObservation.SourceTarget, noOpContributorObservation.SourceTarget);
            AssertTransformApproximately(noContributorObservation.RequestedTarget, noOpContributorObservation.RequestedTarget);
            AssertTransformApproximately(noContributorObservation.RealisedTarget, noOpContributorObservation.RealisedTarget);
            AssertTransformApproximately(noContributorObservation.ExpectedRealisedTarget, noOpContributorObservation.ExpectedRealisedTarget);
            Assert.Equal(noContributorObservation.Feedback.Reason, noOpContributorObservation.Feedback.Reason);
            Assert.Equal(noContributorObservation.ExpectedFeedback.Reason, noOpContributorObservation.ExpectedFeedback.Reason);
            AssertLeftHandPipelinePreservesProviderIntent(fixture.VRIK, sourceTarget);
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
        TestIKTargetIntentProvider fallbackProvider = new()
        {
            Name = "RightHandZeroInfluenceFallbackProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(2.0f, 2.0f, 2.0f)),
                0.0f),
        };
        fixture.Root.AddChild(fallbackProvider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandFallbackIntentProvider = fallbackProvider;

        try
        {
            Transform3D restTarget = fixture.RightHandIKTarget.Transform;
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            AssertTransformApproximately(restTarget, fixture.RightHandIKTarget.Transform);
            Assert.NotEqual(fallbackProvider.TargetIntent.WorldTransform.Origin, fixture.RightHandIKTarget.GlobalPosition);
            Assert.False(fixture.RightHandModifier.Active);
            Assert.Equal(0.0f, fixture.RightHandModifier.Influence);
            AssertTargetDisabled(fixture.RightHandIKTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a CharacterIK fixture with no provider or fallback disables authored-active head and hand IK.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKTargets_WhenNoProvidersOrFallbacks_DisablesModifiersAndTargets()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "NoProviderTargetDisableFixture");

        try
        {
            InvokeOnBeginStage(fixture.VRIK, 1.0d / 60.0d);
            InvokeOnFootProviderStage(fixture.VRIK, 1.0d / 60.0d);
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            Assert.False(fixture.HeadModifier.Active);
            Assert.Equal(0.0f, fixture.HeadModifier.Influence);
            Assert.False(fixture.RightHandModifier.Active);
            Assert.Equal(0.0f, fixture.RightHandModifier.Influence);
            Assert.False(fixture.LeftHandModifier.Active);
            Assert.Equal(0.0f, fixture.LeftHandModifier.Influence);
            Assert.False(fixture.RightFootModifier.Active);
            Assert.Equal(0.0f, fixture.RightFootModifier.Influence);
            AssertTargetDisabled(fixture.HeadIKTarget);
            AssertTargetDisabled(fixture.HeadIKSolveTarget);
            AssertTargetDisabled(fixture.RightHandIKTarget);
            AssertTargetDisabled(fixture.LeftHandIKTarget);
            AssertTargetDisabled(fixture.RightFootIKTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies zero provider influence disables the controlled modifier group and target body.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKTargets_WhenProviderReturnsZeroInfluence_DisablesModifiersAndTargets()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "ZeroProviderTargetDisableFixture");
        TestIKTargetIntentProvider headProvider = new()
        {
            Name = "HeadZeroProvider",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(0.0f, 1.7f, -0.2f)), 0.0f),
        };
        TestIKTargetIntentProvider handProvider = new()
        {
            Name = "RightHandZeroProvider",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(0.45f, 1.1f, -0.3f)), 0.0f),
        };
        fixture.Root.AddChild(headProvider);
        fixture.Root.AddChild(handProvider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.HeadTargetIntentProvider = headProvider;
        fixture.VRIK.RightHandIKTargetIntentProvider = handProvider;

        try
        {
            InvokeOnBeginStage(fixture.VRIK, 1.0d / 60.0d);
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            Assert.False(fixture.HeadModifier.Active);
            Assert.Equal(0.0f, fixture.HeadModifier.Influence);
            Assert.False(fixture.RightHandModifier.Active);
            Assert.Equal(0.0f, fixture.RightHandModifier.Influence);
            AssertTargetDisabled(fixture.HeadIKTarget);
            AssertTargetDisabled(fixture.HeadIKSolveTarget);
            AssertTargetDisabled(fixture.RightHandIKTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies target and modifier state returns to its authored values when provider influence becomes positive again.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIKTargets_WhenInfluenceTransitionsPositive_RestoresModifiersAndTargets()
    {
        SceneTree sceneTree = GetSceneTree();
        CharacterIKFixture<CharacterIK> fixture = await CreateCharacterIKFixtureAsync<CharacterIK>(
            sceneTree,
            "InfluenceRestoreFixture");
        Transform3D positiveTargetTransform = new(Basis.Identity, new Vector3(0.65f, 1.05f, -0.45f));
        TestIKTargetIntentProvider handProvider = new()
        {
            Name = "RightHandTransitionProvider",
            TargetIntent = new IKTargetIntent(positiveTargetTransform, 0.0f),
        };
        fixture.Root.AddChild(handProvider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandIKTargetIntentProvider = handProvider;

        try
        {
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            Assert.False(fixture.RightHandModifier.Active);
            Assert.Equal(0.0f, fixture.RightHandModifier.Influence);
            AssertTargetDisabled(fixture.RightHandIKTarget);

            handProvider.TargetIntent = new IKTargetIntent(positiveTargetTransform, 1.0f);
            Transform3D resolvedTarget = InvokeBuildHandTargetTransform(fixture.VRIK, "BuildRightHandTargetTransform");
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(fixture.RightHandModifier.Active);
            Assert.Equal(1.0f, fixture.RightHandModifier.Influence);
            AssertTargetEnabled(fixture.RightHandIKTarget, 16u, 32u);
            AssertTransformApproximately(positiveTargetTransform, resolvedTarget);
            AssertTransformApproximately(positiveTargetTransform, fixture.RightHandIKTarget.Transform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies unavailable controller sources disable authored hand IK target bodies and modifier state.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKHandFollowTargets_WhenUnbound_DisablesTargetBodiesAndModifierState()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            fixture.PlayerVRIK._Ready();
            fixture.PlayerVRIK.RightHandFallbackIntentProvider = null;
            fixture.PlayerVRIK.LeftHandFallbackIntentProvider = null;
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
            Assert.False(fixture.RightHandIKModifier.Active);
            Assert.Equal(0.0f, fixture.RightHandIKModifier.Influence);
            Assert.False(fixture.LeftHandIKModifier.Active);
            Assert.Equal(0.0f, fixture.LeftHandIKModifier.Influence);
            Assert.Equal(Node.ProcessModeEnum.Disabled, fixture.RightHandIKTarget.ProcessMode);
            Assert.Equal(Node.ProcessModeEnum.Disabled, fixture.LeftHandIKTarget.ProcessMode);
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
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

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
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

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
    /// Verifies the IK-004 limited head target returned by the pose-state tick is applied to the downstream solve target.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIKBeginStage_WhenPoseStateLimitsHead_AppliesLimitedSolveTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            Transform3D limitedHeadTarget = new(
                Basis.Identity.Rotated(Vector3.Up, 0.35f),
                new Vector3(0.12f, 1.35f, -0.08f));
            TestPoseState poseState = new()
            {
                Id = new StringName("Standing"),
                HipTickResult = new HipReconciliationTickResult
                {
                    AppliedHipLocalPosition = new Vector3(0.0f, 0.85f, 0.0f),
                    LimitedHeadTargetTransform = limitedHeadTarget,
                },
            };
            PoseStateMachine stateMachine = CreatePoseStateMachine(poseState);
            fixture.PlayerVRIK.AddChild(stateMachine);

            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);
            fixture.PlayerVRIK.PoseStateMachine = stateMachine;
            Assert.True(fixture.PlayerVRIK.HipBoneIndex >= 0, "Expected fixture hips bone to be resolved.");

            fixture.Camera.CameraNode.Transform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Right, -0.18f),
                new Vector3(0.75f, 2.15f, -0.55f));
            await WaitForNextFrameAsync(sceneTree);

            Transform3D fullPhysicalHeadTarget = fixture.Camera.CameraNode.GlobalTransform
                                                 * fixture.PlayerVRIK.Viewpoint!.Transform.Inverse();
            Assert.NotEqual(fullPhysicalHeadTarget.Origin, limitedHeadTarget.Origin);

            fixture.HeadIKTarget.Transform = fullPhysicalHeadTarget;
            PoseStateMachineTickResult directTick = stateMachine.Tick(new PoseStateContext());
            Transform3D directLimitedTarget = Assert.IsType<Transform3D>(directTick.LimitedHeadTargetTransform);
            AssertTransformApproximately(limitedHeadTarget, directLimitedTarget);
            InvokeApplyHeadSolveTargetTransform(fixture.PlayerVRIK, directLimitedTarget);

            AssertTransformApproximately(limitedHeadTarget, fixture.HeadIKSolveTarget.Transform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies lateral headset movement contributes to the IK-004 hip reconciliation target.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIKBeginStage_WhenHeadMovesLaterally_ProducesHipLateralResponse()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            PoseState poseState = new TestPoseState
            {
                Id = new StringName("Standing"),
                HipReconciliation = new HeadTrackingHipProfile
                {
                    LateralPositionWeight = 0.5f,
                    VerticalPositionWeight = 1.0f,
                    ForwardPositionWeight = 0.5f,
                    RotationCompensationWeight = 0.0f,
                },
            };
            PoseStateMachine stateMachine = CreatePoseStateMachine(poseState);
            fixture.PlayerVRIK.AddChild(stateMachine);

            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);
            fixture.PlayerVRIK.PoseStateMachine = stateMachine;

            float restHipX = fixture.Skeleton.GetBoneGlobalRest(fixture.PlayerVRIK.HipBoneIndex).Origin.X;
            fixture.Camera.CameraNode.Transform = new Transform3D(Basis.Identity, new Vector3(0.5f, 1.6f, 0.0f));
            await WaitForNextFrameAsync(sceneTree);

            Transform3D headRestTransform = fixture.Skeleton.GlobalTransform
                                           * fixture.Skeleton.GetBoneGlobalRest(fixture.HeadBoneIndex)
                                           * fixture.PlayerVRIK.Viewpoint!.Transform;
            PoseStateContext context = new()
            {
                Skeleton = fixture.Skeleton,
                HipBoneIndex = fixture.PlayerVRIK.HipBoneIndex,
                HeadBoneIndex = fixture.HeadBoneIndex,
                HeadTargetRestTransform = headRestTransform,
                HeadTargetTransform = new Transform3D(Basis.Identity, headRestTransform.Origin + new Vector3(0.5f, 0.0f, 0.0f)),
                Delta = 1.0d / 60.0d,
            };
            _ = stateMachine.Tick(context);

            Vector3 hipLocalPosition = InvokeLatestHipLocalPosition(stateMachine);

            Assert.True(
                hipLocalPosition.X > restHipX + 0.05f,
                $"Expected positive lateral hip response from rightward headset movement, got {hipLocalPosition.X:F3} from rest {restHipX:F3}.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies player VRIK keeps animation-owned foot targets and leg modifiers active through foot fallback providers.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIKFootTargets_WhenOnlyAnimationFallbackProviders_KeepAnimationSynchronizedTargetsActive()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);
            fixture.RightFootIKTarget.Transform = new Transform3D(Basis.Identity, new Vector3(0.18f, 0.05f, -0.08f));
            fixture.LeftFootIKTarget.Transform = new Transform3D(Basis.Identity, new Vector3(-0.18f, 0.05f, -0.08f));
            Transform3D initialRightFoot = fixture.RightFootIKTarget.Transform;
            Transform3D initialLeftFoot = fixture.LeftFootIKTarget.Transform;

            InvokeOnFootProviderStage(fixture.PlayerVRIK, 1.0d / 60.0d);

            AssertTransformApproximately(initialRightFoot, fixture.RightFootIKTarget.Transform);
            AssertTransformApproximately(initialLeftFoot, fixture.LeftFootIKTarget.Transform);
            Assert.True(fixture.RightFootModifier.Active);
            Assert.Equal(1.0f, fixture.RightFootModifier.Influence);
            Assert.True(fixture.LeftFootModifier.Active);
            Assert.Equal(1.0f, fixture.LeftFootModifier.Influence);
            AssertTargetProcessEnabled(fixture.RightFootIKTarget);
            AssertTargetProcessEnabled(fixture.LeftFootIKTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies explicit zero-influence player foot providers still disable leg IK despite the no-provider opt-in.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIKFootTargets_WhenProviderInfluenceIsZero_DisablesFootTargetAndModifier()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);
        TestIKTargetIntentProvider provider = new()
        {
            Name = "RightFootZeroProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(3.0f, 3.0f, 3.0f)),
                0.0f),
        };
        fixture.Root.AddChild(provider);

        try
        {
            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);
            fixture.PlayerVRIK.RightFootTargetIntentProvider = provider;
            Transform3D initialRightFoot = fixture.RightFootIKTarget.Transform;

            InvokeOnFootProviderStage(fixture.PlayerVRIK, 1.0d / 60.0d);

            AssertTransformApproximately(initialRightFoot, fixture.RightFootIKTarget.Transform);
            Assert.False(fixture.RightFootModifier.Active);
            Assert.Equal(0.0f, fixture.RightFootModifier.Influence);
            AssertTargetDisabled(fixture.RightFootIKTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies crouch-like head descent does not lift animation-owned player foot targets.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerVRIKCrouch_WhenHeadDescends_DoesNotRaiseFootTargets()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            PoseState poseState = new TestPoseState
            {
                Id = new StringName("Standing"),
                HipReconciliation = new HeadTrackingHipProfile
                {
                    RotationCompensationWeight = 0.0f,
                },
            };
            PoseStateMachine stateMachine = CreatePoseStateMachine(poseState);
            fixture.PlayerVRIK.AddChild(stateMachine);

            bool bound = fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera);

            Assert.True(bound);
            fixture.PlayerVRIK.PoseStateMachine = stateMachine;

            fixture.RightFootIKTarget.Transform = new Transform3D(Basis.Identity, new Vector3(0.18f, 0.05f, -0.08f));
            fixture.LeftFootIKTarget.Transform = new Transform3D(Basis.Identity, new Vector3(-0.18f, 0.05f, -0.08f));
            float initialRightFootY = fixture.RightFootIKTarget.GlobalPosition.Y;
            float initialLeftFootY = fixture.LeftFootIKTarget.GlobalPosition.Y;
            float restHipY = fixture.Skeleton.GetBoneGlobalRest(fixture.PlayerVRIK.HipBoneIndex).Origin.Y;

            fixture.Camera.CameraNode.Transform = new Transform3D(Basis.Identity, new Vector3(0.0f, 1.15f, 0.0f));
            await WaitForNextFrameAsync(sceneTree);

            fixture.HeadIKTarget.Transform = fixture.Camera.CameraNode.GlobalTransform
                                            * fixture.PlayerVRIK.Viewpoint!.Transform.Inverse();
            fixture.HeadIKTarget.ForceUpdateTransform();
            InvokeAfterProviderTargetProcessing(fixture.PlayerVRIK, fixture.Skeleton, 1.0d / 60.0d);
            InvokeOnFootProviderStage(fixture.PlayerVRIK, 1.0d / 60.0d);
            Vector3 hipLocalPosition = InvokeLatestHipLocalPosition(stateMachine);

            Assert.InRange(fixture.RightFootIKTarget.GlobalPosition.Y, initialRightFootY - 1e-4f, initialRightFootY + 1e-4f);
            Assert.InRange(fixture.LeftFootIKTarget.GlobalPosition.Y, initialLeftFootY - 1e-4f, initialLeftFootY + 1e-4f);
            Assert.True(hipLocalPosition.Y < restHipY - 0.1f, "Crouch head descent should lower the reconciled hip target.");
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
        TestIKTargetIntentProvider provider = new()
        {
            Name = "RightHandProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(0.25f, 0.1f, -0.35f)),
                1.0f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightHandIKTargetIntentProvider = provider;

        try
        {
            fixture.VRIK._Ready();
            fixture.RightHandIKTarget.GlobalTransform = Transform3D.Identity;
            fixture.VRIK._PhysicsProcess(1.0d / 60.0d);

            AssertTransformApproximately(provider.TargetIntent.WorldTransform, fixture.RightHandIKTarget.Transform);
            Assert.True(fixture.RightHandModifier.Active);
            Assert.Equal(1.0f, fixture.RightHandModifier.Influence);
            Assert.Equal(1UL, fixture.VRIK.PhysicsActuatorTickCount);
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
        TestIKTargetIntentProvider provider = new()
        {
            Name = "HeadProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(0.001f, 0.001f, 0.001f)),
                0.37f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.HeadTargetIntentProvider = provider;

        try
        {
            fixture.VRIK._Ready();
            fixture.HeadIKTarget.GlobalTransform = Transform3D.Identity;
            fixture.VRIKBeginStage._ProcessModificationWithDelta(1.0d / 60.0d);

            AssertTransformApproximately(provider.TargetIntent.WorldTransform, fixture.HeadIKTarget.Transform);
            AssertTransformApproximately(provider.TargetIntent.WorldTransform, fixture.HeadIKSolveTarget.Transform);
            AssertTransformApproximately(fixture.HeadIKTarget.GlobalTransform, fixture.HeadIKSolveTarget.GlobalTransform);
            Assert.True(fixture.HeadModifier.Active);
            Assert.Equal(0.37f, fixture.HeadModifier.Influence);

            provider.TargetIntent = new IKTargetIntent(provider.TargetIntent.WorldTransform, 0.0f);
            fixture.VRIKBeginStage._ProcessModificationWithDelta(1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);

            AssertTransformApproximately(provider.TargetIntent.WorldTransform, fixture.HeadIKTarget.Transform);
            AssertTransformApproximately(provider.TargetIntent.WorldTransform, fixture.HeadIKSolveTarget.Transform);
            Assert.False(fixture.HeadModifier.Active);
            Assert.Equal(0.0f, fixture.HeadModifier.Influence);
            AssertTargetDisabled(fixture.HeadIKTarget);
            AssertTargetDisabled(fixture.HeadIKSolveTarget);
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
        TestIKTargetIntentProvider provider = new()
        {
            Name = "RightFootProvider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(0.4f, 0.2f, -0.25f)),
                0.58f),
        };
        fixture.Root.AddChild(provider);
        await WaitForNextFrameAsync(sceneTree);
        fixture.VRIK.RightFootTargetIntentProvider = provider;
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
            AssertTransformApproximately(provider.TargetIntent.WorldTransform, fixture.RightFootIKTarget.Transform);
            AssertTransformApproximately(provider.TargetIntent.WorldTransform, consumer.ConsumedLocalTransform);

            provider.TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(2.0f, 2.0f, 2.0f)),
                0.0f);
            fixture.RightFootIKTarget.GlobalTransform = provider.TargetIntent.WorldTransform;
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
            Assert.False(fixture.RightFootModifier.Active);
            Assert.Equal(0.0f, fixture.RightFootModifier.Influence);
            AssertTargetDisabled(fixture.RightFootIKTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static async Task<VrikFixture> CreateVrikFixtureAsync(SceneTree sceneTree, float xrCameraHeight, float initialWorldScale)
    {
        TestGame root = new()
        {
            Name = "PlayerVrikFixture",
        };

        TestXRManager xrManager = new()
        {
            Name = "RenamedXRManagerService",
        };
        root.AddChild(xrManager);

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
        int hipsBoneIndex = skeleton.AddBone("Hips");
        skeleton.SetBoneRest(hipsBoneIndex, new Transform3D(Basis.Identity, new Vector3(0.0f, 0.85f, 0.0f)));
        skeleton.SetBoneRest(headBoneIndex, new Transform3D(Basis.Identity, new Vector3(0.0f, 1.55f, 0.0f)));
        skeleton.SetBonePosePosition(headBoneIndex, new Vector3(0.0f, -0.15f, 0.0f));

        DynamicPhysicalRig physicalRig = new()
        {
            Name = "DynamicPhysicalRig",
            Enabled = false,
        };
        skeleton.AddChild(physicalRig);

        SkeletonModifier3D playerHeadModifier = new()
        {
            Name = "HeadModifier",
            Influence = 1.0f,
            Active = true,
        };
        skeleton.AddChild(playerHeadModifier);

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

        SkeletonModifier3D rightFootModifier = new()
        {
            Name = "RightLegIKController",
            Influence = 1.0f,
            Active = true,
        };
        skeleton.AddChild(rightFootModifier);

        SkeletonModifier3D leftFootModifier = new()
        {
            Name = "LeftLegIKController",
            Influence = 1.0f,
            Active = true,
        };
        skeleton.AddChild(leftFootModifier);

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

        Node3D rightFootIKTarget = new()
        {
            Name = "RightFoot",
            Position = new Vector3(0.18f, 0.05f, -0.08f),
        };
        ikTargets.AddChild(rightFootIKTarget);

        Node3D leftFootIKTarget = new()
        {
            Name = "LeftFoot",
            Position = new Vector3(-0.18f, 0.05f, -0.08f),
        };
        ikTargets.AddChild(leftFootIKTarget);

        PlayerVRIK playerVRIK = new()
        {
            Name = "VRIK",
            Viewpoint = viewpoint,
            HeadIKTarget = headIKTarget,
            HeadIKSolveTarget = headIKSolveTarget,
            RightHandIKTarget = rightHandIKTarget,
            LeftHandIKTarget = leftHandIKTarget,
            RightFootIKTarget = rightFootIKTarget,
            LeftFootIKTarget = leftFootIKTarget,
            HeadModifierGroup = [playerHeadModifier],
            RightHandModifierGroup = [rightArmIKController, rightHandIKModifier, rightHandCopyRotation],
            LeftHandModifierGroup = [leftHandIKModifier],
            RightFootModifierGroup = [rightFootModifier],
            LeftFootModifierGroup = [leftFootModifier],
            PhysicalRig = physicalRig,
            HeadTargetMaximumSpeed = 1000.0f,
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
        xrManager.SetRuntime(new TestXRRuntime(origin, camera, rightHandController, leftHandController));

        XRHeadTargetIntentProvider headFallbackIntentProvider = new()
        {
            Name = "HeadFallbackIntentProvider",
            Viewpoint = viewpoint,
        };
        playerVRIK.AddChild(headFallbackIntentProvider);
        playerVRIK.HeadFallbackIntentProvider = headFallbackIntentProvider;

        XRControllerTargetProvider rightHandFallbackIntentProvider = new()
        {
            Name = "RightHandFallbackIntentProvider",
            Side = LimbSide.Right,
        };
        playerVRIK.AddChild(rightHandFallbackIntentProvider);
        playerVRIK.RightHandFallbackIntentProvider = rightHandFallbackIntentProvider;

        XRControllerTargetProvider leftHandFallbackIntentProvider = new()
        {
            Name = "LeftHandFallbackIntentProvider",
            Side = LimbSide.Left,
        };
        playerVRIK.AddChild(leftHandFallbackIntentProvider);
        playerVRIK.LeftHandFallbackIntentProvider = leftHandFallbackIntentProvider;

        AnimationSynchronizedFootTargetProvider rightFootFallbackIntentProvider = new()
        {
            Name = "RightFootFallbackIntentProvider",
            FootTarget = rightFootIKTarget,
        };
        playerVRIK.AddChild(rightFootFallbackIntentProvider);
        playerVRIK.RightFootFallbackIntentProvider = rightFootFallbackIntentProvider;

        AnimationSynchronizedFootTargetProvider leftFootFallbackIntentProvider = new()
        {
            Name = "LeftFootFallbackIntentProvider",
            FootTarget = leftFootIKTarget,
        };
        playerVRIK.AddChild(leftFootFallbackIntentProvider);
        playerVRIK.LeftFootFallbackIntentProvider = leftFootFallbackIntentProvider;

        root._EnterTree();
        sceneTree.Root.AddChild(root);
        await WaitForFramesAsync(sceneTree, 2);

        if (skeleton.GetNodeOrNull("CharacterIKBeginStage") is null)
        {
            playerVRIK._Ready();
            await WaitForNextFrameAsync(sceneTree);
        }

        return new VrikFixture(
            root,
            playerVRIK,
            skeleton,
            headBoneIndex,
            headIKTarget,
            headIKSolveTarget,
            rightHandIKTarget,
            leftHandIKTarget,
            rightFootIKTarget,
            leftFootIKTarget,
            playerHeadModifier,
            rightHandIKModifier,
            leftHandIKModifier,
            rightFootModifier,
            leftFootModifier,
            headFallbackIntentProvider,
            rightHandFallbackIntentProvider,
            leftHandFallbackIntentProvider,
            origin,
            camera,
            rightHandController,
            leftHandController);
    }

    private static CharacterIK CreateCharacterIKWithRequiredTargets()
    {
        CharacterIK characterIK = new()
        {
            Name = "CharacterIK",
            Viewpoint = new Marker3D { Name = "Viewpoint" },
        };

        Node3D ikTargets = new()
        {
            Name = "IKTargets",
        };
        ikTargets.AddChild(new CharacterBody3D { Name = "Head" });
        ikTargets.AddChild(new Node3D { Name = "HeadSolve" });
        ikTargets.AddChild(new AnimatableBody3D { Name = "RightHand" });
        ikTargets.AddChild(new AnimatableBody3D { Name = "LeftHand" });
        characterIK.AddChild(ikTargets);

        return characterIK;
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

        DynamicPhysicalRig physicalRig = new()
        {
            Name = "DynamicPhysicalRig",
            Enabled = false,
        };
        skeleton.AddChild(physicalRig);

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

        SkeletonModifier3D leftHandModifier = new()
        {
            Name = "LeftHandModifier",
            Active = true,
            Influence = 1.0f,
        };
        skeleton.AddChild(leftHandModifier);

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
            CollisionLayer = 4u,
            CollisionMask = 8u,
        };
        ikTargets.AddChild(headIKTarget);
        CollisionShape3D headCollisionShape = new()
        {
            Name = "HeadCollisionShape",
            Shape = new SphereShape3D(),
        };
        headIKTarget.AddChild(headCollisionShape);

        Node3D headIKSolveTarget = new()
        {
            Name = "HeadSolve",
        };
        ikTargets.AddChild(headIKSolveTarget);

        AnimatableBody3D rightHandIKTarget = new()
        {
            Name = "RightHand",
            CollisionLayer = 16u,
            CollisionMask = 32u,
            SyncToPhysics = false,
        };
        ikTargets.AddChild(rightHandIKTarget);
        CollisionShape3D rightHandCollisionShape = new()
        {
            Name = "RightHandCollisionShape",
            Shape = new SphereShape3D(),
        };
        rightHandIKTarget.AddChild(rightHandCollisionShape);

        AnimatableBody3D leftHandIKTarget = new()
        {
            Name = "LeftHand",
            CollisionLayer = 64u,
            CollisionMask = 128u,
            SyncToPhysics = false,
        };
        ikTargets.AddChild(leftHandIKTarget);
        CollisionShape3D leftHandCollisionShape = new()
        {
            Name = "LeftHandCollisionShape",
            Shape = new SphereShape3D(),
        };
        leftHandIKTarget.AddChild(leftHandCollisionShape);

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
            PhysicalRig = physicalRig,
            HeadModifierGroup = [headModifier],
            RightHandModifierGroup = [rightHandModifier],
            LeftHandModifierGroup = [leftHandModifier],
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
            leftHandIKTarget,
            rightFootIKTarget,
            headModifier,
            rightHandModifier,
            leftHandModifier,
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

    private static void InvokeOnFootProviderStage(CharacterIK characterIK, double delta)
    {
        MethodInfo method = typeof(CharacterIK).GetMethod(
                                "OnFootProviderStage",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("CharacterIK.OnFootProviderStage was not found.");

        _ = method.Invoke(characterIK, [delta]);
    }

    private static void InvokeUpdatePhysicalActuators(CharacterIK characterIK, double delta)
    {
        MethodInfo method = typeof(CharacterIK).GetMethod(
                                "UpdatePhysicalActuators",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException(
                                "CharacterIK.UpdatePhysicalActuators was not found.");

        _ = method.Invoke(characterIK, [delta]);
    }

    private static async Task<CharacterIKRightHandPipelineObservation> RunRightHandPipelineActuatorComparisonAsync(
        SceneTree sceneTree,
        CharacterIKFixture<CharacterIK> fixture,
        Transform3D sourceTarget)
        => await RunRightHandPipelineActuatorComparisonAsync(sceneTree, fixture, sourceTarget, fixture.VRIK.RightHandIKTargetContributors);

    private static async Task<CharacterIKRightHandPipelineObservation> RunRightHandPipelineActuatorComparisonAsync(
        SceneTree sceneTree,
        CharacterIKFixture<CharacterIK> fixture,
        Transform3D sourceTarget,
        IIKTargetContributor[] contributors)
    {
        fixture.VRIK.RightHandIKTargetContributors = contributors;
        Transform3D startingTransform = new(Basis.Identity.Rotated(Vector3.Forward, 0.09f), new Vector3(-0.04f, 0.03f, 0.02f));
        fixture.RightHandIKTarget.GlobalTransform = startingTransform;

        AnimatableBody3D expectedActuatorBody = new()
        {
            Name = "ExpectedRightHandActuatorBody",
            CollisionLayer = fixture.RightHandIKTarget.CollisionLayer,
            CollisionMask = fixture.RightHandIKTarget.CollisionMask,
            SyncToPhysics = false,
            GlobalTransform = startingTransform,
        };
        fixture.Root.AddChild(expectedActuatorBody);
        await WaitForNextFrameAsync(sceneTree);
        expectedActuatorBody.GlobalTransform = startingTransform;

        IKTargetAnimatableActuator expectedActuator = new(
            expectedActuatorBody)
        {
            MaximumSpeed = fixture.VRIK.HandTargetMaximumSpeed,
            PositionResponsiveness = fixture.VRIK.HandTargetPositionResponsiveness,
            MaximumAcceleration = fixture.VRIK.HandTargetMaximumAcceleration,
            SnapDistance = fixture.VRIK.HandTargetSettleDistance,
            RotationResponsiveness = fixture.VRIK.HandTargetRotationResponsiveness,
            DynamicBodyInteractionCollisionMask = fixture.VRIK.HandDynamicInteractionCollisionMask,
            DynamicImpactApproachSpeedThreshold = fixture.VRIK.HandDynamicImpactApproachSpeedThreshold,
            DynamicImpactImpulsePerSpeed = fixture.VRIK.HandDynamicImpactImpulsePerSpeed,
            DynamicImpactImpulseCap = fixture.VRIK.HandDynamicImpactImpulseCap,
            DynamicSustainedPushSpeedThreshold = fixture.VRIK.HandDynamicSustainedPushSpeedThreshold,
            DynamicSustainedForcePerSpeed = fixture.VRIK.HandDynamicSustainedForcePerSpeed,
            DynamicSustainedForceCap = fixture.VRIK.HandDynamicSustainedForceCap,
        };

        IKTargetFollowState expectedFollowState = new(sourceTarget, active: true);
        IKTargetActuationResult expectedActuation = expectedActuator.Actuate(
            new IKTargetPipelineRequest(expectedFollowState, expectedFollowState),
            1.0d / 60.0d);

        InvokeUpdatePhysicalActuators(fixture.VRIK, 1.0d / 60.0d);
        await WaitForNextFrameAsync(sceneTree);

        IKTargetPipelineResult pipelineResult = fixture.VRIK.RightHandTargetPipelineDebugState;
        return new CharacterIKRightHandPipelineObservation(
            pipelineResult.SourceTarget,
            pipelineResult.RequestedTarget,
            pipelineResult.RealisedTarget,
            pipelineResult.Feedback,
            expectedActuation.RealisedTarget,
            expectedActuation.Feedback);
    }

    private static async Task<CharacterIKRightHandPipelineObservation> RunLeftHandPipelineActuatorComparisonAsync(
        SceneTree sceneTree,
        CharacterIKFixture<CharacterIK> fixture,
        Transform3D sourceTarget)
        => await RunLeftHandPipelineActuatorComparisonAsync(sceneTree, fixture, sourceTarget, fixture.VRIK.LeftHandIKTargetContributors);

    private static async Task<CharacterIKRightHandPipelineObservation> RunLeftHandPipelineActuatorComparisonAsync(
        SceneTree sceneTree,
        CharacterIKFixture<CharacterIK> fixture,
        Transform3D sourceTarget,
        IIKTargetContributor[] contributors)
    {
        fixture.VRIK.LeftHandIKTargetContributors = contributors;
        Transform3D startingTransform = new(Basis.Identity.Rotated(Vector3.Forward, -0.08f), new Vector3(0.05f, 0.04f, 0.01f));
        fixture.LeftHandIKTarget.GlobalTransform = startingTransform;

        AnimatableBody3D expectedActuatorBody = new()
        {
            Name = "ExpectedLeftHandActuatorBody",
            CollisionLayer = fixture.LeftHandIKTarget.CollisionLayer,
            CollisionMask = fixture.LeftHandIKTarget.CollisionMask,
            SyncToPhysics = false,
            GlobalTransform = startingTransform,
        };
        fixture.Root.AddChild(expectedActuatorBody);
        await WaitForNextFrameAsync(sceneTree);
        expectedActuatorBody.GlobalTransform = startingTransform;

        IKTargetAnimatableActuator expectedActuator = new(expectedActuatorBody)
        {
            MaximumSpeed = fixture.VRIK.HandTargetMaximumSpeed,
            PositionResponsiveness = fixture.VRIK.HandTargetPositionResponsiveness,
            MaximumAcceleration = fixture.VRIK.HandTargetMaximumAcceleration,
            SnapDistance = fixture.VRIK.HandTargetSettleDistance,
            RotationResponsiveness = fixture.VRIK.HandTargetRotationResponsiveness,
            DynamicBodyInteractionCollisionMask = fixture.VRIK.HandDynamicInteractionCollisionMask,
            DynamicImpactApproachSpeedThreshold = fixture.VRIK.HandDynamicImpactApproachSpeedThreshold,
            DynamicImpactImpulsePerSpeed = fixture.VRIK.HandDynamicImpactImpulsePerSpeed,
            DynamicImpactImpulseCap = fixture.VRIK.HandDynamicImpactImpulseCap,
            DynamicSustainedPushSpeedThreshold = fixture.VRIK.HandDynamicSustainedPushSpeedThreshold,
            DynamicSustainedForcePerSpeed = fixture.VRIK.HandDynamicSustainedForcePerSpeed,
            DynamicSustainedForceCap = fixture.VRIK.HandDynamicSustainedForceCap,
        };

        IKTargetFollowState expectedFollowState = new(sourceTarget, active: true);
        IKTargetActuationResult expectedActuation = expectedActuator.Actuate(
            new IKTargetPipelineRequest(expectedFollowState, expectedFollowState),
            1.0d / 60.0d);

        InvokeUpdatePhysicalActuators(fixture.VRIK, 1.0d / 60.0d);
        await WaitForNextFrameAsync(sceneTree);

        IKTargetPipelineResult pipelineResult = fixture.VRIK.LeftHandTargetPipelineDebugState;
        return new CharacterIKRightHandPipelineObservation(
            pipelineResult.SourceTarget,
            pipelineResult.RequestedTarget,
            pipelineResult.RealisedTarget,
            pipelineResult.Feedback,
            expectedActuation.RealisedTarget,
            expectedActuation.Feedback);
    }

    private static void AssertRightHandPipelinePreservesProviderIntent(CharacterIK characterIK, Transform3D sourceTarget)
    {
        IKTargetPipelineResult debugState = characterIK.RightHandTargetPipelineDebugState;
        AssertTransformApproximately(sourceTarget, debugState.SourceTarget);
        AssertTransformApproximately(sourceTarget, debugState.RequestedTarget);
    }

    private static void AssertLeftHandPipelinePreservesProviderIntent(CharacterIK characterIK, Transform3D sourceTarget)
    {
        IKTargetPipelineResult debugState = characterIK.LeftHandTargetPipelineDebugState;
        AssertTransformApproximately(sourceTarget, debugState.SourceTarget);
        AssertTransformApproximately(sourceTarget, debugState.RequestedTarget);
    }

    private static void InvokeAfterProviderTargetProcessing(PlayerVRIK playerVRIK, Skeleton3D skeleton, double delta)
    {
        MethodInfo method = typeof(PlayerVRIK).GetMethod(
                                "AfterProviderTargetProcessing",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException(
                                "PlayerVRIK.AfterProviderTargetProcessing was not found.");

        _ = method.Invoke(playerVRIK, [skeleton, delta]);
    }

    private static void InvokeApplyHeadSolveTargetTransform(CharacterIK characterIK, Transform3D limitedHeadTarget)
    {
        MethodInfo method = typeof(CharacterIK).GetMethod(
                                "ApplyHeadSolveTargetTransform",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException(
                                "CharacterIK.ApplyHeadSolveTargetTransform was not found.");

        _ = method.Invoke(characterIK, [limitedHeadTarget]);
    }

    private static PoseStateMachine CreatePoseStateMachine(PoseState poseState)
    {
        PoseStateMachine stateMachine = new()
        {
            Name = "PoseStateMachine",
            States = [poseState],
            InitialStateId = poseState.Id,
            Active = true,
        };
        stateMachine.EnsureInitialStateResolved();
        return stateMachine;
    }

    private static Vector3 InvokeLatestHipLocalPosition(PoseStateMachine stateMachine)
    {
        MethodInfo method = typeof(PoseStateMachine).GetMethod(
                                "TryGetLatestHipLocalPosition",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException(
                                "PoseStateMachine.TryGetLatestHipLocalPosition was not found.");
        object?[] arguments = [Vector3.Zero];
        bool resolved = (bool)(method.Invoke(stateMachine, arguments)
                               ?? throw new InvalidOperationException(
                                   "PoseStateMachine.TryGetLatestHipLocalPosition returned null."));
        Assert.True(resolved, "Expected pose state machine to resolve a hip local position.");
        return (Vector3)arguments[0]!;
    }

    private static async Task ProcessBeginStageFramesAsync(SceneTree sceneTree, CharacterIK characterIK, int frameCount)
    {
        for (int i = 0; i < frameCount; i++)
        {
            InvokeOnBeginStage(characterIK, 1.0d / 60.0d);
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static void InvokeOnEndStage(CharacterIK characterIK, double delta)
    {
        MethodInfo method = typeof(CharacterIK).GetMethod(
                                "OnEndStage",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("CharacterIK.OnEndStage was not found.");

        _ = method.Invoke(characterIK, [delta]);
    }

    private static void SetHeadBoneGlobalPose(Skeleton3D skeleton, int boneIndex, Transform3D worldTransform)
    {
        Transform3D skeletonLocalTransform = skeleton.GlobalTransform.AffineInverse() * worldTransform;
        Transform3D restTransform = skeleton.GetBoneGlobalRest(boneIndex);
        skeleton.SetBonePose(boneIndex, restTransform.AffineInverse() * skeletonLocalTransform);
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

    private static Transform3D InvokeBuildHeadTargetTransform(CharacterIK characterIK)
    {
        MethodInfo method = typeof(CharacterIK).GetMethod(
                                "BuildHeadTargetTransform",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("CharacterIK.BuildHeadTargetTransform was not found.");

        return (Transform3D)(method.Invoke(characterIK, [])
                             ?? throw new InvalidOperationException("CharacterIK.BuildHeadTargetTransform returned null."));
    }

    private sealed class VrikFixture(
        Node root,
        PlayerVRIK playerVRIK,
        Skeleton3D skeleton,
        int headBoneIndex,
        CharacterBody3D headIKTarget,
        Node3D headIKSolveTarget,
        AnimatableBody3D rightHandIKTarget,
        AnimatableBody3D leftHandIKTarget,
        Node3D rightFootIKTarget,
        Node3D leftFootIKTarget,
        SkeletonModifier3D headModifier,
        TwoBoneIK3D rightHandIKModifier,
        TwoBoneIK3D leftHandIKModifier,
        SkeletonModifier3D rightFootModifier,
        SkeletonModifier3D leftFootModifier,
        XRHeadTargetIntentProvider headFallbackIntentProvider,
        XRControllerTargetProvider rightHandFallbackIntentProvider,
        XRControllerTargetProvider leftHandFallbackIntentProvider,
        TestXROrigin origin,
        TestXRCamera camera,
        TestXRHandController rightHandController,
        TestXRHandController leftHandController)
    {
        public Node Root { get; } = root;

        public PlayerVRIK PlayerVRIK { get; } = playerVRIK;

        public Skeleton3D Skeleton { get; } = skeleton;

        public int HeadBoneIndex { get; } = headBoneIndex;

        public CharacterBody3D HeadIKTarget { get; } = headIKTarget;

        public Node3D HeadIKSolveTarget { get; } = headIKSolveTarget;

        public AnimatableBody3D RightHandIKTarget { get; } = rightHandIKTarget;

        public AnimatableBody3D LeftHandIKTarget { get; } = leftHandIKTarget;

        public Node3D RightFootIKTarget { get; } = rightFootIKTarget;

        public Node3D LeftFootIKTarget { get; } = leftFootIKTarget;

        public SkeletonModifier3D HeadModifier { get; } = headModifier;

        public TwoBoneIK3D RightHandIKModifier { get; } = rightHandIKModifier;

        public TwoBoneIK3D LeftHandIKModifier { get; } = leftHandIKModifier;

        public SkeletonModifier3D RightFootModifier { get; } = rightFootModifier;

        public SkeletonModifier3D LeftFootModifier { get; } = leftFootModifier;

        public XRHeadTargetIntentProvider HeadFallbackIntentProvider { get; } = headFallbackIntentProvider;

        public XRControllerTargetProvider RightHandFallbackIntentProvider { get; } = rightHandFallbackIntentProvider;

        public XRControllerTargetProvider LeftHandFallbackIntentProvider { get; } = leftHandFallbackIntentProvider;

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
        AnimatableBody3D leftHandIKTarget,
        Node3D rightFootIKTarget,
        SkeletonModifier3D headModifier,
        SkeletonModifier3D rightHandModifier,
        SkeletonModifier3D leftHandModifier,
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

        public AnimatableBody3D LeftHandIKTarget { get; } = leftHandIKTarget;

        public Node3D RightFootIKTarget { get; } = rightFootIKTarget;

        public SkeletonModifier3D HeadModifier { get; } = headModifier;

        public SkeletonModifier3D RightHandModifier { get; } = rightHandModifier;

        public SkeletonModifier3D LeftHandModifier { get; } = leftHandModifier;

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

    private readonly record struct CharacterIKRightHandPipelineObservation(
        Transform3D SourceTarget,
        Transform3D RequestedTarget,
        Transform3D RealisedTarget,
        IKTargetPipelineFeedback Feedback,
        Transform3D ExpectedRealisedTarget,
        IKTargetPipelineFeedback ExpectedFeedback);

    private static void AssertTransformApproximately(Transform3D expected, Transform3D actual, float epsilon = 1e-4f)
    {
        AssertVectorApproximately(expected.Origin, actual.Origin, epsilon);
        AssertBasisApproximately(expected.Basis, actual.Basis, epsilon);
    }

    private static void AssertIntentApproximately(Transform3D expectedTransform, float expectedInfluence, object intent)
    {
        Type intentType = intent.GetType();
        var worldTransform = (Transform3D)(intentType.GetProperty(nameof(IKTargetIntent.WorldTransform))?.GetValue(intent)
            ?? throw new Xunit.Sdk.XunitException("Expected IK target intent WorldTransform to resolve."));
        float desiredInfluence = (float)(intentType.GetProperty(nameof(IKTargetIntent.DesiredInfluence))?.GetValue(intent)
            ?? throw new Xunit.Sdk.XunitException("Expected IK target intent DesiredInfluence to resolve."));

        Assert.Equal(expectedInfluence, desiredInfluence);
        AssertTransformApproximately(expectedTransform, worldTransform);
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

    private static void AssertTargetDisabled(Node3D target)
    {
        Assert.Equal(Node.ProcessModeEnum.Disabled, target.ProcessMode);
        if (target is CollisionObject3D collisionObject)
        {
            Assert.Equal(0u, collisionObject.CollisionLayer);
            Assert.Equal(0u, collisionObject.CollisionMask);
        }

        AssertCollisionShapesDisabled(target, expectedDisabled: true);
    }

    private static void AssertTargetEnabled(Node3D target, uint expectedCollisionLayer, uint expectedCollisionMask)
    {
        Assert.Equal(Node.ProcessModeEnum.Inherit, target.ProcessMode);
        CollisionObject3D collisionObject = Assert.IsAssignableFrom<CollisionObject3D>(target);
        Assert.Equal(expectedCollisionLayer, collisionObject.CollisionLayer);
        Assert.Equal(expectedCollisionMask, collisionObject.CollisionMask);
        AssertCollisionShapesDisabled(target, expectedDisabled: false);
    }

    private static void AssertTargetProcessEnabled(Node3D target)
        => Assert.Equal(Node.ProcessModeEnum.Inherit, target.ProcessMode);

    private static void AssertCollisionShapesDisabled(Node node, bool expectedDisabled)
    {
        int childCount = node.GetChildCount();
        for (int i = 0; i < childCount; i++)
        {
            Node child = node.GetChild(i);
            if (child is CollisionShape3D collisionShape)
            {
                Assert.Equal(expectedDisabled, collisionShape.Disabled);
            }

            AssertCollisionShapesDisabled(child, expectedDisabled);
        }
    }

    private static string AssertNodeScriptPath(Node node)
    {
        Script script = Assert.IsType<Script>(node.GetScript().AsGodotObject(), exactMatch: false);
        return script.ResourcePath;
    }

    private static T? GetScriptProperty<T>(Node node, string propertyName)
        where T : GodotObject
        => node.GetType().GetProperty(propertyName)?.GetValue(node) as T;

    private static void EnsureRuntimeRoleInstalled(Node character)
    {
        Node? installer = character.GetNodeOrNull("PlayerCharacterInstaller")
            ?? character.GetNodeOrNull("NPCCharacterInstaller");
        if (installer is null)
        {
            return;
        }

        Type installerType = installer.GetType();
        Type contextType = installerType.Assembly.GetType("AlleyCat.Core.Installer.SceneInstallationContext")
            ?? throw new InvalidOperationException("Failed to resolve loaded SceneInstallationContext type.");
        object context = Activator.CreateInstance(contextType, character, "alleycat.scene_installer")
            ?? throw new InvalidOperationException("Failed to create loaded scene installation context.");
        object result = installerType.GetMethod("Install")?.Invoke(installer, [context])
            ?? throw new InvalidOperationException("Failed to invoke runtime role installer.");
        bool succeeded = (bool)(result.GetType().GetProperty("Succeeded")?.GetValue(result) ?? false);
        if (!succeeded)
        {
            object? errors = result.GetType().GetProperty("Errors")?.GetValue(result);
            throw new Xunit.Sdk.XunitException(errors?.ToString() ?? "Runtime role installer failed.");
        }
    }

    private static void SetScriptProperty(Node node, string propertyName, object? value)
    {
        PropertyInfo property = node.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Expected script property '{propertyName}' to resolve on '{node.Name}'.");
        property.SetValue(node, value);
    }

    private static void SetScriptField(Node node, string fieldName, object? value)
    {
        FieldInfo field = node.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Expected script field '{fieldName}' to resolve on '{node.Name}'.");
        field.SetValue(node, value);
    }

    private static void InvokeScriptVoidMethod(Node node, string methodName, params object?[] arguments)
    {
        MethodInfo method = node.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Expected script method '{methodName}' to resolve on '{node.Name}'.");
        _ = method.Invoke(node, arguments);
    }

    private static object InvokeScriptMethod(Node node, string methodName, params object?[] arguments)
    {
        MethodInfo method = node.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Expected script method '{methodName}' to resolve on '{node.Name}'.");
        return method.Invoke(node, arguments)
               ?? throw new Xunit.Sdk.XunitException($"Expected script method '{methodName}' to return a value.");
    }

    private static GodotObject? GetGodotNodeProperty(Node node, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            GodotObject? value = node.Get(propertyName).AsGodotObject();
            if (value is not null)
            {
                return value;
            }
        }

        return null;
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

        public void SetRuntime(IXRRuntime runtime)
            => Runtime = runtime;
    }

    private sealed partial class TestGame : Game
    {
        public override void _Ready()
        {
        }
    }

    private sealed class TestXRRuntime(
        IXROrigin origin,
        IXRCamera camera,
        IXRHandController rightHandController,
        IXRHandController leftHandController) : IXRRuntime
    {
        public IXROrigin Origin => origin;

        public IXRCamera Camera => camera;

        public IXRHandController RightHandController => rightHandController;

        public IXRHandController LeftHandController => leftHandController;

        public event Action? PoseRecentered
        {
            add
            {
            }
            remove
            {
            }
        }

        public bool Initialise(SubViewport viewport, int maximumRefreshRate)
        {
            _ = viewport;
            _ = maximumRefreshRate;
            return true;
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

        public override bool BindToXRServices()
        {
            BindCallCount++;
            return true;
        }
    }

    private sealed partial class TestIKTargetIntentProvider : IKTargetIntentProvider
    {
        public IKTargetIntent TargetIntent
        {
            get;
            set;
        }

        public override IKTargetIntent GetTargetIntent()
            => TargetIntent;
    }

    private sealed partial class TestPoseState : PoseState
    {
        public HipReconciliationTickResult? HipTickResult
        {
            get;
            set;
        }

        public override HipReconciliationTickResult? ResolveHipReconciliation(PoseStateContext context)
            => HipTickResult ?? base.ResolveHipReconciliation(context);
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
