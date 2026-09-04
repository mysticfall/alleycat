using AlleyCat.IK;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.XR;

/// <summary>
/// Integration coverage for the runtime hand-pose mode, optical wrist sources, and the VRIK hand-pose
/// target provider using the deterministic mock runtime (XR-002).
/// </summary>
public sealed class XRHandPoseRuntimeIntegrationTests
{
    private const string OpenXRRuntimeScenePath = "res://assets/xr/openxr_runtime.tscn";
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";

    private const float Epsilon = 1e-4f;

    /// <summary>
    /// Both runtime scenes expose separate controller and optical anchors with correct tracker paths, and the
    /// controller calibration anchors remain unchanged (XR-002 TR8, TR9, AC11).
    /// </summary>
    [Headless]
    [Fact]
    public async Task RuntimeScenes_ExposeSeparateControllerAndOpticalAnchors_WithUnchangedControllerCalibration()
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene openXRScene = LoadPackedScene(OpenXRRuntimeScenePath);
        Node openXRRoot = openXRScene.Instantiate();

        try
        {
            Assert.Equal(Node.ProcessModeEnum.Always, openXRRoot.ProcessMode);

            XRNode3D rightOpticalHand = openXRRoot.GetNode<XRNode3D>("RightOpticalHand");
            XRNode3D leftOpticalHand = openXRRoot.GetNode<XRNode3D>("LeftOpticalHand");

            Assert.Equal("/user/hand_tracker/right", rightOpticalHand.Tracker.ToString());
            Assert.Equal("/user/hand_tracker/left", leftOpticalHand.Tracker.ToString());
            Assert.Equal("default", rightOpticalHand.Pose.ToString());
            Assert.Equal("default", leftOpticalHand.Pose.ToString());
            Assert.True(rightOpticalHand.ShowWhenTracked);
            Assert.True(leftOpticalHand.ShowWhenTracked);

            Assert.NotNull(rightOpticalHand.GetNode<Node3D>("WristAnchor"));
            Assert.NotNull(rightOpticalHand.GetNode<Node3D>("WristAnchor/OpticalHandAnchor"));
            Assert.NotNull(leftOpticalHand.GetNode<Node3D>("WristAnchor"));
            Assert.NotNull(leftOpticalHand.GetNode<Node3D>("WristAnchor/OpticalHandAnchor"));

            Node3D rightControllerHand = openXRRoot.GetNode<Node3D>("RightController/HandPosition");
            Node3D leftControllerHand = openXRRoot.GetNode<Node3D>("LeftController/HandPosition");

            // Optical anchors are separate nodes from the controller calibrations.
            Assert.NotEqual(rightControllerHand.GetInstanceId(), rightOpticalHand.GetInstanceId());
            Assert.NotEqual(leftControllerHand.GetInstanceId(), leftOpticalHand.GetInstanceId());
        }
        finally
        {
            openXRRoot.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }

        // Controller calibrations are byte-for-byte the originally authored values (XR-002 TR8).
        string openXRSceneText = Godot.FileAccess.GetFileAsString(OpenXRRuntimeScenePath);

        Assert.Contains(
            "transform = Transform3D(-0.0020973992, -0.5026118, -0.8645097, 0.9894239, -0.12643313, 0.07110564, -0.14504123, -0.85521716, 0.497561, 0, 0, 0.08)",
            openXRSceneText,
            StringComparison.Ordinal);
        Assert.Contains(
            "transform = Transform3D(-0.01074302, 0.66361886, 0.7479937, -0.99840033, 0.03441173, -0.044869334, -0.055515856, -0.7472792, 0.66218704, 0, 0, 0.080010474)",
            openXRSceneText,
            StringComparison.Ordinal);

        PackedScene mockScene = LoadPackedScene(MockRuntimeScenePath);
        Node mockRoot = mockScene.Instantiate();

