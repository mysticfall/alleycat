using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.OpenXR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.XR;

/// <summary>
/// Integration coverage for the production OpenXR optical hand-joint adapter, focusing on the two-tier sample
/// acceptance policy: rotation-only finger joints accept orientation-tracked or orientation-valid samples while the
/// wrist capture stays strictly actively tracked (XR-002 TR20).
/// </summary>
public sealed class OpenXROpticalHandTrackingIntegrationTests
{
    private static readonly StringName _rightTrackerName = new("/user/hand_tracker/right");

    private static readonly StringName _leftTrackerName = new("/user/hand_tracker/left");

    private const float Epsilon = 1e-5f;

    private const XRHandTracker.HandJointFlags ActivelyTracked =
        XRHandTracker.HandJointFlags.PositionTracked | XRHandTracker.HandJointFlags.OrientationTracked;

    private const XRHandTracker.HandJointFlags ValidOnly =
        XRHandTracker.HandJointFlags.PositionValid | XRHandTracker.HandJointFlags.OrientationValid;

    /// <summary>
    /// A finite, actively tracked child sample is rejected when its required tracked source parent is non-finite.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TryGetJoint_NonFiniteRequiredParentWithTrackedFiniteChild_ReturnsFalse()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "OpenXROpticalHandTrackingTestGame",
        };
        sceneTree.Root.AddChild(game);
        await WaitForNextFrameAsync(sceneTree);

        XRHandTracker tracker = new()
        {
            Name = _rightTrackerName,
            HasTrackingData = true,
        };
        bool trackerRegistered = false;
        OpenXROpticalHandTracking tracking = new(
            new OpenXRRuntimeNode(),
            new XRNode3D(),
            new XRNode3D(),
            new Node3D(),
            new Node3D(),
            new OpenXRHandControllerNode(),
            new OpenXRHandControllerNode());

        try
        {
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.ThumbPhalanxProximal, ActivelyTracked);
            tracker.SetHandJointTransform(
                XRHandTracker.HandJoint.ThumbPhalanxProximal,
                new Transform3D(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f)));
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.ThumbPhalanxDistal, ActivelyTracked);
            tracker.SetHandJointTransform(XRHandTracker.HandJoint.ThumbPhalanxDistal, Transform3D.Identity);

            XRServer.AddTracker(tracker);
            trackerRegistered = true;
            Assert.Same(tracker, XRServer.GetTracker(_rightTrackerName));

            // Populate the production adapter's tracker cache through its normal runtime tick seam.
            tracking.EvaluateTick();

            Assert.False(tracking.TryGetJoint(LimbSide.Right, XRHandJoint.ThumbDistal, out XRHandJointSourceSample sample));
            Assert.True(sample.HasTracker);
            Assert.True(sample.HasTrackingData);
            Assert.Equal((long)ActivelyTracked, sample.RawFlags);
            Assert.True(sample.OrientationTracked);
            Assert.True(sample.PositionTracked);
            Assert.True(sample.TrackerLocalTransformFinite);
            Assert.Equal(XRHandJointSourceRejection.ParentNonFinite, sample.RejectionReason);
        }
        finally
        {
            tracking.Shutdown();

            if (trackerRegistered)
            {
                XRServer.RemoveTracker(tracker);
            }

            tracker.Dispose();
            game.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Finger joints reporting only valid (inferred) orientations — without either tracked bit — are accepted for
    /// rotation-only retargeting when the joint and its required source parent are finite (XR-002 TR20).
    /// </summary>
    [Headless]
    [Fact]
    public async Task TryGetJoint_OrientationValidOnlyJointAndParent_AcceptedForRotationOnlyRetargeting()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "OpenXROpticalHandTrackingTestGame",
        };
        sceneTree.Root.AddChild(game);
        await WaitForNextFrameAsync(sceneTree);

        XRHandTracker tracker = new()
        {
            Name = _rightTrackerName,
            HasTrackingData = true,
        };
        bool trackerRegistered = false;
        OpenXROpticalHandTracking tracking = new(
            new OpenXRRuntimeNode(),
            new XRNode3D(),
            new XRNode3D(),
            new Node3D(),
            new Node3D(),
            new OpenXRHandControllerNode(),
            new OpenXRHandControllerNode());

        try
        {
            Transform3D rawParent = new(Basis.Identity.Rotated(Vector3.Right, 0.3f), new Vector3(0.1f, 1.0f, -0.2f));
            Transform3D rawJoint = new(Basis.Identity.Rotated(Vector3.Right, 0.6f), new Vector3(0.11f, 0.99f, -0.2f));

            tracker.SetHandJointFlags(XRHandTracker.HandJoint.ThumbPhalanxProximal, ValidOnly);
            tracker.SetHandJointTransform(XRHandTracker.HandJoint.ThumbPhalanxProximal, rawParent);
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.ThumbPhalanxDistal, ValidOnly);
            tracker.SetHandJointTransform(XRHandTracker.HandJoint.ThumbPhalanxDistal, rawJoint);

            XRServer.AddTracker(tracker);
            trackerRegistered = true;

            tracking.EvaluateTick();

            Assert.True(tracking.TryGetJoint(LimbSide.Right, XRHandJoint.ThumbDistal, out XRHandJointSourceSample sample));

            // The fixture runtime node is at identity with unit world scale, so the world-space joint equals the raw
            // tracker sample (XR-002 frame contract).
            Assert.Equal(LimbSide.Right, sample.Side);
            Assert.Equal(XRHandJoint.ThumbDistal, sample.Joint);
            Assert.True(sample.OrientationValid);
            Assert.False(sample.OrientationTracked);
            Assert.True(sample.PositionValid);
            Assert.False(sample.PositionTracked);
            Assert.Equal((long)ValidOnly, sample.RawFlags);
            Assert.True(sample.ProductionAccepted);
            Assert.Equal(XRHandJointSourceRejection.None, sample.RejectionReason);
            AssertTransformApproximately(rawJoint, sample.TrackerLocalTransform);
            AssertTransformApproximately(rawJoint, sample.ProductionWorldTransform);

            // Position flags are observation-only. Removing them from both source transforms must leave the exact
            // orientation-plus-parent production acceptance unchanged.
            tracker.SetHandJointFlags(
                XRHandTracker.HandJoint.ThumbPhalanxProximal,
                XRHandTracker.HandJointFlags.OrientationValid);
            tracker.SetHandJointFlags(
                XRHandTracker.HandJoint.ThumbPhalanxDistal,
                XRHandTracker.HandJointFlags.OrientationValid);

            Assert.True(tracking.TryGetJoint(LimbSide.Right, XRHandJoint.ThumbDistal, out sample));
            Assert.True(sample.OrientationValid);
            Assert.False(sample.PositionValid);
            Assert.False(sample.PositionTracked);
            Assert.True(sample.ProductionAccepted);
            Assert.Equal((long)XRHandTracker.HandJointFlags.OrientationValid, sample.RawFlags);
        }
        finally
        {
            tracking.Shutdown();

            if (trackerRegistered)
            {
                XRServer.RemoveTracker(tracker);
            }

            tracker.Dispose();
            game.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// An orientation-valid-only joint is still rejected when its transform is non-finite (XR-002 TR20).
    /// </summary>
    [Headless]
    [Fact]
    public async Task TryGetJoint_OrientationValidOnlyJointNonFinite_ReturnsFalse()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "OpenXROpticalHandTrackingTestGame",
        };
        sceneTree.Root.AddChild(game);
        await WaitForNextFrameAsync(sceneTree);

        XRHandTracker tracker = new()
        {
            Name = _rightTrackerName,
            HasTrackingData = true,
        };
        bool trackerRegistered = false;
        OpenXROpticalHandTracking tracking = new(
            new OpenXRRuntimeNode(),
            new XRNode3D(),
            new XRNode3D(),
            new Node3D(),
            new Node3D(),
            new OpenXRHandControllerNode(),
            new OpenXRHandControllerNode());

        try
        {
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.ThumbPhalanxProximal, ValidOnly);
            tracker.SetHandJointTransform(XRHandTracker.HandJoint.ThumbPhalanxProximal, Transform3D.Identity);
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.ThumbPhalanxDistal, ValidOnly);
            tracker.SetHandJointTransform(
                XRHandTracker.HandJoint.ThumbPhalanxDistal,
                new Transform3D(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f)));

            XRServer.AddTracker(tracker);
            trackerRegistered = true;

            tracking.EvaluateTick();

            Assert.False(tracking.TryGetJoint(LimbSide.Right, XRHandJoint.ThumbDistal, out XRHandJointSourceSample sample));
            Assert.False(sample.TrackerLocalTransformFinite);
            Assert.False(sample.ProductionWorldTransformFinite);
            Assert.False(sample.TransformFinite);
            Assert.Equal(XRHandJointSourceRejection.JointNonFinite, sample.RejectionReason);
            Assert.True(float.IsNaN(sample.TrackerLocalTransform.Origin.X));
        }
        finally
        {
            tracking.Shutdown();

            if (trackerRegistered)
            {
                XRServer.RemoveTracker(tracker);
            }

            tracker.Dispose();
            game.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// An orientation-valid-only joint is rejected when its required source parent fails the same orientation gate,
    /// and a joint without any orientation flags is rejected on its own account (XR-002 TR20).
    /// </summary>
    [Headless]
    [Fact]
    public async Task TryGetJoint_OrientationValidOnlyJointWithUnusableParent_ReturnsFalse()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "OpenXROpticalHandTrackingTestGame",
        };
        sceneTree.Root.AddChild(game);
        await WaitForNextFrameAsync(sceneTree);

        XRHandTracker tracker = new()
        {
            Name = _rightTrackerName,
            HasTrackingData = true,
        };
        bool trackerRegistered = false;
        OpenXROpticalHandTracking tracking = new(
            new OpenXRRuntimeNode(),
            new XRNode3D(),
            new XRNode3D(),
            new Node3D(),
            new Node3D(),
            new OpenXRHandControllerNode(),
            new OpenXRHandControllerNode());

        try
        {
            // The parent reports only position-valid: no orientation bits, so it fails the rotation-only tier.
            tracker.SetHandJointFlags(
                XRHandTracker.HandJoint.ThumbPhalanxProximal,
                XRHandTracker.HandJointFlags.PositionValid);
            tracker.SetHandJointTransform(XRHandTracker.HandJoint.ThumbPhalanxProximal, Transform3D.Identity);
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.ThumbPhalanxDistal, ValidOnly);
            tracker.SetHandJointTransform(XRHandTracker.HandJoint.ThumbPhalanxDistal, Transform3D.Identity);

            XRServer.AddTracker(tracker);
            trackerRegistered = true;

            tracking.EvaluateTick();

            Assert.False(tracking.TryGetJoint(LimbSide.Right, XRHandJoint.ThumbDistal, out XRHandJointSourceSample parentRejected));
            Assert.Equal(XRHandJointSourceRejection.ParentOrientationUnusable, parentRejected.RejectionReason);

            // A joint without any orientation flags is rejected on its own account even with a valid parent.
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.ThumbPhalanxProximal, ValidOnly);
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.ThumbPhalanxDistal, default);

            Assert.False(tracking.TryGetJoint(LimbSide.Right, XRHandJoint.ThumbDistal, out XRHandJointSourceSample jointRejected));
            Assert.Equal(XRHandJointSourceRejection.JointOrientationUnusable, jointRejected.RejectionReason);
        }
        finally
        {
            tracking.Shutdown();

            if (trackerRegistered)
            {
                XRServer.RemoveTracker(tracker);
            }

            tracker.Dispose();
            game.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The provider serves the source-only non-thumb metacarpals and gates non-thumb proximals on them: a
    /// valid finite index proximal is accepted only while its required metacarpal source parent is valid and
    /// finite (XR-002 TR14, TR20).
    /// </summary>
    [Headless]
    [Fact]
    public async Task TryGetJoint_IndexProximal_RequiresValidIndexMetacarpalSourceParent()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "OpenXROpticalHandTrackingTestGame",
        };
        sceneTree.Root.AddChild(game);
        await WaitForNextFrameAsync(sceneTree);

        XRHandTracker tracker = new()
        {
            Name = _rightTrackerName,
            HasTrackingData = true,
        };
        bool trackerRegistered = false;
        OpenXROpticalHandTracking tracking = new(
            new OpenXRRuntimeNode(),
            new XRNode3D(),
            new XRNode3D(),
            new Node3D(),
            new Node3D(),
            new OpenXRHandControllerNode(),
            new OpenXRHandControllerNode());

        try
        {
            Transform3D rawMetacarpal = new(Basis.Identity.Rotated(Vector3.Forward, 0.29f), new Vector3(0.1f, 1.0f, -0.2f));
            Transform3D rawJoint = new(Basis.Identity.Rotated(Vector3.Right, 0.3f), new Vector3(0.11f, 0.99f, -0.2f));

            // Wrist, metacarpal, and proximal all finite and orientation-valid: the proximal is served with
            // its metacarpal parent available for the parent-relative derivation.
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.Wrist, ValidOnly);
            tracker.SetHandJointTransform(XRHandTracker.HandJoint.Wrist, Transform3D.Identity);
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.IndexFingerMetacarpal, ValidOnly);
            tracker.SetHandJointTransform(XRHandTracker.HandJoint.IndexFingerMetacarpal, rawMetacarpal);
            tracker.SetHandJointFlags(XRHandTracker.HandJoint.IndexFingerPhalanxProximal, ValidOnly);
            tracker.SetHandJointTransform(XRHandTracker.HandJoint.IndexFingerPhalanxProximal, rawJoint);

            XRServer.AddTracker(tracker);
            trackerRegistered = true;

            tracking.EvaluateTick();

            Assert.True(tracking.TryGetJoint(LimbSide.Right, XRHandJoint.IndexMetacarpal, out XRHandJointSourceSample metacarpal));
            Assert.True(tracking.TryGetJoint(LimbSide.Right, XRHandJoint.IndexProximal, out XRHandJointSourceSample joint));
            AssertTransformApproximately(rawMetacarpal, metacarpal.ProductionWorldTransform);
            AssertTransformApproximately(rawJoint, joint.ProductionWorldTransform);

            // The metacarpal turning non-finite rejects the proximal even though its own sample stays valid.
            tracker.SetHandJointTransform(
                XRHandTracker.HandJoint.IndexFingerMetacarpal,
                new Transform3D(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f)));

            Assert.False(tracking.TryGetJoint(LimbSide.Right, XRHandJoint.IndexProximal, out XRHandJointSourceSample rejected));
            Assert.Equal(XRHandJointSourceRejection.ParentNonFinite, rejected.RejectionReason);
        }
        finally
        {
            tracking.Shutdown();

            if (trackerRegistered)
            {
                XRServer.RemoveTracker(tracker);
            }

            tracker.Dispose();
            game.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The wrist capture keeps the strict actively-tracked tier: with both trackers committing optical mode, a palm
    /// reporting only valid (inferred) flags prevents wrist capture until it reports actively tracked flags
    /// (XR-002 TR20).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CaptureWrist_PalmValidOnly_KeepsStrictActivelyTrackedWristGate()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "OpenXROpticalHandTrackingTestGame",
        };
        sceneTree.Root.AddChild(game);
        await WaitForNextFrameAsync(sceneTree);

        XRHandTracker rightTracker = new()
        {
            Name = _rightTrackerName,
            HasTrackingData = true,
            HandTrackingSource = XRHandTracker.HandTrackingSourceEnum.Unobstructed,
        };
        XRHandTracker leftTracker = new()
        {
            Name = _leftTrackerName,
            HasTrackingData = true,
            HandTrackingSource = XRHandTracker.HandTrackingSourceEnum.Unobstructed,
        };
        bool trackersRegistered = false;
        OpenXROpticalHandTracking tracking = new(
            new OpenXRRuntimeNode(),
            new XRNode3D(),
            new XRNode3D(),
            new Node3D(),
            new Node3D(),
            new OpenXRHandControllerNode(),
            new OpenXRHandControllerNode());

        try
        {
            foreach (XRHandTracker tracker in new[] { rightTracker, leftTracker })
            {
                // Optical mode classification requires an actively tracked wrist (unchanged strict gate).
                tracker.SetHandJointFlags(XRHandTracker.HandJoint.Wrist, ActivelyTracked);
                tracker.SetHandJointTransform(XRHandTracker.HandJoint.Wrist, Transform3D.Identity);

                // The palm only reports valid (inferred) orientation and position.
                tracker.SetHandJointFlags(XRHandTracker.HandJoint.Palm, ValidOnly);
                tracker.SetHandJointTransform(XRHandTracker.HandJoint.Palm, Transform3D.Identity);
            }

            XRServer.AddTracker(rightTracker);
            XRServer.AddTracker(leftTracker);
            trackersRegistered = true;

            tracking.EvaluateTick();

            Assert.Equal(XRHandTrackingMode.Optical, tracking.HandTrackingMode);
            Assert.False(tracking.GetHandPoseSource(LimbSide.Right).EverCapturedWrist);
            Assert.False(tracking.GetHandPoseSource(LimbSide.Left).EverCapturedWrist);

            // Upgrading the palm to actively tracked unlocks the wrist capture on the same strict tier.
            foreach (XRHandTracker tracker in new[] { rightTracker, leftTracker })
            {
                tracker.SetHandJointFlags(XRHandTracker.HandJoint.Palm, ActivelyTracked);
            }

            tracking.EvaluateTick();

            Assert.True(tracking.GetHandPoseSource(LimbSide.Right).EverCapturedWrist);
            Assert.True(tracking.GetHandPoseSource(LimbSide.Left).EverCapturedWrist);
        }
        finally
        {
            tracking.Shutdown();

            if (trackersRegistered)
            {
                XRServer.RemoveTracker(rightTracker);
                XRServer.RemoveTracker(leftTracker);
            }

            rightTracker.Dispose();
            leftTracker.Dispose();
            game.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The WiVRn regression through the production adapter: pose-alive controller trackers (emulated grip poses) with
    /// unknown-source optical data stay in controller mode while the controller-device profile is bound, commit
    /// optical once the controller path switches to the hand-interaction profile, retain optical when the device
    /// profile returns while optical data continues, and return to controller once optical data stops
    /// (XR-002 TR2, TR4).
    /// </summary>
    [Headless(false)]
    [Fact]
    public async Task EvaluateTick_EmulatedControllerWithHandInteractionProfile_CommitsOpticalThenReturnsToController()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "OpenXROpticalHandTrackingTestGame",
        };

        const string handInteractionProfile = "/interaction_profiles/ext/hand_interaction_ext";
        const string touchProfile = "/interaction_profiles/oculus/touch_controller";

        XRPositionalTracker rightControllerTracker = CreateControllerTracker("right_hand", handInteractionProfile);
        XRPositionalTracker leftControllerTracker = CreateControllerTracker("left_hand", handInteractionProfile);
        XRHandTracker rightHandTracker = CreateUnknownSourceHandTracker(_rightTrackerName);
        XRHandTracker leftHandTracker = CreateUnknownSourceHandTracker(_leftTrackerName);
        bool trackersRegistered = false;

        OpenXRHandControllerNode rightController = new()
        {
            Tracker = "right_hand"
        };
        OpenXRHandControllerNode leftController = new()
        {
            Tracker = "left_hand"
        };
        rightController.AddChild(new Node3D { Name = "HandPosition" });
        leftController.AddChild(new Node3D { Name = "HandPosition" });
        game.AddChild(rightController);
        game.AddChild(leftController);

        OpenXROpticalHandTracking tracking = new(
            new OpenXRRuntimeNode(),
            new XRNode3D(),
            new XRNode3D(),
            new Node3D(),
            new Node3D(),
            rightController,
            leftController,
            controllerProfilePollIntervalMsec: 0);

        try
        {
            XRServer.AddTracker(rightControllerTracker);
            XRServer.AddTracker(leftControllerTracker);
            XRServer.AddTracker(rightHandTracker);
            XRServer.AddTracker(leftHandTracker);
            trackersRegistered = true;
            sceneTree.Root.AddChild(game);
            await WaitForFramesAsync(sceneTree, 2);

            // The emulated-controller precondition: controller nodes report tracking data from the registered
            // pose-alive trackers (exactly the WiVRn post-put-down state).
            Assert.True(rightController.HasCurrentTrackingData, "Right controller tracker should be pose-alive.");
            Assert.True(leftController.HasCurrentTrackingData, "Left controller tracker should be pose-alive.");

            // Unknown source + hand-interaction profile proposes optical on both sides despite the tracked
            // controllers; the old controller-liveness-only policy stayed ambiguous here, retaining controller mode.
            tracking.EvaluateTick();

            Assert.Equal(XRHandTrackingMode.Optical, tracking.HandTrackingMode);

            // Picking the controllers up rebinds the device profile while optical data continues: ambiguity retains
            // the committed optical mode instead of flip-flopping.
            rightControllerTracker.Profile = touchProfile;
            leftControllerTracker.Profile = touchProfile;

            tracking.EvaluateTick();

            Assert.Equal(XRHandTrackingMode.Optical, tracking.HandTrackingMode);

            // Optical data stops once the hands hold controllers: bilateral controller proposals commit controller.
            rightHandTracker.HasTrackingData = false;
            leftHandTracker.HasTrackingData = false;

            tracking.EvaluateTick();

            Assert.Equal(XRHandTrackingMode.Controller, tracking.HandTrackingMode);
        }
        finally
        {
            tracking.Shutdown();

            if (trackersRegistered)
            {
                XRServer.RemoveTracker(rightControllerTracker);
                XRServer.RemoveTracker(leftControllerTracker);
                XRServer.RemoveTracker(rightHandTracker);
                XRServer.RemoveTracker(leftHandTracker);
            }

            rightControllerTracker.Dispose();
            leftControllerTracker.Dispose();
            rightHandTracker.Dispose();
            leftHandTracker.Dispose();
            game.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static XRPositionalTracker CreateControllerTracker(string trackerName, string profile)
    {
        XRPositionalTracker tracker = new()
        {
            Name = trackerName,
            Profile = profile,
        };
        tracker.SetPose(
            "default",
            new Transform3D(Basis.Identity, new Vector3(0.3f, 1.2f, -0.5f)),
            Vector3.Zero,
            Vector3.Zero,
            XRPose.TrackingConfidenceEnum.High);

        return tracker;
    }

    /// <summary>
    /// The WiVRn hardware signature through the production adapter: the runtime never reports
    /// <c>hand_interaction</c> — while the hands are active it keeps the pose-alive controller trackers bound to an
    /// emulated Khronos simple-controller profile, with unknown-source optical wrist and palm data. Iteration 2
    /// normalised <c>simple_controller</c> as a controller device, which stranded the committed mode in controller so
    /// the optical session never started; the corrected normalisation commits optical, starts the wrist capture, and
    /// retains the optical mode with frozen wrists through optical data loss (XR-002 TR2, TR4, TR5).
    /// </summary>
    [Headless(false)]
    [Fact]
    public async Task EvaluateTick_EmulatedSimpleControllerProfile_CommitsOpticalAndRetainsOnOpticalLoss()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "OpenXROpticalHandTrackingTestGame",
        };

        const string simpleControllerProfile = "/interaction_profiles/khr/simple_controller";

        XRPositionalTracker rightControllerTracker = CreateControllerTracker("right_hand", simpleControllerProfile);
        XRPositionalTracker leftControllerTracker = CreateControllerTracker("left_hand", simpleControllerProfile);
        XRHandTracker rightHandTracker = CreateUnknownSourceHandTracker(_rightTrackerName);
        XRHandTracker leftHandTracker = CreateUnknownSourceHandTracker(_leftTrackerName);
        bool trackersRegistered = false;

        OpenXRHandControllerNode rightController = new()
        {
            Tracker = "right_hand"
        };
        OpenXRHandControllerNode leftController = new()
        {
            Tracker = "left_hand"
        };
        rightController.AddChild(new Node3D { Name = "HandPosition" });
        leftController.AddChild(new Node3D { Name = "HandPosition" });
        game.AddChild(rightController);
        game.AddChild(leftController);

        OpenXROpticalHandTracking tracking = new(
            new OpenXRRuntimeNode(),
            new XRNode3D(),
            new XRNode3D(),
            new Node3D(),
            new Node3D(),
            rightController,
            leftController,
            controllerProfilePollIntervalMsec: 0);

        try
        {
            XRServer.AddTracker(rightControllerTracker);
            XRServer.AddTracker(leftControllerTracker);
            XRServer.AddTracker(rightHandTracker);
            XRServer.AddTracker(leftHandTracker);
            trackersRegistered = true;
            sceneTree.Root.AddChild(game);
            await WaitForFramesAsync(sceneTree, 2);

            // The WiVRn precondition: emulated pose-alive controller trackers with the simple-controller profile
            // bound (exactly the hardware log's /interaction_profiles/khr/simple_controller).
            Assert.True(rightController.HasCurrentTrackingData, "Right controller tracker should be pose-alive.");
            Assert.True(leftController.HasCurrentTrackingData, "Left controller tracker should be pose-alive.");

            // The hardware log's optical tier: wrist and palm both actively tracked with an unknown source.
            foreach (XRHandTracker tracker in new[] { rightHandTracker, leftHandTracker })
            {
                tracker.SetHandJointFlags(XRHandTracker.HandJoint.Palm, ActivelyTracked);
                tracker.SetHandJointTransform(XRHandTracker.HandJoint.Palm, Transform3D.Identity);
            }

            // Unknown source + simple-controller emulation profile proposes optical on both sides despite the
            // tracked controllers; the optical session starts and the wrist is captured.
            tracking.EvaluateTick();

            Assert.Equal(XRHandTrackingMode.Optical, tracking.HandTrackingMode);
            Assert.True(tracking.GetHandPoseSource(LimbSide.Right).EverCapturedWrist);
            Assert.True(tracking.GetHandPoseSource(LimbSide.Left).EverCapturedWrist);

            Assert.True(tracking.GetHandPoseSource(LimbSide.Right).TryGetCalibratedWristTransform(
                out Transform3D rightWristBeforeLoss));
            Assert.True(tracking.GetHandPoseSource(LimbSide.Left).TryGetCalibratedWristTransform(
                out Transform3D leftWristBeforeLoss));

            // Optical data stops (hands obscured): neither identity is asserted, so ambiguity retains the committed
            // optical mode and both wrists freeze at their last valid transforms.
            rightHandTracker.HasTrackingData = false;
            leftHandTracker.HasTrackingData = false;

            tracking.EvaluateTick();

            Assert.Equal(XRHandTrackingMode.Optical, tracking.HandTrackingMode);

            Assert.True(tracking.GetHandPoseSource(LimbSide.Right).TryGetCalibratedWristTransform(
                out Transform3D rightFrozenWrist));
            Assert.True(tracking.GetHandPoseSource(LimbSide.Left).TryGetCalibratedWristTransform(
                out Transform3D leftFrozenWrist));
            Assert.Equal(rightWristBeforeLoss, rightFrozenWrist);
            Assert.Equal(leftWristBeforeLoss, leftFrozenWrist);
        }
        finally
        {
            tracking.Shutdown();

            if (trackersRegistered)
            {
                XRServer.RemoveTracker(rightControllerTracker);
                XRServer.RemoveTracker(leftControllerTracker);
                XRServer.RemoveTracker(rightHandTracker);
                XRServer.RemoveTracker(leftHandTracker);
            }

            rightControllerTracker.Dispose();
            leftControllerTracker.Dispose();
            rightHandTracker.Dispose();
            leftHandTracker.Dispose();
            game.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static XRHandTracker CreateUnknownSourceHandTracker(StringName trackerName)
    {
        XRHandTracker tracker = new()
        {
            Name = trackerName,
            HasTrackingData = true,
        };

        // The optical proposal's strict gate: an actively tracked wrist. HandTrackingSource stays Unknown, matching
        // runtimes that do not forward XR_EXT_hand_tracking_data_source.
        tracker.SetHandJointFlags(XRHandTracker.HandJoint.Wrist, ActivelyTracked);
        tracker.SetHandJointTransform(XRHandTracker.HandJoint.Wrist, Transform3D.Identity);

        return tracker;
    }

    private static void AssertTransformApproximately(Transform3D expected, Transform3D actual)
    {
        AssertVectorApproximately(expected.Origin, actual.Origin);
        AssertVectorApproximately(expected.Basis.X, actual.Basis.X);
        AssertVectorApproximately(expected.Basis.Y, actual.Basis.Y);
        AssertVectorApproximately(expected.Basis.Z, actual.Basis.Z);
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual)
        => Assert.True(
            (expected - actual).Length() <= Epsilon,
            $"Vector {expected} vs {actual}");

    private sealed partial class TestGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
