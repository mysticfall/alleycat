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
using IKTargetIntent = AlleyCat.IK.IKTargetIntent;
using IKTargetIntentProvider = AlleyCat.IK.IKTargetIntentProvider;

namespace AlleyCat.IntegrationTests.Interaction;

/// <summary>
/// Runtime scene/resource checks for INTR-002 hand grab assets.
/// </summary>
public sealed partial class HandGrabAssetIntegrationTests
{
    private const float PositionToleranceMetres = 0.001f;
    private const float BasisTolerance = 0.001f;
    private const float TestBallReachDistanceMetres = 0.12f;
    private const float TestPipeVisualHeightMetres = 0.5f;
    private const float TestPipeGrabLengthMetres = 0.4f;
    private const float TestPipeReachDistanceMetres = 0.08f;
    private const string TestBallScenePath = "res://assets/items/test_ball.tscn";
    private const string TestStickScenePath = "res://assets/items/test_stick.tscn";
    private static readonly Vector3 _testBallGrabPositionOffsetFromHand = new(0.001f, 0.071f, 0.049f);
    private static readonly Vector3 _testBallGrabRotationOffsetFromHand = new(-0.00048048052f, 0.011107354f, -1.5504136f);
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
    /// Verifies the test pipe asset has the required dimensions and centre cylindrical grab point.
    /// </summary>
    [Headless]
    [Fact]
    public void TestPipe_HasFiftyCentimetreByTwoCentimetreCylinderAndCylindricalGrabPoint()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(TestStickScenePath);
        Node root = scene.Instantiate();

        try
        {
            Assert.Equal(typeof(GrabbableRigidBody3D).FullName, root.GetType().FullName);
            Node3D root3D = Assert.IsAssignableFrom<Node3D>(root);
            AssertTransformApproximatelyEqual(Transform3D.Identity, root3D.Transform, PositionToleranceMetres);
            Assert.Equal("uid://d0aaosrbv6dgv", root.GetMeta("_custom_type_script").AsString());
            Assert.Contains(root.GetType().GetInterfaces(), static iface => iface.Name == nameof(IGrabbable));
            Assert.True(root.IsInGroup("grabbable"));
            MeshInstance3D meshInstance = root.GetNode<MeshInstance3D>("Mesh");
            AssertTransformApproximatelyEqual(Transform3D.Identity, meshInstance.Transform, PositionToleranceMetres);
            CylinderMesh cylinderMesh = Assert.IsType<CylinderMesh>(meshInstance.Mesh);
            Assert.Equal(TestPipeVisualHeightMetres, cylinderMesh.Height, 3);
            Assert.Equal(0.01f, cylinderMesh.TopRadius, 3);
            Assert.Equal(0.01f, cylinderMesh.BottomRadius, 3);
            CollisionShape3D collisionShape = root.GetNode<CollisionShape3D>("CollisionShape3D");
            AssertTransformApproximatelyEqual(Transform3D.Identity, collisionShape.Transform, PositionToleranceMetres);
            CylinderShape3D cylinderShape = Assert.IsType<CylinderShape3D>(collisionShape.Shape);
            Assert.Equal(TestPipeVisualHeightMetres, cylinderShape.Height, 3);
            Assert.Equal(0.01f, cylinderShape.Radius, 3);
            Node3D grabPoint = root.GetNode<Node3D>("CylindricalGrabPoint");
            AssertTransformApproximatelyEqual(Transform3D.Identity, grabPoint.Transform, PositionToleranceMetres);
            Assert.Equal(typeof(CylindricalGrabPoint).FullName, grabPoint.GetType().FullName);
            Assert.Equal("uid://cfbkq153qba1t", grabPoint.GetMeta("_custom_type_script").AsString());
            Assert.Equal(TestPipeGrabLengthMetres, grabPoint.Get("LengthMetres").AsSingle(), 3);
            Assert.Equal(0.05f, (TestPipeVisualHeightMetres - TestPipeGrabLengthMetres) * 0.5f, 3);
            Assert.Equal(TestPipeReachDistanceMetres, grabPoint.Get("ReachDistanceMetres").AsSingle(), 3);
            Assert.Equal(-1.0f, grabPoint.Get("PalmFacingMinimumDot").AsSingle(), 3);
            Vector3 grabPointPositionOffsetFromHand = grabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3();
            Vector3 canonicalOffsetWorld = new Basis(Vector3.Up, Vector3.Right, Vector3.Forward).Orthonormalized()
                * grabPointPositionOffsetFromHand;
            Assert.True(
                Mathf.Abs(canonicalOffsetWorld.Dot(Vector3.Up)) <= 0.005f,
                $"Expected test pipe offset not to bake a half-length displacement along the selected cylinder axis; observed {grabPointPositionOffsetFromHand}.");
            Assert.True(
                Mathf.Abs(canonicalOffsetWorld.Dot(Vector3.Up) - (TestPipeGrabLengthMetres * 0.5f)) > TestPipeGrabLengthMetres * 0.4f,
                $"Expected selected-axis offset to stay far from the pipe half-length {TestPipeGrabLengthMetres * 0.5f}, observed {canonicalOffsetWorld.Dot(Vector3.Up)}.");
            Animation grabAnimation = Assert.IsType<Animation>(grabPoint.Get("GrabAnimation").AsGodotObject(), exactMatch: false);
            Assert.Equal("Grab-pipe-10", grabAnimation.ResourceName);
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
        PackedScene scene = ResourceLoader.Load<PackedScene>(TestBallScenePath);
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
            Assert.Equal(_testBallGrabRotationOffsetFromHand, grabPointRotationOffsetFromHand);
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
        PackedScene scene = ResourceLoader.Load<PackedScene>(TestBallScenePath);
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
            Assert.Equal(_testBallGrabRotationOffsetFromHand, grabPointRotationOffsetFromHand);
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
                Mathf.Abs(candidate.AcquisitionDistance - TestBallReachDistanceMetres) <= PositionToleranceMetres,
                $"Expected spherical asset acquisition distance to remain hand-to-centre, observed {candidate.AcquisitionDistance}.");
            Assert.True(
                candidate.HandTarget.Origin.DistanceTo(assetGrabPoint.GlobalPosition) > PositionToleranceMetres,
                $"Expected offset hand target away from ball centre {assetGrabPoint.GlobalPosition}, observed {candidate.HandTarget.Origin}.");
            AssertBasisApproximatelyEqual(handTransform.Basis, candidate.HandTarget.Basis);
            Transform3D effectiveGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            Assert.True(
                effectiveGrabPoint.Origin.DistanceTo(assetGrabPoint.GlobalPosition) <= PositionToleranceMetres,
                $"Expected effective grab point to stay aligned to ball centre {assetGrabPoint.GlobalPosition}, observed {effectiveGrabPoint.Origin}.");
            Assert.Equal(_testBallGrabPositionOffsetFromHand, candidate.GrabPointPositionOffsetFromHand);
            Assert.Equal(_testBallGrabRotationOffsetFromHand, candidate.GrabPointRotationOffsetFromHand);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the authored physical test pipe yields a candidate along its local-Y grab segment.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TestPipe_CylindricalGrabPointAcceptsClosestPointAlongLength()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PackedScene scene = ResourceLoader.Load<PackedScene>(TestStickScenePath);
        Node3D root = scene.Instantiate<Node3D>();