        try
        {
            Assert.Equal(Node.ProcessModeEnum.Always, mockRoot.ProcessMode);

            foreach (string side in new[] { "Right", "Left" })
            {
                Node3D opticalHand = mockRoot.GetNode<Node3D>(side + "OpticalHand");
                Assert.NotNull(opticalHand.GetNode<Node3D>("WristAnchor"));
                Assert.NotNull(opticalHand.GetNode<Node3D>("WristAnchor/OpticalHandAnchor"));
                Assert.NotNull(mockRoot.GetNode<Node3D>(side + "Controller/HandPosition"));
            }
        }
        finally
        {
            mockRoot.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The committed mode starts as controller and switches only on same-tick bilateral agreement, retaining the
    /// prior mode on disagreement or ambiguity (XR-002 TR1, TR3, TR4, AC10).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandTrackingMode_CommitsOnlyOnSameTickBilateralAgreement_AndStartsController()
    {
        SceneTree sceneTree = GetSceneTree();
        MockRuntimeFixture fixture = await CreateMockRuntimeFixtureAsync(sceneTree);

        try
        {
            MockXRRuntimeNode runtime = fixture.Runtime;

            Assert.Equal(XRHandTrackingMode.Controller, runtime.HandTrackingMode);

            // Disagreement retains the committed mode in both directions.
            Assert.False(runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Controller));
            Assert.Equal(XRHandTrackingMode.Controller, runtime.HandTrackingMode);

            // Ambiguity retains the committed mode.
            Assert.False(runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Ambiguous));
            Assert.Equal(XRHandTrackingMode.Controller, runtime.HandTrackingMode);
            Assert.False(runtime.SetHandObservations(XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Ambiguous));
            Assert.Equal(XRHandTrackingMode.Controller, runtime.HandTrackingMode);

            // Same-tick bilateral agreement commits optical.
            Assert.True(runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical));
            Assert.Equal(XRHandTrackingMode.Optical, runtime.HandTrackingMode);

