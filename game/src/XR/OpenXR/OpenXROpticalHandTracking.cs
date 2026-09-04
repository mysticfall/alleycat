using AlleyCat.Core.Logging;
using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.XR.OpenXR;

/// <summary>
/// Hand-tracking surface owned by the OpenXR runtime node (XR-002 TR26, TR27).
/// </summary>
internal interface IOpenXRHandTracking
{
    /// <summary>
    /// Gets the committed global hand-pose mode.
    /// </summary>
    XRHandTrackingMode HandTrackingMode
    {
        get;
    }

    /// <summary>
    /// Gets the optical hand-joint provider.
    /// </summary>
    IXRHandJointProvider OpticalHandJoints
    {
        get;
    }

    /// <summary>
    /// Raised when the committed global hand-pose mode changes.
    /// </summary>
    event Action? ModeChanged;

    /// <summary>
    /// Gets the per-side hand-pose source selected by the committed mode.
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <returns>The hand-pose source for the requested side.</returns>
    IXRHandPoseSource GetHandPoseSource(LimbSide side);

    /// <summary>
    /// Advances the hand-pose mode and wrist capture by one atomic tick.
    /// </summary>
    void EvaluateTick();
}

/// <summary>
/// Controller-only hand-tracking surface for OpenXR runtimes without articulated hand-tracking support
/// (XR-002 TR26).
/// </summary>
internal sealed class OpenXRControllerHandTracking(XRControllerHandTracking controllerTracking) : IOpenXRHandTracking
{
    /// <inheritdoc />
    public XRHandTrackingMode HandTrackingMode => XRHandTrackingMode.Controller;

    /// <inheritdoc />
    public IXRHandJointProvider OpticalHandJoints => controllerTracking.OpticalHandJoints;

    /// <inheritdoc />
    public event Action? ModeChanged
    {
        add
        {
        }
        remove
        {
        }
    }

    /// <inheritdoc />
    public IXRHandPoseSource GetHandPoseSource(LimbSide side) => controllerTracking.GetHandPoseSource(side);

    /// <inheritdoc />
    public void EvaluateTick()
    {
    }
}

/// <summary>
/// Optical hand-tracking adapter ingesting the standard OpenXR hand trackers through <see cref="XRServer" />
/// (XR-002 TR6).
/// </summary>
/// <remarks>
/// <para>
/// Both sides are classified and evaluated in one atomic tick from the runtime node's physics processing, before
/// IK consumers sample the hand-pose sources. Physics is chosen over process because the target pipeline samples
/// providers from physics processing, and the runtime node lives under the autoloaded XRManager so its physics
/// callback runs before player IK nodes later in the scene tree. OpenXR poses refresh once per rendered frame, so
/// each tick reads the latest consistent bilateral sample. Mode is never committed from individual tracker
/// lifecycle callbacks (XR-002 TR3).
/// </para>
/// <para>
/// The calibrated wrist is composed from the palm-following <see cref="XRNode3D" /> scene node (whose transform
/// the engine adjusts for reference frame, recentering, and world scale exactly once), the live palm-to-wrist
/// relative transform from the same tracker with its translation world-scale-corrected exactly once, and the
/// authored per-side optical calibration anchor (XR-002 TR9, TR10). Optical joints are exposed as world-space
/// transforms composed from the raw origin-space joint transforms with the origin transform and world scale
/// applied exactly once, sharing <see cref="XRHandPoseComposition" /> with the mock runtime so both runtimes
/// produce identical frames; Phase 4 consumes source-relative joint rotations, which are invariant to this outer
/// frame.
/// </para>
/// </remarks>
internal sealed class OpenXROpticalHandTracking : IOpenXRHandTracking, IXRHandJointProvider
{
    private static readonly StringName _rightTrackerName = new("/user/hand_tracker/right");

    private static readonly StringName _leftTrackerName = new("/user/hand_tracker/left");

    /// <summary>
    /// Controller interaction profiles change rarely (controllers picked up or put down), so the XRServer profile
    /// poll is rate-limited to keep the physics tick free of repeated profile reads.
    /// </summary>
    private const uint ControllerProfilePollIntervalMsec = 250;

    private readonly OpenXRRuntimeNode _runtime;

    private readonly Dictionary<LimbSide, OpenXRHandSide> _sides;

