using System.Reflection;
using System.Text;
using AlleyCat.Control;
using AlleyCat.IK;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Interaction;

/// <summary>
/// Real-scene regression coverage for controller grab input: uses the real player scene, a MockXR topology
/// with a non-trivial XR origin (player away from the world origin, active PlayerVRIK origin compensation),
/// a real test ball, and injected controller grip presses driven through the real
/// <see cref="PlayerController" /> input path.
/// </summary>
/// <remarks>
/// <para>
/// Guards the two stages a controller grab passes through: (1) input level — a grip press reaches
/// <see cref="IHandGrabLifecycle.BeginGrab" /> with <see cref="HandGrabInputSource.Controller" /> provenance
/// regardless of the committed hand-tracking mode (committed <c>Optical</c> never drops physical controller
/// edges), and the following release edge ends the grab it started; and (2) candidate level — the
/// acquisition query transform (the canonical source-epoch snapshot on <see cref="HandGrabTargetProvider" />)
/// and the physical hand target node both yield a grab candidate within the ball's reach.
/// </para>
/// <para>
/// All measurements are recorded into a report appended to every assertion message so a failure prints the full
/// evidence trail.
/// </para>
/// </remarks>
public sealed class ControllerGrabRealSceneRegressionIntegrationTests
{
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";
    private const string TestBallScenePath = "res://assets/items/test_ball.tscn";
    private const float BallReachMetres = 0.12f;

