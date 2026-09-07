using System.Diagnostics;
using AlleyCat.Control;
using AlleyCat.Control.Hands;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.XR;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Interaction;

/// <summary>
/// Non-visual integration coverage mirroring the CTRL-002 optical grab interaction photobooth scenarios,
/// loading the same visual fixture scene (the reference female player rig with the production
/// <see cref="HandGrabInputCoordinator" />) and asserting the behaviour behind each screenshot scenario:
/// candidate-aware closure recognition through real grabs, the pending/held lifecycle with optical
/// provenance, authored pose authority, release and mode-switch policies, and per-hand independence.
/// </summary>
/// <remarks>
/// <para>
/// Closure and opening ramps are calibrated per candidate animation by projecting injected flex samples
/// through the live binding staged by the player rig's <see cref="OpticalFingerTrackingModifier" /> (the
/// real Quest 3 calibration profile) and evaluating the production recognition strategy — the same
/// approach as the coordinator fixtures, so no magic flex constants exist.
/// </para>
/// <para>
/// The coordinator runs in <c>_PhysicsProcess</c>, so stability windows advance with
/// <c>WaitForPhysicsFramesAsync</c>; pending-grab commits happen in <see cref="HandPoseBehaviour._Process" />,
/// so settle steps wait process frames. Effective finger output is observed through the shared
/// <c>skeleton_updated</c> pose capture, because modifier writes are per-frame overlays.
/// </para>
/// </remarks>
public sealed class OpticalGrabPhotoboothIntegrationTests
{
    private const string PhotoboothScenePath = "res://tests/interaction/optical_grab_photobooth.tscn";
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";
    private const string GrabBallAnimationPath = "res://assets/characters/reference/female/animations/Grab-ball-40.tres";
    private const string GrabPipeAnimationPath = "res://assets/characters/reference/female/animations/Grab-pipe-10.tres";

    private const int TrackedJointCount = 20;

    private const int FingerCount = OpticalFingerProjectionBinding.FingerBonesPerSide;

    private const float LeftWristYawRadians = 0.8f;

    private const float RightWristYawRadians = -1.1f;

    /// <summary>Held-pose agreement with the authored reference (idle-animation drift absorbed).</summary>
    private const float AuthoredMatchToleranceRadians = 0.03f;

    /// <summary>Fixed-pose stability across drastic tracked changes while held.</summary>
    private const float HeldStableToleranceRadians = 0.005f;

    /// <summary>Distinctness guards: poses that must look different.</summary>
    private const float DistinctPoseRadians = 0.15f;

    /// <summary>Minimum mean finger rotation distinguishing the early partial pending pose from open or held.</summary>
    private const float PendingPartialMinimumDifferenceRadians = 0.08f;

    /// <summary>Tracked-tracking agreement after release or on the opposite hand.</summary>
    private const float TrackedMatchToleranceRadians = 0.03f;

    private const float OverClenchScale = 1.8f;

    private const float AuthoredTrialOpenFlex = 0.15f;

    private const float AuthoredBallOpenFlex = 1.10f;

    private const float AuthoredBallClosedFlex = 1.70f;

    private const float AuthoredPipeOpenFlex = 1.35f;

    private const float AuthoredPipeClosedFlex = 2.35f;

    private const float MovableAttachmentPositionToleranceMetres = 0.008f;

    private const float MovableAttachmentOrientationToleranceDegrees = 5.0f;

    private const float StationaryPositionToleranceMetres = 0.001f;

    private const float AdjacentItemDiscontinuityToleranceMetres = 0.010f;

    private const float AdjacentItemDiscontinuityToleranceDegrees = 5.5f;

    private const float AdjacentHandDiscontinuityToleranceMetres = 0.012f;

    private const float AdjacentHandDiscontinuityToleranceDegrees = 8.0f;