        sceneTree.Root.AddChild(root);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Node3D assetGrabPoint = root.GetNode<Node3D>("CylindricalGrabPoint");
            CylindricalGrabPoint grabPoint = new()
            {
                Name = "TestPipeCylindricalGrabPointEligibilityProbe",
                LengthMetres = assetGrabPoint.Get("LengthMetres").AsSingle(),
                ReachDistanceMetres = assetGrabPoint.Get("ReachDistanceMetres").AsSingle(),
                PalmFacingMinimumDot = assetGrabPoint.Get("PalmFacingMinimumDot").AsSingle(),
                PalmLocalDirection = assetGrabPoint.Get("PalmLocalDirection").AsVector3(),
                GrabAnimation = Assert.IsType<Animation>(assetGrabPoint.Get("GrabAnimation").AsGodotObject(), exactMatch: false),
                GrabPointPositionOffsetFromHand = assetGrabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3(),
                GrabPointRotationOffsetFromHand = assetGrabPoint.Get("GrabPointRotationOffsetFromHand").AsVector3(),
            };
            root.AddChild(grabPoint);
            grabPoint.GlobalTransform = assetGrabPoint.GlobalTransform;
            Basis palmFacingPipeAxisBasis = new(Vector3.Up, Vector3.Right, Vector3.Forward);
            Vector3 expectedClosestPoint = grabPoint.GlobalPosition + new Vector3(0.0f, 0.15f, 0.0f);
            Vector3 authoredGripOffsetWorld = palmFacingPipeAxisBasis.Orthonormalized()
                * grabPoint.GrabPointPositionOffsetFromHand;
            Vector3 rawHandOrigin = expectedClosestPoint + new Vector3(0.005f, 0.0f, 0.0f);
            Assert.True(
                rawHandOrigin.DistanceTo(expectedClosestPoint) <= grabPoint.ReachDistanceMetres,
                "Expected the asset probe to model a real hand positioned on the pipe, within reach by raw origin.");
            Assert.True(
                Mathf.Abs(authoredGripOffsetWorld.Dot(Vector3.Up)) <= 0.005f,
                $"Expected the authored pipe grip offset not to include a fixed selected-axis displacement, observed {authoredGripOffsetWorld}.");
            Transform3D handTransform = new(
                palmFacingPipeAxisBasis,
                rawHandOrigin);

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Right, handTransform);

            Assert.NotNull(candidate);
            Assert.Same(grabPoint, candidate.Source);
            Assert.Equal(LimbSide.Right, candidate.HandSide);
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected test pipe selected grab point on the closest point along the pipe, observed {candidate.GrabPointTransform.Origin}.");
            Assert.True(
                Mathf.Abs(candidate.AcquisitionDistance - rawHandOrigin.DistanceTo(expectedClosestPoint)) <= PositionToleranceMetres,
                $"Expected test pipe acquisition distance from the accepted raw hand reference, observed {candidate.AcquisitionDistance}.");
            Transform3D effectiveGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            Assert.True(
                effectiveGrabPoint.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected offset composition to recover the selected point along the pipe, observed {effectiveGrabPoint.Origin}.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the authored test pipe's normal overhand approach does not preserve the obsolete half-turn calibration.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TestPipe_NormalOverhandCandidateDoesNotRollHandAwayFromQueryBasisWhenCylinderAxisIsInverted()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PackedScene scene = ResourceLoader.Load<PackedScene>(TestStickScenePath);
        Node3D authoredPipe = scene.Instantiate<Node3D>();
        Node3D authoredGrabPoint = authoredPipe.GetNode<Node3D>("CylindricalGrabPoint");
        GrabbableNode pipe = CreateRuntimePipe(Vector3.Zero);
        CylindricalGrabPoint grabPoint = pipe.GetNode<CylindricalGrabPoint>("CylindricalGrabPoint");
        grabPoint.LengthMetres = authoredGrabPoint.Get("LengthMetres").AsSingle();
        grabPoint.ReachDistanceMetres = authoredGrabPoint.Get("ReachDistanceMetres").AsSingle();
        grabPoint.SnapDistanceMetres = authoredGrabPoint.Get("SnapDistanceMetres").AsSingle();
        grabPoint.PalmFacingMinimumDot = authoredGrabPoint.Get("PalmFacingMinimumDot").AsSingle();
        grabPoint.PalmLocalDirection = authoredGrabPoint.Get("PalmLocalDirection").AsVector3();
        grabPoint.GrabAnimation = Assert.IsType<Animation>(authoredGrabPoint.Get("GrabAnimation").AsGodotObject(), exactMatch: false);
        grabPoint.GrabPointPositionOffsetFromHand = authoredGrabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3();
        grabPoint.GrabPointRotationOffsetFromHand = authoredGrabPoint.Get("GrabPointRotationOffsetFromHand").AsVector3();
        authoredPipe.Free();

        sceneTree.Root.AddChild(pipe);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Basis invertedCylinderAxisBasis = Basis.FromEuler(new Vector3(0.0f, 0.0f, Mathf.Pi)).Orthonormalized();
            pipe.GlobalTransform = new Transform3D(invertedCylinderAxisBasis, Vector3.Zero);
            pipe.ForceUpdateTransform();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Node3D assetGrabPoint = pipe.GetNode<Node3D>("CylindricalGrabPoint");
            Basis normalOverhandBasis = new Basis(Vector3.Up, Vector3.Right, Vector3.Forward).Orthonormalized();
            Transform3D queryHand = new(
                normalOverhandBasis,
                assetGrabPoint.GlobalPosition + new Vector3(0.005f, 0.0f, 0.0f));

            object? reflectedCandidate = InvokeGrabPointQuery(assetGrabPoint, LimbSide.Right, queryHand);

            Assert.NotNull(reflectedCandidate);
            Transform3D handTarget = GetCandidateProperty<Transform3D>(reflectedCandidate, nameof(GrabPointCandidate.HandTarget));
            Transform3D providerTarget = handTarget;
            AssertBasisAngularDistanceLessThan(queryHand.Basis, handTarget.Basis, 0.05f, "candidate hand target");
            AssertBasisAngularDistanceLessThan(queryHand.Basis, providerTarget.Basis, 0.05f, "provider target");
            AssertNoPiRollAroundAxis(queryHand.Basis, handTarget.Basis, queryHand.Basis.Y.Normalized(), "candidate hand target");
        }
        finally
        {
            pipe.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a real authored pipe pending grab commits at the provider target and applies the pipe grab pose.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TestPipe_PendingGrabCommitsAtProviderTargetAndAppliesGrabPipePose()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "TestPipePendingGrabCommitRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget"
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(Vector3.Zero);
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider"
        };
        AnimationPlayer animationPlayer = new()
        {
            Name = "AnimationPlayer"
        };
        AnimationTree animationTree = CreateHandPoseAnimationTree();
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            HandBoneAttachment = handAttachment,
            GrabTargetProvider = provider,
            AnimationTree = animationTree,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.5f,
            GrabCommitDistanceMetres = 0.02f,
        };
        PackedScene scene = ResourceLoader.Load<PackedScene>(TestStickScenePath);
        Node3D authoredPipe = scene.Instantiate<Node3D>();
        Node3D authoredGrabPoint = authoredPipe.GetNode<Node3D>("CylindricalGrabPoint");
        GrabbableNode pipe = CreateRuntimePipe(Vector3.Zero);
        CylindricalGrabPoint grabPoint = pipe.GetNode<CylindricalGrabPoint>("CylindricalGrabPoint");
        grabPoint.LengthMetres = authoredGrabPoint.Get("LengthMetres").AsSingle();
        grabPoint.ReachDistanceMetres = authoredGrabPoint.Get("ReachDistanceMetres").AsSingle();
        grabPoint.SnapDistanceMetres = authoredGrabPoint.Get("SnapDistanceMetres").AsSingle();
        grabPoint.PalmFacingMinimumDot = authoredGrabPoint.Get("PalmFacingMinimumDot").AsSingle();
        grabPoint.PalmLocalDirection = authoredGrabPoint.Get("PalmLocalDirection").AsVector3();
        grabPoint.GrabAnimation = Assert.IsType<Animation>(authoredGrabPoint.Get("GrabAnimation").AsGodotObject(), exactMatch: false);
        grabPoint.GrabPointPositionOffsetFromHand = authoredGrabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3();
        grabPoint.GrabPointRotationOffsetFromHand = authoredGrabPoint.Get("GrabPointRotationOffsetFromHand").AsVector3();
        authoredPipe.Free();

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(animationPlayer);
        root.AddChild(animationTree);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(pipe);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        pipe.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Node3D assetGrabPoint = pipe.GetNode<Node3D>("CylindricalGrabPoint");
            Basis invertedCylinderAxisBasis = Basis.FromEuler(new Vector3(0.0f, 0.0f, Mathf.Pi)).Orthonormalized();
            Basis normalOverhandBasis = new Basis(Vector3.Up, Vector3.Right, Vector3.Forward).Orthonormalized();
            pipe.GlobalTransform = new Transform3D(invertedCylinderAxisBasis, Vector3.Zero);
            pipe.ForceUpdateTransform();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            Transform3D queryHand = new(
                normalOverhandBasis,
                assetGrabPoint.GlobalPosition + new Vector3(0.005f, 0.0f, 0.0f));
            handTarget.GlobalTransform = queryHand;
            SetHandAttachmentTransform(skeleton, queryHand);
            InvokeRefreshComponents(pipe);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            GrabPointCandidate? candidate = ((IGrabbable)pipe).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(candidate);
            Transform3D candidateHandTarget = candidate.HandTarget;
            Animation candidateAnimation = candidate.Animation;
            Assert.Equal("Grab-pipe-10", candidateAnimation.ResourceName);
            AssertBasisAngularDistanceLessThan(queryHand.Basis, candidateHandTarget.Basis, 0.05f, "candidate hand target");

            _ = hand.Grab();
            Assert.Null(hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);
            AssertBasisAngularDistanceLessThan(queryHand.Basis, provider.GrabTarget.Basis, 0.05f, "provider target");

            handTarget.GlobalTransform = provider.GrabTarget;
            SetHandAttachmentTransform(skeleton, provider.GrabTarget);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(pipe, hand.CurrentGrabbed);
            Assert.Same(handAttachment, pipe.GetParent());
            Assert.Same(candidateAnimation, hand.CurrentPose);
            Assert.True(animationPlayer.HasAnimation(new StringName("Grab-pipe-10")));
            AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(animationTree.TreeRoot, exactMatch: false);
            AnimationNodeAnimation rightPoseNode = Assert.IsType<AnimationNodeAnimation>(
                rootTree.GetNode(HandPoseAnimationTreePaths.RightHandPoseNode),
                exactMatch: false);
            Assert.Equal(new StringName("Grab-pipe-10"), rightPoseNode.Animation);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the authored test pipe's runtime grab area remains centred on both segment ends despite shifted hand poses.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TestPipe_ActualCylindricalGrabPointUsesAcquisitionMetricAtAuthoredSegmentEnds()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PackedScene scene = ResourceLoader.Load<PackedScene>(TestStickScenePath);
        Node3D root = scene.Instantiate<Node3D>();

        sceneTree.Root.AddChild(root);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Node grabPoint = root.GetNode("CylindricalGrabPoint");
            Assert.Equal(typeof(CylindricalGrabPoint).FullName, grabPoint.GetType().FullName);
            float lengthMetres = grabPoint.Get("LengthMetres").AsSingle();
            float reachDistanceMetres = grabPoint.Get("ReachDistanceMetres").AsSingle();
            Vector3 grabPointPositionOffsetFromHand = grabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3();
            Assert.True(
                grabPointPositionOffsetFromHand.Length() > PositionToleranceMetres,
                "Expected the authored test pipe to carry a non-identity hand target offset.");
            Basis palmFacingPipeAxisBasis = new(Vector3.Up, Vector3.Right, Vector3.Forward);
            float halfLength = lengthMetres * 0.5f;
            Vector3 radialOffset = new(0.005f, 0.0f, 0.0f);
            Transform3D grabPointGlobalTransform = ((Node3D)grabPoint).GlobalTransform;
            Vector3 positiveEnd = grabPointGlobalTransform * new Vector3(0.0f, halfLength, 0.0f);
            Vector3 negativeEnd = grabPointGlobalTransform * new Vector3(0.0f, -halfLength, 0.0f);
            Transform3D positiveEndHand = new(palmFacingPipeAxisBasis, positiveEnd + radialOffset);
            Transform3D negativeEndHand = new(palmFacingPipeAxisBasis, negativeEnd + radialOffset);

            object? positiveEndCandidate = InvokeGrabPointQuery(grabPoint, LimbSide.Right, positiveEndHand);
            object? negativeEndCandidate = InvokeGrabPointQuery(grabPoint, LimbSide.Right, negativeEndHand);

            AssertPipeEndCandidate(positiveEndCandidate, grabPoint, positiveEndHand, positiveEnd, reachDistanceMetres);
            AssertPipeEndCandidate(negativeEndCandidate, grabPoint, negativeEndHand, negativeEnd, reachDistanceMetres);

            Vector3 beyondPositiveEnd = grabPointGlobalTransform * new Vector3(
                0.0f,
                halfLength + reachDistanceMetres + 0.02f,
                0.0f);
            Transform3D beyondPositiveEndHand = new(palmFacingPipeAxisBasis, beyondPositiveEnd);
            object? beyondPositiveEndCandidate = InvokeGrabPointQuery(grabPoint, LimbSide.Right, beyondPositiveEndHand);

            Assert.Null(beyondPositiveEndCandidate);
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
            Assert.Same(root.GetNode("Actors/Player/IKTargets/RightHand"), rightHand.Get("HeldCollisionTarget").AsGodotObject());
            Assert.Same(root.GetNode("Actors/Player/IKTargets/LeftHand"), leftHand.Get("HeldCollisionTarget").AsGodotObject());
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
    /// Verifies cylindrical runtime attachment interprets authored offsets in the hand-bone attachment frame when the
    /// IK target and attachment transforms differ.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_CylindricalGrabWithDistinctIKTarget_AttachesSelectedPointInHandAttachmentFrame()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "CylindricalAttachmentFrameRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget"
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
            HandBoneAttachment = handAttachment,
            GrabTargetProvider = provider,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.5f,
            GrabCommitDistanceMetres = 0.02f,
        };
        GrabbableNode pipe = CreateRuntimePipe(Vector3.Zero);

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(pipe);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        pipe.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Basis targetBasis = Basis.Identity.Rotated(Vector3.Up, 0.35f).Orthonormalized();
            Basis attachmentBasis = Basis.Identity.Rotated(Vector3.Forward, -0.55f).Orthonormalized();
            handTarget.GlobalTransform = new Transform3D(targetBasis, new Vector3(0.005f, 0.0f, 0.0f));
            SetHandAttachmentTransform(skeleton, new Transform3D(attachmentBasis, new Vector3(0.12f, -0.03f, 0.02f)));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            Transform3D targetToAttachment = handTarget.GlobalTransform.AffineInverse() * handAttachment.GlobalTransform;
            pipe.GlobalPosition = Vector3.Zero;
            pipe.RefreshComponents();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            CylindricalGrabPoint grabPoint = pipe.GetNode<CylindricalGrabPoint>("CylindricalGrabPoint");
            GrabPointCandidate? candidate = ((IGrabbable)pipe).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(candidate);
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(grabPoint.GlobalPosition) <= PositionToleranceMetres,
                $"Expected centred cylindrical candidate, observed {candidate.GrabPointTransform.Origin}.");
            Transform3D selectedPointInPipeSpace = pipe.GlobalTransform.AffineInverse() * candidate.GrabPointTransform;

            _ = hand.Grab();
            Transform3D expectedApproachTarget = candidate.GrabPointTransform
                * candidate.GrabPointOffsetFromHand.AffineInverse()
                * targetToAttachment.AffineInverse();
            Assert.True(
                provider.GrabTarget.Origin.DistanceTo(expectedApproachTarget.Origin) <= PositionToleranceMetres,
                $"Expected approach target {provider.GrabTarget.Origin} to compensate for target-to-attachment offset and reach {expectedApproachTarget.Origin}.");
            handTarget.GlobalTransform = provider.GrabTarget;
            SetHandAttachmentTransform(skeleton, provider.GrabTarget * targetToAttachment);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(pipe, hand.CurrentGrabbed);
            Assert.Same(handAttachment, pipe.GetParent());
            Transform3D selectedPointAfterAttach = pipe.GlobalTransform * selectedPointInPipeSpace;
            Transform3D expectedSelectedPoint = handAttachment.GlobalTransform * candidate.GrabPointOffsetFromHand;
            Assert.True(
                selectedPointAfterAttach.Origin.DistanceTo(expectedSelectedPoint.Origin) <= PositionToleranceMetres,
                $"Expected cylindrical selected point {selectedPointAfterAttach.Origin} to align with hand attachment offset {expectedSelectedPoint.Origin}, not IK target {handTarget.GlobalPosition}.");
            Assert.True(
                selectedPointAfterAttach.Origin.DistanceTo(candidate.GrabPointTransform.Origin) <= PositionToleranceMetres,
                $"Expected approach target to place the attachment so the centred cylindrical point remains at {candidate.GrabPointTransform.Origin}, observed {selectedPointAfterAttach.Origin}.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the authored test pipe remains centred after the real runtime attachment path commits a centre grab.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TestPipe_RuntimeAttachmentKeepsCentredCylindricalSegmentAlignedToHandAttachmentOffset()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "TestPipeRuntimeAttachmentRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget"
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(new Vector3(0.2f, 0.0f, 0.0f));
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider"
        };
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            HandBoneAttachment = handAttachment,
            GrabTargetProvider = provider,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.5f,
            GrabCommitDistanceMetres = 0.02f,
        };
        PackedScene scene = ResourceLoader.Load<PackedScene>(TestStickScenePath);
        Node3D pipe = scene.Instantiate<Node3D>();

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(pipe);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        pipe.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Node3D assetGrabPoint = pipe.GetNode<Node3D>("CylindricalGrabPoint");
            Basis queryBasis = new Basis(Vector3.Up, Vector3.Right, Vector3.Forward).Orthonormalized();
            pipe.GlobalTransform = Transform3D.Identity;
            handTarget.GlobalTransform = new Transform3D(queryBasis, assetGrabPoint.GlobalPosition + new Vector3(0.005f, 0.0f, 0.0f));
            SetHandAttachmentTransform(skeleton, new Transform3D(Basis.Identity, new Vector3(0.2f, 0.0f, 0.0f)));
            InvokeRefreshComponents(pipe);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            object? reflectedCandidate = InvokeGrabPointQuery(assetGrabPoint, LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(reflectedCandidate);
            Transform3D candidateGrabPointTransform = GetCandidateProperty<Transform3D>(reflectedCandidate, nameof(GrabPointCandidate.GrabPointTransform));
            Transform3D candidateGrabPointOffset = GetCandidateProperty<Transform3D>(reflectedCandidate, nameof(GrabPointCandidate.GrabPointOffsetFromHand));
            Transform3D authoredGrabPointOffset = new(
                Basis.FromEuler(assetGrabPoint.Get("GrabPointRotationOffsetFromHand").AsVector3()),
                assetGrabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3());
            Assert.True(
                candidateGrabPointTransform.Origin.DistanceTo(assetGrabPoint.GlobalPosition) <= PositionToleranceMetres,
                $"Expected real pipe centre candidate at {assetGrabPoint.GlobalPosition}, observed {candidateGrabPointTransform.Origin}.");
            Transform3D selectedPointInPipeSpace = pipe.GlobalTransform.AffineInverse() * candidateGrabPointTransform;

            GrabPointCandidate candidate = new(
                new MutableGrabPoint(),
                handTarget.GlobalTransform,
                new Animation(),
                LimbSide.Right,
                handTarget.GlobalTransform,
                candidateGrabPointTransform,
                candidateGrabPointOffset);
            InvokeAttachGrabbedNode(hand, pipe, candidate);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(handAttachment, pipe.GetParent());
            Transform3D selectedPointAfterAttach = pipe.GlobalTransform * selectedPointInPipeSpace;
            Transform3D expectedSelectedPoint = handAttachment.GlobalTransform * authoredGrabPointOffset;
            float selectedAxisOffsetFromHand = Mathf.Abs(
                (selectedPointAfterAttach.Origin - handAttachment.GlobalPosition)
                .Dot(selectedPointAfterAttach.Basis.Y.Normalized()));
            Vector3 positiveEndIfMarkerWereAtEnd = selectedPointAfterAttach.Origin + (selectedPointAfterAttach.Basis.Y.Normalized() * (TestPipeGrabLengthMetres * 0.5f));
            Assert.True(
                selectedPointAfterAttach.Origin.DistanceTo(expectedSelectedPoint.Origin) <= PositionToleranceMetres,
                $"Expected real pipe centre to align with independently authored hand attachment offset {expectedSelectedPoint.Origin}, observed {selectedPointAfterAttach.Origin}.");
            Assert.True(
                selectedAxisOffsetFromHand <= 0.005f,
                $"Expected centred pipe grab not to place the root/centre at a half-length selected-axis offset from the hand attachment, observed {selectedAxisOffsetFromHand}.");
            Assert.True(
                positiveEndIfMarkerWereAtEnd.DistanceTo(expectedSelectedPoint.Origin) > TestPipeGrabLengthMetres * 0.4f,
                "Expected attachment not to align an authored pipe end as though the cylindrical marker were located there.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the authored test pipe can be grabbed at a non-centre selected/contact point through the real hand path.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TestPipe_HandGrabPathWithNonCentreContact_AttachesSelectedEndInsteadOfCentre()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "TestPipeAuthoredEndContactRoot"
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget"
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
            HandBoneAttachment = handAttachment,
            GrabTargetProvider = provider,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.5f,
            GrabCommitDistanceMetres = 0.02f,
        };
        PackedScene scene = ResourceLoader.Load<PackedScene>(TestStickScenePath);
        Node3D authoredPipe = scene.Instantiate<Node3D>();
        Node3D authoredGrabPoint = authoredPipe.GetNode<Node3D>("CylindricalGrabPoint");
        GrabbableNode pipe = CreateRuntimePipe(Vector3.Zero);
        CylindricalGrabPoint grabPoint = pipe.GetNode<CylindricalGrabPoint>("CylindricalGrabPoint");
        grabPoint.LengthMetres = authoredGrabPoint.Get("LengthMetres").AsSingle();
        grabPoint.ReachDistanceMetres = authoredGrabPoint.Get("ReachDistanceMetres").AsSingle();
        grabPoint.SnapDistanceMetres = authoredGrabPoint.Get("SnapDistanceMetres").AsSingle();
        grabPoint.PalmFacingMinimumDot = authoredGrabPoint.Get("PalmFacingMinimumDot").AsSingle();
        grabPoint.PalmLocalDirection = authoredGrabPoint.Get("PalmLocalDirection").AsVector3();
        grabPoint.GrabAnimation = Assert.IsType<Animation>(authoredGrabPoint.Get("GrabAnimation").AsGodotObject(), exactMatch: false);
        grabPoint.GrabPointPositionOffsetFromHand = authoredGrabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3();
        grabPoint.GrabPointRotationOffsetFromHand = authoredGrabPoint.Get("GrabPointRotationOffsetFromHand").AsVector3();
        authoredPipe.Free();

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(pipe);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        pipe.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Node3D assetGrabPoint = pipe.GetNode<Node3D>("CylindricalGrabPoint");
            Basis queryBasis = new Basis(Vector3.Up, Vector3.Right, Vector3.Forward).Orthonormalized();
            float halfLength = assetGrabPoint.Get("LengthMetres").AsSingle() * 0.5f;
            pipe.GlobalTransform = Transform3D.Identity;
            Vector3 expectedSelectedEnd = assetGrabPoint.GlobalTransform * new Vector3(0.0f, halfLength, 0.0f);
            Transform3D endContactHandTransform = new(queryBasis, expectedSelectedEnd + new Vector3(0.005f, 0.0f, 0.0f));
            handTarget.GlobalTransform = endContactHandTransform;
            SetHandAttachmentTransform(skeleton, endContactHandTransform);
            InvokeRefreshComponents(pipe);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            object? reflectedCandidate = InvokeGrabPointQuery(assetGrabPoint, LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(reflectedCandidate);
            Transform3D selectedEndTransform = GetCandidateProperty<Transform3D>(reflectedCandidate, nameof(GrabPointCandidate.GrabPointTransform));
            Assert.True(
                selectedEndTransform.Origin.DistanceTo(expectedSelectedEnd) <= PositionToleranceMetres,
                $"Expected non-centre hand contact to select pipe end {expectedSelectedEnd}, observed {selectedEndTransform.Origin}.");
            Assert.True(
                selectedEndTransform.Origin.DistanceTo(assetGrabPoint.GlobalPosition) > TestPipeGrabLengthMetres * 0.4f,
                "Expected non-centre selected point instead of falling back to the raw hand-origin closest point near the pipe centre.");
            Transform3D selectedPointInPipeSpace = pipe.GlobalTransform.AffineInverse() * selectedEndTransform;
            Assert.NotNull(((IGrabbable)pipe).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform));

            _ = hand.Grab();
            Assert.Null(hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);

            handTarget.GlobalTransform = provider.GrabTarget;
            SetHandAttachmentTransform(skeleton, provider.GrabTarget);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            _ = hand.Grab();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(pipe, hand.CurrentGrabbed);
            Assert.Same(handAttachment, pipe.GetParent());
            Transform3D selectedPointAfterAttach = pipe.GlobalTransform * selectedPointInPipeSpace;
            Transform3D authoredGrabPointOffset = new(
                Basis.FromEuler(assetGrabPoint.Get("GrabPointRotationOffsetFromHand").AsVector3()),
                assetGrabPoint.Get("GrabPointPositionOffsetFromHand").AsVector3());
            Transform3D expectedSelectedPoint = handAttachment.GlobalTransform * authoredGrabPointOffset;
            Vector3 selectedAxis = selectedPointAfterAttach.Basis.Y.Normalized();
            Vector3 expectedPipeCentre = selectedPointAfterAttach.Origin - (selectedAxis * halfLength);
            Assert.True(
                selectedPointAfterAttach.Origin.DistanceTo(expectedSelectedPoint.Origin) <= PositionToleranceMetres,
                $"Expected attached pipe end {selectedPointAfterAttach.Origin} to align with independently authored hand attachment contact offset {expectedSelectedPoint.Origin}.");
            Assert.True(
                selectedPointAfterAttach.Origin.DistanceTo(expectedSelectedEnd) <= PositionToleranceMetres,
                $"Expected real hand grab path to preserve selected end {expectedSelectedEnd}, observed {selectedPointAfterAttach.Origin}.");
            Assert.True(
                pipe.GlobalPosition.DistanceTo(expectedPipeCentre) <= PositionToleranceMetres,
                $"Expected pipe centre {pipe.GlobalPosition} to remain exactly one half-length behind selected end {selectedPointAfterAttach.Origin}, observed expected centre {expectedPipeCentre}.");
            Assert.True(
                pipe.GlobalPosition.DistanceTo(selectedPointAfterAttach.Origin) >= (halfLength - PositionToleranceMetres),
                "Expected the pipe centre to remain half a grab length from the selected/contact end after attachment.");
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
    /// Verifies scene and script UID metadata remains preserved after the hand-grab scene edits.
    /// </summary>
    [Headless]
    [Fact]
    public void HandGrabAssets_LoadByPreservedUIDs()
    {
        Assert.NotNull(ResourceLoader.Load<PackedScene>(TestStickScenePath));

        Assert.NotNull(ResourceLoader.Load<PackedScene>("uid://dp3fxu1uko3n7"));
        Assert.NotNull(ResourceLoader.Load<PackedScene>("uid://c1rexm45hq1rf"));
        Assert.NotNull(ResourceLoader.Load<PackedScene>(TestBallScenePath));
        Assert.NotNull(ResourceLoader.Load<Animation>("uid://bhyeepsp5ifv0"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://bdxl0giwm3sg1"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://clntm6ydqb54a"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://ddw5p2rob0g4h"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://d0aaosrbv6dgv"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://cxh7lfqn5k3nw"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://cbwik5eyyjmn5"));
        Assert.NotNull(ResourceLoader.Load<Script>("uid://cfbkq153qba1t"));
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
        StaticIKTargetIntentProvider defaultProvider = new()
        {
            Name = "DefaultProvider",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(1.0f, 0.0f, 0.0f)), 0.25f),
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
        StaticIKTargetIntentProvider defaultProvider = new()
        {
            Name = "DefaultProvider",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(1.0f, 0.0f, 0.0f)), 0.25f),
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

            handTarget.GlobalTransform = provider.GrabTarget;
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(ball, hand.CurrentGrabbed);
            Assert.False(provider.IsGrabOverrideActive);
            IKTargetIntent providerIntent = provider.GetTargetIntent();
            Assert.Equal(defaultProvider.TargetIntent.DesiredInfluence, providerIntent.DesiredInfluence);
            Assert.True(
                providerIntent.WorldTransform.Origin.DistanceTo(defaultProvider.TargetIntent.WorldTransform.Origin) <= PositionToleranceMetres,
                $"Expected movable commit to clear directly to default target {defaultProvider.TargetIntent.WorldTransform.Origin}, observed {providerIntent.WorldTransform.Origin}.");
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
    /// Verifies movable held rigid bodies proxy enabled collision shapes through runtime shape owners until release.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovableRigidBodyCommit_ProxiesHeldCollisionShapesUntilRelease()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "HeldCollisionProxyRoot"
        };
        AnimatableBody3D handTarget = new()
        {
            Name = "HandCollisionTarget"
        };
        (Skeleton3D skeleton, BoneAttachment3D handAttachment) = CreateHandAttachment(Vector3.Zero);
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            HandBoneAttachment = handAttachment,
            HeldCollisionTarget = handTarget,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
        };
        GrabbableRigidBody3D ball = CreateRuntimeRigidMutableGrabbable(Vector3.Zero);
        CollisionShape3D enabledShape = new()
        {
            Name = "EnabledHeldShape",
            Shape = new SphereShape3D { Radius = 0.04f },
            Transform = new Transform3D(new Basis(Vector3.Up, 0.35f), new Vector3(0.02f, 0.03f, -0.01f)),
        };
        Node3D nestedCollisionRoot = new()
        {
            Name = "NestedCollisionRoot",
            Transform = new Transform3D(new Basis(Vector3.Right, -0.2f), new Vector3(-0.04f, 0.01f, 0.03f)),
        };
        CollisionShape3D nestedEnabledShape = new()
        {
            Name = "NestedEnabledHeldShape",
            Shape = new BoxShape3D { Size = new Vector3(0.03f, 0.02f, 0.01f) },
            Transform = new Transform3D(new Basis(Vector3.Forward, 0.25f), new Vector3(0.01f, -0.02f, 0.04f)),
        };
        CollisionShape3D disabledShape = new()
        {
            Name = "DisabledHeldShape",
            Shape = new SphereShape3D { Radius = 0.02f },
            Disabled = true,
        };
        ball.AddChild(enabledShape);
        ball.AddChild(nestedCollisionRoot);
        nestedCollisionRoot.AddChild(nestedEnabledShape);
        ball.AddChild(disabledShape);

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
            int[] initialShapeOwners = handTarget.GetShapeOwners();
            GrabPointCandidate? candidate = ((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(candidate);

            _ = hand.Grab();
            handTarget.GlobalTransform = candidate.HandTarget;
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(ball, hand.CurrentGrabbed);
            Assert.Null(handTarget.GetNodeOrNull<CollisionShape3D>("EnabledHeldShapeHeldProxy"));
            Assert.Null(handTarget.GetNodeOrNull<CollisionShape3D>("NestedEnabledHeldShapeHeldProxy"));
            Assert.Null(handTarget.GetNodeOrNull<CollisionShape3D>("DisabledHeldShapeHeldProxy"));

            int[] heldShapeOwners = handTarget.GetShapeOwners();
            int[] proxyShapeOwners = [.. heldShapeOwners.Except(initialShapeOwners)];
            Assert.Equal(2, proxyShapeOwners.Length);
            uint enabledProxyOwner = AssertSingleShapeOwnerForShape(handTarget, proxyShapeOwners, enabledShape.Shape);
            uint nestedProxyOwner = AssertSingleShapeOwnerForShape(handTarget, proxyShapeOwners, nestedEnabledShape.Shape);
            Assert.DoesNotContain(
                proxyShapeOwners,
                ownerId => ShapeOwnerContainsShape(handTarget, ownerId, disabledShape.Shape));
            Assert.False(handTarget.IsShapeOwnerDisabled(enabledProxyOwner));
            Assert.False(handTarget.IsShapeOwnerDisabled(nestedProxyOwner));
            Assert.True(enabledShape.Disabled);
            Assert.True(nestedEnabledShape.Disabled);
            Assert.True(disabledShape.Disabled);
            Transform3D expectedEnabledManualChildTransform = handTarget.GlobalTransform.AffineInverse()
                * enabledShape.GlobalTransform;
            Transform3D expectedNestedManualChildTransform = handTarget.GlobalTransform.AffineInverse()
                * nestedEnabledShape.GlobalTransform;
            Transform3D capturedEnabledProxyTransform = handTarget.ShapeOwnerGetTransform(enabledProxyOwner);
            Transform3D capturedNestedProxyTransform = handTarget.ShapeOwnerGetTransform(nestedProxyOwner);
            AssertTransformApproximatelyEqual(
                expectedEnabledManualChildTransform,
                capturedEnabledProxyTransform,
                PositionToleranceMetres);
            AssertTransformApproximatelyEqual(
                expectedNestedManualChildTransform,
                capturedNestedProxyTransform,
                PositionToleranceMetres);

            ball.GlobalTransform = new Transform3D(new Basis(Vector3.Up, 0.5f), new Vector3(0.15f, 0.02f, -0.03f));
            handTarget.GlobalTransform = new Transform3D(new Basis(Vector3.Right, 0.2f), new Vector3(0.02f, 0.01f, 0.04f));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            AssertTransformApproximatelyEqual(
                expectedEnabledManualChildTransform,
                handTarget.ShapeOwnerGetTransform(enabledProxyOwner),
                PositionToleranceMetres);
            AssertTransformApproximatelyEqual(
                expectedNestedManualChildTransform,
                handTarget.ShapeOwnerGetTransform(nestedProxyOwner),
                PositionToleranceMetres);
            AssertTransformNotApproximatelyEqual(
                handTarget.GlobalTransform.AffineInverse() * enabledShape.GlobalTransform,
                handTarget.ShapeOwnerGetTransform(enabledProxyOwner),
                PositionToleranceMetres);
            AssertTransformNotApproximatelyEqual(
                handTarget.GlobalTransform.AffineInverse() * nestedEnabledShape.GlobalTransform,
                handTarget.ShapeOwnerGetTransform(nestedProxyOwner),
                PositionToleranceMetres);

            hand.Release();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Null(hand.CurrentGrabbed);
            Assert.Null(handTarget.GetNodeOrNull<CollisionShape3D>("EnabledHeldShapeHeldProxy"));
            Assert.Null(handTarget.GetNodeOrNull<CollisionShape3D>("NestedEnabledHeldShapeHeldProxy"));
            Assert.Equal(initialShapeOwners.OrderBy(ownerId => ownerId), handTarget.GetShapeOwners().OrderBy(ownerId => ownerId));
            Assert.False(enabledShape.Disabled);
            Assert.False(nestedEnabledShape.Disabled);
            Assert.True(disabledShape.Disabled);
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

    private static void AssertPipeEndCandidate(
        object? candidate,
        Node grabPoint,
        Transform3D handTransform,
        Vector3 expectedEnd,
        float reachDistanceMetres)
    {
        Assert.NotNull(candidate);
        Assert.Same(grabPoint, GetCandidateProperty<object>(candidate, nameof(GrabPointCandidate.Source)));
        Transform3D grabPointTransform = GetCandidateProperty<Transform3D>(candidate, nameof(GrabPointCandidate.GrabPointTransform));
        float acquisitionDistance = GetCandidateProperty<float>(candidate, nameof(GrabPointCandidate.AcquisitionDistance));
        Assert.True(
            grabPointTransform.Origin.DistanceTo(expectedEnd) <= PositionToleranceMetres,
            $"Expected actual pipe candidate at authored segment end {expectedEnd}, observed {grabPointTransform.Origin}.");
        Assert.True(
            Mathf.Abs(acquisitionDistance - handTransform.Origin.DistanceTo(expectedEnd)) <= PositionToleranceMetres,
            $"Expected pipe end acquisition distance from hand to segment end, observed {acquisitionDistance}.");
        Assert.True(
            acquisitionDistance <= reachDistanceMetres,
            $"Expected pipe end acquisition within reach {reachDistanceMetres}, observed {acquisitionDistance}.");
    }

    private static object? InvokeGrabPointQuery(Node grabPoint, LimbSide side, Transform3D handTransform)
    {
        MethodInfo method = grabPoint.GetType().GetMethod(nameof(IGrabPoint.GetGrabPoint))
            ?? throw new InvalidOperationException("GetGrabPoint method was not found on the authored grab point node.");
        Type sideParameterType = method.GetParameters()[0].ParameterType;
        object sideArgument = Enum.ToObject(sideParameterType, (int)side);

        return method.Invoke(grabPoint, [sideArgument, handTransform]);
    }

    private static void InvokeRefreshComponents(Node grabbable)
    {
        MethodInfo method = grabbable.GetType().GetMethod("RefreshComponents")
            ?? throw new InvalidOperationException("RefreshComponents method was not found on the authored grabbable node.");
        _ = method.Invoke(grabbable, []);
    }

    private static T GetCandidateProperty<T>(object candidate, string propertyName)
    {
        PropertyInfo property = candidate.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Candidate property {propertyName} was not found.");

        return Assert.IsAssignableFrom<T>(property.GetValue(candidate));
    }

    private static uint AssertSingleShapeOwnerForShape(
        CollisionObject3D collisionObject,
        IEnumerable<int> ownerIds,
        Shape3D expectedShape)
    {
        uint[] matchingOwnerIds =
            [.. ownerIds
                .Where(ownerId => ShapeOwnerContainsShape(collisionObject, ownerId, expectedShape))
                .Select(ownerId => (uint)ownerId)];

        uint ownerId = Assert.Single(matchingOwnerIds);
        Assert.Equal(1, collisionObject.ShapeOwnerGetShapeCount(ownerId));
        Assert.Same(expectedShape, collisionObject.ShapeOwnerGetShape(ownerId, 0));
        return ownerId;
    }

    private static bool ShapeOwnerContainsShape(CollisionObject3D collisionObject, int ownerId, Shape3D expectedShape)
    {
        uint unsignedOwnerId = (uint)ownerId;
        int shapeCount = collisionObject.ShapeOwnerGetShapeCount(unsignedOwnerId);
        for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
        {
            if (collisionObject.ShapeOwnerGetShape(unsignedOwnerId, shapeIndex) == expectedShape)
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertTransformApproximatelyEqual(
        Transform3D expected,
        Transform3D actual,
        float tolerance)
    {
        Assert.True(
            actual.Origin.DistanceTo(expected.Origin) <= tolerance,
            $"Expected transform origin {expected.Origin}, observed {actual.Origin}.");
        Assert.True(
            actual.Basis.X.DistanceTo(expected.Basis.X) <= tolerance,
            $"Expected transform X basis {expected.Basis.X}, observed {actual.Basis.X}.");
        Assert.True(
            actual.Basis.Y.DistanceTo(expected.Basis.Y) <= tolerance,
            $"Expected transform Y basis {expected.Basis.Y}, observed {actual.Basis.Y}.");
        Assert.True(
            actual.Basis.Z.DistanceTo(expected.Basis.Z) <= tolerance,
            $"Expected transform Z basis {expected.Basis.Z}, observed {actual.Basis.Z}.");
    }

    private static void AssertTransformNotApproximatelyEqual(
        Transform3D unexpected,
        Transform3D actual,
        float tolerance)
    {
        bool originsMatch = actual.Origin.DistanceTo(unexpected.Origin) <= tolerance;
        bool xBasesMatch = actual.Basis.X.DistanceTo(unexpected.Basis.X) <= tolerance;
        bool yBasesMatch = actual.Basis.Y.DistanceTo(unexpected.Basis.Y) <= tolerance;
        bool zBasesMatch = actual.Basis.Z.DistanceTo(unexpected.Basis.Z) <= tolerance;

        Assert.False(
            originsMatch && xBasesMatch && yBasesMatch && zBasesMatch,
            $"Expected transform to differ from {unexpected}, observed {actual}.");
    }

    private static void AssertBasisApproximatelyEqual(Basis expected, Basis actual)
    {
        Assert.True(
            actual.X.DistanceTo(expected.X) <= BasisTolerance,
            $"Expected basis X axis {expected.X}, observed {actual.X}.");
        Assert.True(
            actual.Y.DistanceTo(expected.Y) <= BasisTolerance,
            $"Expected basis Y axis {expected.Y}, observed {actual.Y}.");
        Assert.True(
            actual.Z.DistanceTo(expected.Z) <= BasisTolerance,
            $"Expected basis Z axis {expected.Z}, observed {actual.Z}.");
    }

    private static void AssertBasisAngularDistanceLessThan(Basis expected, Basis actual, float maxRadians, string context)
    {
        Quaternion expectedRotation = new(expected.Orthonormalized());
        Quaternion actualRotation = new(actual.Orthonormalized());
        float dot = Mathf.Abs(
            (expectedRotation.X * actualRotation.X)
            + (expectedRotation.Y * actualRotation.Y)
            + (expectedRotation.Z * actualRotation.Z)
            + (expectedRotation.W * actualRotation.W));
        float angle = 2.0f * Mathf.Acos(Mathf.Clamp(dot, -1.0f, 1.0f));

        Assert.True(
            angle <= maxRadians,
            $"Expected {context} rotation to stay within {maxRadians} rad of the query hand basis using quaternion distance, observed {angle} rad.");
    }

    private static void AssertNoPiRollAroundAxis(Basis expected, Basis actual, Vector3 axis, string context)
    {
        Vector3 normalisedAxis = axis.Normalized();
        Vector3 expectedXAxis = ProjectOntoPlane(expected.X, normalisedAxis).Normalized();
        Vector3 actualXAxis = ProjectOntoPlane(actual.X, normalisedAxis).Normalized();
        Vector3 expectedYAxis = ProjectOntoPlane(expected.Y, normalisedAxis).Normalized();
        Vector3 actualYAxis = ProjectOntoPlane(actual.Y, normalisedAxis).Normalized();

        Assert.True(
            expectedXAxis.Dot(actualXAxis) > -0.95f || expectedYAxis.Dot(actualYAxis) > -0.95f,
            $"Expected {context} not to be pi-rolled around axis {normalisedAxis}; observed projected axis dots X={expectedXAxis.Dot(actualXAxis)}, Y={expectedYAxis.Dot(actualYAxis)}.");
    }

    private static Vector3 ProjectOntoPlane(Vector3 vector, Vector3 planeNormal)
        => vector - (planeNormal * vector.Dot(planeNormal));

    private static void SetHandAttachmentPosition(Skeleton3D skeleton, Vector3 position)
        => SetHandAttachmentTransform(skeleton, new Transform3D(Basis.Identity, position));

    private static void SetHandAttachmentTransform(Skeleton3D skeleton, Transform3D transform)
    {
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

    private static GrabbableNode CreateRuntimePipe(Vector3 position)
    {
        GrabbableNode pipe = new()
        {
            Name = "RuntimePipe",
            Position = position
        };
        CylindricalGrabPoint grabPoint = new()
        {
            Name = "CylindricalGrabPoint",
            LengthMetres = TestPipeGrabLengthMetres,
            ReachDistanceMetres = TestPipeReachDistanceMetres,
            SnapDistanceMetres = TestPipeReachDistanceMetres,
            PalmFacingMinimumDot = -1.0f,
            GrabAnimation = new Animation(),
            GrabPointPositionOffsetFromHand = new Vector3(0.04f, 0.02f, -0.03f),
            GrabPointRotationOffsetFromHand = new Vector3(0.1f, -0.2f, 0.3f),
        };
        pipe.AddChild(grabPoint);
        pipe.AddToGroup("grabbable");

        return pipe;
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

    private sealed partial class StaticIKTargetIntentProvider : IKTargetIntentProvider
    {
        public IKTargetIntent TargetIntent { get; set; } = new(Transform3D.Identity, 0.0f);

        public override IKTargetIntent GetTargetIntent() => TargetIntent;
    }
}