    /// <summary>
    /// Controller-mode scenario: with the player away from the world origin and head sway driving active origin
    /// compensation, a grip press near a real ball must begin a pending grab and the following release edge must
    /// end it, and the recorded query-transform measurements verify that both acquisition query paths — the
    /// canonical source-epoch snapshot and the physical hand target node — yield a candidate within the ball's
    /// reach under identical conditions.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ControllerMode_NonTrivialOrigin_GripPressMeasurements()
    {
        SceneTree sceneTree = GetSceneTree();
        RegressionFixture fixture = await RegressionFixture.CreateAsync(sceneTree, XRHandTrackingMode.Controller);

        try
        {
            await fixture.DriveHeadSwayAsync(sceneTree, swayFrames: 24);
            BallPlacement ball = fixture.PlaceBallNearVisualHand();
            _ = await sceneTree.ToSignal(sceneTree.CreateTimer(0.05), "timeout");

            RegressionMeasurement measurement = fixture.CaptureMeasurement(ball);

            // A/B candidate outcome under identical conditions: physical hand target node (the legacy query
            // source) versus canonical epoch snapshot (the current query source).
            IGrabbable grabbable = ball.Grabbable;
            GrabPointCandidate? physicalTargetCandidate = grabbable.GetGrabPoint(LimbSide.Right, measurement.HandTargetNodeTransform);
            GrabPointCandidate? epochSnapshotCandidate = grabbable.GetGrabPoint(LimbSide.Right, measurement.EpochTransform);

            fixture.EmitGripPress();

            HandGrabLifecycleState lifecycleAfterPress = fixture.RightHand.GrabLifecycle;

            fixture.EmitGripRelease();
            HandGrabLifecycleState lifecycleAfterRelease = fixture.RightHand.GrabLifecycle;

            string report = fixture.FormatReport(
                measurement,
                physicalTargetCandidate,
                epochSnapshotCandidate: epochSnapshotCandidate,
                lifecycleAfterPress,
                lifecycleAfterRelease);

            Assert.True(
                lifecycleAfterPress is HandGrabLifecycleState.Pending or HandGrabLifecycleState.Held,
                $"Expected the controller grip press to begin a grab in Controller mode.\n{report}");

            Assert.True(
                lifecycleAfterRelease == HandGrabLifecycleState.None,
                $"Expected the controller grip release to end the grab it began in Controller mode.\n{report}");

            Assert.True(
                epochSnapshotCandidate is not null,
                $"Expected the canonical-epoch query transform to yield a candidate when the visual hand is at the ball.\n{report}");

            Assert.True(
                physicalTargetCandidate is not null,
                $"Expected the physical-target query transform to yield a candidate under the same conditions.\n{report}");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Optical-committed-mode regression: while the committed hand-pose mode is Optical (as retained on real
    /// hardware while optical tracking sees the hands or holds its commit through ambiguity), a controller grip
    /// press near an eligible candidate must still begin a pending grab through the real
    /// <see cref="PlayerController" /> input path — committed Optical mode must never silently drop physical
    /// controller edges — and the following release edge must end that grab.
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpticalCommittedMode_ControllerGripPress_BeginsPendingGrab()
    {
        SceneTree sceneTree = GetSceneTree();
        RegressionFixture fixture = await RegressionFixture.CreateAsync(sceneTree, XRHandTrackingMode.Optical);

        try
        {
            BallPlacement ball = fixture.PlaceBallNearVisualHand();
            _ = await sceneTree.ToSignal(sceneTree.CreateTimer(0.05), "timeout");

            // Precondition: an eligible candidate IS reachable from the live query transform, so a press that
            // reaches BeginGrab begins a pending grab.
            bool candidateMeasurable = fixture.RightHand.TryGetCurrentGrabCandidateMeasurement(out GrabCandidateMeasurement? measurement);

            fixture.EmitGripPress();
            HandGrabLifecycleState lifecycleAfterOpticalPress = fixture.RightHand.GrabLifecycle;
            fixture.EmitGripRelease();
            HandGrabLifecycleState lifecycleAfterOpticalRelease = fixture.RightHand.GrabLifecycle;

            // Control leg: the identical scene, candidate, and press still begin a grab once the committed mode
            // is Controller, proving mode independence rather than candidate reach.
            fixture.Runtime.CommittedMode = XRHandTrackingMode.Controller;
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            fixture.EmitGripRelease();
            fixture.EmitGripPress();
            HandGrabLifecycleState lifecycleAfterControllerPress = fixture.RightHand.GrabLifecycle;
            fixture.EmitGripRelease();

            string report = fixture.FormatModeReport(
                candidateMeasurable,
                measurement?.AcquisitionDistance,
                lifecycleAfterOpticalPress,
                lifecycleAfterOpticalRelease,
                lifecycleAfterControllerPress);

            Assert.True(
                candidateMeasurable,
                $"Expected an eligible candidate near the visual hand as the precondition for the mode-independence regression.\n{report}");

            Assert.True(
                lifecycleAfterOpticalPress is HandGrabLifecycleState.Pending or HandGrabLifecycleState.Held,
                $"Expected the controller press to begin a grab while the committed mode is Optical.\n{report}");

            Assert.True(
                lifecycleAfterOpticalRelease == HandGrabLifecycleState.None,
                $"Expected the controller release to end the grab while the committed mode is Optical.\n{report}");

            Assert.True(
                lifecycleAfterControllerPress is HandGrabLifecycleState.Pending or HandGrabLifecycleState.Held,
                $"Expected the identical press to begin a grab once the committed mode is Controller.\n{report}");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Epoch-capture guard: the real player scene's provider chain must receive AdvanceSimulation ticks, so
    /// the canonical source snapshot exists (a stale or absent epoch would silently fall back to the physical
    /// hand target instead of the independent source intent).
    /// </summary>
    [Headless]
    [Fact]
    public async Task EpochCapture_ReachesRealSceneProviderChain()
    {
        SceneTree sceneTree = GetSceneTree();
        RegressionFixture fixture = await RegressionFixture.CreateAsync(sceneTree, XRHandTrackingMode.Controller);

        try
        {
            await WaitForPhysicsFramesAsync(sceneTree, 5);

            HandGrabTargetProvider rightProvider = fixture.RightGrabProvider;
            ulong actuatorTicks = fixture.PlayerVRIKReference.PhysicsActuatorTickCount;

            string report = fixture.FormatChainReport(rightProvider, actuatorTicks);

            Assert.True(actuatorTicks > 0, $"Expected CharacterIK physics actuator ticks in the real scene.\n{report}");
            Assert.True(rightProvider.HasSourceIntent, $"Expected a captured canonical source sample.\n{report}");
            Assert.True(
                rightProvider.TryGetSourceIntent(out IKTargetIntent intent) && intent.WorldTransform.Origin.Length() > 0.01f,
                $"Expected a non-identity canonical source sample.\n{report}");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private sealed record BallPlacement(GrabbableRigidBody3D Grabbable, Vector3 Centre);

    private sealed record RegressionMeasurement(
        Transform3D EpochTransform,
        Transform3D AnchorLiveTransform,
        Transform3D HandTargetNodeTransform,
        Transform3D HandBoneAttachmentTransform,
        Transform3D OriginTransform,
        Transform3D PlayerTransform,
        float EpochToCentre,
        float HandTargetToCentre,
        float AnchorToCentre,
        float EpochToHandTarget,
        float EpochToAnchorLive,
        float OriginCompensationDelta);

    private sealed class RegressionFixture
    {
        private const float CameraHeight = 1.6f;

        private readonly RegressionGame _root;
        private readonly TestXROrigin _origin;
        private readonly Camera3D _camera;
        private readonly RegressionXRHandControllerNode _rightController;
        private readonly Node3D _playerRoot;
        private readonly Vector3 _cameraRestLocal;

        private RegressionFixture(
            RegressionGame root,
            TestXROrigin origin,
            Camera3D camera,
            RegressionXRHandControllerNode rightController,
            Node3D playerRoot,
            RegressionXRRuntime runtime)
        {
            _root = root;
            _origin = origin;
            _camera = camera;
            _rightController = rightController;
            _playerRoot = playerRoot;
            _cameraRestLocal = camera.Position;
            Runtime = runtime;
        }

        public RegressionXRRuntime Runtime
        {
            get;
        }

        public string? ActuatorBindingNote
        {
            get;
            set;
        }

        private static string ForceRuntimeBindingInitialisation(PlayerVRIK playerVRIK)
        {
            try
            {
                MethodInfo? method = typeof(CharacterIK).GetMethod(
                    "TryInitialiseRuntimeBindings", BindingFlags.Instance | BindingFlags.NonPublic);
                bool initialised = method is not null && (bool)(method.Invoke(playerVRIK, []) ?? false);
                return initialised ? "re-initialised" : "initialisation reported incomplete";
            }
            catch (TargetInvocationException ex)
            {
                return $"re-initialisation threw: {ex.InnerException?.Message}";
            }
        }

        public HandPoseBehaviour RightHand => _playerRoot.GetNode<HandPoseBehaviour>("Hands/RightHand");

        public HandGrabTargetProvider RightGrabProvider => _playerRoot.GetNode<HandGrabTargetProvider>("VRIK/RightHandGrabProvider");

        public PlayerVRIK PlayerVRIKReference => _playerRoot.GetNode<PlayerVRIK>("VRIK");

        public Node3D RightHandTargetNode => _playerRoot.GetNode<Node3D>("IKTargets/RightHand");

        public Node3D RightHandBoneAttachment => _playerRoot.GetNode<Node3D>("Female/GeneralSkeleton/RightHand");

        public static async Task<RegressionFixture> CreateAsync(SceneTree sceneTree, XRHandTrackingMode committedMode)
        {
            RegressionGame root = new()
            {
                Name = $"ControllerGrabRegression_{committedMode}",
            };

            RegressionXRManager xrManager = new()
            {
                Name = "XR",
            };
            root.AddChild(xrManager);

            // Non-trivial XR topology: origin offset from the world origin with yaw, camera at head height,
            // controllers parented under the origin exactly as real XR tracker nodes are.
            TestXROrigin origin = new(1.0f)
            {
                Name = "Origin",
                Position = new Vector3(-1.5f, 0.0f, 2.0f),
            };
            origin.RotateY(Mathf.DegToRad(-35.0f));
            root.AddChild(origin);

            Camera3D camera = new()
            {
                Name = "Camera",
                Position = new Vector3(0.0f, CameraHeight, 0.0f),
            };
            origin.AddChild(camera);

            RegressionXRHandControllerNode rightController = new()
            {
                Name = "RightController",
                Position = new Vector3(0.32f, 1.05f, -0.28f),
            };
            origin.AddChild(rightController);

            RegressionXRHandControllerNode leftController = new()
            {
                Name = "LeftController",
                Position = new Vector3(-0.32f, 1.05f, -0.28f),
            };
            origin.AddChild(leftController);

            RegressionXRCamera xrCamera = new(camera);
            RegressionXRRuntime runtime = new(origin, xrCamera, rightController, leftController)
            {
                CommittedMode = committedMode,
            };
            xrManager.SetRuntime(runtime);

            Node player = LoadPackedScene(PlayerScenePath).Instantiate();

            // The player stands away from the world origin, mirroring real play away from the reset origin.
            var playerRoot = (Node3D)player;
            playerRoot.Position = new Vector3(3.0f, 0.0f, -2.5f);
            playerRoot.RotateY(Mathf.DegToRad(20.0f));

            root.AddChild(player);

            await WaitForNextFrameAsync(sceneTree);
            sceneTree.Root.AddChild(root);

            await WaitForFramesAsync(sceneTree, 10);
            await WaitForPhysicsFramesAsync(sceneTree, 4);
            EnsureRuntimeRoleInstalled(player);

            // PlayerVRIK binds through the same services the startup binder uses; fall back to the public
            // binding call only when the scene's binder has not fired yet.
            PlayerVRIK playerVRIK = playerRoot.GetNode<PlayerVRIK>("VRIK");
            if (!ReadIsBound(playerVRIK))
            {
                _ = playerVRIK.BindToXRRuntime(origin, xrCamera);
            }

            // The real startup completes actuator pipelines in TryInitialiseRuntimeBindings; re-run it so the
            // fixture exercises the same pipeline actuation the real game performs, and record any failure.
            string actuatorBindingNote = ForceRuntimeBindingInitialisation(playerVRIK);

            // PlayerController binds its controller relays after XR initialisation, as in the real startup.
            xrManager.EmitInitialisedResult(succeeded: true);
            await WaitForFramesAsync(sceneTree, 4);
            await WaitForPhysicsFramesAsync(sceneTree, 8);

            return new RegressionFixture(root, origin, camera, rightController, playerRoot, runtime)
            {
                ActuatorBindingNote = actuatorBindingNote,
            };
        }

        /// <summary>
        /// Moves the physical head (camera) in a slow sway so the solved head target lags the physical head and
        /// <see cref="PlayerVRIK" />'s end-stage origin compensation stays active, mirroring real head motion away
        /// from the reset pose.
        /// </summary>
        public async Task DriveHeadSwayAsync(SceneTree sceneTree, int swayFrames)
        {
            for (int frame = 0; frame < swayFrames; frame++)
            {
                float phase = frame / (float)swayFrames;
                _camera.Position = _cameraRestLocal
                    + new Vector3(0.22f * Mathf.Sin(phase * Mathf.Tau), -0.05f * phase, 0.1f * phase);
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }
        }

        public BallPlacement PlaceBallNearVisualHand()
        {
            GrabbableRigidBody3D ball = LoadPackedScene(TestBallScenePath).Instantiate<GrabbableRigidBody3D>();
            ball.Freeze = true;
            _root.AddChild(ball);

            // The user aligns what they see — the visual hand at the physical hand target — with the ball.
            Vector3 handPosition = RightHandTargetNode.GlobalPosition;
            ball.GlobalPosition = handPosition + new Vector3(0.05f, 0.02f, 0.0f);
            ball.ForceUpdateTransform();

            return new BallPlacement(ball, ball.GlobalPosition);
        }

        public RegressionMeasurement CaptureMeasurement(BallPlacement ball)
        {
            bool hasEpoch = RightGrabProvider.TryGetSourceIntent(out IKTargetIntent sourceIntent);
            Transform3D epoch = hasEpoch ? sourceIntent.WorldTransform : Transform3D.Identity;
            Transform3D anchor = _rightController.GlobalTransform;
            Transform3D handTarget = RightHandTargetNode.GlobalTransform;
            Transform3D attachment = RightHandBoneAttachment.GlobalTransform;

            return new RegressionMeasurement(
                epoch,
                anchor,
                handTarget,
                attachment,
                _origin.GlobalTransform,
                _playerRoot.GlobalTransform,
                epoch.Origin.DistanceTo(ball.Centre),
                handTarget.Origin.DistanceTo(ball.Centre),
                anchor.Origin.DistanceTo(ball.Centre),
                epoch.Origin.DistanceTo(handTarget.Origin),
                epoch.Origin.DistanceTo(anchor.Origin),
                _origin.GlobalPosition.DistanceTo(_playerRoot.GlobalPosition));
        }

        public void EmitGripPress()
            => _rightController.EmitActionFloat("grip", 0.9f);

        public void EmitGripRelease()
            => _rightController.EmitActionFloat("grip", 0.05f);

        public string FormatReport(
            RegressionMeasurement measurement,
            GrabPointCandidate? physicalTargetCandidate,
            GrabPointCandidate? epochSnapshotCandidate,
            HandGrabLifecycleState lifecycleAfterPress,
            HandGrabLifecycleState lifecycleAfterRelease)
        {
            StringBuilder builder = new();
            _ = builder.AppendLine("REGRESSION REPORT (controller mode, non-trivial origin):");
            AppendCoreMeasurements(builder, measurement);
            _ = builder.AppendLine($"  physical-target candidate (hand target node): {(physicalTargetCandidate is null ? "NULL" : $"acquisition {physicalTargetCandidate.AcquisitionDistance:F3} m")}");
            _ = builder.AppendLine($"  epoch-snapshot candidate (canonical source): {(epochSnapshotCandidate is null ? "NULL" : $"acquisition {epochSnapshotCandidate.AcquisitionDistance:F3} m")}");
            _ = builder.AppendLine($"  lifecycle after grip press: {lifecycleAfterPress}");
            _ = builder.AppendLine($"  lifecycle after grip release: {lifecycleAfterRelease}");
            _ = builder.AppendLine($"  controller bound: {ReadControllerBound()}");
            _ = builder.AppendLine($"  {FormatPipelineDebugState()}");
            _ = builder.AppendLine($"  actuator binding note: {ActuatorBindingNote ?? "n/a"}");
            return builder.ToString();
        }

        public string FormatModeReport(
            bool candidateMeasurable,
            float? acquisitionDistance,
            HandGrabLifecycleState lifecycleAfterOpticalPress,
            HandGrabLifecycleState lifecycleAfterOpticalRelease,
            HandGrabLifecycleState lifecycleAfterControllerPress)
        {
            StringBuilder builder = new();
            _ = builder.AppendLine("REGRESSION REPORT (mode-independent controller grab edges):");
            _ = builder.AppendLine("  committed mode at first press: Optical");
            _ = builder.AppendLine($"  candidate measurable near visual hand: {candidateMeasurable} (acquisition {acquisitionDistance?.ToString("F3") ?? "n/a"} m)");
            _ = builder.AppendLine($"  lifecycle after Optical-mode press: {lifecycleAfterOpticalPress}");
            _ = builder.AppendLine($"  lifecycle after Optical-mode release: {lifecycleAfterOpticalRelease}");
            _ = builder.AppendLine($"  lifecycle after Controller-mode press (control leg): {lifecycleAfterControllerPress}");
            _ = builder.AppendLine($"  controller bound: {ReadControllerBound()}");
            return builder.ToString();
        }

        public string FormatChainReport(HandGrabTargetProvider rightProvider, ulong actuatorTicks)
        {
            _ = rightProvider.TryGetSourceIntent(out IKTargetIntent intent);
            return $"REGRESSION REPORT (epoch capture chain): actuator ticks {actuatorTicks}, HasSourceIntent {rightProvider.HasSourceIntent}, "
                   + $"epoch origin {intent.WorldTransform.Origin}, VRIK bound {ReadIsBound(PlayerVRIKReference)}.";
        }

        public string FormatPipelineDebugState()
        {
            IKTargetPipelineResult state = PlayerVRIKReference.RightHandTargetPipelineDebugState;
            PlayerVRIK vrik = PlayerVRIKReference;
            FieldInfo? pipelineField = typeof(CharacterIK).GetField("_rightHandTargetPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
            bool pipelineExists = pipelineField?.GetValue(vrik) is not null;
            return $"right-hand pipeline: exists {pipelineExists}, source {state.SourceTarget.Origin}, requested {state.RequestedTarget.Origin}, "
                   + $"realised {state.RealisedTarget.Origin}, feedback '{state.Feedback.Reason}' (error {state.Feedback.ErrorDistance:F3} m); "
                   + $"RightHandIKTargetIntentProvider set: {vrik.Get("RightHandIKTargetIntentProvider").AsGodotObject() is not null}, "
                   + $"RightHandFallbackIntentProvider set: {vrik.Get("RightHandFallbackIntentProvider").AsGodotObject() is not null}, "
                   + $"installer: {_playerRoot.GetNodeOrNull("PlayerCharacterInstaller") is not null}.";
        }

        private static void AppendCoreMeasurements(StringBuilder builder, RegressionMeasurement measurement)
        {
            _ = builder.AppendLine($"  epoch snapshot origin:        {measurement.EpochTransform.Origin}");
            _ = builder.AppendLine($"  live anchor origin:           {measurement.AnchorLiveTransform.Origin}");
            _ = builder.AppendLine($"  hand target node origin:      {measurement.HandTargetNodeTransform.Origin}");
            _ = builder.AppendLine($"  hand bone attachment origin:  {measurement.HandBoneAttachmentTransform.Origin}");
            _ = builder.AppendLine($"  ball centre:                  {measurement.HandTargetNodeTransform.Origin + new Vector3(0.05f, 0.02f, 0.0f)} (placed)");
            _ = builder.AppendLine($"  origin (current):             {measurement.OriginTransform.Origin}");
            _ = builder.AppendLine($"  player root origin:           {measurement.PlayerTransform.Origin}");
            _ = builder.AppendLine($"  distance epoch->centre:       {measurement.EpochToCentre:F3} m (reach {BallReachMetres} m)");
            _ = builder.AppendLine($"  distance handTarget->centre:  {measurement.HandTargetToCentre:F3} m");
            _ = builder.AppendLine($"  distance anchor->centre:      {measurement.AnchorToCentre:F3} m");
            _ = builder.AppendLine($"  distance epoch->handTarget:   {measurement.EpochToHandTarget:F3} m");
            _ = builder.AppendLine($"  distance epoch->anchorLive:   {measurement.EpochToAnchorLive:F3} m");
            _ = builder.AppendLine($"  origin compensation delta:    {measurement.OriginCompensationDelta:F3} m");
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            _root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }

        private bool ReadControllerBound()
        {
            FieldInfo? field = typeof(PlayerController).GetField("_isBound", BindingFlags.Instance | BindingFlags.NonPublic);
            Node? controller = _playerRoot.GetNodeOrNull("PlayerController");
            return field is not null && controller is not null && (bool)(field.GetValue(controller) ?? false);
        }

        private static bool ReadIsBound(PlayerVRIK playerVRIK)
        {
            FieldInfo? field = playerVRIK.GetType().GetField("_isBound", BindingFlags.Instance | BindingFlags.NonPublic);
            return field is not null && (bool)(field.GetValue(playerVRIK) ?? false);
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
            if (!succeeded)
            {
                object? errors = result.GetType().GetProperty("Errors")?.GetValue(result);
                throw new Xunit.Sdk.XunitException(errors?.ToString() ?? "Runtime role installer failed.");
            }
        }
    }

    private sealed partial class RegressionGame : Game
    {
        public override void _Ready()
        {
        }
    }

    private sealed partial class RegressionXRManager : XRManager
    {
        public override void _Ready()
        {
        }

        public void SetRuntime(IXRRuntime runtime)
            => Runtime = runtime;

        public void EmitInitialisedResult(bool succeeded)
        {
            InitialisationAttempted = true;
            InitialisationSucceeded = succeeded;
            _ = EmitSignal(SignalName.Initialised, succeeded);
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

    private sealed partial class RegressionXRHandControllerNode : Node3D, IXRHandController
    {
        public event Action<string, float>? ActionFloatInputChanged;

#pragma warning disable CS0067
        public event Action<string>? ActionButtonPressed;
        public event Action<string>? ActionButtonReleased;
        public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

        public Node3D ControllerNode => this;

        public Node3D HandPositionNode => this;

        public void EmitActionFloat(string actionName, float value)
            => ActionFloatInputChanged?.Invoke(actionName, value);
    }

    private sealed class RegressionXRCamera(Camera3D cameraNode) : IXRCamera
    {
        public Camera3D CameraNode => cameraNode;
    }

    /// <summary>
    /// MockXR runtime with a settable committed hand-pose mode: controller sources use the live controller
    /// anchors exactly as the real controller branch does (see <see cref="XRControllerHandTracking" />).
    /// </summary>
    private sealed class RegressionXRRuntime(
        TestXROrigin origin,
        RegressionXRCamera camera,
        RegressionXRHandControllerNode rightController,
        RegressionXRHandControllerNode leftController) : IXRRuntime
    {
        private readonly XRControllerHandTracking _handTracking = new(rightController, leftController);

        public IXROrigin Origin => origin;

        public IXRCamera Camera => camera;

        public IXRHandController RightHandController => rightController;

        public IXRHandController LeftHandController => leftController;

        public XRHandTrackingMode CommittedMode
        {
            get;
            set;
        } = XRHandTrackingMode.Controller;

        public XRHandTrackingMode HandTrackingMode => CommittedMode;

        public IXRHandJointProvider OpticalHandJoints => XREmptyHandJointProvider.Instance;

        public IXRHandPoseSource GetHandPoseSource(LimbSide side) => _handTracking.GetHandPoseSource(side);

#pragma warning disable CS0067
        public event Action? PoseRecentered;

        public event Action? HandTrackingModeChanged;
#pragma warning restore CS0067

        public bool Initialise(SubViewport viewport, int maximumRefreshRate)
        {
            _ = viewport;
            _ = maximumRefreshRate;
            return true;
        }
    }
}