    private static readonly Vector3 _rightWristRest = new(0.25f, 1.08f, -0.43f);
    // Separately authored terminal wrist pose for the acceptance recording. It is fixed fixture input and must
    // never be recomputed from a candidate, attachment residual, provider output, or target-to-attachment relation.
    private static readonly Vector3 _rightWristHeld = new(0.26f, 1.02f, -0.28f);
    private static readonly Vector3 _ballItemPosition = new(0.309f, 1.163f, -0.427f);
    private static readonly Vector3 _leftWristRest = new(-0.25f, 1.08f, -0.43f);
    private static readonly Vector3 _leftWristMoved = new(-0.30f, 1.10f, -0.45f);
    private static readonly Vector3 _stickCentre = new(0.43f, 1.16f, -0.58f);
    private static readonly Vector3 _stickContact = new(0.25f, 1.16f, -0.58f);
    private static readonly Vector3 _stickPendingGatePosition = new(0.263675f, 2.1f, -0.430114f);
    private static readonly Vector3 _rightWristStickPendingGate = new(0.133675f, 2.1f, -0.430114f);
    private static readonly Transform3D _reachableStickTransform = new(
        new Basis(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, -1f, 0f)),
        new Vector3(0.429f, 1.102f, -0.088f));
    private static readonly Vector3 _reachableStickWrist = new(0.497f, 1.063f, 0.012f);
    private static readonly Vector3 _reachableStickHeldMove = new(0.507f, 1.068f, 0.017f);

    /// <summary>
    /// Authored controller-mode held-movement delta applied after commit: an independently written wrist
    /// displacement, never derived from a candidate, provider output, or solved attachment state.
    /// </summary>
    private static readonly Vector3 _controllerHeldMoveDelta = new(0.055f, -0.03f, 0.045f);

    /// <summary>
    /// Loose sanity bound on commit-boundary item motion for the converted controller regression: the strict
    /// adjacent-discontinuity gate remains with the optical continuity evidence for the dedicated continuity
    /// unit; this bound guards against teleports only.
    /// </summary>
    private const float CommitBoundarySanityBoundMetres = 0.10f;

    /// <summary>
    /// Adjacency tolerance for the controller regression's held and release samples. The controller wrist's
    /// effective pose carries the fixture's XR-origin swing while the body settles, so these boundaries gate
    /// against teleport-level anomalies while the strict 10/12 mm continuity gates stay with the optical
    /// evidence for the dedicated continuity unit.
    /// </summary>
    private const float ControllerHeldAdjacencyToleranceMetres = 0.06f;

    private const float ControllerHeldAdjacencyToleranceDegrees = 8.0f;

    /// <summary>
    /// Body-lean allowance added to the skeleton-measured rest arm envelope when qualifying fixture
    /// destinations: the VRIK hip positioning leans the shoulder toward distant hand targets, extending the
    /// usable reach beyond the rest-pose bone chain. Destinations beyond envelope plus this allowance are
    /// treated as unreachable.
    /// </summary>
    private const float ReachQualificationBodyLeanAllowanceMetres = 0.08f;

    /// <summary>Authored wrist staging pose raised far above any body-lean reach for the unreachable variant.</summary>
    private static readonly Vector3 _unreachableWristStaging = new(0.26f, 1.94f, -0.4f);

    /// <summary>Bounded interval within which a non-convergent pending grab must abandon.</summary>
    private const float NonConvergenceBudgetSeconds = 20.0f;

    /// <summary>Upper bound on commanded-approach growth beyond its calibrated offset while pending.</summary>
    private const float RunawayCommandGrowthBoundMetres = 0.02f;

    /// <summary>Upper bound on commanded-approach distance beyond the authored destination while pending.</summary>
    private const float RunawayCommandBoundMetres = 0.30f;

    /// <summary>
    /// The photobooth fixture loads with all four camera rigs, the six directional markers, the frozen test
    /// items, the production coordinator node, and a valid finger topology on the installed player rig.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PhotoboothFixture_LoadsInteractionRigMarkersAndItems()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            foreach (string cameraName in new[]
                     {
                           "RightHandCamera", "LeftHandCamera", "StickCamera", "InteractionOverviewCamera",
                          "PendingCommitCamera",
                     })
            {
                Assert.NotNull(fixture.Photobooth.Call("get_camera_rig", cameraName).AsGodotObject());
            }

            foreach (string markerName in new[]
                     {
                         "RightHandRest", "LeftHandRest", "BallItem", "BallContact", "StickItem", "StickContact",
                     })
            {
                Assert.NotNull(fixture.Photobooth.Call("get_marker", markerName).AsGodotObject());
            }

            // Directional sanity: the character faces -Z, so every marker must sit in front of her.
            foreach (string markerName in new[] { "RightHandRest", "LeftHandRest", "BallItem", "BallContact", "StickItem", "StickContact" })
            {
                Vector3 position = ((Node3D)fixture.Photobooth.Call("get_marker", markerName)).GlobalPosition;
                Assert.True(position.Z < 0.0f, $"Marker {markerName} must sit in front of the character (z < 0).");
            }

            // The stick contact must be a clearly non-centre point along the stick axis, and the ball must
            // sit within its spherical reach of the right-hand rest.
            Assert.True(
                Mathf.Abs(_stickContact.X - _stickCentre.X) >= 0.12f,
                "The stick contact must sit a non-centre distance along the stick axis.");
            Assert.True(
                _rightWristRest.DistanceTo(fixture.Ball.GlobalPosition) <= 0.12f,
                "The ball must be within the spherical grab reach of the right-hand rest marker.");
            Assert.True(
                fixture.Ball.GlobalPosition.DistanceTo(_ballItemPosition) <= 0.001f,
                "The ball must retain its authored VRIK-settle placement; moving it reintroduces a pending movable grab.");

            Assert.True(fixture.Ball.Freeze, "The photobooth ball must be frozen in place.");
            Assert.True(fixture.Stick.Freeze, "The photobooth stick must be frozen in place.");
            Assert.NotNull(fixture.Coordinator);
            Assert.True(fixture.Modifier.IsFingerTopologyValid);
            Assert.True(fixture.Modifier.Active);

            // The visual runner lives beside the scene per the photobooth convention.
            Assert.True(Godot.FileAccess.FileExists("res://tests/interaction/optical_grab_photobooth.gd"));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario: an open tracked hand near the ball in optical mode stays idle — no pending grab, no approach
    /// override — while the ball is the observed best candidate (CTRL-002 TR10; XR-002 TR47).
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpenHandNearBall_RecognitionStaysIdle()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabBallAnimationPath);

            await WaitForPhysicsFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.False(fixture.RightProvider.IsGrabOverrideActive);
            Assert.True(
                fixture.RightHand.TryGetCurrentGrabCandidate(out GrabCandidateObservation? candidate),
                "Expected the ball to be the observed best candidate at rest.");
            Assert.Equal("Grab-ball-40", candidate?.Animation?.ResourceName);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 20);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Mirrors the early partial PendingAssistance screenshot checkpoint. The optical grab is intentionally
    /// observed before its approach reaches the direct attachment: the ball stays stationary under its original
    /// Items parent, while the hand is visibly between its open tracked baseline and the committed authored grip.
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpticalBallPendingAssistance_EarlyPartialPoseRemainsUnparentedAndStationary()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabBallAnimationPath);
            Quaternion[] openPose = fixture.CaptureFingerRotations(LimbSide.Right);
            Quaternion[] heldPose = SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath);
            Node originalParent = fixture.Ball.GetParent() ?? throw new InvalidOperationException("Ball has no initial items parent.");
            Transform3D stationaryBall = fixture.Ball.GlobalTransform;

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
            await WaitUntilPendingAsync(sceneTree, fixture, LimbSide.Right);
            await WaitForPhysicsFramesAsync(sceneTree, 1);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);
            Assert.Equal(
                OpticalGrabPoseBlendPhase.PendingAssistance,
                fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            Assert.True(fixture.RightProvider.IsGrabOverrideActive);
            Assert.False(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Same(originalParent, fixture.Ball.GetParent());
            Assert.NotSame(fixture.RightHand.HandBoneAttachment, fixture.Ball.GetParent());
            AssertTransformPositionAndRotationNear(
                stationaryBall,
                fixture.Ball.GlobalTransform,
                StationaryPositionToleranceMetres,
                0.1f,
                "early optical-pending ball");

            Quaternion[] earlyPendingPose = fixture.CaptureFingerRotations(LimbSide.Right);
            Assert.True(
                MeanAngle(earlyPendingPose, openPose) >= PendingPartialMinimumDifferenceRadians,
                "The early pending hand must be materially more cupped than the open baseline.");
            Assert.True(
                MeanAngle(earlyPendingPose, heldPose) >= PendingPartialMinimumDifferenceRadians,
                "The early pending hand must remain materially less closed than the committed ball grip.");
            Assert.True(fixture.RightHand.CancelPendingGrab());
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario: a closure ramp past the grab threshold, held for the stability interval, begins a grab with
    /// optical provenance; the VRIK approach settles it into a held grab whose finger pose is the authored
    /// ball animation once the commit blend window completes (INTR-002 R59-60; XR-002 TR31, TR46-TR49).
    /// </summary>
    [Headless]
    [Fact]
    public async Task BallClosure_CommitsHeldWithAuthoredPoseAndArbitration()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabBallAnimationPath);
            Node itemsHolder = fixture.Ball.GetParent() ?? throw new InvalidOperationException("Ball has no initial items parent.");
            GrabCommitTrace trace = await CommitHeldBallAsync(sceneTree, fixture, calibration);
            Transform3D expectedAttachment = trace.ExpectedAttachment;

            AssertCommitTransitionIsContinuous(trace);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);
            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
            Assert.Equal("Grab-ball-40", fixture.RightHand.CurrentPose?.ResourceName);
            Assert.Equal(
                OpticalGrabPoseBlendPhase.HeldSuppressed,
                fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
            Assert.False(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Left).IsOpticalGrabHeld);
            Assert.Same(fixture.RightHand.HandBoneAttachment, fixture.Ball.GetParent());
            Assert.NotSame(itemsHolder, fixture.Ball.GetParent());
            AssertDirectAttachmentWithinMovableGate(
                expectedAttachment,
                fixture.RightHand.HandBoneAttachment?.GlobalTransform ?? Transform3D.Identity,
                "held ball direct attachment");
            Transform3D postGateBallTransform = fixture.Ball.GlobalTransform;
            await WaitForPhysicsFramesAsync(sceneTree, 3);
            AssertTransformPositionAndRotationNear(
                postGateBallTransform,
                fixture.Ball.GlobalTransform,
                StationaryPositionToleranceMetres,
                0.1f,
                "ball after direct-attachment commit (no late snap)");

            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath),
                AuthoredMatchToleranceRadians,
                "held right hand vs authored ball reference");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Neutral ball evidence uses a frozen closure trajectory, reaches Held before a separately authored wrist
    /// movement, and ends only on an explicit stable-open release. B2 continuity remains asserted separately.
    /// </summary>
    [Headless]
    [Fact]
    public async Task BallFrozenInput_CompletesHeldMoveAndExplicitReleaseLifecycle()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.StartOpticalAtRestAsync(sceneTree);
            FlexCalibration calibration = new(AuthoredBallClosedFlex, AuthoredBallOpenFlex);
            GrabCommitTrace trace = await DriveAuthoredCommitInputAsync(
                sceneTree,
                fixture,
                calibration,
                _rightWristRest,
                _rightWristRest,
                fixture.Ball,
                "ball",
                Basis.Identity);

            SetExactOpticalWristWorld(
                fixture.Runtime,
                LimbSide.Right,
                new Transform3D(Basis.Identity, _rightWristHeld));
            await WaitForPhysicsFramesAsync(sceneTree, 12);
            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            for (int frame = 0; frame < 50; frame++)
            {
                await WaitForPhysicsFramesAsync(sceneTree, 1);
                await WaitForNextFrameAsync(sceneTree);
                trace.Samples.Add(fixture.CaptureTemporalSample(fixture.Ball));
                if (fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.None)
                {
                    break;
                }
            }

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            AssertCompleteLifecycleEvidence(trace, "ball");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Mirrors the controller pending comparison screenshot. The real reference-player hand begins through the
    /// Controller lifecycle source, keeps the ball stationary and unparented, retains its VRIK approach override,
    /// and must not publish optical pending assistance or mutate its ordinary controller presentation.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ControllerPendingBall_RetainsControllerProvenanceWithoutOpticalAssistance()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller);
            await WaitForPhysicsFramesAsync(sceneTree, 30);

            Node itemsHolder = fixture.Ball.GetParent() ?? throw new InvalidOperationException("Ball has no initial items parent.");
            Transform3D stationaryBall = fixture.Ball.GlobalTransform;
            Quaternion[] controllerPresentation = fixture.CaptureFingerRotations(LimbSide.Right);

            Assert.Null(fixture.RightHand.BeginGrab(HandGrabInputSource.Controller));
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.Controller, fixture.RightHand.GrabInputSource);
            Assert.True(fixture.RightProvider.IsGrabOverrideActive);
            Assert.Equal(OpticalGrabPoseBlendPhase.Tracking, fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            Assert.False(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
            Assert.Same(itemsHolder, fixture.Ball.GetParent());
            AssertTransformPositionAndRotationNear(
                stationaryBall,
                fixture.Ball.GlobalTransform,
                StationaryPositionToleranceMetres,
                0.1f,
                "controller-pending ball");

            await WaitForFramesAsync(sceneTree, 1);
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                controllerPresentation,
                HeldStableToleranceRadians,
                "controller pending hand presentation (no optical assistance)");
            Assert.True(fixture.RightHand.CancelPendingGrab());
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Reach-qualified controller regression replacing the retired diagnostic: an authored controller wrist
    /// trajectory stages the body before the grab input, the fixture destination is qualified against the
    /// skeleton-measured arm envelope before injection, and the real controller input layer drives
    /// Pending → Held → authored held movement → explicit release with continuity observed at each boundary
    /// (INTR-002 R56/R58; IK-005 TR19-TR22; CTRL-002 controller provenance).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ControllerBall_ReachQualified_CompletesPendingHeldMoveAndExplicitReleaseLifecycle()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Stick.GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);
            fixture.Stick.ForceUpdateTransform();

            var relay = (MockXRHandControllerNode)fixture.Runtime.RightHandController;
            Transform3D restWristNode = relay.HandPositionNode.GlobalTransform;
            Transform3D restWrist = ResolveCanonicalControllerWrist(fixture);

            // Authored destination, computed from authored content (ball candidate at the authored rest-wrist
            // query) before any grab input is injected — never from a live solved attachment or provider output.
            GrabPointCandidate candidate = ((IGrabbable)fixture.Ball).GetGrabPoint(LimbSide.Right, restWrist)
                ?? throw new Xunit.Sdk.XunitException("Expected the authored rest wrist to select the fixture ball.");
            Transform3D destinationAttachment = candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();
            // The terminal wrist sits a hand-length short of the destination along the rest approach
            // direction — the authored contact pose a real wrist takes when the hand wraps the ball — so the
            // solved attachment lands on the destination while the source intent stays independently authored.
            // The authored controller input is the rest wrist: the user reaches from rest toward the
            // qualified ball, letting the assisted approach and body lean close the remaining distance, as in
            // the optical acceptance trials whose authored wrist trajectories also start from rest.
            await HoldControllerWristAtNodePoseAsync(sceneTree, fixture, relay, restWristNode, frames: 30);

            // Reach qualification from skeleton measurements: the destination must sit inside the usable arm
            // envelope measured from the settled shoulder pose (INTR-002 R56; the prior diagnostic measured
            // 0.4802 m from the rest shoulder against a 0.4496 m envelope and could not converge).
            (Vector3 settledShoulder, float armEnvelope) = MeasureRightArmReach(fixture);
            float shoulderToDestination = settledShoulder.DistanceTo(destinationAttachment.Origin);
            Assert.True(
                shoulderToDestination <= armEnvelope + ReachQualificationBodyLeanAllowanceMetres,
                $"The reach-qualified controller fixture destination must sit inside the usable arm envelope "
                + $"including body lean: shoulder→destination {shoulderToDestination:F4} m vs envelope "
                + $"{armEnvelope:F4} m + {ReachQualificationBodyLeanAllowanceMetres:F3} m lean.");

            // Bind the real controller input layer and drive the grab through it.
            _ = fixture.XRManager.EmitSignal(XRManager.SignalName.Initialised, true);
            await WaitForFramesAsync(sceneTree, 4);
            Node itemsHolder = fixture.Ball.GetParent() ?? throw new InvalidOperationException("Ball has no initial items parent.");
            Transform3D stationaryBall = fixture.Ball.GlobalTransform;
            var samples = new List<GrabTemporalSample>();

            relay.TriggerActionButtonPressed("grip_click");
            Transform3D pressWrist = ResolveCanonicalControllerWrist(fixture);
            Assert.True(
                fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.Pending,
                $"Expected the grip press to begin a controller pending grab: canonicalWrist={pressWrist.Origin}, "
                + $"nodeWrist={relay.HandPositionNode.GlobalTransform.Origin}, ball={fixture.Ball.GlobalPosition}, "
                + $"queryCandidate={((IGrabbable)fixture.Ball).GetGrabPoint(LimbSide.Right, pressWrist) is not null}.");
            Assert.Equal(HandGrabInputSource.Controller, fixture.RightHand.GrabInputSource);

            // Pending: the item stays in place under its original parent while the approach runs, with the
            // authored rest input held against the XR-origin swing.
            var stopwatch = Stopwatch.StartNew();
            while (fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.Pending
                   && stopwatch.Elapsed.TotalSeconds < 10.0)
            {
                relay.HandPositionNode.GlobalTransform = restWristNode;
                await WaitForPhysicsFramesAsync(sceneTree, 1);
                await WaitForNextFrameAsync(sceneTree);
                samples.Add(fixture.CaptureTemporalSample(fixture.Ball));
            }

            Assert.True(
                fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.Held,
                $"The reach-qualified controller ball must commit; abandonment reason was "
                + $"{fixture.RightHand.LastPendingGrabAbandonmentReason}.");
            Assert.Equal(HandGrabInputSource.Controller, fixture.RightHand.GrabInputSource);
            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
            Assert.All(
                samples.Where(sample => sample.Lifecycle == HandGrabLifecycleState.Pending),
                sample => Assert.Equal(itemsHolder.GetPath().ToString(), sample.ItemParent));
            Assert.All(
                samples.Where(sample => sample.Lifecycle == HandGrabLifecycleState.Pending),
                sample => Assert.True(
                    sample.ItemTransform.Origin.DistanceTo(stationaryBall.Origin) <= StationaryPositionToleranceMetres,
                    "The ball must stay stationary while the controller approach is pending."));

            // Authored held movement: the wrist source drives the held ball through an authored displacement,
            // observed frame by frame for continuity.
            Transform3D heldStart = relay.HandPositionNode.GlobalTransform;
            var heldEnd = new Transform3D(heldStart.Basis, heldStart.Origin + _controllerHeldMoveDelta);
            for (int frame = 1; frame <= 25; frame++)
            {
                float alpha = frame / 25f;
                relay.HandPositionNode.GlobalTransform = new Transform3D(
                    heldStart.Basis.Slerp(heldEnd.Basis, alpha).Orthonormalized(),
                    heldStart.Origin.Lerp(heldEnd.Origin, alpha));
                await WaitForPhysicsFramesAsync(sceneTree, 1);
                await WaitForNextFrameAsync(sceneTree);
                samples.Add(fixture.CaptureTemporalSample(fixture.Ball));
            }

            await HoldControllerWristAtNodePoseAsync(sceneTree, fixture, relay, heldEnd, frames: 20);
            samples.Add(fixture.CaptureTemporalSample(fixture.Ball));
            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);

            float heldBallDisplacement = stationaryBall.Origin.DistanceTo(fixture.Ball.GlobalTransform.Origin);
            Assert.True(
                heldBallDisplacement >= 0.5f * _controllerHeldMoveDelta.Length(),
                $"The held ball must follow the authored held movement; observed {heldBallDisplacement:F4} m "
                + $"of the authored {_controllerHeldMoveDelta.Length():F4} m wrist displacement.");

            // Explicit release through the real controller input layer restores the unheld state.
            Transform3D heldBallTransform = fixture.Ball.GlobalTransform;
            relay.TriggerActionButtonReleased("grip_click");
            for (int frame = 0; frame < 4; frame++)
            {
                await WaitForPhysicsFramesAsync(sceneTree, 1);
                await WaitForNextFrameAsync(sceneTree);
                samples.Add(fixture.CaptureTemporalSample(fixture.Ball));
            }

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Equal(HandGrabInputSource.None, fixture.RightHand.GrabInputSource);
            Assert.Same(itemsHolder, fixture.Ball.GetParent());
            Assert.True(fixture.Ball.Freeze, "The photobooth ball must return to its authored frozen state.");
            AssertTransformPositionAndRotationNear(
                heldBallTransform,
                fixture.Ball.GlobalTransform,
                StationaryPositionToleranceMetres,
                0.5f,
                "released ball (no drop or launch from a stationary hold)");

            // Continuity observed across the trial. The commit reparent and post-clear hand chase are measured
            // and reported for the dedicated continuity unit (the assisted hand diverges from the source wrist
            // over the wrist-to-contact offset by design); the release and held-movement boundaries are gated
            // strictly, and the commit boundary is guarded against teleports only.
            var trace = new GrabCommitTrace(samples);
            int heldIndex = trace.Samples.FindIndex(sample => sample.Lifecycle == HandGrabLifecycleState.Held);
            Assert.True(heldIndex >= 3, $"The trace must include Pending samples before Held.{System.Environment.NewLine}{trace.Format()}");
            float maximumCommitBoundaryItemDistance = 0.0f;
            for (int index = Math.Max(1, heldIndex - 1); index <= heldIndex + 1 && index < trace.Samples.Count; index++)
            {
                maximumCommitBoundaryItemDistance = MathF.Max(
                    maximumCommitBoundaryItemDistance,
                    trace.Samples[index - 1].ItemTransform.Origin.DistanceTo(trace.Samples[index].ItemTransform.Origin));
            }

            Assert.True(
                maximumCommitBoundaryItemDistance <= CommitBoundarySanityBoundMetres,
                $"The commit boundary must not teleport the item; observed {maximumCommitBoundaryItemDistance:F4} m.");
            Console.WriteLine(
                "Reach-qualified controller-ball commit-boundary evidence: maximum adjacent item motion "
                + "{0:F4} m across the reparent and first post-clear samples (strict gate deferred to the continuity unit).",
                maximumCommitBoundaryItemDistance);
            AssertAdjacentSamplesContinuousAfterIndex(
                trace,
                heldIndex + 6,
                ControllerHeldAdjacencyToleranceMetres,
                ControllerHeldAdjacencyToleranceDegrees,
                "controller held movement and explicit release");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Negative coverage for the same fixture family: the ball is raised far above any body-lean reach while
    /// the staged wrist stays inside its spherical reach, so the pending approach cannot converge. Bounded
    /// non-convergence must abandon the pending grab with the distinct reason inside a bounded interval,
    /// without committing, moving the item, or growing the commanded approach without bound (IK-005 TR22).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ControllerBall_UnreachableDestination_AbandonsPendingAtBoundedNonConvergence()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Stick.GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);
            fixture.Stick.ForceUpdateTransform();

            var relay = (MockXRHandControllerNode)fixture.Runtime.RightHandController;

            // Raise the wrist well above any body-lean reach and hold it there while the body strains,
            // re-pinning the authored input every frame against the XR-origin swing.
            Transform3D stagedWristTarget = new(Basis.Identity, _unreachableWristStaging);
            await DriveControllerWristToCanonicalAsync(sceneTree, fixture, stagedWristTarget, frames: 30);
            await HoldControllerWristAtCanonicalAsync(sceneTree, fixture, stagedWristTarget, frames: 45);

            // The effective wrist is the canonical-epoch source sample the pipeline actuates against; author
            // the raised ball beside that observed input state (never beside a solver or provider output).
            Transform3D stagedWrist = ResolveCanonicalControllerWrist(fixture);
            Vector3 unreachableBallCentre = stagedWrist.Origin + new Vector3(0.03f, 0.06f, -0.02f);
            fixture.Ball.GlobalPosition = unreachableBallCentre;
            fixture.Ball.ForceUpdateTransform();

            GrabPointCandidate candidate = ((IGrabbable)fixture.Ball).GetGrabPoint(LimbSide.Right, stagedWrist)
                ?? throw new Xunit.Sdk.XunitException(
                    $"Expected the staged wrist to select the raised fixture ball: stagedWrist={stagedWrist.Origin}, "
                    + $"ball={fixture.Ball.GlobalPosition}.");
            Transform3D destinationAttachment = candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();

            // Unreachability qualification from skeleton measurements: the destination sits far beyond the
            // usable arm envelope from the settled shoulder, beyond any body-lean compensation.
            (Vector3 settledShoulder, float armEnvelope) = MeasureRightArmReach(fixture);
            float shoulderToDestination = settledShoulder.DistanceTo(destinationAttachment.Origin);
            Assert.True(
                shoulderToDestination > armEnvelope + 0.05f,
                $"The unreachable fixture variant must place the destination beyond the arm envelope: "
                + $"shoulder→destination {shoulderToDestination:F4} m vs envelope {armEnvelope:F4} m.");

            _ = fixture.XRManager.EmitSignal(XRManager.SignalName.Initialised, true);
            await WaitForFramesAsync(sceneTree, 4);
            Node itemsHolder = fixture.Ball.GetParent() ?? throw new InvalidOperationException("Ball has no initial items parent.");
            Transform3D stationaryBall = fixture.Ball.GlobalTransform;

            relay.TriggerActionButtonPressed("grip_click");
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);

            float maximumCommandedOvershoot = 0.0f;
            float initialCommandedOvershoot = float.PositiveInfinity;
            var diagnosticSamples = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            while (fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.Pending
                   && stopwatch.Elapsed.TotalSeconds < NonConvergenceBudgetSeconds)
            {
                await StepControllerWristToCanonicalAsync(
                    sceneTree,
                    fixture,
                    relay,
                    stagedWristTarget.Basis,
                    stagedWristTarget.Origin);
                if (fixture.RightHand.TryGetPendingLastCommandedApproachTransform(out Transform3D commanded))
                {
                    float overshoot = commanded.Origin.DistanceTo(destinationAttachment.Origin);
                    initialCommandedOvershoot = MathF.Min(initialCommandedOvershoot, overshoot);
                    maximumCommandedOvershoot = MathF.Max(maximumCommandedOvershoot, overshoot);
                }

                if (diagnosticSamples.Count < 200)
                {
                    bool queryCandidate = ((IGrabbable)fixture.Ball).GetGrabPoint(
                        LimbSide.Right,
                        ResolveCanonicalControllerWrist(fixture)) is not null;
                    diagnosticSamples.Add(
                        $"pf={Engine.GetProcessFrames()} cmdOvershoot={maximumCommandedOvershoot:F4} queryCandidate={queryCandidate} attachment={fixture.RightHand.HandBoneAttachment?.GlobalPosition}");
                }
            }

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.True(
                fixture.RightHand.LastPendingGrabAbandonmentReason == HandGrabAbandonmentReason.NonConvergence,
                $"Expected the unreachable destination to abandon for bounded non-convergence within "
                + $"{NonConvergenceBudgetSeconds:F0} s; observed {fixture.RightHand.LastPendingGrabAbandonmentReason}."
                + $"{System.Environment.NewLine}{string.Join(System.Environment.NewLine, diagnosticSamples.TakeLast(40))}");
            Assert.False(fixture.RightHand.LastPendingGrabAbandonedForCandidateLoss);
            Assert.True(
                maximumCommandedOvershoot - initialCommandedOvershoot <= RunawayCommandGrowthBoundMetres,
                $"The commanded approach must not grow while non-convergent; observed growth from "
                + $"{initialCommandedOvershoot:F4} m to {maximumCommandedOvershoot:F4} m beyond the destination.");
            Assert.True(
                maximumCommandedOvershoot <= RunawayCommandBoundMetres,
                $"The commanded approach must stay bounded while non-convergent; observed a maximum "
                + $"{maximumCommandedOvershoot:F4} m overshoot beyond the destination.");
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Same(itemsHolder, fixture.Ball.GetParent());
            Assert.True(
                stationaryBall.Origin.DistanceTo(fixture.Ball.GlobalTransform.Origin) <= StationaryPositionToleranceMetres,
                "The unreachable pending grab must leave the ball stationary — no commit, no magnetism.");
            Assert.False(fixture.RightProvider.IsGrabOverrideActive, "Abandonment must release the provider override.");
            Console.WriteLine(
                "Unreachable controller-ball evidence: shoulder→destination={0:F4} m, envelope={1:F4} m, "
                + "maximum commanded overshoot={2:F4} m.",
                shoulderToDestination,
                armEnvelope,
                maximumCommandedOvershoot);

            relay.TriggerActionButtonReleased("grip_click");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// The same unreachable geometry in optical mode surfaces the bounded abandonment through the coordinator's
    /// neutral evaluation trace with the distinct <c>PendingAbandonedNonConvergence</c> reason (IK-005 TR22).
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpticalBall_UnreachableDestination_AbandonsPendingWithNonConvergenceTraceReason()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Stick.GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);
            fixture.Stick.ForceUpdateTransform();

            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabBallAnimationPath);
            SetExactOpticalWristWorld(
                fixture.Runtime,
                LimbSide.Right,
                new Transform3D(Basis.Identity, _unreachableWristStaging));
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 45);

            // Author the raised ball beside the observed canonical-epoch wrist — input state, not solver output.
            Transform3D stagedWrist = fixture.RightProvider.TryGetSourceIntent(out IKTargetIntent sourceIntent)
                ? sourceIntent.WorldTransform
                : throw new Xunit.Sdk.XunitException("Expected a canonical optical source sample.");
            fixture.Ball.GlobalPosition = stagedWrist.Origin + new Vector3(0.03f, 0.06f, -0.02f);
            fixture.Ball.ForceUpdateTransform();

            var traces = new List<OpticalGrabEvaluationTrace>();
            fixture.Coordinator.OpticalGrabEvaluated += traces.Add;
            Node itemsHolder = fixture.Ball.GetParent() ?? throw new InvalidOperationException("Ball has no initial items parent.");

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
            await WaitUntilPendingAsync(sceneTree, fixture, LimbSide.Right);

            var stopwatch = Stopwatch.StartNew();
            while (fixture.RightHand.GrabLifecycle != HandGrabLifecycleState.None
                   && stopwatch.Elapsed.TotalSeconds < NonConvergenceBudgetSeconds)
            {
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabAbandonmentReason.NonConvergence, fixture.RightHand.LastPendingGrabAbandonmentReason);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Same(itemsHolder, fixture.Ball.GetParent());
            Assert.False(fixture.RightProvider.IsGrabOverrideActive);
            // The abandonment lands in a process frame; the coordinator publishes its trace on the next
            // physics evaluation tick, so let at least one run before asserting the neutral trace.
            await WaitForPhysicsFramesAsync(sceneTree, 3);
            Assert.Contains(
                traces,
                trace => trace.Side == LimbSide.Right
                    && trace.Edge == GripEdge.None
                    && trace.LifecycleBefore == HandGrabLifecycleState.Pending
                    && trace.LifecycleAfter == HandGrabLifecycleState.None
                    && trace.Reason == OpticalGrabEvaluationReason.PendingAbandonedNonConvergence
                    && trace.AttemptID is not null);

            // Stop the still-closed grip from immediately re-entering recognition before disposal.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 5);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Mirrors the labelled non-centre stick comparison screenshot. The fixture-only raised stick remains selected at
    /// a non-centre contact, but its real VRIK bone attachment stays above the 8 mm direct position gate while the
    /// orientation remains inside 5 degrees; it therefore stays pending, stationary, unparented, and overridden.
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpticalNonCentreStickAbove8mmGate_RemainsPendingAndStationary()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabPipeAnimationPath);
            fixture.Ball.GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);
            fixture.Stick.GlobalTransform = new Transform3D(fixture.Stick.GlobalTransform.Basis, _stickPendingGatePosition);
            fixture.Stick.ForceUpdateTransform();
            SetExactOpticalWristWorld(
                fixture.Runtime,
                LimbSide.Right,
                new Transform3D(Basis.Identity, _rightWristStickPendingGate));
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 65);

            GrabPointCandidate candidate = ((IGrabbable)fixture.Stick).GetGrabPoint(
                LimbSide.Right,
                fixture.RightHand.HandTargetNode?.GlobalTransform ?? Transform3D.Identity)
                ?? throw new Xunit.Sdk.XunitException("Expected the raised fixture stick to remain reachable.");
            Transform3D expectedAttachment = candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();
            Node itemsHolder = fixture.Stick.GetParent() ?? throw new InvalidOperationException("Stick has no initial items parent.");
            Transform3D stationaryStick = fixture.Stick.GlobalTransform;

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
            var stopwatch = Stopwatch.StartNew();
            while (fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.None && stopwatch.Elapsed.TotalSeconds < 5.0)
            {
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);
            // The gate evidence is observed inside the bounded non-convergence interval (INTR-002 recovery
            // boundaries; IK-005 TR22): the raised fixture stick's approach cannot converge, so the pending
            // grab is legitimately abandoned by the bounded interval or a source transition shortly after
            // this observation window.
            await WaitForPhysicsFramesAsync(sceneTree, 25);

            Transform3D actualAttachment = fixture.RightHand.HandBoneAttachment?.GlobalTransform ?? Transform3D.Identity;
            float positionResidual = expectedAttachment.Origin.DistanceTo(actualAttachment.Origin);
            float orientationResidual = AngularDifferenceDegrees(expectedAttachment.Basis, actualAttachment.Basis);
            Assert.True(
                positionResidual > MovableAttachmentPositionToleranceMetres,
                $"Expected non-centre stick direct attachment residual above 8 mm; observed {positionResidual:F4} m.");
            Assert.InRange(orientationResidual, 0.0f, MovableAttachmentOrientationToleranceDegrees);
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.True(fixture.RightProvider.IsGrabOverrideActive);
            Assert.Same(itemsHolder, fixture.Stick.GetParent());
            AssertTransformPositionAndRotationNear(
                stationaryStick,
                fixture.Stick.GlobalTransform,
                StationaryPositionToleranceMetres,
                0.1f,
                "non-centre stick pending above the 8 mm gate");
            Assert.True(fixture.RightHand.CancelPendingGrab());
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }


    /// <summary>
    /// Scenario: an over-clench beyond the reference articulation while held never releases and never moves
    /// the fingers — the bones stay at the authored pose and must NOT match the projected over-clench pose
    /// (CTRL-002 UR4; XR-002 TR48, TR31). The mismatch guard is the visual anomaly guard: a held hand whose
    /// fingers followed the hidden tracked clench would read as a changing grip in the screenshots.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldBall_OverClench_KeepsAuthoredPoseAndStaysHeld()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabBallAnimationPath);
            _ = await CommitHeldBallAsync(sceneTree, fixture, calibration);
            Quaternion[] heldPose = fixture.CaptureFingerRotations(LimbSide.Right);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex * OverClenchScale);
            await WaitForPhysicsFramesAsync(sceneTree, 40);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                heldPose,
                HeldStableToleranceRadians,
                "held pose through the over-clench");

            // Anomaly guard: the held bones must NOT equal the projected tracked over-clench pose.
            Quaternion[] overClenchProjection = fixture.ProjectCurrentPose(LimbSide.Right);
            Assert.True(
                MaxAngle(fixture.CaptureFingerRotations(LimbSide.Right), overClenchProjection) >= DistinctPoseRadians,
                "Anomaly guard: the held hand must not follow the hidden over-clench projection.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario: a stable aggregate opening releases through the ordinary restoration — item freed and
    /// unparented, arbitration withdrawn, blend-out complete, and the hand resumes tracking the projected
    /// open pose (INTR-002 R59, R61).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldBall_StableOpen_ReleasesAndResumesTrackedPose()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabBallAnimationPath);
            Node itemsHolder = fixture.Ball.GetParent();
            _ = await CommitHeldBallAsync(sceneTree, fixture, calibration);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 30);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Same(itemsHolder, fixture.Ball.GetParent());
            Assert.False(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
            Assert.Equal(
                OpticalGrabPoseBlendPhase.Tracking,
                fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));

            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                fixture.ProjectCurrentPose(LimbSide.Right),
                TrackedMatchToleranceRadians,
                "released hand vs projected open pose");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario: closing near a clearly non-centre point along the stick commits the authored pipe grab
    /// through the same generic recognition path — the grip differs materially from the ball grip, and the
    /// solved hand sits at the offset contact point rather than the stick centre (CTRL-002 TR22; XR-002
    /// TR50). The ball-vs-stick grip mismatch guard is the visual anomaly guard: the two screenshots must
    /// read as visibly different grips.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StickClosure_AtOffsetPoint_CommitsPipePoseDistinctFromBall()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.StartOpticalAtRestAsync(sceneTree);
            FlexCalibration calibration = new(AuthoredPipeClosedFlex, AuthoredPipeOpenFlex);

            fixture.Stick.GlobalTransform = _reachableStickTransform;
            fixture.Stick.ForceUpdateTransform();
            // Static competitor placement: both candidates are eligible at the authored query, with the stick
            // slightly closer by construction. These values are frozen before the lifecycle begins.
            fixture.Ball.GlobalPosition = _reachableStickWrist + new Vector3(0.0795f, 0.0f, 0.0f);
            fixture.Ball.ForceUpdateTransform();

            SetExactOpticalWristWorld(
                fixture.Runtime,
                LimbSide.Right,
                new Transform3D(Basis.Identity, _reachableStickWrist));
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 90);

            Assert.True(
                fixture.RightHand.TryGetCurrentGrabCandidate(out GrabCandidateObservation? candidate),
                "Expected the stick to be the observed best candidate at the offset position.");
            Transform3D selectionHand = fixture.RightHand.HandTargetNode?.GlobalTransform ?? Transform3D.Identity;
            GrabPointCandidate? ballCandidate = ((IGrabbable)fixture.Ball).GetGrabPoint(LimbSide.Right, selectionHand);
            GrabPointCandidate? stickCandidate = ((IGrabbable)fixture.Stick).GetGrabPoint(LimbSide.Right, selectionHand);
            Assert.True(
                stickCandidate is not null
                && (ballCandidate is null || stickCandidate.AcquisitionDistance < ballCandidate.AcquisitionDistance),
                $"Authored stick waypoint must make stick objectively closest: hand={selectionHand.Origin}, "
                + $"stick={stickCandidate?.AcquisitionDistance.ToString("F4") ?? "ineligible"}, "
                + $"ball={ballCandidate?.AcquisitionDistance.ToString("F4") ?? "ineligible"}, "
                + $"stickTransform={fixture.Stick.GlobalTransform}.");
            Assert.Equal("Grab-pipe-10", candidate?.Animation?.ResourceName);

            GrabCommitTrace trace = await DriveAuthoredCommitInputAsync(
                sceneTree,
                fixture,
                calibration,
                _reachableStickWrist,
                _reachableStickWrist,
                fixture.Stick,
                "non-centre stick",
                Basis.Identity);
            Assert.True(
                await WaitForBlendPhaseAsync(
                    sceneTree,
                    fixture.Modifier,
                    LimbSide.Right,
                    OpticalGrabPoseBlendPhase.HeldSuppressed,
                    budgetSeconds: 5.0),
                "Expected the stick commit blend window to complete.");
            // Let the AnimationTree's 0.2 s pose-weight transition finish applying the authored keys.
            await WaitForFramesAsync(sceneTree, 15);

            Assert.Same(fixture.Stick, fixture.RightHand.CurrentGrabbed);
            Assert.Equal("Grab-pipe-10", fixture.RightHand.CurrentPose?.ResourceName);
            Assert.Equal(
                OpticalGrabPoseBlendPhase.HeldSuppressed,
                fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                SampleAuthoredReference(LimbSide.Right, GrabPipeAnimationPath),
                AuthoredMatchToleranceRadians,
                "held right hand vs authored pipe reference");

            // Anomaly guard: the authored pipe and ball grips must differ materially.
            Assert.True(
                MaxAngle(
                    SampleAuthoredReference(LimbSide.Right, GrabPipeAnimationPath),
                    SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath)) >= DistinctPoseRadians,
                "Anomaly guard: the pipe grip must differ materially from the ball grip.");

            // Offset guard: the solved hand grips at the contact point, not the stick centre.
            Vector3 handAttachment = fixture.RightHand.HandBoneAttachment?.GlobalPosition ?? Vector3.Zero;
            Vector3 authoredStickContact = stickCandidate!.GrabPointTransform.Origin;
            Assert.True(
                handAttachment.DistanceTo(authoredStickContact) + 0.02f
                    <= handAttachment.DistanceTo(fixture.Stick.GlobalPosition),
                $"The held hand must sit closer to the offset contact point ({handAttachment.DistanceTo(authoredStickContact):F3}) " +
                    $"than to the stick centre ({handAttachment.DistanceTo(fixture.Stick.GlobalPosition):F3}).");

            SetExactOpticalWristWorld(
                fixture.Runtime,
                LimbSide.Right,
                new Transform3D(Basis.Identity, _reachableStickHeldMove));
            await WaitForPhysicsFramesAsync(sceneTree, 12);
            Assert.Same(fixture.Stick, fixture.RightHand.CurrentGrabbed);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            for (int frame = 0; frame < 50; frame++)
            {
                await WaitForPhysicsFramesAsync(sceneTree, 1);
                await WaitForNextFrameAsync(sceneTree);
                trace.Samples.Add(fixture.CaptureTemporalSample(fixture.Stick));
                if (fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.None)
                {
                    break;
                }
            }

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            AssertCompleteLifecycleEvidence(trace, "stick");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario: invalidating the right hand's optical observations while held preserves the grab — still
    /// held, item retained, arbitration unchanged, finger pose fixed at the authored reference — and recovery
    /// while still closed keeps holding (INTR-002 R59; XR-002 TR51).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldBall_TrackingLoss_PreservesGrabAndPose()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabBallAnimationPath);
            _ = await CommitHeldBallAsync(sceneTree, fixture, calibration);
            Quaternion[] heldPose = fixture.CaptureFingerRotations(LimbSide.Right);

            foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
            {
                fixture.Runtime.ClearHandJointSample(LimbSide.Right, joint);
            }

            await WaitForPhysicsFramesAsync(sceneTree, 40);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                heldPose,
                HeldStableToleranceRadians,
                "held pose through the tracking loss");

            // Recovery while still closed keeps the hold.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 40);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario: an explicit committed mode switch to Controller while held releases the optical grab — item
    /// freed, provenance cleared, arbitration withdrawn (INTR-002 R58, R59).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldBall_ModeSwitchToController_Releases()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabBallAnimationPath);
            _ = await CommitHeldBallAsync(sceneTree, fixture, calibration);

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller);
            await WaitForPhysicsFramesAsync(sceneTree, 30);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Equal(HandGrabInputSource.None, fixture.RightHand.GrabInputSource);
            Assert.False(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
            Assert.Equal(XRHandTrackingMode.Controller, fixture.Runtime.HandTrackingMode);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario: the two hands operate independently — the right hand holds the ball at its fixed authored
    /// pose while the left hand tracks an open pose moved to a new wrist position and stays idle (CTRL-002
    /// UR5; XR-002 TR22). The left-vs-right mismatch guard is the visual anomaly guard: the two hand
    /// cameras must read as grip versus open hand.
    /// </summary>
    [Headless]
    [Fact]
    public async Task IndependentHands_RightHeldWhileLeftTracksMovedPose()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            FlexCalibration calibration = await fixture.CommitOpticalAtRestAsync(sceneTree, GrabBallAnimationPath);
            _ = await CommitHeldBallAsync(sceneTree, fixture, calibration);
            Quaternion[] authoredBall = SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath);

            SetExactOpticalWristWorld(fixture.Runtime, LimbSide.Left, new Transform3D(Basis.Identity, _leftWristMoved));
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 45);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                authoredBall,
                AuthoredMatchToleranceRadians,
                "right held pose while the left hand moves");

            Assert.Equal(HandGrabLifecycleState.None, fixture.LeftHand.GrabLifecycle);
            Assert.Null(fixture.LeftHand.CurrentGrabbed);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Left),
                fixture.ProjectCurrentPose(LimbSide.Left),
                TrackedMatchToleranceRadians,
                "left hand vs its projected open pose");

            // Anomaly guard: the held right-hand pose and the tracking left-hand pose differ materially.
            Assert.True(
                MaxAngle(
                    fixture.CaptureFingerRotations(LimbSide.Right),
                    fixture.CaptureFingerRotations(LimbSide.Left)) >= DistinctPoseRadians,
                "Anomaly guard: the held right hand and the tracking left hand must read as different poses.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Commits the optical ball grab from rest wrists, waits out the commit blend window, and returns the
    /// captured temporal trace for boundary assertions.
    /// </summary>
    private static async Task<GrabCommitTrace> CommitHeldBallAsync(
        SceneTree sceneTree,
        PhotoboothFixture fixture,
        FlexCalibration calibration)
    {
        GrabCommitTrace trace = await DriveAuthoredCommitInputAsync(
            sceneTree,
            fixture,
            calibration,
            _rightWristRest,
            _rightWristHeld,
            fixture.Ball,
            "ball",
            Basis.Identity);

        // The commit blend window (0.2 s) must complete into zero-write suppression before any pose
        // authority or stability assertion.
        Assert.True(
            await WaitForBlendPhaseAsync(
                sceneTree,
                fixture.Modifier,
                LimbSide.Right,
                OpticalGrabPoseBlendPhase.HeldSuppressed,
                budgetSeconds: 5.0),
            "Expected the commit blend window to complete into held suppression.");
        await WaitForFramesAsync(sceneTree, 3);
        return trace;
    }

    private static async Task<GrabCommitTrace> DriveAuthoredCommitInputAsync(
        SceneTree sceneTree,
        PhotoboothFixture fixture,
        FlexCalibration calibration,
        Vector3 wristStart,
        Vector3 wristEnd,
        Node3D item,
        string context,
        Basis wristBasis)
    {
        const int closureRampFrames = 10;
        const int wristTrajectoryDelayFrames = 18;
        const int wristTrajectoryFrames = 30;
        const int requiredInitialHeldFrames = 6;
        const int maximumAuthoredFrames = 180;
        var samples = new List<GrabTemporalSample>();
        int heldFrames = 0;

        // Freeze the complete trial input before execution. No candidate, provider, solver, attachment, or
        // lifecycle observation can alter a later wrist or articulation sample.
        AuthoredGrabInput[] authoredInputs = [.. Enumerable.Range(0, maximumAuthoredFrames).Select(frame =>
        {
            float closureAlpha = Mathf.Clamp((frame + 1.0f) / closureRampFrames, 0.0f, 1.0f);
            float wristAlpha = Mathf.Clamp(
                (frame + 1.0f - wristTrajectoryDelayFrames) / wristTrajectoryFrames,
                0.0f,
                1.0f);
            return new AuthoredGrabInput(
                new Transform3D(wristBasis, wristStart.Lerp(wristEnd, wristAlpha)),
                Mathf.Lerp(calibration.OpenFlex, calibration.ClosedFlex, closureAlpha));
        })];

        foreach (AuthoredGrabInput input in authoredInputs)
        {
            SetExactOpticalWristWorld(fixture.Runtime, LimbSide.Right, input.Wrist);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, input.Flex);
            await WaitForPhysicsFramesAsync(sceneTree, 1);
            await WaitForNextFrameAsync(sceneTree);
            samples.Add(fixture.CaptureTemporalSample(item));

            heldFrames = fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.Held ? heldFrames + 1 : 0;
            if (heldFrames >= requiredInitialHeldFrames)
            {
                break;
            }
        }

        GrabCommitTrace trace = new(samples);
        Assert.True(
            trace.Samples.Any(sample => sample.Lifecycle == HandGrabLifecycleState.Pending),
            $"The independently authored {context} closure must enter Pending.\n{trace.Format()}");
        Assert.True(
            fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.Held,
            $"The independently authored {context} input did not commit; no feedback correction was applied.\n{trace.Format()}");
        Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);
        Assert.True(trace.HasExpectedAttachment, $"The {context} trace never observed the Movable attachment target.\n{trace.Format()}");

        return trace;
    }

    private readonly record struct AuthoredGrabInput(Transform3D Wrist, float Flex);

    private static async Task WaitUntilPendingAsync(SceneTree sceneTree, PhotoboothFixture fixture, LimbSide side)
    {
        HandPoseBehaviour hand = side == LimbSide.Left ? fixture.LeftHand : fixture.RightHand;
        var stopwatch = Stopwatch.StartNew();
        while (hand.GrabLifecycle == HandGrabLifecycleState.None && stopwatch.Elapsed.TotalSeconds < 5.0)
        {
            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        Assert.Equal(HandGrabLifecycleState.Pending, hand.GrabLifecycle);
    }

    private static async Task<bool> WaitForBlendPhaseAsync(
        SceneTree sceneTree,
        OpticalFingerTrackingModifier modifier,
        LimbSide side,
        OpticalGrabPoseBlendPhase phase,
        double budgetSeconds)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed.TotalSeconds < budgetSeconds)
        {
            if (modifier.GetGrabPoseBlendPhase(side) == phase)
            {
                return true;
            }

            await WaitForPhysicsFramesAsync(sceneTree, 1);
            await WaitForNextFrameAsync(sceneTree);
        }

        return modifier.GetGrabPoseBlendPhase(side) == phase;
    }

    /// <summary>
    /// Drives the effective controller wrist — the canonical-epoch source sample the pipeline actuates
    /// against — along an authored trajectory. The XR origin swings while the body leans, so the raw node
    /// global is corrected every frame until the canonical sample tracks the authored target, mirroring the
    /// optical driver's per-frame wrist injection.
    /// </summary>
    private static async Task DriveControllerWristToCanonicalAsync(
        SceneTree sceneTree,
        PhotoboothFixture fixture,
        Transform3D target,
        int frames)
    {
        var relay = (MockXRHandControllerNode)fixture.Runtime.RightHandController;
        Transform3D startEpoch = ResolveCanonicalControllerWrist(fixture);
        for (int frame = 1; frame <= frames; frame++)
        {
            float alpha = frame / (float)frames;
            Vector3 desiredEpochOrigin = startEpoch.Origin.Lerp(target.Origin, alpha);
            Basis desiredEpochBasis = startEpoch.Basis.Slerp(target.Basis, alpha).Orthonormalized();
            await StepControllerWristToCanonicalAsync(sceneTree, fixture, relay, desiredEpochBasis, desiredEpochOrigin);
        }
    }

    /// <summary>
    /// Holds the controller wrist node at a fixed authored world pose, re-pinning every frame against the
    /// XR-origin swing while the body leans during the approach.
    /// </summary>
    private static async Task HoldControllerWristAtNodePoseAsync(
        SceneTree sceneTree,
        PhotoboothFixture fixture,
        MockXRHandControllerNode relay,
        Transform3D wrist,
        int frames)
    {
        _ = fixture;
        for (int frame = 0; frame < frames; frame++)
        {
            relay.HandPositionNode.GlobalTransform = wrist;
            await WaitForPhysicsFramesAsync(sceneTree, 1);
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>Holds the effective controller wrist at a fixed authored canonical-epoch pose.</summary>
    private static async Task HoldControllerWristAtCanonicalAsync(
        SceneTree sceneTree,
        PhotoboothFixture fixture,
        Transform3D wrist,
        int frames)
    {
        var relay = (MockXRHandControllerNode)fixture.Runtime.RightHandController;
        for (int frame = 0; frame < frames; frame++)
        {
            await StepControllerWristToCanonicalAsync(sceneTree, fixture, relay, wrist.Basis, wrist.Origin);
        }
    }

    private static async Task StepControllerWristToCanonicalAsync(
        SceneTree sceneTree,
        PhotoboothFixture fixture,
        MockXRHandControllerNode relay,
        Basis desiredEpochBasis,
        Vector3 desiredEpochOrigin)
    {
        Transform3D epoch = ResolveCanonicalControllerWrist(fixture);
        Transform3D node = relay.HandPositionNode.GlobalTransform;
        Vector3 correctedNodeOrigin = node.Origin + desiredEpochOrigin - epoch.Origin;
        relay.HandPositionNode.GlobalTransform = new Transform3D(desiredEpochBasis, correctedNodeOrigin);
        await WaitForPhysicsFramesAsync(sceneTree, 1);
        await WaitForNextFrameAsync(sceneTree);
    }

    /// <summary>
    /// Reads the right controller wrist at the canonical source-sampling epoch. The raw node global transform
    /// is phase-dependent (the XR origin moves across VRIK stages), so candidate queries consume the same
    /// canonical snapshot the pipeline actuates against (IK-005 TR19).
    /// </summary>
    private static Transform3D ResolveCanonicalControllerWrist(PhotoboothFixture fixture)
        => fixture.RightProvider.TryGetSourceIntent(out IKTargetIntent sourceIntent)
            ? sourceIntent.WorldTransform
            : throw new Xunit.Sdk.XunitException(
                "Expected the photobooth grab provider to expose a canonical controller source sample.");

    /// <summary>
    /// Measures the usable right-arm envelope from skeleton rest poses and the current shoulder position — the
    /// same measurement the retired controller diagnostic used for reach qualification.
    /// </summary>
    private static (Vector3 Shoulder, float ArmEnvelope) MeasureRightArmReach(PhotoboothFixture fixture)
    {
        Skeleton3D skeleton = fixture.PlayerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        int shoulderBone = skeleton.FindBone("RightUpperArm");
        int lowerArmBone = skeleton.FindBone("RightLowerArm");
        int handBone = skeleton.FindBone("RightHand");
        Assert.True(shoulderBone >= 0 && lowerArmBone >= 0 && handBone >= 0, "Expected the right-arm bones.");
        Vector3 shoulder = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(shoulderBone).Origin;
        float armEnvelope = skeleton.GetBoneGlobalRest(shoulderBone).Origin.DistanceTo(skeleton.GetBoneGlobalRest(lowerArmBone).Origin)
                           + skeleton.GetBoneGlobalRest(lowerArmBone).Origin.DistanceTo(skeleton.GetBoneGlobalRest(handBone).Origin);
        Assert.InRange(armEnvelope, 0.4f, 0.5f);
        return (shoulder, armEnvelope);
    }

    /// <summary>
    /// Asserts adjacent item/hand continuity for every sample from the given index onwards, covering held
    /// movement and explicit release boundaries that sit outside the commit-transition helper's window.
    /// </summary>
    private static void AssertAdjacentSamplesContinuousAfterIndex(
        GrabCommitTrace trace,
        int startIndex,
        float positionToleranceMetres,
        float orientationToleranceDegrees,
        string context)
    {
        Assert.True(startIndex > 0 && startIndex < trace.Samples.Count, $"No samples after {context} to inspect.");
        for (int index = startIndex + 1; index < trace.Samples.Count; index++)
        {
            GrabTemporalSample previous = trace.Samples[index - 1];
            GrabTemporalSample current = trace.Samples[index];
            float itemDistance = previous.ItemTransform.Origin.DistanceTo(current.ItemTransform.Origin);
            float itemAngle = AngularDifferenceDegrees(previous.ItemTransform.Basis, current.ItemTransform.Basis);
            float handDistance = previous.ActualAttachment.Origin.DistanceTo(current.ActualAttachment.Origin);
            float handAngle = AngularDifferenceDegrees(previous.ActualAttachment.Basis, current.ActualAttachment.Basis);
            Assert.True(
                itemDistance <= positionToleranceMetres
                && itemAngle <= orientationToleranceDegrees
                && handDistance <= positionToleranceMetres
                && handAngle <= orientationToleranceDegrees,
                $"{context} discontinuity at sample {index}: item={itemDistance:F5} m/{itemAngle:F3}°, "
                + $"hand={handDistance:F5} m/{handAngle:F3}°.");
        }
    }


    internal static void SetExactOpticalWristWorld(MockXRRuntimeNode runtime, LimbSide side, Transform3D worldWrist)
    {
        Transform3D anchor = Transform3D.Identity;
        if (runtime.GetNodeOrNull<Node3D>($"{(side == LimbSide.Right ? "Right" : "Left")}OpticalHand/WristAnchor/OpticalHandAnchor")
            is { } seededAnchor)
        {
            anchor = seededAnchor.Transform;
        }

        float worldScale = runtime.WorldScale;
        Transform3D scaledOrigin = runtime.OriginNode.GlobalTransform
            .Scaled(new Vector3(worldScale, worldScale, worldScale));
        runtime.SetOpticalWristSample(side, scaledOrigin.AffineInverse() * (worldWrist * anchor.AffineInverse()));
    }

    /// <summary>
    /// Deterministic flex-ramp synthesis identical to the coordinator fixtures: each joint is injected as
    /// <c>wristRotation * cumulativeLocal</c> so wrist rotation, metacarpal offsets, and chain state all cancel
    /// in the parent-relative quotient that supplies <c>S</c> (XR-002 TR17, TR20).
    /// </summary>
    internal static void InjectTrackedHandPose(MockXRRuntimeNode runtime, LimbSide side, float flexScale)
    {
        float wristYaw = side == LimbSide.Left ? LeftWristYawRadians : RightWristYawRadians;
        Basis wristRotation = Basis.Identity.Rotated(Vector3.Up, wristYaw);
        Vector3 wristOrigin = new(side == LimbSide.Left ? -0.25f : 0.25f, 1.0f, -0.35f);

        runtime.SetHandJointSample(side, XRHandJoint.Wrist, new Transform3D(wristRotation, wristOrigin));

        Dictionary<XRHandJoint, Basis> cumulative = [];
        foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
        {
            if (joint == XRHandJoint.Wrist)
            {
                cumulative[joint] = Basis.Identity;
                continue;
            }

            var local = new Quaternion(Vector3.Right, JointLocalFlexRadians(joint) * flexScale);
            Basis localBasis = new(local);
            XRHandJoint parent = OpticalFingerTrackingTestTopology.GetTrackedParent(joint)
                ?? throw new InvalidOperationException($"Joint {joint} unexpectedly lacks a source parent.");
            cumulative[joint] = cumulative[parent] * localBasis;

            Vector3 translation = wristOrigin + new Vector3(0.0f, -0.03f * (int)joint, 0.02f);
            runtime.SetHandJointSample(side, joint, new Transform3D(wristRotation * cumulative[joint], translation));
        }
    }

    internal static Quaternion[] SampleAuthoredReference(LimbSide side, string animationPath)
    {
        Animation animation = ResourceLoader.Load<Animation>(animationPath)
            ?? throw new InvalidOperationException($"Failed to load grab animation '{animationPath}'.");
        Assert.True(
            AuthoredHandPoseReferenceSampler.TrySample(animation, side, out AuthoredHandPoseSideReference reference, out string error),
            $"Authored reference sampling failed for '{animationPath}': {error}");

        return reference.Poses.ToArray();
    }

    private static float MaxAngle(Quaternion[] first, Quaternion[] second)
    {
        Assert.Equal(first.Length, second.Length);
        float maxAngle = 0f;
        for (int index = 0; index < first.Length; index++)
        {
            // Hemisphere-align the expected side to the observed one: equivalent rotations may carry
            // opposite signs.
            Quaternion aligned = FingerRetargetingMath.StabiliseRotationHemisphere(second[index], first[index]);
            maxAngle = MathF.Max(maxAngle, first[index].AngleTo(aligned));
        }

        return maxAngle;
    }

    private static float MeanAngle(Quaternion[] first, Quaternion[] second)
    {
        Assert.Equal(first.Length, second.Length);
        float totalAngle = 0f;
        for (int index = 0; index < first.Length; index++)
        {
            Quaternion aligned = FingerRetargetingMath.StabiliseRotationHemisphere(second[index], first[index]);
            totalAngle += first[index].AngleTo(aligned);
        }

        return totalAngle / first.Length;
    }

    private static void AssertMaxAngleAtMost(Quaternion[] actual, Quaternion[] expected, float tolerance, string context)
    {
        float maxAngle = MaxAngle(actual, expected);
        Assert.True(
            maxAngle <= tolerance,
                $"Expected near-exact rotations for {context}; max angle {maxAngle:R} rad exceeds {tolerance:R} rad.");
    }

    private static void AssertDirectAttachmentWithinMovableGate(
        Transform3D expectedAttachment,
        Transform3D actualAttachment,
        string context)
    {
        float positionResidual = expectedAttachment.Origin.DistanceTo(actualAttachment.Origin);
        float orientationResidual = AngularDifferenceDegrees(expectedAttachment.Basis, actualAttachment.Basis);
        Assert.True(
            positionResidual <= MovableAttachmentPositionToleranceMetres,
            $"Expected {context} direct attachment position residual within 8 mm; observed {positionResidual:F4} m.");
        Assert.InRange(orientationResidual, 0.0f, MovableAttachmentOrientationToleranceDegrees);
    }

    private static void AssertTransformPositionAndRotationNear(
        Transform3D expected,
        Transform3D actual,
        float positionToleranceMetres,
        float orientationToleranceDegrees,
        string context)
    {
        float positionResidual = expected.Origin.DistanceTo(actual.Origin);
        float orientationResidual = AngularDifferenceDegrees(expected.Basis, actual.Basis);
        Assert.True(
            positionResidual <= positionToleranceMetres,
            $"Expected {context} position residual <= {positionToleranceMetres:F4} m; observed {positionResidual:F4} m.");
        Assert.True(
            orientationResidual <= orientationToleranceDegrees,
            $"Expected {context} orientation residual <= {orientationToleranceDegrees:F2}°; observed {orientationResidual:F2}°.");
    }

    private static float AngularDifferenceDegrees(Basis expected, Basis actual)
    {
        Quaternion expectedRotation = new(expected.Orthonormalized());
        Quaternion actualRotation = new(actual.Orthonormalized());
        float absoluteDot = Mathf.Clamp(MathF.Abs(expectedRotation.Dot(actualRotation)), 0.0f, 1.0f);
        return Mathf.RadToDeg(2.0f * MathF.Acos(absoluteDot));
    }

    private static float JointLocalFlexRadians(XRHandJoint joint)
        => joint switch
        {
            XRHandJoint.ThumbMetacarpal => 0.21f,
            XRHandJoint.ThumbProximal => 0.34f,
            XRHandJoint.ThumbDistal => 0.42f,
            XRHandJoint.IndexMetacarpal => 0.12f,
            XRHandJoint.IndexProximal => 0.30f,
            XRHandJoint.IndexIntermediate => 0.51f,
            XRHandJoint.IndexDistal => 0.40f,
            XRHandJoint.MiddleMetacarpal => 0.10f,
            XRHandJoint.MiddleProximal => 0.27f,
            XRHandJoint.MiddleIntermediate => 0.48f,
            XRHandJoint.MiddleDistal => 0.37f,
            XRHandJoint.RingMetacarpal => 0.11f,
            XRHandJoint.RingProximal => 0.24f,
            XRHandJoint.RingIntermediate => 0.45f,
            XRHandJoint.RingDistal => 0.34f,
            XRHandJoint.LittleMetacarpal => 0.09f,
            XRHandJoint.LittleProximal => 0.18f,
            XRHandJoint.LittleIntermediate => 0.39f,
            XRHandJoint.LittleDistal => 0.28f,
            XRHandJoint.Wrist => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a finger joint."),
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a finger joint."),
        };

    private static void AssertCommitTransitionIsContinuous(GrabCommitTrace trace)
    {
        int heldIndex = trace.Samples.FindIndex(sample => sample.Lifecycle == HandGrabLifecycleState.Held);
        Assert.True(heldIndex >= 3, $"The trace must include at least three Pending samples before Held.\n{trace.Format()}");
        Assert.True(
            trace.Samples.Skip(heldIndex - 3).Take(3).All(sample => sample.Lifecycle == HandGrabLifecycleState.Pending),
            $"The trace must retain the last three adjacent Pending samples.\n{trace.Format()}");
        Assert.True(
            trace.Samples.Count - heldIndex >= 6,
            $"The trace must include the first six Held samples after commit.\n{trace.Format()}");
        Assert.True(trace.Samples[heldIndex - 1].OverrideActive, $"The final Pending sample must retain the override.\n{trace.Format()}");
        Assert.False(trace.Samples[heldIndex].OverrideActive, $"The first Held sample must observe override clear.\n{trace.Format()}");
        Assert.NotEqual(trace.Samples[heldIndex - 1].ItemParent, trace.Samples[heldIndex].ItemParent);

        float maximumItemDistance = 0.0f;
        float maximumItemAngle = 0.0f;
        float maximumHandDistance = 0.0f;
        float maximumHandAngle = 0.0f;
        for (int index = 1; index < trace.Samples.Count; index++)
        {
            GrabTemporalSample previous = trace.Samples[index - 1];
            GrabTemporalSample current = trace.Samples[index];
            float itemDistance = previous.ItemTransform.Origin.DistanceTo(current.ItemTransform.Origin);
            float itemAngle = AngularDifferenceDegrees(previous.ItemTransform.Basis, current.ItemTransform.Basis);
            float handDistance = previous.ActualAttachment.Origin.DistanceTo(current.ActualAttachment.Origin);
            float handAngle = AngularDifferenceDegrees(previous.ActualAttachment.Basis, current.ActualAttachment.Basis);

            maximumItemDistance = MathF.Max(maximumItemDistance, itemDistance);
            maximumItemAngle = MathF.Max(maximumItemAngle, itemAngle);
            maximumHandDistance = MathF.Max(maximumHandDistance, handDistance);
            maximumHandAngle = MathF.Max(maximumHandAngle, handAngle);
        }

        GrabTemporalSample alignmentBefore = trace.Samples[heldIndex - 1];
        GrabTemporalSample alignmentAfter = trace.Samples[heldIndex];
        GrabTemporalSample firstAfterClear = trace.Samples[heldIndex + 1];
        float alignmentItemDistance = alignmentBefore.ItemTransform.Origin.DistanceTo(alignmentAfter.ItemTransform.Origin);
        float alignmentHandDistance = alignmentBefore.ActualAttachment.Origin.DistanceTo(alignmentAfter.ActualAttachment.Origin);
        float postClearItemDistance = alignmentAfter.ItemTransform.Origin.DistanceTo(firstAfterClear.ItemTransform.Origin);
        float postClearHandDistance = alignmentAfter.ActualAttachment.Origin.DistanceTo(firstAfterClear.ActualAttachment.Origin);
        bool continuous = maximumItemDistance <= AdjacentItemDiscontinuityToleranceMetres
                          && maximumItemAngle <= AdjacentItemDiscontinuityToleranceDegrees
                          && maximumHandDistance <= AdjacentHandDiscontinuityToleranceMetres
                          && maximumHandAngle <= AdjacentHandDiscontinuityToleranceDegrees;
        Assert.True(
            continuous,
            $"Adjacent discontinuity classification: max item={maximumItemDistance:F5} m/{maximumItemAngle:F3}°, "
            + $"max hand={maximumHandDistance:F5} m/{maximumHandAngle:F3}°; alignment/reparent item={alignmentItemDistance:F5} m, "
            + $"hand={alignmentHandDistance:F5} m; first post-clear item={postClearItemDistance:F5} m, "
            + $"hand={postClearHandDistance:F5} m.\n{trace.Format()}");
    }

    private static void AssertCompleteLifecycleEvidence(GrabCommitTrace trace, string context)
    {
        Assert.Contains(trace.Samples, sample => sample.Lifecycle == HandGrabLifecycleState.None);
        Assert.Contains(trace.Samples, sample => sample.Lifecycle == HandGrabLifecycleState.Pending);
        Assert.Contains(trace.Samples, sample => sample.Lifecycle == HandGrabLifecycleState.Held);
        Assert.Contains(
            trace.Samples,
            sample => sample.Edge == GripEdge.Release
                && sample.LifecycleBefore == HandGrabLifecycleState.Held
                && sample.Lifecycle == HandGrabLifecycleState.None);
        ulong[] evaluationIDs = [.. trace.Samples.Select(sample => sample.EvaluationID).Where(id => id > 0)];
        Assert.True(evaluationIDs.SequenceEqual(evaluationIDs.Order()), $"{context} evaluation IDs must be monotonic.");
        ulong? attemptID = trace.Samples.First(sample => sample.AttemptID.HasValue).AttemptID;
        Assert.All(
            trace.Samples.Where(sample => sample.Lifecycle is HandGrabLifecycleState.Pending or HandGrabLifecycleState.Held),
            sample => Assert.Equal(attemptID, sample.AttemptID));
    }

    private sealed record GrabCommitTrace(List<GrabTemporalSample> Samples)
    {
        public bool HasExpectedAttachment => Samples.Any(sample => sample.ExpectedAttachment.HasValue);

        public Transform3D ExpectedAttachment => Samples
            .First(sample => sample.ExpectedAttachment.HasValue)
            .ExpectedAttachment!.Value;

        public string Format()
            => string.Join(System.Environment.NewLine, Samples.Select((sample, index) => $"[{index:D3}] {sample}"));
    }

    private sealed record GrabTemporalSample(
        ulong ProcessFrame,
        ulong PhysicsTick,
        ulong EvaluationID,
        ulong? AttemptID,
        GripEdge Edge,
        HandGrabLifecycleState LifecycleBefore,
        HandGrabLifecycleState Lifecycle,
        string Candidate,
        Transform3D CalibratedWrist,
        Transform3D ProviderCommand,
        IKTargetIntent ProviderOutput,
        Transform3D ActuatorRequested,
        Transform3D ActuatorRealised,
        IKTargetPipelineFeedback ActuatorFeedback,
        Transform3D? ExpectedAttachment,
        Transform3D ActualAttachment,
        Transform3D ItemTransform,
        float FingerBlend,
        OpticalGrabPoseBlendPhase PresentationPhase,
        string ItemParent,
        bool OverrideActive)
    {
        public override string ToString()
            => $"pf={ProcessFrame} pt={PhysicsTick} eval={EvaluationID} attempt={AttemptID?.ToString() ?? "-"} "
               + $"edge={Edge} lifecycle={LifecycleBefore}->{Lifecycle} candidate={Candidate} wrist={CalibratedWrist} "
               + $"providerCommand={ProviderCommand} providerOutput={ProviderOutput.WorldTransform}/{ProviderOutput.DesiredInfluence:F2} "
               + $"actuator={ActuatorRequested}->{ActuatorRealised} feedback={ActuatorFeedback.Reason}/"
               + $"{ActuatorFeedback.RequestedToRealisedDelta}/{ActuatorFeedback.ErrorDistance:F5} "
               + $"expected={ExpectedAttachment?.ToString() ?? "-"} actual={ActualAttachment} item={ItemTransform} "
               + $"finger={FingerBlend:F3}/{PresentationPhase} parent={ItemParent} override={OverrideActive}";
    }

    /// <summary>Profile-relative flex scales for one candidate animation (mirrors the coordinator fixtures).</summary>
    private sealed record FlexCalibration(float ClosedFlex, float OpenFlex);

    /// <summary>
    /// Production-shaped fixture: the shared interaction photobooth scene with the installed player rig
    /// (coordinator, modifier, hands, VRIK), the deterministic mock XR runtime exposed through a test game
    /// service provider, and the effective-pose capture probe.
    /// </summary>
    private sealed class PhotoboothFixture : IDisposable
    {
        private PhotoboothFixture(
            TestGame root,
            TestXRManager xrManager,
            MockXRRuntimeNode runtime,
            Node photobooth,
            Node playerRoot,
            RigidBody3D ball,
            RigidBody3D stick,
            HandPoseBehaviour rightHand,
            HandPoseBehaviour leftHand,
            HandGrabTargetProvider rightProvider,
            PlayerVRIK playerVRIK,
            OpticalFingerTrackingModifier modifier,
            int[][] fingerBoneIndices,
            OpticalFingerTrackingModifierIntegrationTests.SkeletonPoseCapture poseCapture)
        {
            Root = root;
            XRManager = xrManager;
            Runtime = runtime;
            Photobooth = photobooth;
            PlayerRoot = playerRoot;
            Ball = ball;
            Stick = stick;
            RightHand = rightHand;
            LeftHand = leftHand;
            RightProvider = rightProvider;
            PlayerVRIK = playerVRIK;
            Modifier = modifier;
            FingerBoneIndices = fingerBoneIndices;
            PoseCapture = poseCapture;
            Coordinator.OpticalGrabEvaluated += trace =>
            {
                if (trace.Side == LimbSide.Right)
                {
                    LastRightEvaluationTrace = trace;
                }
            };
        }

        public TestGame Root
        {
            get;
        }

        public TestXRManager XRManager
        {
            get;
        }

        public MockXRRuntimeNode Runtime
        {
            get;
        }

        public Node Photobooth
        {
            get;
        }

        public Node PlayerRoot
        {
            get;
        }

        public RigidBody3D Ball
        {
            get;
        }

        public RigidBody3D Stick
        {
            get;
        }

        public HandPoseBehaviour RightHand
        {
            get;
        }

        public HandPoseBehaviour LeftHand
        {
            get;
        }

        public HandGrabTargetProvider RightProvider
        {
            get;
        }

        public PlayerVRIK PlayerVRIK
        {
            get;
        }

        public OpticalFingerTrackingModifier Modifier
        {
            get;
        }

        private int[][] FingerBoneIndices
        {
            get;
        }

        private OpticalFingerTrackingModifierIntegrationTests.SkeletonPoseCapture PoseCapture
        {
            get;
        }

        public HandGrabInputCoordinator Coordinator => PlayerRoot.GetNode<HandGrabInputCoordinator>("HandGrabInputCoordinator");

        private OpticalGrabEvaluationTrace? LastRightEvaluationTrace
        {
            get;
            set;
        }

        public GrabTemporalSample CaptureTemporalSample(Node3D item)
        {
            OpticalGrabEvaluationTrace? evaluation = LastRightEvaluationTrace;
            _ = Runtime.GetHandPoseSource(LimbSide.Right)
                .TryGetCalibratedWristTransform(out Transform3D calibratedWrist);
            Transform3D? expectedAttachment = RightHand.TryGetPendingExpectedAttachmentTransform(out Transform3D expected)
                ? expected
                : null;
            IKTargetPipelineResult pipeline = PlayerVRIK.RightHandTargetPipelineDebugState;
            string candidate = RightHand.ActiveGrabAnimation?.ResourceName ?? string.Empty;
            AnimationTree animationTree = PlayerRoot.GetNode<AnimationTree>("AnimationTree");
            float fingerBlend = animationTree
                .Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right))
                .AsSingle();

            return new GrabTemporalSample(
                Engine.GetProcessFrames(),
                Engine.GetPhysicsFrames(),
                evaluation?.EvaluationID ?? 0UL,
                evaluation?.AttemptID,
                evaluation?.Edge ?? GripEdge.None,
                evaluation?.LifecycleBefore ?? RightHand.GrabLifecycle,
                RightHand.GrabLifecycle,
                candidate,
                calibratedWrist,
                RightProvider.GrabTarget,
                RightProvider.LastOutputIntent,
                pipeline.RequestedTarget,
                pipeline.RealisedTarget,
                pipeline.Feedback,
                expectedAttachment,
                RightHand.HandBoneAttachment?.GlobalTransform ?? Transform3D.Identity,
                item.GlobalTransform,
                fingerBlend,
                Modifier.GetGrabPoseBlendPhase(LimbSide.Right),
                item.GetParent()?.GetPath().ToString() ?? string.Empty,
                RightProvider.IsGrabOverrideActive);
        }

        public async Task StartOpticalAtRestAsync(SceneTree sceneTree)
        {
            _ = Runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
            SetExactOpticalWristWorld(Runtime, LimbSide.Right, new Transform3D(Basis.Identity, _rightWristRest));
            SetExactOpticalWristWorld(Runtime, LimbSide.Left, new Transform3D(Basis.Identity, _leftWristRest));
            InjectTrackedHandPose(Runtime, LimbSide.Right, AuthoredTrialOpenFlex);
            InjectTrackedHandPose(Runtime, LimbSide.Left, AuthoredTrialOpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 45);

            Assert.True(Modifier.IsOpticalSessionActive, "Expected the optical session to start for the fixture.");
            Assert.Equal(XRHandTrackingMode.Optical, Runtime.HandTrackingMode);
        }

        /// <summary>
        /// Commits the optical mode with both hands at their rest wrists and an open tracked shape, waits for
        /// the optical session to stage the live binding, then calibrates the candidate ramp.
        /// </summary>
        public async Task<FlexCalibration> CommitOpticalAtRestAsync(SceneTree sceneTree, string animationPath)
        {
            _ = Runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
            SetExactOpticalWristWorld(Runtime, LimbSide.Right, new Transform3D(Basis.Identity, _rightWristRest));
            SetExactOpticalWristWorld(Runtime, LimbSide.Left, new Transform3D(Basis.Identity, _leftWristRest));
            InjectTrackedHandPose(Runtime, LimbSide.Right, 0.15f);
            InjectTrackedHandPose(Runtime, LimbSide.Left, 0.15f);

            await WaitForPhysicsFramesAsync(sceneTree, 45);

            Assert.True(Modifier.IsOpticalSessionActive, "Expected the optical session to start for the fixture.");
            Assert.Equal(XRHandTrackingMode.Optical, Runtime.HandTrackingMode);

            FlexCalibration calibration = Calibrate(LimbSide.Right, animationPath);

            // Calibration sweeps leave high-flex samples injected; restore the open rest shape.
            InjectTrackedHandPose(Runtime, LimbSide.Right, calibration.OpenFlex);
            InjectTrackedHandPose(Runtime, LimbSide.Left, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 20);

            return calibration;
        }

        /// <summary>
        /// Calibrates the closure ramp by projecting injected flex samples through the live binding and
        /// evaluating the production strategy until the aggregate crosses the configured threshold bands.
        /// </summary>
        public FlexCalibration Calibrate(LimbSide side, string animationPath)
        {
            var strategy = new PowerGripRecognitionStrategy();
            Assert.True(
                AuthoredHandPoseReferenceSampler.TrySample(
                    animationPath,
                    side,
                    out AuthoredHandPoseSideReference reference,
                    out string sampleError),
                $"Reference sampling failed for '{animationPath}': {sampleError}");

            Span<Quaternion> neutrals = stackalloc Quaternion[FingerCount];
            Assert.True(
                Modifier.TryCopyOpticalFingerEffectiveNeutrals(side, neutrals),
                "Expected the staged projection binding to expose the side effective neutrals.");
            Assert.True(
                strategy.TryDeriveProfile(reference, neutrals, PowerGripRecognitionSettings.Default, out IGripRecognitionProfile? profile, out string deriveError),
                $"Profile derivation failed for '{animationPath}': {deriveError}");

            PowerGripRecognitionSettings settings = PowerGripRecognitionSettings.Default;
            float closedFlex = -1.0f;
            float openFlex = 0.0f;
            for (int step = 0; step <= 80; step++)
            {
                float flex = step * 0.05f;
                InjectTrackedHandPose(Runtime, side, flex);
                float? score = TryEvaluateScore(strategy, profile!, side);
                Assert.True(score.HasValue, $"Score evaluation failed for '{animationPath}' at flex {flex:R}.");

                if (closedFlex < 0.0f && score!.Value >= MathF.Min(0.95f, settings.GrabThreshold + 0.05f))
                {
                    closedFlex = flex;
                }

                if (score!.Value <= settings.ReleaseThreshold - 0.15f)
                {
                    openFlex = flex;
                }
            }

            Assert.True(closedFlex > 0.0f, $"No injected flex crossed the grab threshold for '{animationPath}'.");
            Assert.True(
                openFlex < closedFlex,
                $"The open flex calibration must sit below the closed flex for '{animationPath}'.");

            return new FlexCalibration(closedFlex, openFlex);
        }

        private float? TryEvaluateScore(PowerGripRecognitionStrategy strategy, IGripRecognitionProfile profile, LimbSide side)
        {
            var samples = new XRHandJointSourceSample[TrackedJointCount];
            for (int jointIndex = 0; jointIndex < TrackedJointCount; jointIndex++)
            {
                _ = Runtime.TryGetJoint(side, (XRHandJoint)jointIndex, out samples[jointIndex]);
            }

            var poses = new OpticalFingerProjectedPose[FingerCount];
            return Modifier.TryProjectOpticalFingers(side, samples, poses)
                && strategy.TryEvaluate(profile, poses, out GripRecognitionEvaluation evaluation)
                ? evaluation.Score
                : null;
        }

        /// <summary>Captures the effective per-frame finger rotations through the skeleton-updated probe.</summary>
        public Quaternion[] CaptureFingerRotations(LimbSide side)
        {
            int[] boneIndices = FingerBoneIndices[(int)side];
            var rotations = new Quaternion[FingerCount];
            for (int fingerIndex = 0; fingerIndex < FingerCount; fingerIndex++)
            {
                rotations[fingerIndex] = PoseCapture.Rotations[boneIndices[fingerIndex]];
            }

            return rotations;
        }

        /// <summary>Projects the currently injected samples through the live binding — the tracking oracle.</summary>
        public Quaternion[] ProjectCurrentPose(LimbSide side)
        {
            var samples = new XRHandJointSourceSample[TrackedJointCount];
            for (int jointIndex = 0; jointIndex < TrackedJointCount; jointIndex++)
            {
                _ = Runtime.TryGetJoint(side, (XRHandJoint)jointIndex, out samples[jointIndex]);
            }

            var poses = new OpticalFingerProjectedPose[FingerCount];
            Assert.True(
                Modifier.TryProjectOpticalFingers(side, samples, poses),
                "Expected the staged binding to project the injected samples.");

            var rotations = new Quaternion[FingerCount];
            for (int fingerIndex = 0; fingerIndex < FingerCount; fingerIndex++)
            {
                Assert.True(poses[fingerIndex].IsValid, $"Expected a valid projection for finger {fingerIndex}.");
                rotations[fingerIndex] = poses[fingerIndex].Rotation;
            }

            return rotations;
        }

        public static async Task<PhotoboothFixture> CreateAsync(SceneTree sceneTree)
        {
            // The integration runner starts test bodies inside TestRuntimeRunner._Ready, where scene-tree
            // attachment does not propagate until the first process frame; wait one frame before attaching.
            await WaitForNextFrameAsync(sceneTree);

            TestGame root = new()
            {
                Name = "OpticalGrabPhotoboothFixture",
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

            Node photobooth = LoadPackedScene(PhotoboothScenePath).Instantiate();
            root.AddChild(photobooth);

            Node playerRoot = photobooth.GetNode("Subject/Player");
            EnsureCharacterRuntimeInstalled(playerRoot);

            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 2);

            // The mock head camera drives the VRIK head target at a natural standing viewpoint; the
            // controller anchors pin the hands at the positions the visual fixture frames.
            if (runtime.GetNodeOrNull<Camera3D>("MainCamera") is { } mainCamera)
            {
                mainCamera.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.0f, 1.62f, 0.0f));
            }

            runtime.RightHandController.HandPositionNode.GlobalTransform =
                new Transform3D(Basis.Identity, _rightWristRest);
            runtime.LeftHandController.HandPositionNode.GlobalTransform =
                new Transform3D(Basis.Identity, _leftWristRest);

            AnimationTree animationTree = playerRoot.GetNode<AnimationTree>("AnimationTree");
            animationTree.Active = true;
            AnimationNodeStateMachinePlayback playback = animationTree
                .Get(HandPoseAnimationTreePaths.GetNestedStateMachinePlaybackParameter())
                .As<AnimationNodeStateMachinePlayback>()
                ?? throw new InvalidOperationException("Fixture AnimationTree lacks the upstream playback.");
            playback.Start(new StringName("StandingCrouching"), true);

            // Bind after installation and animation activation: the installer defers a pose state machine
            // restart that invalidates earlier bindings.
            PlayerVRIK playerVrik = playerRoot.GetNode<PlayerVRIK>("VRIK");
            Assert.True(playerVrik.BindToXRServices(), "Expected the fixture player VRIK to bind to the mock runtime.");

            Skeleton3D skeleton = playerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            OpticalFingerTrackingModifier modifier = Assert.IsType<OpticalFingerTrackingModifier>(
                skeleton.GetNode("OpticalFingerTrackingModifier"),
                exactMatch: false);
            var poseCapture = OpticalFingerTrackingModifierIntegrationTests.SkeletonPoseCapture.Attach(skeleton);

            await WaitForFramesAsync(sceneTree, 40);

            Assert.True(poseCapture.CaptureCount > 0, "Expected the fixture pose capture to run at least once.");
            Assert.True(
                modifier.IsFingerTopologyValid,
                "The photobooth player rig must expose a valid bilateral finger topology.");

            return new PhotoboothFixture(
                root,
                xrManager,
                runtime,
                photobooth,
                playerRoot,
                photobooth.GetNode<RigidBody3D>("Items/Ball"),
                photobooth.GetNode<RigidBody3D>("Items/Stick"),
                playerRoot.GetNode<HandPoseBehaviour>("Hands/RightHand"),
                playerRoot.GetNode<HandPoseBehaviour>("Hands/LeftHand"),
                playerRoot.GetNode<HandGrabTargetProvider>("VRIK/RightHandGrabProvider"),
                playerVrik,
                modifier,
                [
                    ResolveFingerBoneIndices(skeleton, LimbSide.Left),
                    ResolveFingerBoneIndices(skeleton, LimbSide.Right),
                ],
                poseCapture);
        }

        private static int[] ResolveFingerBoneIndices(Skeleton3D skeleton, LimbSide side)
        {
            string prefix = side == LimbSide.Left ? "Left" : "Right";
            int[] boneIndices = new int[FingerCount];
            for (int fingerIndex = 0; fingerIndex < FingerCount; fingerIndex++)
            {
                string boneName = prefix + XRHandJoints.DestinationJoints[fingerIndex];
                boneIndices[fingerIndex] = skeleton.FindBone(boneName);
                Assert.True(boneIndices[fingerIndex] >= 0, $"Expected the fixture skeleton to contain '{boneName}'.");
            }

            return boneIndices;
        }

        public void Dispose()
        {
            if (GodotObject.IsInstanceValid(Root))
            {
                Root.Dispose();
            }
        }

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
        {
            Runtime = runtime;
            runtime.HandTrackingModeChanged += EmitHandTrackingModeChangedSignal;
        }

        private void EmitHandTrackingModeChangedSignal()
            => _ = EmitSignal(SignalName.HandTrackingModeChanged);
    }

    private sealed partial class TestGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