    private readonly Dictionary<LimbSide, IXRHandPoseSource> _handPoseSources;

    private readonly XRHandTrackingModeArbiter _arbiter = new();

    private readonly ILogger<OpenXROpticalHandTracking> _logger = GameLoggerResolver.ResolveRequired<OpenXROpticalHandTracking>();

    private bool _trackerSignalsBound;

    private ulong _nextControllerProfilePollMsec;

    private readonly uint _controllerProfilePollIntervalMsec;

    /// <summary>
    /// Creates the adapter bound to one runtime node and its optical hand scene hierarchy.
    /// </summary>
    /// <param name="runtime">OpenXR runtime node providing the origin transform and world scale.</param>
    /// <param name="rightPalmNode">Palm-following XRNode3D for the right hand.</param>
    /// <param name="leftPalmNode">Palm-following XRNode3D for the left hand.</param>
    /// <param name="rightCalibrationAnchor">Authored right-hand optical calibration anchor node.</param>
    /// <param name="leftCalibrationAnchor">Authored left-hand optical calibration anchor node.</param>
    /// <param name="rightController">Right-hand OpenXR controller node.</param>
    /// <param name="leftController">Left-hand OpenXR controller node.</param>
    /// <param name="controllerProfilePollIntervalMsec">
    /// Minimum interval between controller interaction-profile polls; integration tests pass <c>0</c> for
    /// per-tick deterministic polls.
    /// </param>
    public OpenXROpticalHandTracking(
        OpenXRRuntimeNode runtime,
        XRNode3D rightPalmNode,
        XRNode3D leftPalmNode,
        Node3D rightCalibrationAnchor,
        Node3D leftCalibrationAnchor,
        OpenXRHandControllerNode rightController,
        OpenXRHandControllerNode leftController,
        uint controllerProfilePollIntervalMsec = ControllerProfilePollIntervalMsec)
    {
        _runtime = runtime;
        _controllerProfilePollIntervalMsec = controllerProfilePollIntervalMsec;

        _sides = new Dictionary<LimbSide, OpenXRHandSide>
        {
            [LimbSide.Right] = new(
                LimbSide.Right,
                _rightTrackerName,
                rightController.Tracker,
                rightPalmNode,
                rightCalibrationAnchor,
                rightController),
            [LimbSide.Left] = new(
                LimbSide.Left,
                _leftTrackerName,
                leftController.Tracker,
                leftPalmNode,
                leftCalibrationAnchor,
                leftController),
        };

        _handPoseSources = new Dictionary<LimbSide, IXRHandPoseSource>
        {
            [LimbSide.Right] = new OpenXRHandPoseSource(this, _sides[LimbSide.Right]),
            [LimbSide.Left] = new OpenXRHandPoseSource(this, _sides[LimbSide.Left]),
        };

        XRServer.TrackerAdded += OnTrackerAdded;
        XRServer.TrackerUpdated += OnTrackerUpdated;
        XRServer.TrackerRemoved += OnTrackerRemoved;
        _trackerSignalsBound = true;
    }

    /// <inheritdoc />
    public XRHandTrackingMode HandTrackingMode => _arbiter.CommittedMode;

    /// <inheritdoc />
    public IXRHandJointProvider OpticalHandJoints => this;

    /// <inheritdoc />
    public event Action? ModeChanged;

    /// <inheritdoc />
    public IXRHandPoseSource GetHandPoseSource(LimbSide side) => _handPoseSources[side];

