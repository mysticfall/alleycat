using AlleyCat.Common;
using AlleyCat.Core.Logging;
using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.XR.Mock;

/// <summary>
/// Mock XR runtime implementation bound to the mock runtime scene root.
/// </summary>
/// <remarks>
/// Provides deterministic hand-tracking hooks (XR-002 TR28): per-side controller/optical tracked state, direct
/// bilateral observation injection with atomic mode re-evaluation, optical wrist samples with calibration anchors and
/// freeze-on-loss retention, and per-joint sample/tracked flags for the optical joint provider.
/// </remarks>
[GlobalClass]
public partial class MockXRRuntimeNode : Node3D, IXRRuntime, IXROrigin, IXRHandJointProvider
{
    private readonly XRHandTrackingModeArbiter _handModeArbiter = new();

    private readonly Dictionary<LimbSide, XRHandSourceObservation> _handObservations = new()
    {
        [LimbSide.Left] = XRHandSourceObservation.Ambiguous,
        [LimbSide.Right] = XRHandSourceObservation.Ambiguous,
    };

    private readonly Dictionary<LimbSide, bool> _controllerTracked = new()
    {
        [LimbSide.Left] = true,
        [LimbSide.Right] = true,
    };

    private readonly Dictionary<LimbSide, XRControllerProfileKind> _controllerProfiles = new()
    {
        [LimbSide.Left] = XRControllerProfileKind.ControllerDevice,
        [LimbSide.Right] = XRControllerProfileKind.ControllerDevice,
    };

    private readonly Dictionary<LimbSide, bool> _opticalTracked = new()
    {
        [LimbSide.Left] = false,
        [LimbSide.Right] = false,
    };

    private readonly Dictionary<LimbSide, XRWristFreezeCache> _wristCaches = new()
    {
        [LimbSide.Left] = new XRWristFreezeCache(),
        [LimbSide.Right] = new XRWristFreezeCache(),
    };

    private readonly Dictionary<LimbSide, Transform3D> _opticalCalibrationAnchors = new()
    {
        [LimbSide.Left] = Transform3D.Identity,
        [LimbSide.Right] = Transform3D.Identity,
    };

    private readonly Dictionary<LimbSide, Dictionary<XRHandJoint, Transform3D>> _handJointSamples = new()
    {
        [LimbSide.Left] = [],
        [LimbSide.Right] = [],
    };

    private readonly Dictionary<LimbSide, Dictionary<XRHandJoint, XRHandTracker.HandJointFlags>> _handJointFlags = new()
    {
        [LimbSide.Left] = [],
        [LimbSide.Right] = [],
    };

    private MockHandPoseSource? _rightHandPoseSource;

    private MockHandPoseSource? _leftHandPoseSource;

    /// <inheritdoc />
    public IXROrigin Origin => this;

    /// <inheritdoc />
    public IXRCamera Camera
    {
        get;
        private set;
    } = null!;

    /// <inheritdoc />
    public IXRHandController RightHandController
    {
        get;
        private set;
    } = null!;

    /// <inheritdoc />
    public IXRHandController LeftHandController
    {
        get;
        private set;
    } = null!;

    /// <inheritdoc />
    public XRHandTrackingMode HandTrackingMode => _handModeArbiter.CommittedMode;

    /// <inheritdoc />
    public IXRHandJointProvider OpticalHandJoints => this;

    /// <inheritdoc />
    public event Action? PoseRecentered;

    /// <inheritdoc />
    public event Action? HandTrackingModeChanged;

    /// <inheritdoc />
    public Node3D OriginNode => this;

    /// <inheritdoc />
    public float WorldScale
    {
        get;
        set;
    } = 1.0f;

    /// <inheritdoc />
    public bool Initialise(SubViewport viewport, int maximumRefreshRate)
    {
        _ = viewport;
        _ = maximumRefreshRate;

        Camera = this.RequireNode<MockXRCameraNode>("MainCamera");
        RightHandController = this.RequireNode<MockXRHandControllerNode>("RightController");
        LeftHandController = this.RequireNode<MockXRHandControllerNode>("LeftController");
        SeedOpticalCalibrationAnchorsFromScene();

        if (GameLoggerResolver.TryResolve(out ILogger<MockXRRuntimeNode>? logger) && logger is not null)
        {
            logger.LogDebug(
                "Mock XR runtime initialised with hand-pose mode {HandTrackingMode}.",
                HandTrackingMode);
        }

        return true;
    }

