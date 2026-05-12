using System.Collections;
using System.Reflection;
using AlleyCat.Body;
using AlleyCat.Body.Hands;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Interaction;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using HandGrabTargetProvider = AlleyCat.IK.HandGrabTargetProvider;
using IKTargetState = AlleyCat.IK.IKTargetState;
using IKTargetStateProvider = AlleyCat.IK.IKTargetStateProvider;

namespace AlleyCat.IntegrationTests.Interaction;

/// <summary>
/// Runtime scene/resource checks for INTR-002 hand grab assets.
/// </summary>
public sealed partial class HandGrabAssetIntegrationTests
{
    private const float PositionToleranceMetres = 0.001f;
    private const float TestBallReachDistanceMetres = 0.12f;
    private static readonly Vector3 _testBallGrabPositionOffsetFromHand = new(0.001f, 0.071f, 0.049f);
    private static readonly StringName _pendingGrabGroupName = new("pending_grab_test_grabbable");

    /// <summary>
    /// Verifies the reference female scene includes hand bone attachments for held objects.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemale_HasHandBoneAttachments()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>("res://assets/characters/reference/female/reference_female.tscn");
        Node root = scene.Instantiate();

        try
        {
            Assert.NotNull(root.GetNodeOrNull<BoneAttachment3D>("Female_export/GeneralSkeleton/RightHand"));
            Assert.NotNull(root.GetNodeOrNull<BoneAttachment3D>("Female_export/GeneralSkeleton/LeftHand"));
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Verifies the test ball asset has the required radius and centre spherical grab point.
    /// </summary>
    [Headless]
    [Fact]
    public void TestBall_HasFourCentimetreSphereAndSphericalGrabPoint()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>("res://assets/items/test_ball_grabbable.tscn");
        Node root = scene.Instantiate();

        try
        {
            Assert.Equal(typeof(GrabbableRigidBody3D).FullName, root.GetType().FullName);
            Assert.Equal("uid://d0aaosrbv6dgv", root.GetMeta("_custom_type_script").AsString());
            Assert.Contains(root.GetType().GetInterfaces(), static iface => iface.Name == nameof(IGrabbable));
            Assert.True(root.IsInGroup("grabbable"));
            MeshInstance3D meshInstance = root.GetNode<MeshInstance3D>("Mesh");
            SphereMesh sphereMesh = Assert.IsType<SphereMesh>(meshInstance.Mesh);
            Assert.Equal(0.04f, sphereMesh.Radius, 3);
            Assert.NotNull(root.GetNodeOrNull<CollisionShape3D>("CollisionShape3D"));
            Node grabPoint = root.GetNode("SphericalGrabPoint");
            Assert.Equal(typeof(SphericalGrabPoint).FullName, grabPoint.GetType().FullName);
            Assert.Equal("uid://cbwik5eyyjmn5", grabPoint.GetMeta("_custom_type_script").AsString());
            float reachDistanceMetres = grabPoint.Get("ReachDistanceMetres").AsSingle();
            Assert.Equal(TestBallReachDistanceMetres, reachDistanceMetres, 3);
            Vector3 grabPointPositionOffsetFromHand = grabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3();
            Vector3 grabPointRotationOffsetFromHand = grabPoint.Get("GrabPointRotationOffsetFromHand").AsVector3();
            Assert.Equal(_testBallGrabPositionOffsetFromHand, grabPointPositionOffsetFromHand);
            Assert.Equal(Vector3.Zero, grabPointRotationOffsetFromHand);
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Verifies the authored physical test ball yields a candidate at its configured reach distance.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TestBall_SphericalGrabPointAcceptsConfiguredReachDistance()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PackedScene scene = ResourceLoader.Load<PackedScene>("res://assets/items/test_ball_grabbable.tscn");
        Node3D root = scene.Instantiate<Node3D>();

        sceneTree.Root.AddChild(root);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Node3D assetGrabPoint = root.GetNode<Node3D>("SphericalGrabPoint");
            Vector3 grabPointPositionOffsetFromHand = assetGrabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3();
            Vector3 grabPointRotationOffsetFromHand = assetGrabPoint.Get("GrabPointRotationOffsetFromHand").AsVector3();
            Assert.True(
                grabPointPositionOffsetFromHand.Length() > PositionToleranceMetres,
                "Expected the authored test ball to carry a non-identity hand target offset.");
            Assert.Equal(_testBallGrabPositionOffsetFromHand, grabPointPositionOffsetFromHand);
            Assert.Equal(Vector3.Zero, grabPointRotationOffsetFromHand);
            SphericalGrabPoint eligibilityProbe = new()
            {
                Name = "TestBallSphericalGrabPointEligibilityProbe",
                ReachDistanceMetres = assetGrabPoint.Get("ReachDistanceMetres").AsSingle(),
                PalmFacingMinimumDot = assetGrabPoint.Get("PalmFacingMinimumDot").AsSingle(),
                PalmLocalDirection = assetGrabPoint.Get("PalmLocalDirection").AsVector3(),
                GrabAnimation = Assert.IsType<Animation>(assetGrabPoint.Get("GrabAnimation").AsGodotObject(), exactMatch: false),
                GrabPointPositionOffsetFromHand = grabPointPositionOffsetFromHand,
                GrabPointRotationOffsetFromHand = grabPointRotationOffsetFromHand,
            };
            root.AddChild(eligibilityProbe);
            eligibilityProbe.GlobalTransform = assetGrabPoint.GlobalTransform;
            Transform3D handTransform = new(
                Basis.Identity,
                assetGrabPoint.GlobalPosition + new Vector3(0.0f, TestBallReachDistanceMetres, 0.0f));

            GrabPointCandidate? candidate = eligibilityProbe.GetGrabPoint(LimbSide.Right, handTransform);

            Assert.NotNull(candidate);
            Assert.Same(eligibilityProbe, candidate.Source);
            Assert.True(
                candidate.HandTarget.Origin.DistanceTo(assetGrabPoint.GlobalPosition) > PositionToleranceMetres,
                $"Expected offset hand target away from ball centre {assetGrabPoint.GlobalPosition}, observed {candidate.HandTarget.Origin}.");
            Transform3D effectiveGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            Assert.True(
                effectiveGrabPoint.Origin.DistanceTo(assetGrabPoint.GlobalPosition) <= PositionToleranceMetres,
                $"Expected effective grab point to stay aligned to ball centre {assetGrabPoint.GlobalPosition}, observed {effectiveGrabPoint.Origin}.");
            Assert.Equal(_testBallGrabPositionOffsetFromHand, candidate.GrabPointPositionOffsetFromHand);
            Assert.Equal(Vector3.Zero, candidate.GrabPointRotationOffsetFromHand);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the player scene keeps custom-type metadata for hand-grab scripted nodes.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerScene_HandGrabScriptedNodesHaveCustomTypeMetadata()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>("res://assets/characters/reference/player.tscn");
        Node root = scene.Instantiate();

        try
        {
            Node rightProvider = root.GetNode("VRIK/RightHandGrabProvider");
            Node leftProvider = root.GetNode("VRIK/LeftHandGrabProvider");
            Node hands = root.GetNode("Hands");
            Node rightHand = root.GetNode("Hands/RightHand");
            Node leftHand = root.GetNode("Hands/LeftHand");

            Assert.Equal("uid://bdxl0giwm3sg1", rightProvider.GetMeta("_custom_type_script").AsString());
            Assert.Equal("uid://bdxl0giwm3sg1", leftProvider.GetMeta("_custom_type_script").AsString());
            Assert.Equal("uid://clntm6ydqb54a", hands.GetMeta("_custom_type_script").AsString());
            Assert.Equal("uid://cxh7lfqn5k3nw", rightHand.GetMeta("_custom_type_script").AsString());
            Assert.Equal("uid://cxh7lfqn5k3nw", leftHand.GetMeta("_custom_type_script").AsString());
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Verifies the mirror room does not opt the playable player hands into verbose grab debug notifications.
    /// </summary>
    [Headless]
    [Fact]
    public void MirrorRoom_PlayerHandsHaveGrabDebugOutputDisabledByDefault()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>("res://assets/testing/mirror_room/mirror_room.tscn");
        Node root = scene.Instantiate();

        try
        {
            Node rightHand = root.GetNode("Actors/Player/Hands/RightHand");
            Node leftHand = root.GetNode("Actors/Player/Hands/LeftHand");

            Assert.Equal(typeof(HandPoseBehaviour).FullName, rightHand.GetType().FullName);
            Assert.Equal(typeof(HandPoseBehaviour).FullName, leftHand.GetType().FullName);
            Assert.False(rightHand.Get("DebugGrabOutput").AsBool());
            Assert.False(leftHand.Get("DebugGrabOutput").AsBool());
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Verifies held objects align the selected grab point to the hand attachment instead of preserving pre-grab drift.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_GrabWithInitialOffset_AlignsSelectedGrabPointToHandAttachment()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "HandGrabAlignmentRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget",
            Position = new Vector3(-0.1f, 0.0f, 0.0f)
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(new Vector3(0.25f, 0.0f, 0.0f));
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            HandBoneAttachment = handAttachment,
            DiscoveryRangeMetres = 0.3f,
        };
        GrabbableNode ball = CreateRuntimeBall(Vector3.Zero);

        sceneTree.Root.AddChild(root);
        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(hand);
        root.AddChild(ball);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            SetHandAttachmentPosition(skeleton, new Vector3(0.25f, 0.0f, 0.0f));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            SphericalGrabPoint grabPoint = ball.GetNode<SphericalGrabPoint>("SphericalGrabPoint");
            Transform3D grabPointOffsetFromHand = new(Basis.Identity, new Vector3(0.0f, -0.04f, 0.0f));
            GrabPointCandidate candidate = new(
                grabPoint,
                grabPoint.GlobalTransform,
                new Animation(),
                LimbSide.Right,
                handTarget.GlobalTransform,
                grabPoint.GlobalTransform,
                grabPointOffsetFromHand);

            InvokeAttachGrabbedNode(hand, ball, candidate);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.True(
                ball.Transform.Origin.DistanceTo(grabPointOffsetFromHand.Origin) <= PositionToleranceMetres,
                $"Expected centre spherical ball to apply authored local held offset {grabPointOffsetFromHand.Origin}, observed {ball.Transform.Origin}.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies execution-time freshness validation rejects a candidate after its source state has moved.
    /// </summary>
    [Headless]
    [Fact]
    public async Task GrabbableNode_GrabAfterGrabPointMoved_RejectsStaleCandidate()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "StaleCandidateRoot"
        };
        GrabbableNode ball = new()
        {
            Name = "MutableGrabbable"
        };
        MutableGrabPoint grabPoint = new()
        {
            Name = "MutableGrabPoint"
        };
        ball.AddChild(grabPoint);
        sceneTree.Root.AddChild(root);
        root.AddChild(ball);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            ball.RefreshComponents();
            Transform3D handTransform = new(Basis.Identity, new Vector3(-0.05f, 0.0f, 0.0f));
            GrabPointCandidate? candidate = ((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTransform);
            Assert.NotNull(candidate);

            grabPoint.TargetOrigin = new Vector3(1.0f, 0.0f, 0.0f);

            Assert.False(ball.Grab(candidate));
            Assert.False(ball.IsGrabbed);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies scene and script UID metadata can be loaded by UID after the hand-grab scene edits.
    /// </summary>
    [Headless]
    [Fact]
    public void HandGrabAssets_LoadByPreservedUIDs()
    {
        Assert.NotNull(ResourceLoader.Load<PackedScene>("uid://dp3fxu1uko3n7"));
        Assert.NotNull(ResourceLoader.Load<PackedScene>("uid://c1rexm45hq1rf"));
        Assert.NotNull(ResourceLoader.Load<PackedScene>("uid://df4i6mqjgm16e"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://bdxl0giwm3sg1"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://clntm6ydqb54a"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://ddw5p2rob0g4h"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://d0aaosrbv6dgv"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://cxh7lfqn5k3nw"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://cbwik5eyyjmn5"));
    }

    /// <summary>
    /// Verifies grab press only starts the IK approach and does not move the item before the hand reaches the target.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_GrabPressBeforeSettle_DoesNotMoveObjectOrCommit()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "PendingGrabRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget",
            Position = new Vector3(0.05f, 0.0f, 0.0f)
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(new Vector3(-0.25f, 0.0f, 0.0f));
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider"
        };
        StaticIKTargetStateProvider defaultProvider = new()
        {
            Name = "DefaultProvider",
            TargetState = new IKTargetState(new Transform3D(Basis.Identity, new Vector3(1.0f, 0.0f, 0.0f)), 0.25f),
        };
        provider.DefaultProvider = defaultProvider;
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            HandBoneAttachment = handAttachment,
            GrabTargetProvider = provider,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
        };
        GrabbableNode ball = CreateRuntimeMutableGrabbable(new Vector3(0.1f, 0.0f, 0.0f));

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(ball);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        ball.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            SetHandAttachmentPosition(skeleton, new Vector3(-0.25f, 0.0f, 0.0f));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            ball.GlobalPosition = new Vector3(0.1f, 0.0f, 0.0f);
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(-0.1f, 0.0f, 0.0f));
            ball.RefreshComponents();
            Assert.NotNull(((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform));
            Assert.True(ball.IsInGroup(_pendingGrabGroupName));
            Assert.True(root.IsInsideTree());
            Assert.True(hand.IsInsideTree());
            Assert.NotNull(hand.GetTree());
            Vector3 initialBallPosition = ball.GlobalPosition;

            IGrabbable? grabbed = hand.Grab();

            Assert.Null(grabbed);
            Assert.Null(hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);
            Assert.True(initialBallPosition.DistanceTo(ball.GlobalPosition) <= PositionToleranceMetres);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a movable pending grab commits after the hand reaches the target and then releases the IK override.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovablePendingGrabCommitsAfterSettleAndReturnsProviderToDefault()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "PendingGrabCommitRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget",
            Position = new Vector3(-0.1f, 0.0f, 0.0f)
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(new Vector3(-0.25f, 0.0f, 0.0f));
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider"
        };
        AnimationPlayer animationPlayer = new()
        {
            Name = "AnimationPlayer"
        };
        AnimationTree animationTree = CreateHandPoseAnimationTree();
        StaticIKTargetStateProvider defaultProvider = new()
        {
            Name = "DefaultProvider",
            TargetState = new IKTargetState(new Transform3D(Basis.Identity, new Vector3(1.0f, 0.0f, 0.0f)), 0.25f),
        };
        provider.DefaultProvider = defaultProvider;
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            HandBoneAttachment = handAttachment,
            GrabTargetProvider = provider,
            AnimationTree = animationTree,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
        };
        Animation grabPose = new()
        {
            ResourceName = "RuntimeGrabPose"
        };
        GrabbableNode ball = CreateRuntimeMutableGrabbable(
            new Vector3(0.1f, 0.0f, 0.0f),
            GrabbableMobility.Movable,
            new Vector3(0.1f, 0.05f, 0.0f),
            new Transform3D(Basis.Identity, new Vector3(0.0f, -0.05f, 0.0f)),
            grabPose);

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(animationPlayer);
        root.AddChild(animationTree);
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(ball);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        ball.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            SetHandAttachmentPosition(skeleton, new Vector3(-0.25f, 0.0f, 0.0f));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            ball.GlobalPosition = new Vector3(0.1f, 0.0f, 0.0f);
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.1f, 0.1f, 0.0f));
            ball.RefreshComponents();
            GrabPointCandidate? candidate = ((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(candidate);
            Assert.True(
                candidate.HandTarget.Origin.DistanceTo(ball.GlobalPosition) > PositionToleranceMetres,
                "Expected runtime movable commit fixture to use a hand target offset outside the object centre.");
            _ = hand.Grab();
            Assert.Null(hand.CurrentGrabbed);

            handTarget.GlobalTransform = candidate.HandTarget;
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(ball, hand.CurrentGrabbed);
            Assert.False(provider.IsGrabOverrideActive);
            IKTargetState providerState = provider.GetTargetState();
            Assert.Equal(defaultProvider.TargetState.DesiredInfluence, providerState.DesiredInfluence);
            Assert.True(
                providerState.WorldTransform.Origin.DistanceTo(defaultProvider.TargetState.WorldTransform.Origin) <= PositionToleranceMetres,
                $"Expected movable commit to clear directly to default target {defaultProvider.TargetState.WorldTransform.Origin}, observed {providerState.WorldTransform.Origin}.");
            Assert.True(ball.GetParent() == handAttachment);
            Assert.Same(grabPose, hand.CurrentPose);
            Assert.True(animationPlayer.HasAnimation(new StringName(grabPose.ResourceName)));
            AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(animationTree.TreeRoot, exactMatch: false);
            AnimationNodeAnimation rightPoseNode = Assert.IsType<AnimationNodeAnimation>(
                rootTree.GetNode(HandPoseAnimationTreePaths.RightHandPoseNode),
                exactMatch: false);
            Assert.Equal(new StringName(grabPose.ResourceName), rightPoseNode.Animation);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies movable held rigid bodies add temporary same-side hand collision exceptions and restore them on release.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovableRigidBodyCommit_AddsSameSideCollisionExceptionsUntilRelease()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "HeldMovableCollisionExceptionRoot"
        };
        AnimatableBody3D handTarget = new()
        {
            Name = "RightHandTarget"
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(Vector3.Zero);
        DynamicPhysicalRig rig = new()
        {
            Name = nameof(DynamicPhysicalRig),
            Enabled = false,
        };
        AnimatableBody3D rightHandProxy = new()
        {
            Name = "RightHandProxy"
        };
        AnimatableBody3D rightLowerArmProxy = new()
        {
            Name = "RightLowerArmProxy"
        };
        AnimatableBody3D rightFingerProxy = new()
        {
            Name = "RightFingerProxy"
        };
        AnimatableBody3D leftHandProxy = new()
        {
            Name = "LeftHandProxy"
        };
        AnimatableBody3D leftFingerProxy = new()
        {
            Name = "LeftFingerProxy"
        };
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            HandBoneAttachment = handAttachment,
            PhysicalRig = rig,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
        };
        GrabbableRigidBody3D ball = CreateRuntimeRigidMutableGrabbable(Vector3.Zero);

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        skeleton.AddChild(rig);
        root.AddChild(rightHandProxy);
        root.AddChild(rightLowerArmProxy);
        root.AddChild(rightFingerProxy);
        root.AddChild(leftHandProxy);
        root.AddChild(leftFingerProxy);
        root.AddChild(hand);
        root.AddChild(ball);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        ball.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            PopulateGeneratedRigBodies(
                rig,
                rightHandProxy,
                rightLowerArmProxy,
                rightFingerProxy,
                leftHandProxy,
                leftFingerProxy);
            ball.GlobalPosition = Vector3.Zero;
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.0f, 0.05f, 0.0f));
            await TestUtils.WaitForNextFrameAsync(sceneTree);
            ball.RefreshComponents();
            GrabPointCandidate? candidate = ((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(candidate);

            _ = hand.Grab();
            handTarget.GlobalTransform = candidate.HandTarget;
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(ball, hand.CurrentGrabbed);
            AssertBodiesHaveMutualCollisionException(ball, handTarget);
            AssertBodiesHaveMutualCollisionException(ball, rightHandProxy);
            AssertBodiesHaveMutualCollisionException(ball, rightLowerArmProxy);
            AssertBodiesHaveMutualCollisionException(ball, rightFingerProxy);
            AssertBodiesDoNotHaveCollisionException(ball, leftHandProxy);
            AssertBodiesDoNotHaveCollisionException(ball, leftFingerProxy);

            hand.Release();

            Assert.Null(hand.CurrentGrabbed);
            AssertBodiesDoNotHaveCollisionException(ball, handTarget);
            AssertBodiesDoNotHaveCollisionException(ball, rightHandProxy);
            AssertBodiesDoNotHaveCollisionException(ball, rightLowerArmProxy);
            AssertBodiesDoNotHaveCollisionException(ball, rightFingerProxy);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies release transfers recent held-object motion to a movable rigid body so it can be thrown.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovableRigidBodyReleaseAfterMotion_TransfersReleaseVelocity()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "ThrowReleaseVelocityRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget"
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(Vector3.Zero);
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            HandBoneAttachment = handAttachment,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
            ThrowVelocitySmoothingFactor = 1.0f,
        };
        GrabbableRigidBody3D ball = CreateRuntimeRigidMutableGrabbable(Vector3.Zero);

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(hand);
        root.AddChild(ball);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        ball.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            ball.GlobalPosition = Vector3.Zero;
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, Vector3.Zero);
            ball.RefreshComponents();
            GrabPointCandidate? candidate = ((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(candidate);

            _ = hand.Grab();
            handTarget.GlobalTransform = candidate.HandTarget;
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            Assert.Same(ball, hand.CurrentGrabbed);

            SetHandAttachmentPosition(skeleton, new Vector3(0.05f, 0.0f, 0.0f));
            await TestUtils.WaitForNextFrameAsync(sceneTree);
            SetHandAttachmentPosition(skeleton, new Vector3(0.1f, 0.0f, 0.0f));
            await TestUtils.WaitForNextFrameAsync(sceneTree);

            hand.Release();

            Assert.Null(hand.CurrentGrabbed);
            Assert.True(
                ball.LinearVelocity.X > 0.1f,
                $"Expected positive throw velocity along +X after release, observed {ball.LinearVelocity}.");
            Assert.True(
                Mathf.Abs(ball.LinearVelocity.Y) < 0.1f && Mathf.Abs(ball.LinearVelocity.Z) < 0.1f,
                $"Expected throw velocity to stay near the simulated release direction, observed {ball.LinearVelocity}.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies releasing a stationary held movable rigid body does not inject throw velocity.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_StationaryMovableRigidBodyRelease_LeavesNearZeroVelocity()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "StationaryThrowReleaseRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget"
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(Vector3.Zero);
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            HandBoneAttachment = handAttachment,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
        };
        GrabbableRigidBody3D ball = CreateRuntimeRigidMutableGrabbable(Vector3.Zero);

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(hand);
        root.AddChild(ball);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        ball.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            ball.GlobalPosition = Vector3.Zero;
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, Vector3.Zero);
            ball.RefreshComponents();
            GrabPointCandidate? candidate = ((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(candidate);

            _ = hand.Grab();
            handTarget.GlobalTransform = candidate.HandTarget;
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            Assert.Same(ball, hand.CurrentGrabbed);

            hand.Release();

            Assert.True(
                ball.LinearVelocity.Length() <= 0.01f,
                $"Expected stationary release to remain near zero velocity, observed {ball.LinearVelocity}.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies immovable rigid-body releases restore physics state without applying tracked throw momentum.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_ImmovableRigidBodyRelease_DoesNotInjectThrowVelocity()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "ImmovableThrowReleaseRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget"
        };
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
            ThrowVelocitySmoothingFactor = 1.0f,
        };
        GrabbableRigidBody3D fixedProp = CreateRuntimeRigidMutableGrabbable(Vector3.Zero, GrabbableMobility.Immovable);

        root.AddChild(handTarget);
        root.AddChild(hand);
        root.AddChild(fixedProp);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        fixedProp.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            fixedProp.GlobalPosition = Vector3.Zero;
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, Vector3.Zero);
            fixedProp.RefreshComponents();
            GrabPointCandidate? candidate = ((IGrabbable)fixedProp).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(candidate);

            _ = hand.Grab();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            Assert.Same(fixedProp, hand.CurrentGrabbed);

            handTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.2f, 0.0f, 0.0f));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            hand.Release();

            Assert.Null(hand.CurrentGrabbed);
            Assert.True(
                fixedProp.LinearVelocity.Length() <= 0.01f,
                $"Expected immovable release not to inject throw velocity, observed {fixedProp.LinearVelocity}.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a movable pending grab without a hand attachment is abandoned instead of retrying every frame.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovablePendingGrabMissingAttachment_AbandonsPendingGrabAndReleasesProvider()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "MissingAttachmentPendingGrabRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget",
            Position = new Vector3(-0.1f, 0.0f, 0.0f)
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(Vector3.Zero);
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider"
        };
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            GrabTargetProvider = provider,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
        };
        GrabbableNode ball = CreateRuntimeMutableGrabbable(new Vector3(0.1f, 0.0f, 0.0f));

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(ball);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        ball.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            ball.GlobalPosition = new Vector3(0.1f, 0.0f, 0.0f);
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.1f, 0.0f, 0.0f));
            ball.RefreshComponents();
            Node originalParent = ball.GetParent() ?? throw new InvalidOperationException("Ball has no parent.");

            _ = hand.Grab();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Null(hand.CurrentGrabbed);
            Assert.False(provider.IsGrabOverrideActive);
            Assert.Same(originalParent, ball.GetParent());

            hand.HandBoneAttachment = handAttachment;
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Null(hand.CurrentGrabbed);
            Assert.Same(originalParent, ball.GetParent());
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies an immovable pending grab commits without attachment and keeps the IK override active until release.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_ImmovablePendingGrabCommitsWithoutReparentingAndKeepsProviderOverrideUntilRelease()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "ImmovablePendingGrabCommitRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget",
            Position = new Vector3(-0.1f, 0.0f, 0.0f)
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider"
        };
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            GrabTargetProvider = provider,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
        };
        GrabbableNode fixedProp = CreateRuntimeMutableGrabbable(new Vector3(0.1f, 0.0f, 0.0f), GrabbableMobility.Immovable);

        root.AddChild(handTarget);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(fixedProp);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        fixedProp.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            fixedProp.GlobalPosition = new Vector3(0.1f, 0.0f, 0.0f);
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(-0.1f, 0.0f, 0.0f));
            fixedProp.RefreshComponents();
            Assert.NotNull(((IGrabbable)fixedProp).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform));
            Node originalParent = fixedProp.GetParent() ?? throw new InvalidOperationException("Fixed prop has no parent.");

            _ = hand.Grab();
            Assert.Null(hand.CurrentGrabbed);

            handTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.1f, 0.0f, 0.0f));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(fixedProp, hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);
            Assert.Same(originalParent, fixedProp.GetParent());

            hand.Release();

            Assert.Null(hand.CurrentGrabbed);
            Assert.False(provider.IsGrabOverrideActive);
            Assert.Same(originalParent, fixedProp.GetParent());
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    private static (Skeleton3D Skeleton, BoneAttachment3D Attachment) CreateHandAttachment(Vector3 position)
    {
        Skeleton3D skeleton = new()
        {
            Name = "HandSkeleton"
        };
        _ = skeleton.AddBone("Hand");
        SetHandAttachmentPosition(skeleton, position);

        BoneAttachment3D attachment = new()
        {
            Name = "HandAttachment",
            BoneName = "Hand",
            BoneIdx = 0,
        };
        skeleton.AddChild(attachment);

        return (skeleton, attachment);
    }

    private static void SetHandAttachmentPosition(Skeleton3D skeleton, Vector3 position)
    {
        Transform3D transform = new(Basis.Identity, position);
        skeleton.SetBoneRest(0, transform);
        skeleton.SetBoneGlobalPose(0, transform);
    }

    private static GrabbableNode CreateRuntimeBall(Vector3 position)
    {
        GrabbableNode ball = new()
        {
            Name = "RuntimeBall",
            Position = position
        };
        SphericalGrabPoint grabPoint = new()
        {
            Name = "SphericalGrabPoint",
            ReachDistanceMetres = 0.3f,
            PalmFacingMinimumDot = -1.0f,
            GrabAnimation = new Animation(),
        };
        ball.AddChild(grabPoint);
        ball.AddToGroup("grabbable");

        return ball;
    }

    private static GrabbableNode CreateRuntimeMutableGrabbable(
        Vector3 targetOrigin,
        GrabbableMobility mobility = GrabbableMobility.Movable,
        Vector3? handTargetOrigin = null,
        Transform3D? grabPointOffsetFromHand = null,
        Animation? animation = null)
    {
        GrabbableNode grabbable = new()
        {
            Name = "RuntimeMutableGrabbable",
            Position = targetOrigin,
            Mobility = mobility,
        };
        grabbable.AddChild(new MutableGrabPoint
        {
            Name = "MutableGrabPoint",
            TargetOrigin = targetOrigin,
            HandTargetOrigin = handTargetOrigin ?? targetOrigin,
            GrabPointOffsetFromHand = grabPointOffsetFromHand ?? Transform3D.Identity,
            Animation = animation,
        });
        grabbable.AddToGroup("grabbable");

        return grabbable;
    }

    private static GrabbableRigidBody3D CreateRuntimeRigidMutableGrabbable(
        Vector3 position,
        GrabbableMobility mobility = GrabbableMobility.Movable)
    {
        GrabbableRigidBody3D ball = new()
        {
            Name = "RuntimeRigidBall",
            Position = position,
            Mobility = mobility,
            Freeze = false,
            GravityScale = 0.0f,
        };
        ball.AddChild(new MutableGrabPoint
        {
            Name = "MutableGrabPoint",
            TargetOrigin = position,
            HandTargetOrigin = position,
            Animation = new Animation(),
        });
        ball.AddToGroup("grabbable");

        return ball;
    }

    private static void PopulateGeneratedRigBodies(
        DynamicPhysicalRig rig,
        PhysicsBody3D rightHandProxy,
        PhysicsBody3D rightLowerArmProxy,
        PhysicsBody3D rightFingerProxy,
        PhysicsBody3D leftHandProxy,
        PhysicsBody3D leftFingerProxy)
    {
        Dictionary<StringName, List<PhysicsBody3D>> bodiesByBoneName = GetPrivateField<Dictionary<StringName, List<PhysicsBody3D>>>(
            rig,
            "_generatedBodiesByBoneName");
        bodiesByBoneName[new StringName("RightHand")] = [rightHandProxy];
        bodiesByBoneName[new StringName("RightLowerArm")] = [rightLowerArmProxy];
        bodiesByBoneName[new StringName("LeftHand")] = [leftHandProxy];

        IDictionary fingerBodiesBySide = GetPrivateField<IDictionary>(rig, "_generatedFingerBodiesBySide");
        Type sideType = fingerBodiesBySide.GetType().GetGenericArguments()[0];
        object rightSide = Enum.Parse(sideType, "Right");
        object leftSide = Enum.Parse(sideType, "Left");
        fingerBodiesBySide[rightSide] = new List<PhysicsBody3D> { rightFingerProxy };
        fingerBodiesBySide[leftSide] = new List<PhysicsBody3D> { leftFingerProxy };
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {fieldName} was not found on {instance.GetType().Name}.");
        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
    }

    private static void AssertBodiesHaveMutualCollisionException(PhysicsBody3D first, PhysicsBody3D second)
    {
        AssertBodyHasCollisionException(first, second);
        AssertBodyHasCollisionException(second, first);
    }

    private static void AssertBodiesDoNotHaveCollisionException(PhysicsBody3D first, PhysicsBody3D second)
    {
        AssertBodyDoesNotHaveCollisionException(first, second);
        AssertBodyDoesNotHaveCollisionException(second, first);
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

    private static AnimationTree CreateHandPoseAnimationTree()
    {
        AnimationNodeBlendTree root = new();
        root.AddNode(HandPoseAnimationTreePaths.LeftHandPoseNode, new AnimationNodeAnimation(), Vector2.Zero);
        root.AddNode(HandPoseAnimationTreePaths.RightHandPoseNode, new AnimationNodeAnimation(), new Vector2(200.0f, 0.0f));

        return new AnimationTree
        {
            Name = "AnimationTree",
            TreeRoot = root,
            AnimPlayer = new NodePath("../AnimationPlayer"),
            Active = true,
        };
    }

    private static void InvokeAttachGrabbedNode(HandPoseBehaviour hand, Node3D grabbedNode, GrabPointCandidate candidate)
    {
        MethodInfo method = typeof(HandPoseBehaviour).GetMethod(
            "AttachGrabbedNode",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandPoseBehaviour.AttachGrabbedNode was not found.");
        _ = method.Invoke(hand, [grabbedNode, candidate]);
    }

    private sealed partial class MutableGrabPoint : Node, IGrabPoint
    {
        private readonly Animation _animation = new();

        public Vector3 TargetOrigin { get; set; } = Vector3.Zero;

        public Vector3? HandTargetOrigin
        {
            get; set;
        }

        public Transform3D GrabPointOffsetFromHand { get; set; } = Transform3D.Identity;

        public Animation? Animation
        {
            get; set;
        }

        public GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)
        {
            Transform3D handTarget = new(Basis.Identity, HandTargetOrigin ?? TargetOrigin);
            Transform3D grabPointTransform = new(Basis.Identity, TargetOrigin);
            return new GrabPointCandidate(
                this,
                handTarget,
                Animation ?? _animation,
                handSide,
                handTransform,
                grabPointTransform,
                GrabPointOffsetFromHand);
        }
    }

    private sealed partial class StaticIKTargetStateProvider : IKTargetStateProvider
    {
        public IKTargetState TargetState { get; set; } = new(Transform3D.Identity, 0.0f);

        public override IKTargetState GetTargetState() => TargetState;
    }
}
