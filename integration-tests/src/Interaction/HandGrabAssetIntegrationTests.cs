using System.Collections;
using System.Reflection;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;

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
    private const string ValidGrabPoseAnimationPath =
        "res://assets/characters/reference/female/animations/Grab-ball-40.tres";
    private const string ReferencePlayerFixtureScenePath = "res://assets/testing/reference_player_fixture/reference_player_fixture.tscn";
    // World-space contacts near the mock-runtime-calibrated reference target rest frame (0.497, 1.063, 0.012).
    // They are deliberately fixed fixture geometry, never derived from the solved bone attachment.
    private static readonly Transform3D _referenceBallContactTransform = new(Basis.Identity, new Vector3(0.430f, 1.120f, 0.012f));
    // Static authoring-qualified cylinder contact; it is independent of the solved bone attachment.
    private static readonly Transform3D _referenceStickContactTransform = new(
        new Basis(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, -1f, 0f)),
        new Vector3(0.429f, 1.102f, -0.088f));
    private static readonly Transform3D _referenceBallCommitAttachmentTransform = new(
        Basis.Identity,
        new Vector3(0.419f, 1.049f, -0.037f));
    private static readonly Transform3D _referenceMockQueryTarget = new(Basis.Identity, new Vector3(0.497f, 1.063f, 0.012f));
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";
    private const float MovableAttachmentPositionToleranceMetres = 0.008f;
    private const float MovableAttachmentOrientationToleranceDegrees = 5.0f;
    private const int MovableAttachmentSettleProcessFrames = 2;
    private const int TargetConvergencePhysicsFrames = 90;
    // These are authored optical/controller inputs. Ball placement is derived from the shared asset offset, never
    // from a solved hand attachment, so a later wrist motion remains an independent source stimulus.
    private static readonly Transform3D _authoredOpticalBallWrist = new(
        Basis.Identity,
        new Vector3(0.419f, 1.049f, -0.037f));
    private static readonly Transform3D _authoredOpticalBallWristMoved = new(
        Basis.Identity.Rotated(Vector3.Up, 0.13f),
        new Vector3(0.500f, 1.035f, -0.040f));
    private static readonly Transform3D _authoredOpticalBallWristIsolationMoved = new(
        Basis.Identity.Rotated(Vector3.Up, 0.27f),
        new Vector3(0.590f, 1.040f, -0.060f));
    private static readonly Transform3D _authoredControllerBallWrist = new(
        Basis.Identity.Rotated(Vector3.Up, 0.16f),
        new Vector3(0.405f, 1.025f, -0.025f));
    private static readonly Vector3 _testBallGrabPositionOffsetFromHand = new(0.001f, 0.071f, 0.049f);
    private static readonly Vector3 _testBallGrabRotationOffsetFromHand = new(-0.00048048052f, 0.011107354f, -1.5504136f);
    private static readonly StringName _pendingGrabGroupName = new("pending_grab_test_grabbable");

    /// <summary>
    /// Verifies the reference female scene includes hand bone attachments for held objects.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferenceFemale_HasHandBoneAttachments()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>("res://assets/characters/reference/ally_npc.tscn");
        Node root = scene.Instantiate();
        SceneTree sceneTree = TestUtils.GetSceneTree();
        sceneTree.Root.AddChild(root);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 8);
            EnsureRuntimeRoleInstalled(root);
            Assert.NotNull(root.GetNodeOrNull<BoneAttachment3D>("Female/GeneralSkeleton/RightHand"));
            Assert.NotNull(root.GetNodeOrNull<BoneAttachment3D>("Female/GeneralSkeleton/LeftHand"));
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
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
    public async Task PlayerScene_HandGrabScriptedNodesHaveCustomTypeMetadata()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>("res://assets/characters/reference/ally_player.tscn");
        Node root = scene.Instantiate();
        SceneTree sceneTree = TestUtils.GetSceneTree();
        sceneTree.Root.AddChild(root);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 8);
            EnsureRuntimeRoleInstalled(root);
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
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the reference-player fixture hands keep their authored hand-pose behaviour and collision targets.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferencePlayerFixture_PlayerHandsUseAuthoredHandPoseBehaviourAndCollisionTargets()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(ReferencePlayerFixtureScenePath);
        Node root = scene.Instantiate();

        try
        {
            EnsureRuntimeRoleInstalled(root.GetNode("Actors/Player"));
            Node rightHand = root.GetNode("Actors/Player/Hands/RightHand");
            Node leftHand = root.GetNode("Actors/Player/Hands/LeftHand");

            Assert.Equal(typeof(HandPoseBehaviour).FullName, rightHand.GetType().FullName);
            Assert.Equal(typeof(HandPoseBehaviour).FullName, leftHand.GetType().FullName);
            Assert.Same(root.GetNode("Actors/Player/IKTargets/RightHand"), rightHand.Get("HeldCollisionTarget").AsGodotObject());
            Assert.Same(root.GetNode("Actors/Player/IKTargets/LeftHand"), leftHand.Get("HeldCollisionTarget").AsGodotObject());
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Verifies both live player hands retain their authored actuator-owned held collision targets.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferencePlayerFixture_RuntimeHandsUseActuatorOwnedHeldCollisionTargets()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        ReferencePlayerMockXRFixture fixture = await ReferencePlayerMockXRFixture.CreateAsync(sceneTree);

        try
        {
            Assert.Same(fixture.RightHandTarget, fixture.RightHand.HeldCollisionTarget);
            Assert.Same(fixture.LeftHandTarget, fixture.LeftHand.HeldCollisionTarget);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the reference player uses the mock XR runtime through its declared grab-provider default and preserves
    /// the independently measured hand-attachment residual for movable authored props.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferencePlayerFixture_MockXRProviderDrivesPlayerVRIKAndReportsMovableAttachmentResiduals()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        ReferencePlayerMockXRFixture fixture = await ReferencePlayerMockXRFixture.CreateAsync(sceneTree);

        try
        {
            Assert.Equal(typeof(HandGrabTargetProvider), fixture.RightHandGrabProvider.GetType());
            Assert.Same(fixture.RightHandGrabProvider, fixture.RightHand.GrabTargetProvider);
            Assert.Same(fixture.RightHandTarget, fixture.RightHand.HandTargetNode);
            Assert.Same(fixture.RightHandAttachment, fixture.RightHand.HandBoneAttachment);
            Assert.Same(fixture.RightHandGrabProvider, fixture.PlayerVRIK.RightHandIKTargetIntentProvider);
            Assert.Same(fixture.RightHandFallbackProvider, fixture.RightHandGrabProvider.DefaultProvider);
            Assert.Equal(MovableAttachmentPositionToleranceMetres, fixture.RightHand.MovableAttachmentPositionToleranceMetres);
            Assert.Equal(MovableAttachmentOrientationToleranceDegrees, fixture.RightHand.MovableAttachmentOrientationToleranceDegrees);
            Assert.Equal(MovableAttachmentSettleProcessFrames, fixture.RightHand.MovableAttachmentSettleProcessFrames);

            IKTargetIntent priorIntent = fixture.RightHandGrabProvider.GetTargetIntent();
            Transform3D firstMockSource = new(
                Basis.Identity.Rotated(Vector3.Up, 0.35f),
                priorIntent.WorldTransform.Origin + new Vector3(0.42f, 0.18f, -0.31f));
            SetMockRightWristTransform(fixture.Runtime, firstMockSource);

            Assert.True(
                priorIntent.WorldTransform.Origin.DistanceTo(firstMockSource.Origin) > 0.25f,
                "The injected mock wrist must differ materially from the previous provider target.");

            IKTargetIntent firstProviderIntent = fixture.RightHandGrabProvider.GetTargetIntent();
            AssertTransformApproximatelyEqual(firstMockSource, firstProviderIntent.WorldTransform, PositionToleranceMetres);
            Assert.Equal(1.0f, firstProviderIntent.DesiredInfluence);
            AssertProviderRequestReachesPipeline(fixture);

            IKTargetPipelineResult firstPipeline = await WaitForPipelineConvergenceAsync(sceneTree, fixture);
            AssertTransformApproximatelyEqual(firstPipeline.SourceTarget, firstPipeline.RequestedTarget, PositionToleranceMetres);
            AssertTransformApproximatelyEqual(firstPipeline.RealisedTarget, fixture.RightHandTarget.GlobalTransform, PositionToleranceMetres);
            AssertPhysicalTargetAgreesWithRequest(firstPipeline, "first mock wrist source");

            Transform3D secondMockSource = new(
                Basis.Identity.Rotated(Vector3.Up, -0.29f),
                firstMockSource.Origin + new Vector3(0.34f, 0.11f, -0.27f));
            SetMockRightWristTransform(fixture.Runtime, secondMockSource);
            IKTargetIntent secondProviderIntent = fixture.RightHandGrabProvider.GetTargetIntent();

            Assert.True(
                firstProviderIntent.WorldTransform.Origin.DistanceTo(secondProviderIntent.WorldTransform.Origin) > 0.25f,
                "Changing the mock wrist must materially change the grab provider request; this detects a disconnected default source.");
            AssertTransformApproximatelyEqual(secondMockSource, secondProviderIntent.WorldTransform, PositionToleranceMetres);
            AssertProviderRequestReachesPipeline(fixture);

            IKTargetPipelineResult secondPipeline = await WaitForPipelineConvergenceAsync(sceneTree, fixture);
            Assert.True(
                firstPipeline.SourceTarget.Origin.DistanceTo(secondPipeline.SourceTarget.Origin) > 0.25f,
                "The physical target pipeline source must change with the provider request, not retain a test-local value.");
            AssertPhysicalTargetAgreesWithRequest(secondPipeline, "changed mock wrist source");

            AttachmentResidual ballResidual = await MeasureMovableAttachmentResidualAsync(
                sceneTree,
                fixture,
                fixture.Ball,
                "ball");
            AttachmentResidual stickResidual = await MeasureMovableAttachmentResidualAsync(
                sceneTree,
                fixture,
                fixture.Stick,
                "stick");

            Console.WriteLine(
                "Mock-XR VRIK attachment residuals: ball={0:F3} m/{1:F1}°, stick={2:F3} m/{3:F1}°.",
                ballResidual.PositionMetres,
                ballResidual.OrientationDegrees,
                stickResidual.PositionMetres,
                stickResidual.OrientationDegrees);

        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the real reference-player ball commits only after two consecutive direct attachment samples meet the
    /// Movable 8 mm/5° gate. The mock wrist is the normal authored PlayerVRIK source; this test neither replaces
    /// the hand target nor writes skeleton bone poses.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferencePlayerFixture_MockXRReachQualifiedBall_CommitsOnlyAfterTwoDirectAttachmentSamples()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        ReferencePlayerMockXRFixture fixture = await ReferencePlayerMockXRFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Stick.GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);
            fixture.Stick.ForceUpdateTransform();
            Transform3D expectedAttachment = ConfigureBallExpectedAttachment(
                fixture,
                _referenceBallCommitAttachmentTransform.Origin);
            SetMockRightWristTransformForPlayerVRIK(fixture, expectedAttachment);
            _ = await WaitForPipelineConvergenceAsync(sceneTree, fixture);

            GrabPointCandidate candidate = ((IGrabbable)fixture.Ball).GetGrabPoint(
                LimbSide.Right,
                fixture.RightHandTarget.GlobalTransform)
                ?? throw new Xunit.Sdk.XunitException("Expected the authored test ball to remain reach-qualified.");
            Transform3D candidateExpectedAttachment = candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();
            AssertTransformApproximatelyEqual(expectedAttachment, candidateExpectedAttachment, PositionToleranceMetres);
            Node originalParent = fixture.Ball.GetParent() ?? throw new InvalidOperationException("Ball has no parent.");
            Vector3 originalPosition = fixture.Ball.GlobalPosition;

            Assert.Null(fixture.RightHand.Grab());
            Assert.True(fixture.RightHandGrabProvider.IsGrabOverrideActive);
            Assert.Same(originalParent, fixture.Ball.GetParent());

            // Yield the next process frame so the first qualifying direct-attachment sample is consumed.
            await TestUtils.WaitForFramesAsync(sceneTree, 1);

            AssertDirectAttachmentWithinMovableGate(
                candidateExpectedAttachment,
                fixture.RightHandAttachment.GlobalTransform,
                "first ball sample");
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.True(fixture.RightHandGrabProvider.IsGrabOverrideActive);
            Assert.Same(originalParent, fixture.Ball.GetParent());
            Assert.True(fixture.Ball.GlobalPosition.DistanceTo(originalPosition) <= PositionToleranceMetres);

            // The second consecutive process sample commits; the following frame observes the committed state.
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
            Assert.False(fixture.RightHandGrabProvider.IsGrabOverrideActive);
            Assert.Same(fixture.RightHandAttachment, fixture.Ball.GetParent());
            Assert.True(fixture.Ball.Freeze);
            Assert.Equal(Vector3.Zero, fixture.Ball.LinearVelocity);
            Assert.Equal(Vector3.Zero, fixture.Ball.AngularVelocity);
            Assert.Same(candidate.Animation, fixture.RightHand.CurrentPose);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Reproduces the reported held-hand path with a real PlayerVRIK and active optical wrist source. After a movable
    /// ball commits, an independently authored 9 cm optical wrist movement through clear air must propagate through
    /// every runtime stage while the held collision proxy remains on the collision-aware hand actuator.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferencePlayerFixture_HeldBall_OpticalWristMovesAllTargetsAndHeldItemThroughClearAir()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        ReferencePlayerMockXRFixture fixture = await ReferencePlayerMockXRFixture.CreateAsync(sceneTree);

        try
        {
            await CommitBallFromAuthoredOpticalWristAsync(sceneTree, fixture);

            AnimatableBody3D heldCollisionTarget = Assert.IsType<AnimatableBody3D>(fixture.RightHand.HeldCollisionTarget);
            CollisionShape3D heldBallShape = fixture.Ball.GetNode<CollisionShape3D>("CollisionShape3D");
            Assert.Same(fixture.RightHandTarget, heldCollisionTarget);
            Assert.Equal(fixture.RightHandTarget.CollisionLayer, heldCollisionTarget.CollisionLayer);
            Assert.Equal(fixture.RightHandTarget.CollisionMask, heldCollisionTarget.CollisionMask);
            Assert.Contains(
                heldCollisionTarget.GetShapeOwners(),
                ownerId => ShapeOwnerContainsShape(heldCollisionTarget, ownerId, heldBallShape.Shape));
            Assert.Contains(
                fixture.RightHandTarget.GetShapeOwners(),
                ownerId => ShapeOwnerContainsShape(fixture.RightHandTarget, ownerId, heldBallShape.Shape));
            Assert.True(heldBallShape.Disabled);

            OpticalHeldMotionObservation motion = await MoveHeldOpticalWristAsync(
                sceneTree,
                fixture,
                _authoredOpticalBallWristMoved);

            Assert.InRange(motion.CalibratedSourceMovementMetres, 0.08f, 0.10f);
            Assert.True(motion.ProviderMovementMetres > 0.08f, "The grab provider intent must follow the valid optical wrist.");
            Assert.True(motion.RequestedTargetMovementMetres > 0.08f, "PlayerVRIK must request the moved provider target.");
            Assert.True(motion.RealisedTargetMovementMetres > 0.01f, "The physical hand target must not freeze after the held proxy is present.");
            Assert.True(motion.AttachmentMovementMetres > 0.01f, "The right-hand BoneAttachment3D must follow the moved target.");
            Assert.True(motion.HeldItemMovementMetres > 0.01f, "The parented held ball must follow the moving hand attachment.");
            Assert.Equal("None", motion.CollisionFeedbackReason);
            Assert.False(fixture.RightHandGrabProvider.IsGrabOverrideActive);

            fixture.RightHand.Release();

            Assert.DoesNotContain(
                heldCollisionTarget.GetShapeOwners(),
                ownerId => ShapeOwnerContainsShape(heldCollisionTarget, ownerId, heldBallShape.Shape));
            Assert.False(heldBallShape.Disabled);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the clear-air held optical wrist path remains collision-free without a test-only hand-target collision
    /// mask bypass while held-item proxy shapes remain on the actuator-swept IK target.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferencePlayerFixture_HeldBall_OpticalWristMovesThroughClearAirWithoutCollisionMaskBypass()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        ReferencePlayerMockXRFixture fixture = await ReferencePlayerMockXRFixture.CreateAsync(sceneTree);

        try
        {
            await CommitBallFromAuthoredOpticalWristAsync(sceneTree, fixture);
            OpticalHeldMotionObservation motion = await MoveHeldOpticalWristAsync(
                sceneTree,
                fixture,
                _authoredOpticalBallWristIsolationMoved);

            Assert.InRange(motion.CalibratedSourceMovementMetres, 0.16f, 0.19f);
            Assert.True(motion.ProviderMovementMetres > 0.16f);
            Assert.True(motion.RequestedTargetMovementMetres > 0.16f);
            Assert.True(motion.RealisedTargetMovementMetres > 0.01f);
            Assert.True(motion.AttachmentMovementMetres > 0.01f);
            Assert.True(motion.HeldItemMovementMetres > 0.01f);
            Assert.Equal("None", motion.CollisionFeedbackReason);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Keeps controller compatibility tied to the shared test-ball asset: a calibrated controller input must
    /// reconstruct the selected grab point through its existing authored offset, without a source-specific offset.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferencePlayerFixture_ControllerCalibratedInput_ReconstructsSharedBallSelectedGrabPoint()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        ReferencePlayerMockXRFixture fixture = await ReferencePlayerMockXRFixture.CreateAsync(sceneTree);

        try
        {
            SetMockRightWristTransformForPlayerVRIK(fixture, _authoredControllerBallWrist);
            IKTargetPipelineResult pipeline = await WaitForPipelineConvergenceAsync(sceneTree, fixture);
            ConfigureBallForAuthoredWrist(fixture, pipeline.SourceTarget);
            GrabPointCandidate candidate = ((IGrabbable)fixture.Ball).GetGrabPoint(
                LimbSide.Right,
                fixture.RightHandTarget.GlobalTransform)
                ?? throw new Xunit.Sdk.XunitException("Expected the shared test ball to be reachable from the calibrated controller target.");

            Assert.Equal(XRHandTrackingMode.Controller, fixture.Runtime.HandTrackingMode);
            AssertTransformApproximatelyEqual(_authoredControllerBallWrist, pipeline.SourceTarget, PositionToleranceMetres);
            AssertTransformApproximatelyEqual(
                candidate.GrabPointTransform,
                candidate.HandTarget * candidate.GrabPointOffsetFromHand,
                PositionToleranceMetres);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies provider refresh lets a non-centre cylindrical contact converge through the same Movable direct gate.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferencePlayerFixture_MockXRNonCentreStick_RefreshesProviderAndCommitsThroughDirectGate()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        ReferencePlayerMockXRFixture fixture = await ReferencePlayerMockXRFixture.CreateAsync(sceneTree);
        try
        {
            AttachmentResidual residual = await MeasureMovableAttachmentResidualAsync(
                sceneTree,
                fixture,
                fixture.Stick,
                "stick");
            Assert.InRange(residual.PositionMetres, 0.0f, MovableAttachmentPositionToleranceMetres);
            Assert.InRange(residual.OrientationDegrees, 0.0f, MovableAttachmentOrientationToleranceDegrees);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies exact direct-target controls remain stable through the authored mock-XR source, player VRIK pipeline,
    /// physical target, and right-hand attachment without beginning a grab.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferencePlayerFixture_StaticDirectTargets_ExactControlsRemainStable()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        StaticTargetTrace ballInterior = await CaptureStaticTargetTraceAsync(
            sceneTree,
            "ball-interior",
            static fixture => ConfigureBallExpectedAttachment(fixture, new Vector3(0.419f, 1.049f, -0.037f)));
        StaticTargetTrace stickOffset = await CaptureStaticTargetTraceAsync(sceneTree, "stick-offset", static fixture =>
        {
            GrabPointCandidate candidate = ((IGrabbable)fixture.Stick).GetGrabPoint(LimbSide.Right, _referenceMockQueryTarget)
                ?? throw new Xunit.Sdk.XunitException("Expected the fixed authored stick query to resolve.");
            return candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();
        });

        AssertStaticTargetExact(ballInterior);
        AssertStaticTargetExact(stickOffset);
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
                LoadValidGrabPoseAnimation(),
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
                new MutableGrabPoint { Animation = LoadValidGrabPoseAnimation() },
                handTarget.GlobalTransform,
                LoadValidGrabPoseAnimation(),
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
    /// Verifies a moving physical ball refreshes its pending candidate at commit so it can still be caught.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovingRigidBodyPendingGrab_RefreshesCandidateAndCommits()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "MovingRigidBodyGrabRoot"
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
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.005f,
        };
        Animation grabPose = LoadValidGrabPoseAnimation();
        GrabbableRigidBody3D ball = new()
        {
            Name = "MovingRigidBall",
            Position = new Vector3(0.1f, 0.0f, 0.0f),
            GravityScale = 0.0f,
        };
        SphericalGrabPoint grabPoint = new()
        {
            Name = "SphericalGrabPoint",
            ReachDistanceMetres = 0.2f,
            PalmFacingMinimumDot = -1.0f,
            GrabAnimation = grabPose,
            GrabPointPositionOffsetFromHand = new Vector3(0.05f, 0.0f, 0.0f),
        };
        ball.AddChild(grabPoint);

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(animationPlayer);
        root.AddChild(animationTree);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(ball);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        ball.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            ball.RefreshComponents();
            Transform3D initialHandTransform = new(Basis.Identity, new Vector3(0.05f, 0.0f, 0.0f));
            handTarget.GlobalTransform = initialHandTransform;
            SetHandAttachmentTransform(skeleton, initialHandTransform);
            GrabPointCandidate? initialCandidate = ((IGrabbable)ball).GetGrabPoint(LimbSide.Right, initialHandTransform);
            Assert.NotNull(initialCandidate);

            _ = hand.Grab();
            Assert.Null(hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);

            Transform3D movedBallTransform = new(Basis.Identity, new Vector3(0.12f, 0.0f, 0.0f));
            Transform3D movedHandTransform = new(Basis.Identity, new Vector3(0.07f, 0.0f, 0.0f));
            ball.GlobalTransform = movedBallTransform;
            ball.ForceUpdateTransform();
            handTarget.GlobalTransform = movedHandTransform;
            SetHandAttachmentTransform(skeleton, movedHandTransform);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(ball, hand.CurrentGrabbed);
            Assert.Same(handAttachment, ball.GetParent());
            Assert.True(ball.IsGrabbed);
            Assert.True(ball.Freeze);
            Assert.Same(grabPose, hand.CurrentPose);
            Assert.True(
                initialCandidate.GrabPointTransform.Origin.DistanceTo(movedBallTransform.Origin) > PositionToleranceMetres,
                "Expected the selected grab point to move far enough that the original candidate would be stale.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies pending movable refresh has a small extra reach tolerance without loosening initial acquisition.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovingPendingGrab_UsesPendingToleranceWithoutChangingInitialReach()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "MovingPendingToleranceRoot"
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
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.5f,
            PendingMovableGrabAcquisitionToleranceMetres = 0.02f,
        };
        GrabbableNode ball = CreateRuntimeMutableGrabbable(new Vector3(0.1f, 0.0f, 0.0f));

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(animationPlayer);
        root.AddChild(animationTree);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(ball);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        ball.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, Vector3.Zero);
            SetHandAttachmentTransform(skeleton, handTarget.GlobalTransform);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            ball.RefreshComponents();
            MutableGrabPoint grabPoint = ball.GetNode<MutableGrabPoint>("MutableGrabPoint");
            grabPoint.ReachDistanceMetres = 0.1f;
            grabPoint.TargetOrigin = new Vector3(0.1f, 0.0f, 0.0f);
            grabPoint.HandTargetOrigin = Vector3.Zero;
            Assert.NotNull(((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform));

            _ = hand.Grab();
            Assert.Null(hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);
            Assert.Equal(HandGrabLifecycleState.Pending, hand.GrabLifecycle);

            grabPoint.TargetOrigin = new Vector3(0.115f, 0.0f, 0.0f);
            grabPoint.HandTargetOrigin = Vector3.Zero;
            Assert.Null(((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform));
            GrabPointCandidate? refreshedCandidate = ((IGrabbable)ball).GetGrabPoint(
                LimbSide.Right,
                handTarget.GlobalTransform,
                hand.PendingMovableGrabAcquisitionToleranceMetres);
            Assert.NotNull(refreshedCandidate);
            _ = hand.Grab();
            Assert.Equal(HandGrabLifecycleState.Pending, hand.GrabLifecycle);
            Assert.True(provider.IsGrabOverrideActive);

            // The same-source tolerance refresh changes the attachment target. It cannot bypass the movable
            // gate: settle the actual bone attachment at the refreshed expected transform for two frames.
            SetHandAttachmentTransform(
                skeleton,
                refreshedCandidate.GrabPointTransform * refreshedCandidate.GrabPointOffsetFromHand.AffineInverse());
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(ball, hand.CurrentGrabbed);
            Assert.True(ball.IsGrabbed);
            Assert.Same(handAttachment, ball.GetParent());
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a Movable pending grab is abandoned when refresh can only select a different grab-point source.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovingPendingGrabTolerance_AbandonsWhenRefreshChangesSource()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "MovingPendingDifferentSourceRoot"
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
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.005f,
            PendingMovableGrabAcquisitionToleranceMetres = 0.03f,
        };
        GrabbableRigidBody3D ball = new()
        {
            Name = "MultiSourceBall",
            Mobility = GrabbableMobility.Movable,
            GravityScale = 0.0f,
        };
        MutableGrabPoint originalGrabPoint = new()
        {
            Name = "OriginalGrabPoint",
            TargetOrigin = new Vector3(0.1f, 0.0f, 0.0f),
            HandTargetOrigin = Vector3.Zero,
            ReachDistanceMetres = 0.1f,
            Animation = LoadValidGrabPoseAnimation(),
        };
        MutableGrabPoint alternateGrabPoint = new()
        {
            Name = "AlternateGrabPoint",
            TargetOrigin = new Vector3(0.2f, 0.0f, 0.0f),
            HandTargetOrigin = Vector3.Zero,
            ReachDistanceMetres = 0.1f,
            Animation = LoadValidGrabPoseAnimation(),
        };
        ball.AddChild(originalGrabPoint);
        ball.AddChild(alternateGrabPoint);

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        root.AddChild(animationPlayer);
        root.AddChild(animationTree);
        root.AddChild(provider);
        root.AddChild(hand);
        root.AddChild(ball);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
        ball.AddToGroup(_pendingGrabGroupName);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            handTarget.GlobalTransform = new Transform3D(Basis.Identity, Vector3.Zero);
            SetHandAttachmentTransform(skeleton, handTarget.GlobalTransform);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            ball.RefreshComponents();
            GrabPointCandidate? initialCandidate = ((IGrabbable)ball).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform);
            Assert.NotNull(initialCandidate);
            Assert.Same(originalGrabPoint, initialCandidate.Source);
            Node originalParent = ball.GetParent() ?? throw new InvalidOperationException("Ball has no parent.");
            bool originalFreeze = ball.Freeze;
            RigidBody3D.FreezeModeEnum originalFreezeMode = ball.FreezeMode;
            Vector3 originalLinearVelocity = ball.LinearVelocity;
            Vector3 originalAngularVelocity = ball.AngularVelocity;

            _ = hand.Grab();
            Assert.Null(hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);
            Assert.Equal(HandGrabLifecycleState.Pending, hand.GrabLifecycle);

            originalGrabPoint.TargetOrigin = new Vector3(0.2f, 0.0f, 0.0f);
            alternateGrabPoint.TargetOrigin = new Vector3(0.115f, 0.0f, 0.0f);
            _ = hand.Grab();

            Assert.Null(hand.CurrentGrabbed);
            Assert.Equal(HandGrabLifecycleState.None, hand.GrabLifecycle);
            Assert.False(ball.IsGrabbed);
            Assert.False(provider.IsGrabOverrideActive);
            Assert.Same(originalParent, ball.GetParent());
            Assert.Equal(originalFreeze, ball.Freeze);
            Assert.Equal(originalFreezeMode, ball.FreezeMode);
            Assert.Equal(originalLinearVelocity, ball.LinearVelocity);
            Assert.Equal(originalAngularVelocity, ball.AngularVelocity);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies repeated translation observations below the 8 mm refresh tolerance are accumulated against the last
    /// provider command rather than forgotten after each observation.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovablePendingGrab_CumulativeSubToleranceTranslationRefreshesAndSettles()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PendingRefreshFixture fixture = await PendingRefreshFixture.CreateAsync(sceneTree);

        try
        {
            Assert.Null(fixture.Hand.Grab());
            Transform3D initialCommand = fixture.Provider.GrabTarget;

            fixture.GrabPoint.TargetOrigin += new Vector3(0.004f, 0.0f, 0.0f);
            Assert.Null(fixture.Hand.Grab());
            AssertTransformApproximatelyEqual(initialCommand, fixture.Provider.GrabTarget, PositionToleranceMetres);
            fixture.GrabPoint.TargetOrigin += new Vector3(0.004f, 0.0f, 0.0f);
            Assert.Null(fixture.Hand.Grab());
            fixture.GrabPoint.TargetOrigin += new Vector3(0.004f, 0.0f, 0.0f);
            Assert.Null(fixture.Hand.Grab());

            Assert.True(fixture.Hand.TryGetPendingLastCommandedApproachTransform(out Transform3D lastCommanded));
            Assert.True(fixture.Hand.TryGetPendingExpectedAttachmentTransform(out Transform3D settlement));
            AssertTransformApproximatelyEqual(fixture.Provider.GrabTarget, lastCommanded, PositionToleranceMetres);
            Assert.True(
                initialCommand.Origin.DistanceTo(lastCommanded.Origin) > fixture.Hand.MovableAttachmentPositionToleranceMetres,
                "Expected accumulated sub-tolerance translation to refresh the provider command.");
            await fixture.SettleAsync(sceneTree, settlement);
            Assert.Same(fixture.Grabbable, fixture.Hand.CurrentGrabbed);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies repeated rotation observations below 5 degrees eventually refresh the provider command.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovablePendingGrab_CumulativeSubToleranceRotationRefreshesAndSettles()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PendingRefreshFixture fixture = await PendingRefreshFixture.CreateAsync(sceneTree);

        try
        {
            Assert.Null(fixture.Hand.Grab());
            Transform3D initialCommand = fixture.Provider.GrabTarget;

            foreach (float degrees in new[] { 2.0f, 4.0f, 6.0f })
            {
                fixture.GrabPoint.TargetBasis = new Basis(Vector3.Up, Mathf.DegToRad(degrees));
                Assert.Null(fixture.Hand.Grab());
            }

            Assert.True(fixture.Hand.TryGetPendingLastCommandedApproachTransform(out Transform3D lastCommanded));
            Assert.True(fixture.Hand.TryGetPendingExpectedAttachmentTransform(out Transform3D settlement));
            AssertTransformApproximatelyEqual(fixture.Provider.GrabTarget, lastCommanded, PositionToleranceMetres);
            Assert.True(
                GetAngularDifferenceDegrees(initialCommand.Basis, lastCommanded.Basis)
                    > fixture.Hand.MovableAttachmentOrientationToleranceDegrees,
                "Expected accumulated sub-tolerance rotation to refresh the provider command.");
            await fixture.SettleAsync(sceneTree, settlement);
            Assert.Same(fixture.Grabbable, fixture.Hand.CurrentGrabbed);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies object motion followed by a stop leaves the provider command at the current settlement destination.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovablePendingGrab_ObjectMotionThenStopDoesNotLeaveStaleCommand()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PendingRefreshFixture fixture = await PendingRefreshFixture.CreateAsync(sceneTree);

        try
        {
            Assert.Null(fixture.Hand.Grab());
            for (int step = 0; step < 4; step++)
            {
                fixture.GrabPoint.TargetOrigin += new Vector3(0.003f, 0.0f, 0.0f);
                Assert.Null(fixture.Hand.Grab());
            }

            Assert.True(fixture.Hand.TryGetPendingLastCommandedApproachTransform(out Transform3D lastCommanded));
            Assert.True(fixture.Hand.TryGetPendingExpectedAttachmentTransform(out Transform3D settlement));
            Assert.InRange(
                settlement.Origin.DistanceTo(lastCommanded.Origin),
                0.0f,
                fixture.Hand.MovableAttachmentPositionToleranceMetres);
            await fixture.SettleAsync(sceneTree, settlement);
            Assert.Same(fixture.Grabbable, fixture.Hand.CurrentGrabbed);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies pending eligibility is re-evaluated from the current hand rather than the original query transform.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovablePendingGrab_CurrentHandLeavesReach_AbandonsAttempt()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PendingRefreshFixture fixture = await PendingRefreshFixture.CreateAsync(sceneTree);

        try
        {
            Assert.Null(fixture.Hand.Grab());
            fixture.HandTarget.GlobalPosition = new Vector3(0.6f, 0.0f, 0.0f);
            Assert.Null(fixture.Hand.Grab());

            Assert.Equal(HandGrabLifecycleState.None, fixture.Hand.GrabLifecycle);
            Assert.True(fixture.Hand.LastPendingGrabAbandonedForCandidateLoss);
            Assert.False(fixture.Provider.IsGrabOverrideActive);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the same source cannot change candidate content and retarget an existing pending attempt.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_MovablePendingGrab_SameSourceContentChangeCannotRetarget()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PendingRefreshFixture fixture = await PendingRefreshFixture.CreateAsync(sceneTree);

        try
        {
            Assert.Null(fixture.Hand.Grab());
            Transform3D originalCommand = fixture.Provider.GrabTarget;
            Animation originalAnimation = fixture.GrabPoint.Animation
                ?? throw new InvalidOperationException("Pending candidate should retain its valid authored grab pose.");
            Animation replacementAnimation = CreateDistinctValidGrabPoseAnimation();
            Assert.NotSame(originalAnimation, replacementAnimation);
            fixture.GrabPoint.Animation = replacementAnimation;
            fixture.GrabPoint.TargetOrigin += new Vector3(0.05f, 0.0f, 0.0f);
            Assert.Null(fixture.Hand.Grab());

            Assert.Equal(HandGrabLifecycleState.None, fixture.Hand.GrabLifecycle);
            Assert.False(fixture.Provider.IsGrabOverrideActive);
            Assert.False(fixture.Grabbable.IsGrabbed);
            AssertTransformApproximatelyEqual(originalCommand, fixture.Provider.GrabTarget, PositionToleranceMetres);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
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

        Assert.NotNull(ResourceLoader.Load<PackedScene>("uid://c7xlydturc3b0"));
        Assert.NotNull(ResourceLoader.Load<PackedScene>("uid://bibq37gjbxvhn"));
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
    /// Verifies a movable pending grab waits for the actual attachment to settle at the candidate offset transform
    /// for two process frames even when the hand target remains outside the approach-distance threshold.
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
        Animation grabPose = LoadValidGrabPoseAnimation();
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

            float approachDistance = handTarget.GlobalPosition.DistanceTo(provider.GrabTarget.Origin);
            Assert.True(
                approachDistance > hand.GrabCommitDistanceMetres,
                $"Expected hand target to remain outside the {hand.GrabCommitDistanceMetres:F3} m approach gate, observed {approachDistance:F4} m.");

            // The actual bone attachment remains wrong. Movable attachment settlement is the authoritative gate,
            // so the ball stays put and pending even though the hand target never reaches its approach target.
            Transform3D expectedAttachment = candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();
            float positionResidual = expectedAttachment.Origin.DistanceTo(handAttachment.GlobalPosition);
            Assert.True(
                positionResidual > MovableAttachmentPositionToleranceMetres,
                $"Expected the direct attachment to exceed the 8 mm position gate, observed {positionResidual:F4} m.");
            Assert.Null(hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);
            Assert.Same(root, ball.GetParent());

            Transform3D overRotationAttachment = new(
                Basis.Identity.Rotated(Vector3.Up, Mathf.DegToRad(10f)),
                expectedAttachment.Origin);
            float angularResidual = GetAngularDifferenceDegrees(expectedAttachment.Basis, overRotationAttachment.Basis);
            Assert.True(
                angularResidual > MovableAttachmentOrientationToleranceDegrees,
                $"Expected the direct attachment to exceed the 5° orientation gate, observed {angularResidual:F2}°.");
            SetHandAttachmentTransform(skeleton, overRotationAttachment);
            await TestUtils.WaitForFramesAsync(sceneTree, 1);

            Assert.Null(hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);

            SetHandAttachmentTransform(skeleton, expectedAttachment);
            await TestUtils.WaitForFramesAsync(sceneTree, 1);

            Assert.Null(hand.CurrentGrabbed);
            Assert.True(provider.IsGrabOverrideActive);

            // The second consecutive settled process frame commits the unchanged candidate.
            await TestUtils.WaitForFramesAsync(sceneTree, 1);

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
        AnimatableBody3D heldCollisionTarget = new()
        {
            Name = "HeldCollisionTarget",
        };
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
            HeldCollisionTarget = heldCollisionTarget,
            PhysicalRig = rig,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
        };
        GrabbableRigidBody3D ball = CreateRuntimeRigidMutableGrabbable(Vector3.Zero);

        root.AddChild(handTarget);
        root.AddChild(skeleton);
        handAttachment.AddChild(heldCollisionTarget);
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
            AssertBodiesHaveMutualCollisionException(heldCollisionTarget, handTarget);
            AssertBodiesHaveMutualCollisionException(heldCollisionTarget, rightHandProxy);
            AssertBodiesHaveMutualCollisionException(heldCollisionTarget, rightLowerArmProxy);
            AssertBodiesHaveMutualCollisionException(heldCollisionTarget, rightFingerProxy);
            AssertBodiesDoNotHaveCollisionException(ball, leftHandProxy);
            AssertBodiesDoNotHaveCollisionException(ball, leftFingerProxy);

            hand.Release();

            Assert.Null(hand.CurrentGrabbed);
            AssertBodiesDoNotHaveCollisionException(ball, handTarget);
            AssertBodiesDoNotHaveCollisionException(ball, rightHandProxy);
            AssertBodiesDoNotHaveCollisionException(ball, rightLowerArmProxy);
            AssertBodiesDoNotHaveCollisionException(ball, rightFingerProxy);
            AssertBodiesDoNotHaveCollisionException(heldCollisionTarget, handTarget);
            AssertBodiesDoNotHaveCollisionException(heldCollisionTarget, rightHandProxy);
            AssertBodiesDoNotHaveCollisionException(heldCollisionTarget, rightLowerArmProxy);
            AssertBodiesDoNotHaveCollisionException(heldCollisionTarget, rightFingerProxy);
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

    /// <summary>
    /// Verifies an unsettled immovable approach is abandoned when its selected grab-point source is removed.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandPoseBehaviour_ImmovablePendingGrabRemovedSource_AbandonsWithoutPhysicsOrPoseSideEffects()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "ImmovablePendingRemovedSourceRoot",
        };
        Node3D handTarget = new()
        {
            Name = "HandTarget",
            Position = new Vector3(-0.1f, 0.0f, 0.0f),
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
        };
        AnimationPlayer animationPlayer = new()
        {
            Name = "AnimationPlayer",
        };
        AnimationTree animationTree = CreateHandPoseAnimationTree();
        HandPoseBehaviour hand = new()
        {
            Name = "RightHandBehaviour",
            Side = LimbSide.Right,
            HandTargetNode = handTarget,
            GrabTargetProvider = provider,
            AnimationTree = animationTree,
            GrabbableGroupName = _pendingGrabGroupName,
            DiscoveryRangeMetres = 0.3f,
            GrabCommitDistanceMetres = 0.02f,
        };
        GrabbableRigidBody3D fixedProp = CreateRuntimeRigidMutableGrabbable(
            new Vector3(0.1f, 0.0f, 0.0f),
            GrabbableMobility.Immovable);

        root.AddChild(handTarget);
        root.AddChild(animationPlayer);
        root.AddChild(animationTree);
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
            MutableGrabPoint originalGrabPoint = fixedProp.GetNode<MutableGrabPoint>("MutableGrabPoint");
            Assert.NotNull(((IGrabbable)fixedProp).GetGrabPoint(LimbSide.Right, handTarget.GlobalTransform));
            Node originalParent = fixedProp.GetParent() ?? throw new InvalidOperationException("Fixed prop has no parent.");
            bool originalFreeze = fixedProp.Freeze;
            RigidBody3D.FreezeModeEnum originalFreezeMode = fixedProp.FreezeMode;
            Vector3 originalLinearVelocity = fixedProp.LinearVelocity;
            Vector3 originalAngularVelocity = fixedProp.AngularVelocity;

            _ = hand.Grab();

            Assert.Null(hand.CurrentGrabbed);
            Assert.Equal(HandGrabLifecycleState.Pending, hand.GrabLifecycle);
            Assert.True(provider.IsGrabOverrideActive);

            fixedProp.RemoveChild(originalGrabPoint);
            originalGrabPoint.Free();
            fixedProp.RefreshComponents();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Null(hand.CurrentGrabbed);
            Assert.Equal(HandGrabLifecycleState.None, hand.GrabLifecycle);
            Assert.False(provider.IsGrabOverrideActive);
            Assert.False(fixedProp.IsGrabbed);
            Assert.Same(originalParent, fixedProp.GetParent());
            Assert.Equal(originalFreeze, fixedProp.Freeze);
            Assert.Equal(originalFreezeMode, fixedProp.FreezeMode);
            Assert.Equal(originalLinearVelocity, fixedProp.LinearVelocity);
            Assert.Equal(originalAngularVelocity, fixedProp.AngularVelocity);
            Assert.Null(hand.CurrentPose);
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
        MethodInfo method = grabPoint.GetType().GetMethods()
            .SingleOrDefault(method => method.Name == nameof(IGrabPoint.GetGrabPoint)
                && method.GetParameters() is [_, { ParameterType: Type parameterType }]
                && parameterType == typeof(Transform3D))
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

    private static Transform3D ConfigureBallExpectedAttachment(
        ReferencePlayerMockXRFixture fixture,
        Vector3 expectedAttachmentOrigin)
    {
        SphericalGrabPoint grabPoint = fixture.Ball.GetNode<SphericalGrabPoint>("SphericalGrabPoint");
        Transform3D offset = new(
            Basis.FromEuler(grabPoint.GrabPointRotationOffsetFromHand),
            grabPoint.GrabPointPositionOffsetFromHand);
        Transform3D independentlySpecified = new(Basis.Identity, expectedAttachmentOrigin);
        fixture.Ball.GlobalTransform = independentlySpecified * offset;
        fixture.Ball.ForceUpdateTransform();
        Transform3D expected = grabPoint.GlobalTransform * offset.AffineInverse();
        AssertTransformApproximatelyEqual(independentlySpecified, expected, PositionToleranceMetres);
        return expected;
    }

    private static void ConfigureBallForAuthoredWrist(
        ReferencePlayerMockXRFixture fixture,
        Transform3D authoredWrist)
    {
        SphericalGrabPoint grabPoint = fixture.Ball.GetNode<SphericalGrabPoint>("SphericalGrabPoint");
        Transform3D assetOffset = new(
            Basis.FromEuler(grabPoint.GrabPointRotationOffsetFromHand),
            grabPoint.GrabPointPositionOffsetFromHand);
        fixture.Ball.GlobalTransform = authoredWrist * assetOffset;
        fixture.Ball.ForceUpdateTransform();

        GrabPointCandidate candidate = ((IGrabbable)fixture.Ball).GetGrabPoint(LimbSide.Right, authoredWrist)
            ?? throw new Xunit.Sdk.XunitException("Expected the authored wrist to remain inside the shared ball reach distance.");
        AssertTransformApproximatelyEqual(
            candidate.GrabPointTransform,
            candidate.HandTarget * candidate.GrabPointOffsetFromHand,
            PositionToleranceMetres);
    }

    private static async Task CommitBallFromAuthoredOpticalWristAsync(
        SceneTree sceneTree,
        ReferencePlayerMockXRFixture fixture)
    {
        fixture.Stick.GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);
        fixture.Stick.ForceUpdateTransform();
        ConfigureBallForAuthoredWrist(fixture, _authoredOpticalBallWrist);
        _ = fixture.Runtime.SetHandObservations(
            XRHandSourceObservation.Optical,
            XRHandSourceObservation.Optical);
        SetCalibratedOpticalWristWorld(fixture.Runtime, LimbSide.Right, _authoredOpticalBallWrist);
        SetCalibratedOpticalWristWorld(
            fixture.Runtime,
            LimbSide.Left,
            new Transform3D(Basis.Identity, new Vector3(-0.25f, 1.00f, -0.35f)));

        _ = await WaitForOpticalPipelineSourceAsync(sceneTree, fixture);
        GrabPointCandidate candidate = ((IGrabbable)fixture.Ball).GetGrabPoint(
            LimbSide.Right,
            fixture.RightHandTarget.GlobalTransform)
            ?? throw new Xunit.Sdk.XunitException("Expected the authored optical wrist to select the shared ball.");
        AssertTransformApproximatelyEqual(
            candidate.GrabPointTransform,
            candidate.HandTarget * candidate.GrabPointOffsetFromHand,
            PositionToleranceMetres);
        Assert.Null(fixture.RightHand.Grab());

        for (int frame = 0; frame < 180 && fixture.RightHand.CurrentGrabbed is null; frame++)
        {
            await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 1);
            await TestUtils.WaitForFramesAsync(sceneTree, 1);
        }

        Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
        Assert.Same(fixture.RightHandAttachment, fixture.Ball.GetParent());
        Assert.False(fixture.RightHandGrabProvider.IsGrabOverrideActive);
    }

    private static async Task<OpticalHeldMotionObservation> MoveHeldOpticalWristAsync(
        SceneTree sceneTree,
        ReferencePlayerMockXRFixture fixture,
        Transform3D authoredWrist)
    {
        Transform3D calibratedBefore = GetRequiredCalibratedOpticalWrist(fixture.Runtime, LimbSide.Right);
        Transform3D providerBefore = fixture.RightHandGrabProvider.GetTargetIntent().WorldTransform;
        IKTargetPipelineResult pipelineBefore = fixture.PlayerVRIK.RightHandTargetPipelineDebugState;
        Transform3D attachmentBefore = fixture.RightHandAttachment.GlobalTransform;
        Transform3D heldBallBefore = fixture.Ball.GlobalTransform;

        SetCalibratedOpticalWristWorld(fixture.Runtime, LimbSide.Right, authoredWrist);
        IKTargetPipelineResult pipelineAfter = await WaitForOpticalPipelineSourceAsync(sceneTree, fixture);
        Transform3D calibratedAfter = GetRequiredCalibratedOpticalWrist(fixture.Runtime, LimbSide.Right);
        IKTargetIntent providerAfter = fixture.RightHandGrabProvider.GetTargetIntent();
        Transform3D attachmentAfter = fixture.RightHandAttachment.GlobalTransform;
        Transform3D heldBallAfter = fixture.Ball.GlobalTransform;

        Assert.Equal(1.0f, providerAfter.DesiredInfluence);
        AssertTransformApproximatelyEqual(calibratedAfter, providerAfter.WorldTransform, PositionToleranceMetres);
        AssertTransformApproximatelyEqual(providerAfter.WorldTransform, pipelineAfter.SourceTarget, PositionToleranceMetres);
        AssertTransformApproximatelyEqual(pipelineAfter.SourceTarget, pipelineAfter.RequestedTarget, PositionToleranceMetres);
        Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
        Assert.Same(fixture.RightHandAttachment, fixture.Ball.GetParent());
        Assert.False(fixture.RightHandGrabProvider.IsGrabOverrideActive);

        return new OpticalHeldMotionObservation(
            calibratedBefore.Origin.DistanceTo(calibratedAfter.Origin),
            providerBefore.Origin.DistanceTo(providerAfter.WorldTransform.Origin),
            pipelineBefore.RequestedTarget.Origin.DistanceTo(pipelineAfter.RequestedTarget.Origin),
            pipelineBefore.RealisedTarget.Origin.DistanceTo(pipelineAfter.RealisedTarget.Origin),
            attachmentBefore.Origin.DistanceTo(attachmentAfter.Origin),
            heldBallBefore.Origin.DistanceTo(heldBallAfter.Origin),
            pipelineAfter.Feedback.Reason);
    }

    private static async Task<IKTargetPipelineResult> WaitForOpticalPipelineSourceAsync(
        SceneTree sceneTree,
        ReferencePlayerMockXRFixture fixture)
    {
        for (int frame = 0; frame < TargetConvergencePhysicsFrames; frame++)
        {
            await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 1);
            await TestUtils.WaitForFramesAsync(sceneTree, 1);
        }

        Transform3D calibratedWrist = GetRequiredCalibratedOpticalWrist(fixture.Runtime, LimbSide.Right);
        IKTargetIntent providerIntent = fixture.RightHandGrabProvider.GetTargetIntent();
        IKTargetPipelineResult pipeline = fixture.PlayerVRIK.RightHandTargetPipelineDebugState;
        Assert.Equal(XRHandTrackingMode.Optical, fixture.Runtime.HandTrackingMode);
        AssertTransformApproximatelyEqual(calibratedWrist, providerIntent.WorldTransform, PositionToleranceMetres);
        AssertTransformApproximatelyEqual(providerIntent.WorldTransform, pipeline.SourceTarget, PositionToleranceMetres);
        AssertTransformApproximatelyEqual(pipeline.SourceTarget, pipeline.RequestedTarget, PositionToleranceMetres);
        AssertTransformApproximatelyEqual(pipeline.RealisedTarget, fixture.RightHandTarget.GlobalTransform, PositionToleranceMetres);
        return pipeline;
    }

    private static Transform3D GetRequiredCalibratedOpticalWrist(MockXRRuntimeNode runtime, LimbSide side)
    {
        IXRHandPoseSource source = runtime.GetHandPoseSource(side);
        Assert.True(source.TryGetCalibratedWristTransform(out Transform3D wrist));
        return wrist;
    }

    private static void SetCalibratedOpticalWristWorld(
        MockXRRuntimeNode runtime,
        LimbSide side,
        Transform3D worldWrist)
    {
        Transform3D calibrationAnchor = Transform3D.Identity;
        string anchorPath = side == LimbSide.Right
            ? "RightOpticalHand/WristAnchor/OpticalHandAnchor"
            : "LeftOpticalHand/WristAnchor/OpticalHandAnchor";
        if (runtime.GetNodeOrNull<Node3D>(anchorPath) is { } seededAnchor)
        {
            calibrationAnchor = seededAnchor.Transform;
        }

        Transform3D scaledOrigin = runtime.OriginNode.GlobalTransform
            .Scaled(new Vector3(runtime.WorldScale, runtime.WorldScale, runtime.WorldScale));
        runtime.SetOpticalWristSample(side, scaledOrigin.AffineInverse() * (worldWrist * calibrationAnchor.AffineInverse()));
    }

    private static async Task<StaticTargetTrace> CaptureStaticTargetTraceAsync(
        SceneTree sceneTree,
        string label,
        Func<ReferencePlayerMockXRFixture, Transform3D> expectedAttachmentFactory)
    {
        ReferencePlayerMockXRFixture fixture = await ReferencePlayerMockXRFixture.CreateAsync(sceneTree);
        try
        {
            Transform3D expectedAttachment = expectedAttachmentFactory(fixture);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.False(fixture.RightHandGrabProvider.IsGrabOverrideActive);
            SetMockRightWristTransformForPlayerVRIK(fixture, expectedAttachment);

            IKTargetPipelineResult pipeline = default;
            for (int frame = 0; frame < 180; frame++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 1);
                await TestUtils.WaitForFramesAsync(sceneTree, 1);
                pipeline = fixture.PlayerVRIK.RightHandTargetPipelineDebugState;
                if (pipeline.SourceTarget.Origin.DistanceTo(expectedAttachment.Origin) <= PositionToleranceMetres
                    && pipeline.RequestedTarget.Origin.DistanceTo(expectedAttachment.Origin) <= PositionToleranceMetres
                    && pipeline.Feedback.ErrorDistance <= PositionToleranceMetres
                    && fixture.RightHandTarget.GlobalPosition.DistanceTo(pipeline.RealisedTarget.Origin) <= PositionToleranceMetres)
                {
                    break;
                }

                if (frame == 179)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Static target '{label}' did not converge through the source/request/realised pipeline. "
                        + $"E={expectedAttachment.Origin}, S={pipeline.SourceTarget.Origin}, R={pipeline.RequestedTarget.Origin}, "
                        + $"P={pipeline.RealisedTarget.Origin}, actuator={pipeline.Feedback.ErrorDistance:F6} m.");
                }
            }

            var samples = new List<StaticTargetSample>(30);
            for (int frame = 0; frame < 30; frame++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 1);
                await TestUtils.WaitForFramesAsync(sceneTree, 1);
                pipeline = fixture.PlayerVRIK.RightHandTargetPipelineDebugState;
                Transform3D attachment = fixture.RightHandAttachment.GlobalTransform;
                samples.Add(new StaticTargetSample(
                    attachment.Origin.DistanceTo(expectedAttachment.Origin),
                    GetAngularDifferenceDegrees(expectedAttachment.Basis, attachment.Basis),
                    pipeline.Feedback.ErrorDistance));
                Assert.Null(fixture.RightHand.CurrentGrabbed);
                Assert.False(fixture.RightHandGrabProvider.IsGrabOverrideActive);
            }

            int shoulderBone = fixture.Skeleton.FindBone("RightUpperArm");
            int lowerArmBone = fixture.Skeleton.FindBone("RightLowerArm");
            int handBone = fixture.Skeleton.FindBone("RightHand");
            Assert.True(shoulderBone >= 0 && lowerArmBone >= 0 && handBone >= 0, "Expected the authored right-arm rest chain.");

            Vector3 shoulderWorldRest = WorldRestOrigin(fixture.Skeleton, shoulderBone);
            Vector3 lowerArmWorldRest = WorldRestOrigin(fixture.Skeleton, lowerArmBone);
            Vector3 handWorldRest = WorldRestOrigin(fixture.Skeleton, handBone);
            float restChainLength = shoulderWorldRest.DistanceTo(lowerArmWorldRest) + lowerArmWorldRest.DistanceTo(handWorldRest);
            float shoulderToExpectedAttachment = shoulderWorldRest.DistanceTo(expectedAttachment.Origin);
            return new StaticTargetTrace(
                label,
                expectedAttachment,
                pipeline.SourceTarget,
                pipeline.RequestedTarget,
                pipeline.RealisedTarget,
                samples,
                shoulderToExpectedAttachment,
                restChainLength,
                restChainLength - shoulderToExpectedAttachment);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static Vector3 WorldRestOrigin(Skeleton3D skeleton, int bone)
        => skeleton.GlobalTransform * skeleton.GetBoneGlobalRest(bone).Origin;

    private static void AssertStaticTargetExact(StaticTargetTrace trace)
    {
        Assert.All(
            trace.Samples,
            sample =>
            {
                Assert.InRange(sample.PositionErrorMetres, 0.0f, PositionToleranceMetres);
                Assert.InRange(sample.AngleErrorDegrees, 0.0f, 0.01f);
                Assert.InRange(sample.ActuatorErrorMetres, 0.0f, PositionToleranceMetres);
            });
    }

    private static void SetHandAttachmentPosition(Skeleton3D skeleton, Vector3 position)
        => SetHandAttachmentTransform(skeleton, new Transform3D(Basis.Identity, position));

    private static void SetHandAttachmentTransform(Skeleton3D skeleton, Transform3D transform)
    {
        skeleton.SetBoneRest(0, transform);
        skeleton.SetBoneGlobalPose(0, transform);
    }

    private static void SetMockRightWristTransform(MockXRRuntimeNode runtime, Transform3D transform)
    {
        Node3D wristSource = runtime.RightHandController.HandPositionNode;
        wristSource.GlobalTransform = transform;
        wristSource.ForceUpdateTransform();
    }

    private static void SetMockRightWristTransformForPlayerVRIK(
        ReferencePlayerMockXRFixture fixture,
        Transform3D desiredWorldTransform)
    {
        Node3D wristSource = fixture.Runtime.RightHandController.HandPositionNode;
        wristSource.Transform = fixture.PlayerVRIK.GlobalTransform.AffineInverse() * desiredWorldTransform;
        wristSource.ForceUpdateTransform();
    }

    private static async Task<IKTargetPipelineResult> WaitForPipelineConvergenceAsync(
        SceneTree sceneTree,
        ReferencePlayerMockXRFixture fixture)
    {
        await TestUtils.WaitForPhysicsFramesAsync(sceneTree, TargetConvergencePhysicsFrames);

        IKTargetPipelineResult pipeline = fixture.PlayerVRIK.RightHandTargetPipelineDebugState;
        Transform3D expectedMockWristAtActuation = fixture.PlayerVRIK.GlobalTransform
                                                   * fixture.Runtime.RightHandController.ControllerNode.Transform
                                                   * fixture.Runtime.RightHandController.HandPositionNode.Transform;
        AssertTransformApproximatelyEqual(expectedMockWristAtActuation, pipeline.SourceTarget, PositionToleranceMetres);
        AssertTransformApproximatelyEqual(pipeline.RealisedTarget, fixture.RightHandTarget.GlobalTransform, PositionToleranceMetres);
        AssertPhysicalTargetAgreesWithRequest(pipeline, "mock XR source convergence");
        return pipeline;
    }

    private static void AssertProviderRequestReachesPipeline(ReferencePlayerMockXRFixture fixture)
    {
        // Run the actual player physics path once so the XR origin is at the same actuation-stage pose for both
        // observations. This avoids comparing a provider read after end-stage origin compensation with a request
        // sampled before it, while neither the physical target nor the attachment is written by the test.
        fixture.PlayerVRIK._PhysicsProcess(1.0d / 60.0d);

        IKTargetIntent providerIntent = fixture.RightHandGrabProvider.GetTargetIntent();
        IKTargetPipelineResult pipeline = fixture.PlayerVRIK.RightHandTargetPipelineDebugState;
        AssertTransformApproximatelyEqual(providerIntent.WorldTransform, pipeline.SourceTarget, PositionToleranceMetres);
        AssertTransformApproximatelyEqual(pipeline.SourceTarget, pipeline.RequestedTarget, PositionToleranceMetres);
    }

    private static void AssertPhysicalTargetAgreesWithRequest(IKTargetPipelineResult pipeline, string context)
    {
        float rotationErrorDegrees = GetAngularDifferenceDegrees(pipeline.RequestedTarget.Basis, pipeline.RealisedTarget.Basis);
        Assert.True(
            pipeline.Feedback.ErrorDistance <= MovableAttachmentPositionToleranceMetres,
            $"Expected unobstructed physical target to realise the {context} request within {MovableAttachmentPositionToleranceMetres:F3} m; "
            + $"observed {pipeline.Feedback.ErrorDistance:F4} m ({pipeline.Feedback.Reason}).");
        Assert.True(
            rotationErrorDegrees <= MovableAttachmentOrientationToleranceDegrees,
            $"Expected unobstructed physical target to realise the {context} orientation within {MovableAttachmentOrientationToleranceDegrees:F1}°; "
            + $"observed {rotationErrorDegrees:F2}°.");
    }

    private static async Task AssertMovableRemainsPendingOutsideDirectAttachmentPositionGateAsync(
        SceneTree sceneTree,
        ReferencePlayerMockXRFixture fixture,
        IGrabbable grabbable,
        Node3D otherGrabbable,
        string label,
        bool requireNonCentreContact)
    {
        otherGrabbable.GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);
        otherGrabbable.ForceUpdateTransform();
        Node3D grabbableNode = Assert.IsAssignableFrom<Node3D>(grabbable);
        grabbableNode.GlobalTransform = requireNonCentreContact
            ? _referenceStickContactTransform
            : _referenceBallContactTransform;
        grabbableNode.ForceUpdateTransform();
        SetMockRightWristTransformForPlayerVRIK(fixture, _referenceMockQueryTarget);
        _ = await WaitForPipelineConvergenceAsync(sceneTree, fixture);
        Assert.True(fixture.RightHandTarget.GlobalPosition.DistanceTo(_referenceMockQueryTarget.Origin) <= 0.002f);
        _ = fixture.RightHandAttachment.GlobalTransform;
        GrabPointCandidate candidate = grabbable.GetGrabPoint(LimbSide.Right, fixture.RightHandTarget.GlobalTransform)
            ?? throw new Xunit.Sdk.XunitException($"Expected independently authored {label} contact to be reachable from the reference target; "
                + $"H0={fixture.RightHandTarget.GlobalTransform.Origin}, item={grabbableNode.GlobalTransform.Origin}.");
        _ = candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();

        SetMockRightWristTransformForPlayerVRIK(fixture, candidate.HandTransform);
        _ = await WaitForPipelineConvergenceAsync(sceneTree, fixture);
        candidate = grabbable.GetGrabPoint(LimbSide.Right, fixture.RightHandTarget.GlobalTransform)
            ?? throw new Xunit.Sdk.XunitException($"Expected the authored {label} to remain reachable from the physical hand target.");
        Transform3D expectedAttachment = candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();
        Transform3D attachmentBeforeGrab = fixture.RightHandAttachment.GlobalTransform;

        if (requireNonCentreContact)
        {
            Node3D contactGrabbableNode = Assert.IsAssignableFrom<Node3D>(grabbable);
            Transform3D selectedPointInGrabbable = contactGrabbableNode.GlobalTransform.AffineInverse() * candidate.GrabPointTransform;
            Assert.True(
                candidate.Source is CylindricalGrabPoint);
            Assert.True(
                MathF.Abs(selectedPointInGrabbable.Origin.Y) >= 0.08f,
                $"Expected the stick candidate to select a non-centre point, observed {selectedPointInGrabbable.Origin}.");
            Assert.InRange(new Vector2(selectedPointInGrabbable.Origin.X, selectedPointInGrabbable.Origin.Z).Length(), 0.0f, 0.08f);
        }

        Transform3D targetToAttachment = candidate.HandTransform.AffineInverse() * attachmentBeforeGrab;
        Transform3D expectedApproachTarget = expectedAttachment * targetToAttachment.AffineInverse();
        Node originalParent = grabbableNode.GetParent() ?? throw new InvalidOperationException($"{label} has no parent.");
        Vector3 originalPosition = grabbableNode.GlobalPosition;
        bool originalFreeze = grabbable is RigidBody3D rigidBody && rigidBody.Freeze;
        Assert.Null(fixture.RightHand.Grab());
        Assert.True(fixture.RightHandGrabProvider.IsGrabOverrideActive);
        AssertTransformApproximatelyEqual(
            expectedApproachTarget,
            fixture.RightHandGrabProvider.GrabTarget,
            MovableAttachmentPositionToleranceMetres);
        // Let the authored PlayerVRIK path complete its ordinary orientation approach. The following samples isolate
        // the position gate: orientation is acceptable while the non-centre contact remains more than 8 mm away.
        await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 30);
        for (int frame = 0; frame < 8; frame++)
        {
            await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 2);
            await TestUtils.WaitForFramesAsync(sceneTree, 1);
            Transform3D actualAttachment = fixture.RightHandAttachment.GlobalTransform;
            GrabPointCandidate? sampledCandidate = grabbable.GetGrabPoint(LimbSide.Right, candidate.HandTransform);
            float positionResidual = expectedAttachment.Origin.DistanceTo(actualAttachment.Origin);
            float angularResidual = GetAngularDifferenceDegrees(expectedAttachment.Basis, actualAttachment.Basis);
            Assert.NotNull(sampledCandidate);
            Assert.Same(candidate.Source, sampledCandidate.Source);
            Assert.True(
                positionResidual > MovableAttachmentPositionToleranceMetres,
                $"Expected {label} direct attachment sample {frame} to stay outside the 8 mm position gate; "
                + $"observed {positionResidual:F4} m.");
            Assert.InRange(angularResidual, 0.0f, MovableAttachmentOrientationToleranceDegrees);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.True(fixture.RightHandGrabProvider.IsGrabOverrideActive);
            Assert.Same(originalParent, grabbableNode.GetParent());
            Assert.True(grabbableNode.GlobalPosition.DistanceTo(originalPosition) <= PositionToleranceMetres);
            if (grabbable is RigidBody3D movableBody)
            {
                Assert.Equal(originalFreeze, movableBody.Freeze);
                Assert.Equal(Vector3.Zero, movableBody.LinearVelocity);
                Assert.Equal(Vector3.Zero, movableBody.AngularVelocity);
            }
        }

        int shoulderBone = fixture.Skeleton.FindBone("RightUpperArm");
        int lowerArmBone = fixture.Skeleton.FindBone("RightLowerArm");
        int handBone = fixture.Skeleton.FindBone("RightHand");
        Assert.True(shoulderBone >= 0 && lowerArmBone >= 0 && handBone >= 0, "Expected the reference right-arm bones.");
        Vector3 shoulder = fixture.Skeleton.GlobalTransform * fixture.Skeleton.GetBoneGlobalPose(shoulderBone).Origin;
        float shoulderToExpectedAttachment = shoulder.DistanceTo(expectedAttachment.Origin);
        float reachableArmLength = fixture.Skeleton.GetBoneGlobalRest(shoulderBone).Origin.DistanceTo(fixture.Skeleton.GetBoneGlobalRest(lowerArmBone).Origin)
                                   + fixture.Skeleton.GetBoneGlobalRest(lowerArmBone).Origin.DistanceTo(fixture.Skeleton.GetBoneGlobalRest(handBone).Origin);
        if (requireNonCentreContact)
        {
            Assert.True(
                shoulderToExpectedAttachment <= 0.40f,
                $"Expected the static {label} attachment to remain inside the 0.40 m rest-arm qualification; "
                + $"target={shoulderToExpectedAttachment:F4} m, rest={reachableArmLength:F4} m.");
        }
        Assert.InRange(reachableArmLength, 0.445f, 0.455f);

    }

    private static async Task<AttachmentResidual> MeasureMovableAttachmentResidualAsync(
        SceneTree sceneTree,
        ReferencePlayerMockXRFixture fixture,
        IGrabbable grabbable,
        string propName)
    {
        Node3D grabbableNode = Assert.IsAssignableFrom<Node3D>(grabbable);
        Node3D otherGrabbable = ReferenceEquals(grabbable, fixture.Ball) ? fixture.Stick : fixture.Ball;
        otherGrabbable.GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);
        grabbableNode.GlobalTransform = propName == "stick"
            ? _referenceStickContactTransform
            : _referenceBallContactTransform;
        grabbableNode.ForceUpdateTransform();
        SetMockRightWristTransformForPlayerVRIK(fixture, _referenceMockQueryTarget);
        _ = await WaitForPipelineConvergenceAsync(sceneTree, fixture);
        AssertTransformApproximatelyEqual(_referenceMockQueryTarget, fixture.RightHandTarget.GlobalTransform, PositionToleranceMetres);

        GrabPointCandidate candidate = grabbable.GetGrabPoint(LimbSide.Right, fixture.RightHandTarget.GlobalTransform)
            ?? throw new Xunit.Sdk.XunitException($"Expected the authored {propName} to be reachable from the physical target.");
        Transform3D expectedAttachment = candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();

        Assert.Null(fixture.RightHand.Grab());
        Assert.True(fixture.RightHandGrabProvider.IsGrabOverrideActive);

        for (int frame = 0; frame < 60 && fixture.RightHand.CurrentGrabbed is null; frame++)
        {
            await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 1);
            if (fixture.RightHand.TryGetPendingExpectedAttachmentTransform(out Transform3D currentSettlement))
            {
                expectedAttachment = currentSettlement;
            }
        }

        Transform3D actualAttachment = fixture.RightHandAttachment.GlobalTransform;
        AttachmentResidual residual = new(
            expectedAttachment.Origin.DistanceTo(actualAttachment.Origin),
            GetAngularDifferenceDegrees(expectedAttachment.Basis, actualAttachment.Basis));

        Assert.Same(grabbable, fixture.RightHand.CurrentGrabbed);
        Assert.False(fixture.RightHandGrabProvider.IsGrabOverrideActive);
        Assert.InRange(residual.PositionMetres, 0.0f, fixture.RightHand.MovableAttachmentPositionToleranceMetres);
        Assert.InRange(residual.OrientationDegrees, 0.0f, fixture.RightHand.MovableAttachmentOrientationToleranceDegrees);

        fixture.RightHand.Release();
        await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 45);
        return residual;
    }

    private static float GetAngularDifferenceDegrees(Basis expected, Basis actual)
    {
        Quaternion expectedRotation = new(expected.Orthonormalized());
        Quaternion actualRotation = new(actual.Orthonormalized());
        float absoluteDot = Mathf.Clamp(Mathf.Abs(expectedRotation.Dot(actualRotation)), 0.0f, 1.0f);
        return Mathf.RadToDeg(2.0f * Mathf.Acos(absoluteDot));
    }

    private static void AssertDirectAttachmentWithinMovableGate(
        Transform3D expectedAttachment,
        Transform3D actualAttachment,
        string context)
    {
        float positionResidual = expectedAttachment.Origin.DistanceTo(actualAttachment.Origin);
        float angularResidual = GetAngularDifferenceDegrees(expectedAttachment.Basis, actualAttachment.Basis);
        Assert.True(
            positionResidual <= MovableAttachmentPositionToleranceMetres,
            $"Expected {context} direct attachment position residual to be within the 8 mm gate; "
            + $"observed {positionResidual:F4} m.");
        Assert.True(
            angularResidual <= MovableAttachmentOrientationToleranceDegrees,
            $"Expected {context} direct attachment orientation residual to be within the 5° gate; "
            + $"observed {angularResidual:F2}°.");
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
            GrabAnimation = LoadValidGrabPoseAnimation(),
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
            GrabAnimation = LoadValidGrabPoseAnimation(),
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
            Animation = animation ?? LoadValidGrabPoseAnimation(),
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
            Animation = LoadValidGrabPoseAnimation(),
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
        Assert.True(succeeded, "Runtime role installer failed.");
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

    private static Animation LoadValidGrabPoseAnimation()
        => ResourceLoader.Load<Animation>(ValidGrabPoseAnimationPath)
            ?? throw new InvalidOperationException($"Could not load valid grab pose '{ValidGrabPoseAnimationPath}'.");

    private static Animation CreateDistinctValidGrabPoseAnimation()
        => Assert.IsType<Animation>(LoadValidGrabPoseAnimation().Duplicate(), exactMatch: false);

    private static void InvokeAttachGrabbedNode(HandPoseBehaviour hand, Node3D grabbedNode, GrabPointCandidate candidate)
    {
        MethodInfo method = typeof(HandPoseBehaviour).GetMethod(
            "AttachGrabbedNode",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandPoseBehaviour.AttachGrabbedNode was not found.");
        _ = method.Invoke(hand, [grabbedNode, candidate]);
    }

    private sealed class PendingRefreshFixture(
        Node3D root,
        Node3D handTarget,
        Skeleton3D skeleton,
        BoneAttachment3D handAttachment,
        HandGrabTargetProvider provider,
        HandPoseBehaviour hand,
        GrabbableNode grabbable,
        MutableGrabPoint grabPoint)
    {
        public Node3D Root { get; } = root;

        public Node3D HandTarget { get; } = handTarget;

        public Skeleton3D Skeleton { get; } = skeleton;

        public BoneAttachment3D HandAttachment { get; } = handAttachment;

        public HandGrabTargetProvider Provider { get; } = provider;

        public HandPoseBehaviour Hand { get; } = hand;

        public GrabbableNode Grabbable { get; } = grabbable;

        public MutableGrabPoint GrabPoint { get; } = grabPoint;

        public static async Task<PendingRefreshFixture> CreateAsync(SceneTree sceneTree)
        {
            Node3D root = new()
            {
                Name = "PendingRefreshRoot"
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
                DiscoveryRangeMetres = 0.3f,
            };
            GrabbableNode grabbable = new()
            {
                Name = "RefreshGrabbable",
                Mobility = GrabbableMobility.Movable,
            };
            MutableGrabPoint grabPoint = new()
            {
                Name = "MutableGrabPoint",
                TargetOrigin = new Vector3(0.1f, 0.0f, 0.0f),
                ReachDistanceMetres = 0.3f,
                Animation = LoadValidGrabPoseAnimation(),
            };
            grabbable.AddChild(grabPoint);
            root.AddChild(handTarget);
            root.AddChild(skeleton);
            root.AddChild(provider);
            root.AddChild(hand);
            root.AddChild(grabbable);
            sceneTree.Root.AddChild(root);
            grabbable.AddToGroup(_pendingGrabGroupName);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            grabbable.RefreshComponents();

            return new PendingRefreshFixture(
                root,
                handTarget,
                skeleton,
                handAttachment,
                provider,
                hand,
                grabbable,
                grabPoint);
        }

        public async Task SettleAsync(SceneTree sceneTree, Transform3D settlement)
        {
            HandTarget.GlobalTransform = settlement;
            SetHandAttachmentTransform(Skeleton, settlement);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            Root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    private sealed partial class MutableGrabPoint : Node, IGrabPoint
    {
        public Vector3 TargetOrigin { get; set; } = Vector3.Zero;

        public Basis TargetBasis { get; set; } = Basis.Identity;

        public Vector3? HandTargetOrigin
        {
            get; set;
        }

        public Transform3D GrabPointOffsetFromHand { get; set; } = Transform3D.Identity;

        public Animation? Animation
        {
            get; set;
        }

        public float ReachDistanceMetres { get; set; } = float.PositiveInfinity;

        public GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)
            => GetGrabPoint(handSide, handTransform, 0.0f);

        public GrabPointCandidate? GetGrabPoint(
            LimbSide handSide,
            Transform3D handTransform,
            float acquisitionToleranceMetres)
        {
            Transform3D handTarget = new(TargetBasis, HandTargetOrigin ?? TargetOrigin);
            Transform3D grabPointTransform = new(TargetBasis, TargetOrigin);
            float acquisitionDistance = handTransform.Origin.DistanceTo(TargetOrigin);
            float effectiveReachDistanceMetres = ReachDistanceMetres + Mathf.Max(0.0f, acquisitionToleranceMetres);
            return acquisitionDistance > effectiveReachDistanceMetres
                ? null
                : new GrabPointCandidate(
                this,
                handTarget,
                Animation ?? LoadValidGrabPoseAnimation(),
                handSide,
                handTransform,
                grabPointTransform,
                GrabPointOffsetFromHand,
                acquisitionDistance)
                {
                    AcquisitionToleranceMetres = Mathf.Max(0.0f, acquisitionToleranceMetres),
                };
        }
    }

    private sealed partial class StaticIKTargetIntentProvider : IKTargetIntentProvider
    {
        public IKTargetIntent TargetIntent { get; set; } = new(Transform3D.Identity, 0.0f);

        public override IKTargetIntent GetTargetIntent() => TargetIntent;
    }

    private readonly record struct AttachmentResidual(float PositionMetres, float OrientationDegrees);

    private readonly record struct OpticalHeldMotionObservation(
        float CalibratedSourceMovementMetres,
        float ProviderMovementMetres,
        float RequestedTargetMovementMetres,
        float RealisedTargetMovementMetres,
        float AttachmentMovementMetres,
        float HeldItemMovementMetres,
        string CollisionFeedbackReason);

    private readonly record struct StaticTargetSample(
        float PositionErrorMetres,
        float AngleErrorDegrees,
        float ActuatorErrorMetres);

    private sealed record StaticTargetTrace(
        string Label,
        Transform3D ExpectedAttachment,
        Transform3D Source,
        Transform3D Requested,
        Transform3D Realised,
        IReadOnlyList<StaticTargetSample> Samples,
        float ShoulderToExpectedAttachmentMetres,
        float RestChainLengthMetres,
        float ReachMarginMetres);

    private sealed class ReferencePlayerMockXRFixture(
        MockRuntimeTestGame root,
        MockXRRuntimeNode runtime,
        PlayerVRIK playerVRIK,
        HandPoseBehaviour rightHand,
        HandPoseBehaviour leftHand,
        HandGrabTargetProvider rightHandGrabProvider,
        IKTargetIntentProvider rightHandFallbackProvider,
        AnimatableBody3D rightHandTarget,
        AnimatableBody3D leftHandTarget,
        BoneAttachment3D rightHandAttachment,
        BoneAttachment3D leftHandAttachment,
        Skeleton3D skeleton,
        GrabbableRigidBody3D ball,
        GrabbableRigidBody3D stick)
    {
        public MockRuntimeTestGame Root { get; } = root;

        public MockXRRuntimeNode Runtime { get; } = runtime;

        public PlayerVRIK PlayerVRIK { get; } = playerVRIK;

        public HandPoseBehaviour RightHand { get; } = rightHand;

        public HandPoseBehaviour LeftHand { get; } = leftHand;

        public HandGrabTargetProvider RightHandGrabProvider { get; } = rightHandGrabProvider;

        public IKTargetIntentProvider RightHandFallbackProvider { get; } = rightHandFallbackProvider;

        public AnimatableBody3D RightHandTarget { get; } = rightHandTarget;

        public AnimatableBody3D LeftHandTarget { get; } = leftHandTarget;

        public BoneAttachment3D RightHandAttachment { get; } = rightHandAttachment;

        public BoneAttachment3D LeftHandAttachment { get; } = leftHandAttachment;

        public Skeleton3D Skeleton { get; } = skeleton;

        public GrabbableRigidBody3D Ball { get; } = ball;

        public GrabbableRigidBody3D Stick { get; } = stick;

        public static async Task<ReferencePlayerMockXRFixture> CreateAsync(SceneTree sceneTree)
        {
            // Match test-session fixture ordering: Game discovers the XR service before the player enters the tree,
            // while the mock runtime is explicitly initialised instead of allowing XRManager to select OpenXR.
            await TestUtils.WaitForNextFrameAsync(sceneTree);

            MockRuntimeTestGame root = new()
            {
                Name = "ReferencePlayerMockXRFixture",
            };
            MockRuntimeTestXRManager xrManager = new()
            {
                Name = "XR",
            };
            MockXRRuntimeNode runtime = ResourceLoader.Load<PackedScene>(MockRuntimeScenePath).Instantiate<MockXRRuntimeNode>();
            Node referenceFixture = ResourceLoader.Load<PackedScene>(ReferencePlayerFixtureScenePath).Instantiate();

            root.AddChild(xrManager);
            root.AddChild(runtime);
            root.AddChild(referenceFixture);
            Assert.True(runtime.Initialise(new SubViewport(), maximumRefreshRate: 90));
            xrManager.SetRuntime(runtime);

            sceneTree.Root.AddChild(root);
            await TestUtils.WaitForFramesAsync(sceneTree, 10);
            await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 2);

            Node player = referenceFixture.GetNode("Actors/Player");
            EnsureRuntimeRoleInstalled(player);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            PlayerVRIK playerVRIK = player.GetNode<PlayerVRIK>("VRIK");
            Assert.True(playerVRIK.BindToXRServices());
            await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 4);

            HandPoseBehaviour rightHand = player.GetNode<HandPoseBehaviour>("Hands/RightHand");
            HandPoseBehaviour leftHand = player.GetNode<HandPoseBehaviour>("Hands/LeftHand");
            HandGrabTargetProvider rightHandGrabProvider = player.GetNode<HandGrabTargetProvider>("VRIK/RightHandGrabProvider");
            IKTargetIntentProvider rightHandFallbackProvider = player.GetNode<IKTargetIntentProvider>("VRIK/RightHandFallbackIntentProvider");
            AnimatableBody3D rightHandTarget = player.GetNode<AnimatableBody3D>("IKTargets/RightHand");
            AnimatableBody3D leftHandTarget = player.GetNode<AnimatableBody3D>("IKTargets/LeftHand");
            BoneAttachment3D rightHandAttachment = player.GetNode<BoneAttachment3D>("Female/GeneralSkeleton/RightHand");
            BoneAttachment3D leftHandAttachment = player.GetNode<BoneAttachment3D>("Female/GeneralSkeleton/LeftHand");
            Skeleton3D skeleton = player.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            GrabbableRigidBody3D ball = referenceFixture.GetNode<GrabbableRigidBody3D>("Items/Ball");
            GrabbableRigidBody3D stick = ResourceLoader.Load<PackedScene>(TestStickScenePath).Instantiate<GrabbableRigidBody3D>();
            Node3D items = referenceFixture.GetNode<Node3D>("Items");

            ball.Freeze = true;
            ball.LinearVelocity = Vector3.Zero;
            ball.AngularVelocity = Vector3.Zero;
            ball.GlobalTransform = _referenceBallContactTransform;
            ball.ForceUpdateTransform();
            stick.Freeze = true;
            stick.GravityScale = 0.0f;
            items.AddChild(stick);
            stick.GlobalTransform = _referenceStickContactTransform;
            stick.ForceUpdateTransform();
            await TestUtils.WaitForPhysicsFramesAsync(sceneTree, 2);

            return new ReferencePlayerMockXRFixture(
                root,
                runtime,
                playerVRIK,
                rightHand,
                leftHand,
                rightHandGrabProvider,
                rightHandFallbackProvider,
                rightHandTarget,
                leftHandTarget,
                rightHandAttachment,
                leftHandAttachment,
                skeleton,
                ball,
                stick);
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await TestUtils.WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    private sealed partial class MockRuntimeTestGame : Game
    {
        public override void _Ready()
        {
        }
    }

    private sealed partial class MockRuntimeTestXRManager : XRManager
    {
        public override void _Ready()
        {
        }

        public void SetRuntime(IXRRuntime runtime) => Runtime = runtime;
    }
}