    /// <summary>
    /// Seeds the per-side optical calibration anchors from the authored scene hierarchy
    /// (<c>RightOpticalHand/WristAnchor/OpticalHandAnchor</c> and the left mirror) when present, mirroring the
    /// OpenXR runtime scene wiring (XR-002 TR9). Test hooks such as
    /// <see cref="SetOpticalCalibrationAnchor" /> override the seeded values.
    /// </summary>
    private void SeedOpticalCalibrationAnchorsFromScene()
    {
        foreach ((LimbSide side, string prefix) in new[] { (LimbSide.Right, "Right"), (LimbSide.Left, "Left") })
        {
            if (GetNodeOrNull<Node3D>($"{prefix}OpticalHand/WristAnchor/OpticalHandAnchor") is { } anchor)
            {
                _opticalCalibrationAnchors[side] = anchor.Transform;
            }
        }
    }

    /// <inheritdoc />
    public IXRHandPoseSource GetHandPoseSource(LimbSide side)
        => side == LimbSide.Right
            ? _rightHandPoseSource ??= new MockHandPoseSource(this, LimbSide.Right)
            : _leftHandPoseSource ??= new MockHandPoseSource(this, LimbSide.Left);

    /// <summary>
    /// Triggers the <see cref="PoseRecentered" /> event to notify subscribers that the pose has been recentered.
    /// </summary>
    public void TriggerPoseRecentered() => PoseRecentered?.Invoke();

    /// <summary>
    /// Sets per-side whether the controller is tracked for hand-pose mode arbitration. Does not re-evaluate the
    /// committed mode; call <see cref="EvaluateHandTrackingMode" /> or <see cref="SetHandObservations" /> for the
    /// atomic bilateral step.
    /// </summary>
    public void SetControllerHandTracked(LimbSide side, bool tracked) => _controllerTracked[side] = tracked;

    /// <summary>
    /// Sets the per-side controller interaction-profile kind fed into the production observation policy, so
    /// profile-driven classification is testable without hardware (XR-002 TR28): for example WiVRn's
    /// hand-interaction profile, or the emulated Khronos simple-controller profile WiVRn actually keeps pose-alive
    /// from the hands. Defaults to <see cref="XRControllerProfileKind.ControllerDevice" />. Does not re-evaluate the
    /// committed mode; call <see cref="EvaluateHandTrackingMode" /> for the atomic bilateral step.
    /// </summary>
    public void SetControllerProfile(LimbSide side, XRControllerProfileKind kind) => _controllerProfiles[side] = kind;

    /// <summary>
    /// Sets per-side whether optical tracking is tracked for hand-pose mode arbitration. Does not re-evaluate the
    /// committed mode; call <see cref="EvaluateHandTrackingMode" /> or <see cref="SetHandObservations" /> for the
    /// atomic bilateral step.
    /// </summary>
    public void SetOpticalHandTracked(LimbSide side, bool tracked) => _opticalTracked[side] = tracked;

    /// <summary>
    /// Atomically re-evaluates the committed hand-pose mode from the current per-side tracked state
    /// (XR-002 TR3-TR4).
    /// </summary>
    /// <returns><see langword="true" /> when the committed mode changed; otherwise <see langword="false" />.</returns>
    public bool EvaluateHandTrackingMode()
        => ApplyObservations(DeriveObservation(LimbSide.Left), DeriveObservation(LimbSide.Right));

    /// <summary>
    /// Atomically re-evaluates the committed hand-pose mode from an explicit bilateral observation pair, bypassing
    /// tracked-state derivation.
    /// </summary>
    /// <param name="left">Left-hand observation.</param>
    /// <param name="right">Right-hand observation.</param>
    /// <returns><see langword="true" /> when the committed mode changed; otherwise <see langword="false" />.</returns>
    public bool SetHandObservations(XRHandSourceObservation left, XRHandSourceObservation right)
        => ApplyObservations(left, right);

    /// <summary>
    /// Sets the per-side optical calibration anchor applied to injected wrist samples (XR-002 TR9, TR11).
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <param name="anchor">Authorable full calibration anchor transform.</param>
    public void SetOpticalCalibrationAnchor(LimbSide side, Transform3D anchor)
        => _opticalCalibrationAnchors[side] = anchor;

    /// <summary>
    /// Injects a raw origin-space optical wrist sample. The mock composes the origin transform, world scale, and the
    /// per-side calibration anchor exactly once into the calibrated world-space wrist (XR-002 TR10) and captures it
    /// with freeze-on-loss semantics.
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <param name="originSpaceWrist">Raw wrist transform relative to the XR origin.</param>
    public void SetOpticalWristSample(LimbSide side, Transform3D originSpaceWrist)
        => _wristCaches[side].Capture(ComposeCalibratedWrist(side, originSpaceWrist));