            // Disagreement retains optical and does not revert.
            Assert.False(runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Optical));
            Assert.Equal(XRHandTrackingMode.Optical, runtime.HandTrackingMode);

            // Bilateral controller agreement commits controller again.
            Assert.True(runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller));
            Assert.Equal(XRHandTrackingMode.Controller, runtime.HandTrackingMode);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// The WiVRn scenario commits optical through the tracked-state derivation path: a pose-alive controller
    /// (emulated grip poses) with unknown-source optical data stays controller while a controller-device profile is
    /// bound, and commits optical once the controller path switches to the hand-interaction profile; putting the
    /// controllers back on returns the mode to controller once optical data stops (XR-002 TR2, TR4, AC10).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandTrackingMode_TrackedStateDerivation_UsesControllerProfileDiscriminator()
    {
        SceneTree sceneTree = GetSceneTree();
        MockRuntimeFixture fixture = await CreateMockRuntimeFixtureAsync(sceneTree);

        try
        {
            MockXRRuntimeNode runtime = fixture.Runtime;

            // Controllers held: tracked controllers with device profiles and no optical data derive controller.
            runtime.SetControllerHandTracked(LimbSide.Left, tracked: true);
            runtime.SetControllerHandTracked(LimbSide.Right, tracked: true);
            runtime.SetOpticalHandTracked(LimbSide.Left, tracked: false);
            runtime.SetOpticalHandTracked(LimbSide.Right, tracked: false);

            Assert.False(runtime.EvaluateHandTrackingMode());
            Assert.Equal(XRHandTrackingMode.Controller, runtime.HandTrackingMode);

            // Controllers put down, optical tracking, but the controller-device profile is still bound: the
            // pose-alive controller keeps the observation ambiguous, retaining controller mode.
            runtime.SetOpticalHandTracked(LimbSide.Left, tracked: true);
            runtime.SetOpticalHandTracked(LimbSide.Right, tracked: true);

            Assert.False(runtime.EvaluateHandTrackingMode());
            Assert.Equal(XRHandTrackingMode.Controller, runtime.HandTrackingMode);

            // The runtime switches the controller path to the hand-interaction profile: optical commits.
            runtime.SetControllerProfile(LimbSide.Left, XRControllerProfileKind.HandInteraction);
            runtime.SetControllerProfile(LimbSide.Right, XRControllerProfileKind.HandInteraction);

            Assert.True(runtime.EvaluateHandTrackingMode());
            Assert.Equal(XRHandTrackingMode.Optical, runtime.HandTrackingMode);

            // Controllers picked up again: the device profile returns, optical data stops, controller commits.
            runtime.SetControllerProfile(LimbSide.Left, XRControllerProfileKind.ControllerDevice);
            runtime.SetControllerProfile(LimbSide.Right, XRControllerProfileKind.ControllerDevice);
            runtime.SetOpticalHandTracked(LimbSide.Left, tracked: false);
            runtime.SetOpticalHandTracked(LimbSide.Right, tracked: false);

            Assert.True(runtime.EvaluateHandTrackingMode());
            Assert.Equal(XRHandTrackingMode.Controller, runtime.HandTrackingMode);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// The WiVRn hardware signature through the mock runtime's tracked-state derivation: WiVRn never reports
    /// <c>hand_interaction</c> — it keeps the pose-alive controller trackers bound to an emulated Khronos
    /// simple-controller profile while the hands drive unknown-source optical data. The simple-controller profile
    /// commits optical despite the tracked controllers, and optical data loss retains the committed optical mode
    /// through ambiguity instead of reverting to controller (XR-002 TR2, TR4, TR5, AC10).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandTrackingMode_TrackedStateDerivation_SimpleControllerProfile_CommitsOpticalAndRetainsOnLoss()
    {
        SceneTree sceneTree = GetSceneTree();
        MockRuntimeFixture fixture = await CreateMockRuntimeFixtureAsync(sceneTree);

        try
        {
            MockXRRuntimeNode runtime = fixture.Runtime;

            // Controllers put down, optical tracking, but WiVRn's emulated simple-controller profile is still bound
            // to the pose-alive controller path: optical commits despite the tracked controllers.
            runtime.SetControllerHandTracked(LimbSide.Left, tracked: true);
            runtime.SetControllerHandTracked(LimbSide.Right, tracked: true);
            runtime.SetOpticalHandTracked(LimbSide.Left, tracked: true);
            runtime.SetOpticalHandTracked(LimbSide.Right, tracked: true);
            runtime.SetControllerProfile(LimbSide.Left, XRControllerProfileKind.SimpleController);
            runtime.SetControllerProfile(LimbSide.Right, XRControllerProfileKind.SimpleController);

            Assert.True(runtime.EvaluateHandTrackingMode());
            Assert.Equal(XRHandTrackingMode.Optical, runtime.HandTrackingMode);

            // Optical data stops (hands obscured): the emulation profile asserts neither identity, so ambiguity
            // retains the committed optical mode rather than reverting to controller.
            runtime.SetOpticalHandTracked(LimbSide.Left, tracked: false);
            runtime.SetOpticalHandTracked(LimbSide.Right, tracked: false);

            Assert.False(runtime.EvaluateHandTrackingMode());
            Assert.Equal(XRHandTrackingMode.Optical, runtime.HandTrackingMode);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// The mode-changed event fires exactly once per committed transition and never while the committed mode is
    /// retained (XR-002 TR3, AC10).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandTrackingModeChanged_FiresExactlyOncePerCommittedTransition()
    {
        SceneTree sceneTree = GetSceneTree();
        MockRuntimeFixture fixture = await CreateMockRuntimeFixtureAsync(sceneTree);

        try
        {
            MockXRRuntimeNode runtime = fixture.Runtime;
            int modeChangedCount = 0;
            runtime.HandTrackingModeChanged += () => modeChangedCount++;

            // Repeated identical observations never fire.
            _ = runtime.SetHandObservations(XRHandSourceObservation.Controller, XRHandSourceObservation.Controller);
            _ = runtime.SetHandObservations(XRHandSourceObservation.Controller, XRHandSourceObservation.Controller);
            Assert.Equal(0, modeChangedCount);

            // Retained disagreement never fires.
            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Ambiguous);
            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Controller);
            Assert.Equal(0, modeChangedCount);

            // Each committed transition fires exactly once.
            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
            Assert.Equal(1, modeChangedCount);
            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
            Assert.Equal(1, modeChangedCount);

            _ = runtime.SetHandObservations(XRHandSourceObservation.Controller, XRHandSourceObservation.Controller);
            Assert.Equal(2, modeChangedCount);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Controller and optical calibrations are independent: mutating the optical calibration anchor never affects
    /// controller anchor values and vice versa (XR-002 TR8, TR9, AC11).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ControllerAndOpticalCalibrations_AreIndependent()
    {
        SceneTree sceneTree = GetSceneTree();
        MockRuntimeFixture fixture = await CreateMockRuntimeFixtureAsync(sceneTree);

        try
        {
            MockXRRuntimeNode runtime = fixture.Runtime;
            Node3D rightControllerHand = runtime.RightHandController.HandPositionNode;
            Node3D leftControllerHand = runtime.LeftHandController.HandPositionNode;
            Transform3D rightControllerCalibration = rightControllerHand.Transform;
            Transform3D leftControllerCalibration = leftControllerHand.Transform;

            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);

            // Mutating the optical calibration anchor and injecting optical samples leaves the controller
            // calibration anchors untouched.
            runtime.SetOpticalCalibrationAnchor(
                LimbSide.Right,
                new Transform3D(Basis.Identity.Rotated(Vector3.Up, 0.4f), new Vector3(0.05f, 0.01f, 0.0f)));
            runtime.SetOpticalWristSample(LimbSide.Right, new Transform3D(Basis.Identity, new Vector3(0.3f, 1.0f, -0.2f)));
            runtime.SetOpticalWristSample(LimbSide.Left, new Transform3D(Basis.Identity, new Vector3(-0.3f, 1.0f, -0.2f)));

            Assert.Equal(rightControllerCalibration, rightControllerHand.Transform);
            Assert.Equal(leftControllerCalibration, leftControllerHand.Transform);

            // Moving the controller calibration anchors never affects the committed optical wrist values.
            Transform3D rightOpticalWrist = GetRequiredWrist(runtime, LimbSide.Right);
            Transform3D leftOpticalWrist = GetRequiredWrist(runtime, LimbSide.Left);
            rightControllerHand.Transform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Right, 0.2f),
                new Vector3(0.4f, 1.1f, -0.3f));
            leftControllerHand.Transform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Up, -0.2f),
                new Vector3(-0.4f, 1.1f, -0.3f));

            Assert.Equal(rightOpticalWrist, GetRequiredWrist(runtime, LimbSide.Right));
            Assert.Equal(leftOpticalWrist, GetRequiredWrist(runtime, LimbSide.Left));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// The calibrated optical wrist reaches the XR hand-pose target provider as a world-space intent with full
    /// influence, and zero influence is only returned before any valid pose exists (XR-002 TR7, TR13).
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpticalWrist_ReachesHandPoseTargetProvider_AsWorldSpaceIntent()
    {
        SceneTree sceneTree = GetSceneTree();
        MockRuntimeFixture fixture = await CreateMockRuntimeFixtureAsync(sceneTree);

        try
        {
            MockXRRuntimeNode runtime = fixture.Runtime;

            // Before any valid optical pose exists, the optical source reports no wrist and the provider intent is
            // zero-influence rather than an identity pose.
            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);

            IKTargetIntent missingIntent = fixture.RightProvider.GetTargetIntent();

            Assert.Equal(0.0f, missingIntent.DesiredInfluence);

            Transform3D rawWrist = new(
                Basis.Identity.Rotated(Vector3.Up, 0.3f),
                new Vector3(0.35f, 1.05f, -0.25f));
            Transform3D calibrationAnchor = new(
                Basis.Identity.Rotated(Vector3.Forward, 0.1f),
                new Vector3(0.02f, 0.0f, 0.0f));

            runtime.SetOpticalCalibrationAnchor(LimbSide.Right, calibrationAnchor);
            runtime.SetOpticalWristSample(LimbSide.Right, rawWrist);

            IKTargetIntent intent = fixture.RightProvider.GetTargetIntent();

            // The fixture origin is at identity with unit world scale, so the calibrated wrist is exactly the raw
            // wrist with its authored anchor applied (XR-002 TR10).
            Transform3D expectedWrist = rawWrist * calibrationAnchor;

            Assert.Equal(1.0f, intent.DesiredInfluence);
            AssertTransformApproximately(expectedWrist, intent.WorldTransform);
            AssertTransformApproximately(expectedWrist, GetRequiredWrist(runtime, LimbSide.Right));

            // A temporary optical loss after a valid pose keeps the frozen world-space intent at full influence.
            runtime.MarkOpticalWristLost(LimbSide.Right);

            IKTargetIntent frozenIntent = fixture.RightProvider.GetTargetIntent();

            Assert.Equal(1.0f, frozenIntent.DesiredInfluence);
            AssertTransformApproximately(expectedWrist, frozenIntent.WorldTransform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Losing optical tracking on one side while committed optical freezes that side at its last valid world
    /// transform across subsequent origin movement while the other side continues (XR-002 TR5, AC4).
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpticalLossOnOneSide_WhileCommittedOptical_FreezesOnlyThatSide()
    {
        SceneTree sceneTree = GetSceneTree();
        MockRuntimeFixture fixture = await CreateMockRuntimeFixtureAsync(sceneTree);

        try
        {
            MockXRRuntimeNode runtime = fixture.Runtime;

            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
            runtime.SetOpticalWristSample(LimbSide.Right, new Transform3D(Basis.Identity, new Vector3(0.3f, 1.0f, -0.2f)));
            runtime.SetOpticalWristSample(LimbSide.Left, new Transform3D(Basis.Identity, new Vector3(-0.3f, 1.0f, -0.2f)));

            Transform3D rightWristBeforeLoss = GetRequiredWrist(runtime, LimbSide.Right);

            runtime.MarkOpticalWristLost(LimbSide.Right);

            // Origin movement after the loss: the frozen right wrist keeps its world-space transform.
            runtime.GlobalTransform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Up, 0.25f),
                new Vector3(1.5f, 0.2f, -0.8f));
            runtime.ForceUpdateTransform();

            Assert.Equal(rightWristBeforeLoss, GetRequiredWrist(runtime, LimbSide.Right));

            // The other side continues tracking with fresh samples composed against the moved origin.
            runtime.SetOpticalWristSample(LimbSide.Left, new Transform3D(Basis.Identity, new Vector3(-0.35f, 1.0f, -0.2f)));

            Transform3D expectedLeftWrist = runtime.GlobalTransform * new Transform3D(Basis.Identity, new Vector3(-0.35f, 1.0f, -0.2f));

            AssertTransformApproximately(expectedLeftWrist, GetRequiredWrist(runtime, LimbSide.Left));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Losing optical tracking on both sides while committed optical freezes both hands and retains the committed
    /// optical mode (XR-002 TR5, AC3, AC10).
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpticalLossOnBothSides_WhileCommittedOptical_FreezesBothAndRetainsMode()
    {
        SceneTree sceneTree = GetSceneTree();
        MockRuntimeFixture fixture = await CreateMockRuntimeFixtureAsync(sceneTree);

        try
        {
            MockXRRuntimeNode runtime = fixture.Runtime;

            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
            runtime.SetOpticalWristSample(LimbSide.Right, new Transform3D(Basis.Identity, new Vector3(0.3f, 1.0f, -0.2f)));
            runtime.SetOpticalWristSample(LimbSide.Left, new Transform3D(Basis.Identity, new Vector3(-0.3f, 1.0f, -0.2f)));

            Transform3D rightWristBeforeLoss = GetRequiredWrist(runtime, LimbSide.Right);
            Transform3D leftWristBeforeLoss = GetRequiredWrist(runtime, LimbSide.Left);

            runtime.MarkOpticalWristLost(LimbSide.Left);
            runtime.MarkOpticalWristLost(LimbSide.Right);
            // Sample-loss observations feed ambiguity, which must retain the committed optical mode.
            _ = runtime.SetHandObservations(XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Ambiguous);

            Assert.Equal(XRHandTrackingMode.Optical, runtime.HandTrackingMode);
            Assert.Equal(rightWristBeforeLoss, GetRequiredWrist(runtime, LimbSide.Right));
            Assert.Equal(leftWristBeforeLoss, GetRequiredWrist(runtime, LimbSide.Left));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// A non-unit world scale with a rotated, moved origin produces the correct world-space wrist with the origin
    /// transform and world scale applied exactly once (XR-002 TR10, AC12).
    /// </summary>
    [Headless]
    [Fact]
    public async Task NonUnitWorldScaleWithMovedRotatedOrigin_ComposesWorldSpaceWristExactlyOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        MockRuntimeFixture fixture = await CreateMockRuntimeFixtureAsync(sceneTree);

        try
        {
            MockXRRuntimeNode runtime = fixture.Runtime;

            runtime.GlobalTransform = new Transform3D(
                Basis.Identity.Rotated(Vector3.Up, Mathf.Pi / 2.0f),
                new Vector3(2.0f, 0.0f, 0.0f));
            runtime.ForceUpdateTransform();
            runtime.WorldScale = 1.5f;

            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);

            Basis anchorRotation = Basis.Identity.Rotated(Vector3.Forward, Mathf.Pi / 6.0f);
            Transform3D rawWrist = new(Basis.Identity, new Vector3(1.0f, 0.0f, 0.0f));
            Transform3D calibrationAnchor = new(anchorRotation, new Vector3(0.1f, 0.0f, 0.0f));

            runtime.SetOpticalCalibrationAnchor(LimbSide.Right, calibrationAnchor);
            runtime.SetOpticalWristSample(LimbSide.Right, rawWrist);

            Transform3D wrist = GetRequiredWrist(runtime, LimbSide.Right);

            // Hand-derived expectation: origin (2,0,0) scaled by 1.5 lands at (3,0,0); the raw wrist at +X 1.1 m
            // (1.0 raw + 0.1 anchor) scaled by 1.5 rotates under the +90 degree yaw to (0, 0, -1.65). The basis is
            // the yaw rotation composed with the anchor rotation, carrying the 1.5 uniform world scale exactly
            // once (never 2.25 or 1.0).
            AssertVectorApproximately(new Vector3(3.0f, 0.0f, -1.65f), wrist.Origin);
            Basis expectedRotation = Basis.Identity.Rotated(Vector3.Up, Mathf.Pi / 2.0f) * anchorRotation;
            AssertBasisColumnApproximately(expectedRotation.X * 1.5f, wrist.Basis.X);
            AssertBasisColumnApproximately(expectedRotation.Y * 1.5f, wrist.Basis.Y);
            AssertBasisColumnApproximately(expectedRotation.Z * 1.5f, wrist.Basis.Z);

            IKTargetIntent intent = fixture.RightProvider.GetTargetIntent();

            Assert.Equal(1.0f, intent.DesiredInfluence);
            AssertTransformApproximately(wrist, intent.WorldTransform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static Transform3D GetRequiredWrist(MockXRRuntimeNode runtime, LimbSide side)
        => runtime.GetHandPoseSource(side).TryGetCalibratedWristTransform(out Transform3D wrist)
            ? wrist
            : throw new Xunit.Sdk.XunitException($"Expected {side} wrist transform to be available.");

    private static async Task<MockRuntimeFixture> CreateMockRuntimeFixtureAsync(SceneTree sceneTree)
    {
        // The integration runner starts test bodies inside TestRuntimeRunner._Ready, where scene-tree
        // attachment does not propagate until the first process frame; wait one frame before attaching.
        await WaitForNextFrameAsync(sceneTree);

        TestGame root = new()
        {
            Name = "XrHandPoseRuntimeFixture",
        };

        TestXRManager xrManager = new()
        {
            Name = "XR",
        };
        root.AddChild(xrManager);

        Node3D originHolder = new()
        {
            Name = "OriginHolder",
        };
        root.AddChild(originHolder);

        MockXRRuntimeNode runtime = LoadPackedScene(MockRuntimeScenePath).Instantiate<MockXRRuntimeNode>();
        originHolder.AddChild(runtime);
        _ = runtime.Initialise(new SubViewport(), maximumRefreshRate: 90);
        xrManager.SetRuntime(runtime);

        XRHandPoseTargetProvider rightProvider = new()
        {
            Name = "RightHandPoseTargetProvider",
            Side = LimbSide.Right,
        };
        root.AddChild(rightProvider);

        XRHandPoseTargetProvider leftProvider = new()
        {
            Name = "LeftHandPoseTargetProvider",
            Side = LimbSide.Left,
        };
        root.AddChild(leftProvider);

        sceneTree.Root.AddChild(root);
        await WaitForFramesAsync(sceneTree, 2);

        return new MockRuntimeFixture(root, runtime, rightProvider, leftProvider);
    }

    private static void AssertTransformApproximately(Transform3D expected, Transform3D actual)
    {
        AssertVectorApproximately(expected.Origin, actual.Origin);
        AssertBasisColumnApproximately(expected.Basis.X, actual.Basis.X);
        AssertBasisColumnApproximately(expected.Basis.Y, actual.Basis.Y);
        AssertBasisColumnApproximately(expected.Basis.Z, actual.Basis.Z);
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual)
        => Assert.True(
            (expected - actual).Length() <= Epsilon,
            $"Vector {expected} vs {actual}");

    private static void AssertBasisColumnApproximately(Vector3 expected, Vector3 actual)
        => AssertVectorApproximately(expected, actual);

    private sealed record MockRuntimeFixture(
        TestGame Root,
        MockXRRuntimeNode Runtime,
        XRHandPoseTargetProvider RightProvider,
        XRHandPoseTargetProvider LeftProvider)
    {
        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
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
}