    /// <summary>
    /// Unsubscribes tracker lifecycle signals. Called by the runtime node on exit.
    /// </summary>
    public void Shutdown()
    {
        if (!_trackerSignalsBound)
        {
            return;
        }

        XRServer.TrackerAdded -= OnTrackerAdded;
        XRServer.TrackerUpdated -= OnTrackerUpdated;
        XRServer.TrackerRemoved -= OnTrackerRemoved;
        _trackerSignalsBound = false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tracker lifecycle callbacks only refresh the resolved tracker cache and log availability; they never commit
    /// the hand-pose mode. Finger joint transforms are accepted for rotation-only retargeting when the joint and its
    /// required source parent are finite and orientation-usable — <see cref="XRHandTracker.HandJointFlags.OrientationTracked" />
    /// or <see cref="XRHandTracker.HandJointFlags.OrientationValid" /> (XR-002 TR20).
    /// </remarks>
    public bool TryGetJoint(LimbSide side, XRHandJoint joint, out XRHandJointSourceSample sample)
    {
        if (!_sides.TryGetValue(side, out OpenXRHandSide? handSide) || handSide.Tracker is not { } tracker)
        {
            sample = MissingSourceSample(side, joint, hasTracker: false, XRHandJointSourceRejection.NoTracker);

            return false;
        }

        if (!tracker.HasTrackingData)
        {
            sample = MissingSourceSample(side, joint, hasTracker: true, XRHandJointSourceRejection.NoTrackingData);

            return false;
        }

        XRHandTracker.HandJoint godotJoint = ToGodotJoint(joint);
        XRHandTracker.HandJointFlags flags = tracker.GetHandJointFlags(godotJoint);
        Transform3D trackerLocal = tracker.GetHandJointTransform(godotJoint);

        Transform3D productionWorld = XRHandPoseComposition.ComposeWorldSpace(
            _runtime.GlobalTransform,
            _runtime.WorldScale,
            trackerLocal);
        bool trackerLocalFinite = XRHandTrackingMath.IsFinite(trackerLocal);
        XRHandJointSourceRejection rejection = ClassifyJointSample(
            tracker,
            joint,
            flags,
            trackerLocalFinite);
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

    private static XRHandJointSourceSample MissingSourceSample(
        LimbSide side,
        XRHandJoint joint,
        bool hasTracker,
        XRHandJointSourceRejection rejection)
        => new(
            side,
            joint,
            hasTracker,
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
            rejection);

    /// <inheritdoc />
    public void EvaluateTick()
    {
        RefreshTrackers();
        RefreshControllerProfiles();

        XRHandSourceObservation left = Classify(_sides[LimbSide.Left]);
        XRHandSourceObservation right = Classify(_sides[LimbSide.Right]);

        _sides[LimbSide.Left].Observation = left;
        _sides[LimbSide.Right].Observation = right;

        bool changed = _arbiter.Evaluate(left, right);

        if (changed)
        {
            if (_arbiter.CommittedMode == XRHandTrackingMode.Controller)
            {
                // Mirror the mock runtime: returning to controller mode ends the optical session.
                _sides[LimbSide.Left].WristCache.Reset();
                _sides[LimbSide.Right].WristCache.Reset();
            }
            _logger.LogInformation(
                "OpenXR hand-pose mode committed to {HandTrackingMode} (left={LeftObservation}, right={RightObservation}).",
                _arbiter.CommittedMode,
                left,
                right);

            ModeChanged?.Invoke();
        }

        if (_arbiter.CommittedMode == XRHandTrackingMode.Optical)
        {
            CaptureWrist(_sides[LimbSide.Left]);
            CaptureWrist(_sides[LimbSide.Right]);

        }
    }

    /// <summary>
    /// Captures the calibrated world-space wrist from the palm-following node and the live palm-to-wrist relative
    /// transform. The wrist path keeps the strict actively-tracked tier — wrist and palm must both report
    /// <see cref="XRHandTracker.HandJointFlags.PositionTracked" /> and
    /// <see cref="XRHandTracker.HandJointFlags.OrientationTracked" /> — because wrist capture consumes positions
    /// (XR-002 TR20).
    /// </summary>
    private void CaptureWrist(OpenXRHandSide side)
    {
        if (side.Tracker is not { HasTrackingData: true } tracker
            || !IsJointActivelyTracked(tracker, XRHandTracker.HandJoint.Wrist)
            || !IsJointActivelyTracked(tracker, XRHandTracker.HandJoint.Palm))
        {
            side.WristCache.MarkLost();

            return;
        }

        Transform3D palm = tracker.GetHandJointTransform(XRHandTracker.HandJoint.Palm);
        Transform3D wrist = tracker.GetHandJointTransform(XRHandTracker.HandJoint.Wrist);

        if (!XRHandTrackingMath.IsFinite(palm) || !XRHandTrackingMath.IsFinite(wrist))
        {
            side.WristCache.MarkLost();

            return;
        }

        side.WristCache.Capture(XRHandPoseComposition.ComposeCalibratedWrist(
            side.PalmNode.GlobalTransform,
            XRHandPoseComposition.ComposePalmToWristRelative(palm, wrist),
            _runtime.WorldScale,
            side.CalibrationAnchorNode.Transform));
    }

    private static XRHandSourceObservation Classify(OpenXRHandSide side)
    {
        XRHandTracker? tracker = side.Tracker;

        bool opticalHasData = tracker is { HasTrackingData: true };

        return XRHandObservationPolicy.Classify(new XRHandObservationInputs(
            side.Controller.HasCurrentTrackingData,
            side.ControllerProfileKind,
            tracker is not null,
            opticalHasData,
            opticalHasData && IsJointActivelyTracked(tracker!, XRHandTracker.HandJoint.Wrist),
            ToOpticalSource(tracker?.HandTrackingSource)));
    }

    /// <summary>
    /// Refreshes the per-side controller interaction profile from the XR server's positional tracker. Runtimes such
    /// as WiVRn keep the controller tracker pose-alive but switch its bound profile (vendor controller device versus
    /// hand-input emulation — hand interaction, or the simple controller WiVRn emulates from the hands), which is the
    /// reliable optical discriminator for unknown hand-tracking sources.
    /// </summary>
    /// <remarks>
    /// The poll is rate-limited because profiles only change when the player picks up or puts down controllers; the
    /// cached kind keeps the physics tick free of repeated profile reads.
    /// </remarks>
    private void RefreshControllerProfiles()
    {
        ulong now = Time.GetTicksMsec();

        if (now < _nextControllerProfilePollMsec)
        {
            return;
        }

        _nextControllerProfilePollMsec = now + _controllerProfilePollIntervalMsec;

        foreach (OpenXRHandSide side in _sides.Values)
        {
            string? profile = (XRServer.GetTracker(side.ControllerTrackerName) as XRPositionalTracker)?.Profile;

            if (!string.Equals(profile, side.ControllerProfile, StringComparison.Ordinal))
            {
                side.ControllerProfile = profile;
                side.ControllerProfileKind = ToControllerProfileKind(profile);
            }
        }
    }

    /// <summary>
    /// Normalises a bound controller interaction-profile path into the policy's profile kind: empty paths and the
    /// runtime's <c>/interaction_profiles/none</c> startup placeholder mean no bound profile, <c>hand_interaction</c>
    /// paths (the action map binds <c>/interaction_profiles/ext/hand_interaction_ext</c>) and
    /// <c>simple_controller</c> paths (WiVRn never reports <c>hand_interaction</c> — it emulates
    /// <c>/interaction_profiles/khr/simple_controller</c> from the hands) mean hand-input emulation, and vendor device
    /// profiles (for example <c>/interaction_profiles/oculus/touch_controller</c>) mean a held physical controller.
    /// </summary>
    private static XRControllerProfileKind ToControllerProfileKind(string? profile)
        => string.IsNullOrEmpty(profile) || profile.Equals("/interaction_profiles/none", StringComparison.Ordinal)
            ? XRControllerProfileKind.None
            : profile.Contains("hand_interaction", StringComparison.Ordinal)
                ? XRControllerProfileKind.HandInteraction
                : profile.Contains("simple_controller", StringComparison.Ordinal)
                    ? XRControllerProfileKind.SimpleController
                    : XRControllerProfileKind.ControllerDevice;

    /// <summary>Classifies one joint sample through the gates <see cref="TryGetJoint" /> enforces.</summary>
    private static XRHandJointSourceRejection ClassifyJointSample(
        XRHandTracker tracker,
        XRHandJoint joint,
        XRHandTracker.HandJointFlags jointFlags,
        bool jointTransformFinite)
    {
        if (!IsJointOrientationUsable(jointFlags))
        {
            return XRHandJointSourceRejection.JointOrientationUnusable;
        }

        if (!jointTransformFinite)
        {
            return XRHandJointSourceRejection.JointNonFinite;
        }

        if (XRHandJoints.GetRequiredSourceParent(joint) is { } parent)
        {
            XRHandTracker.HandJoint sourceParent = ToGodotJoint(parent);

            XRHandTracker.HandJointFlags parentFlags = tracker.GetHandJointFlags(sourceParent);
            bool parentOrientationUsable = IsJointOrientationUsable(parentFlags);

            bool parentTransformFinite = parentOrientationUsable
                && XRHandTrackingMath.IsFinite(tracker.GetHandJointTransform(sourceParent));

            if (!parentOrientationUsable)
            {
                return XRHandJointSourceRejection.ParentOrientationUnusable;
            }

            if (!parentTransformFinite)
            {
                return XRHandJointSourceRejection.ParentNonFinite;
            }
        }

        return XRHandJointSourceRejection.None;
    }

    private static bool IsJointActivelyTracked(XRHandTracker tracker, XRHandTracker.HandJoint joint)
    {
        XRHandTracker.HandJointFlags flags = tracker.GetHandJointFlags(joint);

        return (flags & XRHandTracker.HandJointFlags.PositionTracked) != 0
               && (flags & XRHandTracker.HandJointFlags.OrientationTracked) != 0;
    }

    /// <summary>
    /// Rotation-only validity tier: a joint orientation is usable when the tracker reports it as tracked, or as valid
    /// but inferred (XR-002 TR20).
    /// </summary>
    private static bool IsJointOrientationUsable(XRHandTracker.HandJointFlags flags)
        => (flags & XRHandTracker.HandJointFlags.OrientationTracked) != 0
           || (flags & XRHandTracker.HandJointFlags.OrientationValid) != 0;

    private static XROpticalHandTrackingSource ToOpticalSource(XRHandTracker.HandTrackingSourceEnum? source)
    {
        return source switch
        {
            XRHandTracker.HandTrackingSourceEnum.Unobstructed => XROpticalHandTrackingSource.Unobstructed,
            XRHandTracker.HandTrackingSourceEnum.Controller => XROpticalHandTrackingSource.Controller,
            XRHandTracker.HandTrackingSourceEnum.NotTracked => XROpticalHandTrackingSource.NotTracked,
            XRHandTracker.HandTrackingSourceEnum.Unknown => XROpticalHandTrackingSource.Unknown,
            XRHandTracker.HandTrackingSourceEnum.Max => XROpticalHandTrackingSource.Unknown,
            null => XROpticalHandTrackingSource.Unknown,
            _ => XROpticalHandTrackingSource.Unknown,
        };
    }

    private static XRHandTracker.HandJoint ToGodotJoint(XRHandJoint joint)
        => joint switch
        {
            XRHandJoint.Wrist => XRHandTracker.HandJoint.Wrist,
            XRHandJoint.ThumbMetacarpal => XRHandTracker.HandJoint.ThumbMetacarpal,
            XRHandJoint.ThumbProximal => XRHandTracker.HandJoint.ThumbPhalanxProximal,
            XRHandJoint.ThumbDistal => XRHandTracker.HandJoint.ThumbPhalanxDistal,
            XRHandJoint.IndexMetacarpal => XRHandTracker.HandJoint.IndexFingerMetacarpal,
            XRHandJoint.IndexProximal => XRHandTracker.HandJoint.IndexFingerPhalanxProximal,
            XRHandJoint.IndexIntermediate => XRHandTracker.HandJoint.IndexFingerPhalanxIntermediate,
            XRHandJoint.IndexDistal => XRHandTracker.HandJoint.IndexFingerPhalanxDistal,
            XRHandJoint.MiddleMetacarpal => XRHandTracker.HandJoint.MiddleFingerMetacarpal,
            XRHandJoint.MiddleProximal => XRHandTracker.HandJoint.MiddleFingerPhalanxProximal,
            XRHandJoint.MiddleIntermediate => XRHandTracker.HandJoint.MiddleFingerPhalanxIntermediate,
            XRHandJoint.MiddleDistal => XRHandTracker.HandJoint.MiddleFingerPhalanxDistal,
            XRHandJoint.RingMetacarpal => XRHandTracker.HandJoint.RingFingerMetacarpal,
            XRHandJoint.RingProximal => XRHandTracker.HandJoint.RingFingerPhalanxProximal,
            XRHandJoint.RingIntermediate => XRHandTracker.HandJoint.RingFingerPhalanxIntermediate,
            XRHandJoint.RingDistal => XRHandTracker.HandJoint.RingFingerPhalanxDistal,
            XRHandJoint.LittleMetacarpal => XRHandTracker.HandJoint.PinkyFingerMetacarpal,
            XRHandJoint.LittleProximal => XRHandTracker.HandJoint.PinkyFingerPhalanxProximal,
            XRHandJoint.LittleIntermediate => XRHandTracker.HandJoint.PinkyFingerPhalanxIntermediate,
            XRHandJoint.LittleDistal => XRHandTracker.HandJoint.PinkyFingerPhalanxDistal,
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Unknown hand joint."),
        };

    private void RefreshTrackers()
    {
        foreach (OpenXRHandSide side in _sides.Values)
        {
            side.Tracker ??= XRServer.GetTracker(side.TrackerName) as XRHandTracker;
        }
    }

    private void OnTrackerAdded(StringName trackerName, long trackerType)
    {
        if (trackerType == (long)XRServer.TrackerType.Hand)
        {
            _logger.LogDebug("OpenXR hand tracker added: {TrackerName}.", trackerName);
        }

        RefreshTracker(trackerName);
    }

    private void OnTrackerUpdated(StringName trackerName, long trackerType)
    {
        _ = trackerType;

        RefreshTracker(trackerName);
    }

    private void OnTrackerRemoved(StringName trackerName, long trackerType)
    {
        _ = trackerType;

        foreach (OpenXRHandSide side in _sides.Values)
        {
            if (side.TrackerName == trackerName)
            {
                _logger.LogInformation("OpenXR hand tracker removed: {TrackerName}.", trackerName);
                side.Tracker = null;
            }
        }
    }

    private void RefreshTracker(string trackerName)
    {
        foreach (OpenXRHandSide side in _sides.Values)
        {
            if (side.TrackerName == trackerName)
            {
                var resolved = XRServer.GetTracker(side.TrackerName) as XRHandTracker;

                if (resolved is not null && side.Tracker is null)
                {
                    _logger.LogInformation("OpenXR hand tracker available: {TrackerName}.", trackerName);
                }

                side.Tracker = resolved;
            }
        }
    }

    private sealed class OpenXRHandSide(
        LimbSide side,
        StringName trackerName,
        StringName controllerTrackerName,
        XRNode3D palmNode,
        Node3D calibrationAnchorNode,
        OpenXRHandControllerNode controller)
    {
        public XRHandTracker? Tracker
        {
            get;
            set;
        }

        public XRHandSourceObservation Observation
        {
            get;
            set;
        } = XRHandSourceObservation.Ambiguous;

        /// <summary>Raw bound controller interaction-profile path, or <c>null</c> when none is bound.</summary>
        public string? ControllerProfile
        {
            get;
            set;
        }

        public XRControllerProfileKind ControllerProfileKind
        {
            get;
            set;
        } = XRControllerProfileKind.None;

        public XRWristFreezeCache WristCache { get; } = new();

        public LimbSide Side => side;

        public StringName TrackerName => trackerName;

        public StringName ControllerTrackerName => controllerTrackerName;

        public XRNode3D PalmNode => palmNode;

        public Node3D CalibrationAnchorNode => calibrationAnchorNode;

        public OpenXRHandControllerNode Controller => controller;
    }

    /// <summary>
    /// Per-side hand-pose source view over the adapter's committed mode, observations, and wrist caches.
    /// </summary>
    private sealed class OpenXRHandPoseSource(OpenXROpticalHandTracking owner, OpenXRHandSide side) : IXRHandPoseSource
    {
        /// <inheritdoc />
        public LimbSide Side => side.Side;

        /// <inheritdoc />
        public XRHandTrackingMode SelectedMode => owner._arbiter.CommittedMode;

        /// <inheritdoc />
        public XRHandSourceObservation Observation => side.Observation;

        /// <inheritdoc />
        public bool EverCapturedWrist => side.WristCache.EverCaptured;

        /// <inheritdoc />
        /// <remarks>
        /// In optical mode the frozen world-space wrist is served with freeze-on-loss semantics (XR-002 TR5, TR25);
        /// in controller mode the live calibrated controller hand-position anchor is returned unchanged
        /// (XR-002 TR8).
        /// </remarks>
        public bool TryGetCalibratedWristTransform(out Transform3D transform)
        {
            if (owner._arbiter.CommittedMode == XRHandTrackingMode.Optical)
            {
                return side.WristCache.TryGetTransform(out transform);
            }

            transform = side.Controller.HandPositionNode.GlobalTransform;

            return true;
        }
    }
}