    /// <summary>
    /// Marks the per-side optical wrist as lost without mutating the retained world-space transform
    /// (XR-002 TR5, TR25).
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    public void MarkOpticalWristLost(LimbSide side) => _wristCaches[side].MarkLost();

    /// <summary>
    /// Injects or replaces an origin-space joint sample and marks the joint tracked. Any tracked joint can be
    /// injected, including the source-only non-thumb metacarpals that anchor proximal rotations (XR-002 TR14,
    /// TR28).
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <param name="joint">Tracked joint to inject.</param>
    /// <param name="originSpaceTransform">Raw joint transform relative to the XR origin.</param>
    public void SetHandJointSample(LimbSide side, XRHandJoint joint, Transform3D originSpaceTransform)
    {
        _handJointSamples[side][joint] = originSpaceTransform;
        _handJointFlags[side][joint] = XRHandTracker.HandJointFlags.OrientationTracked
                                       | XRHandTracker.HandJointFlags.PositionTracked;
    }

    /// <summary>
    /// Sets the exact raw engine flags returned with an injected mock joint sample. Position flags are retained for
    /// observation but never participate in the production orientation gate.
    /// </summary>
    public void SetHandJointFlags(
        LimbSide side,
        XRHandJoint joint,
        XRHandTracker.HandJointFlags flags)
        => _handJointFlags[side][joint] = flags;

    /// <summary>
    /// Removes a joint sample, invalidating it for provider queries.
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <param name="joint">Tracked joint to clear.</param>
    public void ClearHandJointSample(LimbSide side, XRHandJoint joint)
    {
        _ = _handJointSamples[side].Remove(joint);
        _ = _handJointFlags[side].Remove(joint);
    }

    /// <summary>
    /// Sets a per-joint tracked flag without mutating the stored joint transform.
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <param name="joint">Tracked joint to update.</param>
    /// <param name="tracked">Whether the joint is actively tracked.</param>
    public void SetHandJointTracked(LimbSide side, XRHandJoint joint, bool tracked)
        => _handJointFlags[side][joint] = tracked
            ? XRHandTracker.HandJointFlags.OrientationTracked | XRHandTracker.HandJointFlags.PositionTracked
            : default;

    /// <inheritdoc />
    public bool TryGetJoint(LimbSide side, XRHandJoint joint, out XRHandJointSourceSample sample)
    {
        if (!_handJointSamples[side].TryGetValue(joint, out Transform3D trackerLocal))
        {
            sample = MissingJointSample(side, joint);
            return false;
        }

        XRHandTracker.HandJointFlags flags = _handJointFlags[side].GetValueOrDefault(joint);
        Transform3D productionWorld = ComposeWorldSpaceTransform(trackerLocal);
        bool trackerLocalFinite = XRHandTrackingMath.IsFinite(trackerLocal);
        XRHandJointSourceRejection rejection = ClassifyJointSample(side, joint, flags, trackerLocalFinite);
        bool accepted = rejection == XRHandJointSourceRejection.None;

        sample = new XRHandJointSourceSample(
            side,
            joint,
            HasTracker: true,
            HasTrackingData: true,
            (long)flags,
            (flags & XRHandTracker.HandJointFlags.OrientationValid) != 0,
            (flags & XRHandTracker.HandJointFlags.OrientationTracked) != 0,
            (flags & XRHandTracker.HandJointFlags.PositionValid) != 0,
            (flags & XRHandTracker.HandJointFlags.PositionTracked) != 0,
            trackerLocal,
            productionWorld,
            trackerLocalFinite,
            XRHandTrackingMath.IsFinite(productionWorld),
            accepted,
            rejection);

        return accepted;
    }

    private static XRHandJointSourceSample MissingJointSample(LimbSide side, XRHandJoint joint)
        => new(
            side,
            joint,
            HasTracker: true,
            HasTrackingData: false,
            RawFlags: 0,
            OrientationValid: false,
            OrientationTracked: false,
            PositionValid: false,
            PositionTracked: false,
            Transform3D.Identity,
            Transform3D.Identity,
            TrackerLocalTransformFinite: true,
            ProductionWorldTransformFinite: true,
            ProductionAccepted: false,
            XRHandJointSourceRejection.JointUnavailable);

    private XRHandJointSourceRejection ClassifyJointSample(
        LimbSide side,
        XRHandJoint joint,
        XRHandTracker.HandJointFlags flags,
        bool trackerLocalFinite)
    {
        if (!IsOrientationUsable(flags))
        {
            return XRHandJointSourceRejection.JointOrientationUnusable;
        }

        if (!trackerLocalFinite)
        {
            return XRHandJointSourceRejection.JointNonFinite;
        }

        if (XRHandJoints.GetRequiredSourceParent(joint) is not { } parent)
        {
            return XRHandJointSourceRejection.None;
        }

        if (!_handJointSamples[side].TryGetValue(parent, out Transform3D parentTransform))
        {
            return XRHandJointSourceRejection.ParentUnavailable;
        }

        XRHandTracker.HandJointFlags parentFlags = _handJointFlags[side].GetValueOrDefault(parent);
        return !IsOrientationUsable(parentFlags)
            ? XRHandJointSourceRejection.ParentOrientationUnusable
            : XRHandTrackingMath.IsFinite(parentTransform)
                ? XRHandJointSourceRejection.None
                : XRHandJointSourceRejection.ParentNonFinite;
    }

    private static bool IsOrientationUsable(XRHandTracker.HandJointFlags flags)
        => (flags & XRHandTracker.HandJointFlags.OrientationTracked) != 0
           || (flags & XRHandTracker.HandJointFlags.OrientationValid) != 0;

    private bool TryGetCalibratedWrist(LimbSide side, out Transform3D transform)
    {
        if (_handModeArbiter.CommittedMode == XRHandTrackingMode.Optical)
        {
            return _wristCaches[side].TryGetTransform(out transform);
        }

        IXRHandController controller = side == LimbSide.Right ? RightHandController : LeftHandController;
        transform = controller.HandPositionNode.GlobalTransform;

        return true;
    }

    private bool ApplyObservations(XRHandSourceObservation left, XRHandSourceObservation right)
    {
        bool changed = _handModeArbiter.Evaluate(left, right);

        _handObservations[LimbSide.Left] = left;
        _handObservations[LimbSide.Right] = right;

        if (changed && _handModeArbiter.CommittedMode == XRHandTrackingMode.Controller)
        {
            _wristCaches[LimbSide.Left].Reset();
            _wristCaches[LimbSide.Right].Reset();
        }

        if (GameLoggerResolver.TryResolve(out ILogger<MockXRRuntimeNode>? logger) && logger is not null)
        {
            logger.LogDebug(
                "Mock XR hand-pose mode evaluated: left={LeftObservation}, right={RightObservation}, " +
                "committed={CommittedMode}, changed={ModeChanged}.",
                left,
                right,
                _handModeArbiter.CommittedMode,
                changed);
        }

        if (changed)
        {
            HandTrackingModeChanged?.Invoke();
        }

        return changed;
    }

    /// <summary>
    /// Derives a side's observation through the production <see cref="XRHandObservationPolicy" />: the mock's
    /// optical-tracked state maps to an unknown-source tracker that exists, has data, and tracks the wrist, so the
    /// retained tracked-state semantics (controller-only, optical-only, otherwise ambiguous) come from the same
    /// classification as the OpenXR runtime while the profile hook drives the unknown-source discriminator
    /// (XR-002 TR2, TR28).
    /// </summary>
    private XRHandSourceObservation DeriveObservation(LimbSide side)
        => XRHandObservationPolicy.Classify(new XRHandObservationInputs(
            _controllerTracked[side],
            _controllerProfiles[side],
            _opticalTracked[side],
            _opticalTracked[side],
            _opticalTracked[side],
            XROpticalHandTrackingSource.Unknown));

    private Transform3D ComposeCalibratedWrist(LimbSide side, Transform3D originSpaceWrist)
        => ComposeWorldSpaceTransform(originSpaceWrist * _opticalCalibrationAnchors[side]);

    private Transform3D ComposeWorldSpaceTransform(Transform3D originSpaceTransform)
        => XRHandPoseComposition.ComposeWorldSpace(OriginNode.GlobalTransform, WorldScale, originSpaceTransform);

    private sealed class MockHandPoseSource(MockXRRuntimeNode runtime, LimbSide side) : IXRHandPoseSource
    {
        /// <inheritdoc />
        public LimbSide Side => side;

        /// <inheritdoc />
        public XRHandTrackingMode SelectedMode => runtime._handModeArbiter.CommittedMode;

        /// <inheritdoc />
        public XRHandSourceObservation Observation => runtime._handObservations[side];

        /// <inheritdoc />
        public bool EverCapturedWrist => runtime._wristCaches[side].EverCaptured;

        /// <inheritdoc />
        public bool TryGetCalibratedWristTransform(out Transform3D transform)
            => runtime.TryGetCalibratedWrist(side, out transform);
    }
}
