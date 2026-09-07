using AlleyCat.IK;
using AlleyCat.Rigging;
using AlleyCat.Rigging.Physics;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.Interaction.Hands;

/// <summary>
/// Godot node facade exposing BODY-001 Hands hand-pose control to scene consumers.
/// </summary>
[GlobalClass]
public sealed partial class HandPoseBehaviour : Node, IHand, IHandGrabLifecycle
{
    private static readonly StringName _rightHandBoneName = new("RightHand");
    private static readonly StringName _leftHandBoneName = new("LeftHand");
    private static readonly StringName _rightUpperArmBoneName = new("RightUpperArm");
    private static readonly StringName _leftUpperArmBoneName = new("LeftUpperArm");
    private static readonly StringName _rightLowerArmBoneName = new("RightLowerArm");
    private static readonly StringName _leftLowerArmBoneName = new("LeftLowerArm");

    /// <summary>Node metadata key counting the hands currently protecting a pending candidate body.</summary>
    private const string ApproachProtectionReferenceMetaKey = "alleycat_hand_approach_protection_references";

    private HandPoseController? _controller;
    private GrabAttachmentState? _attachmentState;
    private HeldCollisionProxyState? _heldCollisionProxyState;
    private PendingGrabState? _pendingGrabState;
    private PendingGrabCollisionProtectionState? _pendingGrabCollisionProtection;
    private readonly ReleaseVelocityTracker _releaseVelocityTracker = new();
    private readonly List<CollisionExceptionPair> _heldMovableCollisionExceptions = [];

    private HandGrabInputSource _grabInputSource = HandGrabInputSource.None;
    private Animation? ActiveGrabAnimationState
    {
        get;
        set;
    }

    private GrabPoseReference? ActiveGrabReferenceState
    {
        get;
        set;
    }

    private string? ActiveGrabRecognitionStrategyNameState
    {
        get;
        set;
    }
    private IOpticalGrabPresentationArbiter? _opticalGrabArbiter;
    private OpticalGrabPresentationOwner? _opticalGrabPresentationOwner;

    // This is an identity allocator only: it never retains a hand or any other Godot object.
    private static long _nextOpticalGrabPresentationGeneration;

    /// <summary>
    /// Gets or sets the animation tree controlled by this behaviour.
    /// </summary>
    [Export]
    public AnimationTree? AnimationTree
    {
        get;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                _controller = null;
            }

            field = value;
            TryInitialiseController();
        }
    }

    /// <inheritdoc />
    [Export]
    public LimbSide Side
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the clamped rest-to-pose blend weight for this hand side.
    /// </summary>
    public float PoseWeight
    {
        get => Side == LimbSide.Left ? LeftHandPoseWeight : RightHandPoseWeight;
        set
        {
            if (Side == LimbSide.Left)
            {
                LeftHandPoseWeight = value;
            }
            else
            {
                RightHandPoseWeight = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the target pose animation for this hand side; <see langword="null" /> clears the override.
    /// </summary>
    public Animation? Pose
    {
        get => Side == LimbSide.Left ? LeftHandPose : RightHandPose;
        set => SetPose(value);
    }

    /// <summary>
    /// Gets the currently applied pose animation for this hand side after transition state has settled.
    /// </summary>
    public Animation? CurrentPose => Side == LimbSide.Left ? CurrentLeftHandPose : CurrentRightHandPose;

    /// <inheritdoc />
    public IGrabbable? CurrentGrabbed
    {
        get;
        private set;
    }

    /// <inheritdoc />
    public HandGrabLifecycleState GrabLifecycle
        => CurrentGrabbed is not null
            ? HandGrabLifecycleState.Held
            : _pendingGrabState is not null
                ? HandGrabLifecycleState.Pending
                : HandGrabLifecycleState.None;

    /// <inheritdoc />
    public HandGrabInputSource GrabInputSource
    {
        get
        {
            HandGrabLifecycleState lifecycle = GrabLifecycle;
            return lifecycle == HandGrabLifecycleState.None ? HandGrabInputSource.None : _grabInputSource;
        }
    }

    /// <inheritdoc />
    public Animation? ActiveGrabAnimation => GrabLifecycle == HandGrabLifecycleState.None ? null : ActiveGrabAnimationState;

    /// <inheritdoc />
    public GrabPoseReference? ActiveGrabReference => GrabLifecycle == HandGrabLifecycleState.None ? null : ActiveGrabReferenceState;

    /// <inheritdoc />
    public string? ActiveGrabRecognitionStrategyName => GrabLifecycle == HandGrabLifecycleState.None
        ? null
        : ActiveGrabRecognitionStrategyNameState;

    /// <summary>
    /// Gets whether the most recently abandoned pending grab lost its selected source during its same-source
    /// refresh. This read-only diagnostic distinguishes candidate/reach loss from an attachment settlement wait.
    /// It resets when a new pending grab begins.
    /// </summary>
    public bool LastPendingGrabAbandonedForCandidateLoss
        => LastPendingGrabAbandonmentReason == HandGrabAbandonmentReason.CandidateLoss;

    /// <summary>
    /// Gets why the most recently abandoned pending grab ended, distinguishing bounded non-convergence — an
    /// unreachable or collision-limited commanded approach whose direct-attachment residual stopped shrinking
    /// outside the commit gate — from candidate loss (IK-005 TR22). It resets when a new pending grab begins.
    /// </summary>
    public HandGrabAbandonmentReason LastPendingGrabAbandonmentReason
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets whether the most recent Movable pending attempt reached its direct attachment gate but was rejected by
    /// the grabbable's stale-candidate guard. This read-only diagnostic isolates post-gate candidate freshness from
    /// reach, recognition, and attachment settlement; it resets when a new pending grab begins.
    /// </summary>
    public bool LastPendingMovableGrabRejectedByFreshness
    {
        get;
        private set;
    }

    /// <summary>
    /// Node whose global transform represents the associated hand for discovery queries.
    /// </summary>
    [ExportGroup("Grab")]
    [Export]
    public Node3D? HandTargetNode
    {
        get; set;
    }

    /// <summary>
    /// Bone attachment used as the parent for held objects.
    /// </summary>
    [Export]
    public BoneAttachment3D? HandBoneAttachment
    {
        get; set;
    }

    /// <summary>
    /// Optional collision object that receives temporary held-item runtime shape owners while movable items are held.
    /// </summary>
    [Export]
    public CollisionObject3D? HeldCollisionTarget
    {
        get; set;
    }

    /// <summary>
    /// Optional target provider used to drive hand IK smoothly toward the selected grab point.
    /// </summary>
    [Export]
    public HandGrabTargetProvider? GrabTargetProvider
    {
        get; set;
    }

    /// <summary>
    /// Optional physical rig used to resolve same-side hand, finger, and lower-arm proxy collision bodies.
    /// </summary>
    [Export]
    public DynamicPhysicalRig? PhysicalRig
    {
        get; set;
    }

    /// <summary>
    /// Godot group queried for discoverable grabbable objects.
    /// </summary>
    [Export]
    public StringName GrabbableGroupName { get; set; } = new("grabbable");

    /// <summary>
    /// Maximum hand-to-grab-point discovery distance in metres.
    /// </summary>
    [Export(PropertyHint.Range, "0.001,2,0.001,or_greater,suffix:m")]
    public float DiscoveryRangeMetres { get; set; } = 0.3f;

    /// <summary>
    /// Maximum hand-target distance from the selected approach target before a pending Immovable grab commits.
    /// </summary>
    [Export(PropertyHint.Range, "0.001,0.2,0.001,or_greater,suffix:m")]
    public float GrabCommitDistanceMetres { get; set; } = 0.025f;

    /// <summary>
    /// Maximum positional error for the Movable direct-attachment gate between a candidate's expected hand attachment
    /// transform and the actual hand bone attachment before that candidate can commit. The production default is 8 mm.
    /// </summary>
    [Export(PropertyHint.Range, "0.0001,0.02,0.0001,or_greater,suffix:m")]
    public float MovableAttachmentPositionToleranceMetres { get; set; } = 0.008f;

    /// <summary>
    /// Maximum rotational error between a Movable candidate's expected hand attachment transform and the actual
    /// hand bone attachment before that candidate can commit. The production default is 5 degrees.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,45,0.1,or_greater,suffix:°")]
    public float MovableAttachmentOrientationToleranceDegrees { get; set; } = 5.0f;

    /// <summary>
    /// Consecutive process frames for which a Movable hand attachment must meet its positional and rotational
    /// settlement tolerances before committing. The production default is two frames.
    /// </summary>
    [Export(PropertyHint.Range, "1,10,1,or_greater")]
    public int MovableAttachmentSettleProcessFrames { get; set; } = 2;

    /// <summary>
    /// Additional grab-point reach tolerance used only when refreshing a pending movable grab candidate.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.1,0.001,or_greater,suffix:m")]
    public float PendingMovableGrabAcquisitionToleranceMetres { get; set; } = 0.015f;

    /// <summary>
    /// Physics ticks a pending Movable grab may spend without improving its direct-attachment residual before
    /// the commanded approach is treated as non-convergent and abandoned (IK-005 TR22). Improvements reset the
    /// interval, so ordinary and slow approaches remain pending; the commit gate itself is unchanged. The
    /// interval counts authoritative physics ticks so it stays bounded in wall time regardless of process-frame
    /// rate.
    /// </summary>
    [Export(PropertyHint.Range, "10,600,1,or_greater")]
    public int MovableAttachmentNonConvergencePhysicsFrames { get; set; } = 90;

    /// <summary>
    /// Minimum direct-attachment residual improvement in metres that counts as convergence progress, resetting
    /// the non-convergence interval. Reach-limited or obstructed approaches plateau above this improvement and
    /// are abandoned once the interval elapses.
    /// </summary>
    [Export(PropertyHint.Range, "0.0001,0.01,0.0001,or_greater,suffix:m")]
    public float MovableAttachmentNonConvergenceImprovementMetres { get; set; } = 0.0005f;

    /// <summary>
    /// Margin added to the skeleton-measured arm envelope when bounding the Movable approach command: the
    /// command may never sit further from the live shoulder than upper+lower arm length plus this margin,
    /// because the arm cannot physically realise such a command and compensation there can only chase its own
    /// residual (IK-005 TR22).
    /// </summary>
    [Export(PropertyHint.Range, "0,0.2,0.005,or_greater,suffix:m")]
    public float MovableApproachCommandReachMarginMetres { get; set; } = 0.08f;

    /// <summary>
    /// How far the compensated Movable approach command may rebound beyond its initially calibrated offset
    /// from the settlement destination. A stable target-to-attachment offset is legitimate compensation and is
    /// preserved in full; only growth beyond this rebound allowance — the command chasing its own solver
    /// residual — is clamped, so reach-limited or obstructed compensation cannot compound into an unbounded
    /// moving-goal loop (IK-005 TR22). The residual plateau is then reported as bounded non-convergence.
    /// </summary>
    [Export(PropertyHint.Range, "0.001,0.05,0.001,or_greater,suffix:m")]
    public float MovableApproachCommandMaximumReboundMetres { get; set; } = 0.01f;

    /// <summary>
    /// Minimum tracked release speed transferred to a released movable rigid body.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01,or_greater,suffix:m/s")]
    public float ThrowMinimumReleaseSpeedMetresPerSecond { get; set; } = 0.05f;

    /// <summary>
    /// Candidate-body linear speed above which a pending Movable grab treats its candidate as moving. A
    /// candidate whose measured speed stays above this bound for
    /// <see cref="PendingMovableGrabMovingCandidatePhysicsFrames" /> consecutive physics ticks is abandoned
    /// with <see cref="HandGrabAbandonmentReason.MovingCandidate" /> — the approach is chasing a live
    /// destination rather than converging onto a settled one. Any below-threshold sample resets the interval.
    /// </summary>
    [Export(PropertyHint.Range, "0.005,1,0.005,or_greater,suffix:m/s")]
    public float PendingMovableGrabMovingCandidateSpeedMetresPerSecond { get; set; } = 0.05f;

    /// <summary>
    /// Sustained-motion interval, in authoritative physics ticks, after which a persistently moving pending
    /// candidate is abandoned for <see cref="HandGrabAbandonmentReason.MovingCandidate" />. The candidate
    /// must measure above <see cref="PendingMovableGrabMovingCandidateSpeedMetresPerSecond" /> for this many
    /// consecutive physics ticks; any below-threshold sample restarts the interval. The 15-tick (0.25 s)
    /// production default is shorter than a typical approach convergence so a persistently moving candidate
    /// is abandoned before its chase can masquerade as non-convergence.
    /// </summary>
    [Export(PropertyHint.Range, "5,600,1,or_greater")]
    public int PendingMovableGrabMovingCandidatePhysicsFrames { get; set; } = 15;

    /// <summary>
    /// Maximum tracked release speed transferred to a released movable rigid body.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,30,0.1,or_greater,suffix:m/s")]
    public float ThrowMaximumReleaseSpeedMetresPerSecond { get; set; } = 8.0f;

    /// <summary>
    /// Blend factor for recent held-object velocity samples; higher values favour the latest frame.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ThrowVelocitySmoothingFactor { get; set; } = 0.35f;

    /// <summary>
    /// Gets or sets the target left hand pose animation.
    /// </summary>
    [Export]
    public Animation? LeftHandPose
    {
        get => _controller?.LeftHandPose;
        set
        {
            if (_controller is null)
            {
                _leftHandPose = value;
                return;
            }

            _controller.LeftHandPose = value;
        }
    }

    /// <summary>
    /// Gets or sets the target right hand pose animation.
    /// </summary>
    [Export]
    public Animation? RightHandPose
    {
        get => _controller?.RightHandPose;
        set
        {
            if (_controller is null)
            {
                _rightHandPose = value;
                return;
            }

            _controller.RightHandPose = value;
        }
    }

    /// <summary>
    /// Gets or sets the clamped left rest-to-pose blend weight.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float LeftHandPoseWeight
    {
        get => _controller?.LeftHandPoseWeight ?? _leftHandPoseWeight;
        set
        {
            _leftHandPoseWeight = Mathf.Clamp(value, 0f, 1f);
            if (_controller is HandPoseController controller)
            {
                controller.LeftHandPoseWeight = _leftHandPoseWeight;
            }
        }
    }

    /// <summary>
    /// Gets or sets the clamped right rest-to-pose blend weight.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float RightHandPoseWeight
    {
        get => _controller?.RightHandPoseWeight ?? _rightHandPoseWeight;
        set
        {
            _rightHandPoseWeight = Mathf.Clamp(value, 0f, 1f);
            if (_controller is HandPoseController controller)
            {
                controller.RightHandPoseWeight = _rightHandPoseWeight;
            }
        }
    }

    /// <summary>
    /// Gets or sets the hand-pose activation transition duration.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater")]
    public float TransitionDuration
    {
        get => _controller?.TransitionDuration ?? _transitionDuration;
        set
        {
            _transitionDuration = Mathf.Max(0f, value);
            if (_controller is HandPoseController controller)
            {
                controller.TransitionDuration = _transitionDuration;
            }
        }
    }

    /// <summary>
    /// Gets the currently applied left hand pose after transition state has settled.
    /// </summary>
    public Animation? CurrentLeftHandPose => _controller?.CurrentLeftHandPose;

    /// <summary>
    /// Gets the currently applied right hand pose after transition state has settled.
    /// </summary>
    public Animation? CurrentRightHandPose => _controller?.CurrentRightHandPose;

    private Animation? _leftHandPose;
    private Animation? _rightHandPose;
    private float _leftHandPoseWeight = 1f;
    private float _rightHandPoseWeight = 1f;
    private float _transitionDuration = 0.2f;

    /// <inheritdoc />
    public override void _Ready()
    {
        AnimationTree ??= GetParentOrNull<AnimationTree>();
        TryInitialiseController();
        if (_controller is null)
        {
            GD.PushWarning($"{nameof(HandPoseBehaviour)} '{Name}' is waiting for an AnimationTree binding.");
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
        // Teardown determinism: the arbitration publication must not outlive this hand (for example across
        // integration-test fixtures), even when the tree is freed without an explicit release.
        => ClearActiveGrabInputState();

    private void TryInitialiseController()
    {
        if (_controller is not null || AnimationTree is null)
        {
            return;
        }

        AnimationTree.Active = true;

        _controller = HandPoseController.GetOrCreate(AnimationTree);
        _controller.TransitionDuration = _transitionDuration;
        if (Side == LimbSide.Left)
        {
            _controller.LeftHandPoseWeight = _leftHandPoseWeight;
            _controller.SetHandPose(LimbSide.Left, _leftHandPose, immediate: true);
        }
        else
        {
            _controller.RightHandPoseWeight = _rightHandPoseWeight;
            _controller.SetHandPose(LimbSide.Right, _rightHandPose, immediate: true);
        }
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_controller is not null && !IsInstanceValid(_controller.AnimationTree))
        {
            _controller = null;
            AnimationTree = IsInsideTree() ? GetParentOrNull<AnimationTree>() : null;
            TryInitialiseController();
        }

        // Commit may select the authored hand pose this frame. Process it before the animation-tree controller
        // advances so its transition window begins in the same frame as Held publication and the modifier's
        // handoff window; otherwise the modifier could relinquish ownership one frame early.
        TryCommitPendingGrab();
        _controller?.Update(Side, delta);
        UpdateHeldReleaseVelocity(delta);
    }

    /// <summary>
    /// Sets or clears the pose for this hand, optionally overriding the weight and bypassing smoothing.
    /// </summary>
    public void SetPose(Animation? pose, float? weight = null, bool immediate = false)
        => SetHandPose(Side, pose, weight, immediate);

    /// <summary>
    /// Sets or clears a hand pose for the requested side, optionally overriding the weight and bypassing smoothing.
    /// </summary>
    public void SetHandPose(LimbSide side, Animation? pose, float? weight = null, bool immediate = false)
    {
        if (_controller is null)
        {
            if (side == LimbSide.Left)
            {
                _leftHandPose = pose;
                if (weight.HasValue)
                {
                    _leftHandPoseWeight = Mathf.Clamp(weight.Value, 0f, 1f);
                }
            }
            else
            {
                _rightHandPose = pose;
                if (weight.HasValue)
                {
                    _rightHandPoseWeight = Mathf.Clamp(weight.Value, 0f, 1f);
                }
            }

            return;
        }

        _controller.SetHandPose(side, pose, weight, immediate);
    }

    /// <summary>
    /// Clears the requested hand pose override.
    /// </summary>
    public void ClearHandPose(LimbSide side, bool immediate = false)
        => _controller?.ClearHandPose(side, immediate);

    /// <inheritdoc />
    public void ClearPose(bool immediate = false) => ClearHandPose(Side, immediate);

    /// <inheritdoc />
    public IGrabbable? Grab()
    {
        if (CurrentGrabbed is not null)
        {
            return CurrentGrabbed;
        }

        if (_pendingGrabState is not null)
        {
            TryCommitPendingGrab();
            return CurrentGrabbed;
        }

        Transform3D handTransform = ResolveQueryHandTransform();
        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            EnumerateDiscoverableGrabbables(),
            Side,
            handTransform,
            DiscoveryRangeMetres);

        if (selection is null)
        {
            // No grab begins, so no provenance may linger from an immediately preceding BeginGrab call.
            ClearActiveGrabInputState();
            return null;
        }

        LastPendingGrabAbandonmentReason = HandGrabAbandonmentReason.None;
        LastPendingMovableGrabRejectedByFreshness = false;
        // The approach compensation relation is sampled from the realised target and solved attachment; the
        // acquisition query above already consumed the independent canonical-epoch source intent. The command's
        // initially calibrated offset from the destination bounds its later rebound (IK-005 TR22).
        Transform3D expectedAttachment = ResolveExpectedAttachmentTransform(selection.Candidate);
        Transform3D uncompensatedApproach = ResolveApproachHandTarget(selection.Candidate, ResolveHandTransform());
        float initialCommandOffset = uncompensatedApproach.Origin.DistanceTo(expectedAttachment.Origin);
        _ = TryResolvePendingReachBound(out Skeleton3D? reachSkeleton, out int shoulderBoneIndex, out float armReachBound);
        Transform3D approachHandTarget = uncompensatedApproach;
        GrabTargetProvider?.SetGrabTarget(approachHandTarget);
        _pendingGrabState = new PendingGrabState(
            selection.Grabbable,
            selection.Candidate,
            approachHandTarget,
            expectedAttachment)
        {
            InitialCommandOffsetFromDestinationMetres = initialCommandOffset,
            ReachSkeleton = reachSkeleton,
            ShoulderBoneIndex = shoulderBoneIndex,
            ArmReachBoundMetres = armReachBound,
        };
        ActiveGrabReferenceState = selection.Candidate.ValidatedReference
            ?? throw new InvalidOperationException("The selected grab candidate must carry a validated pose reference.");
        ActiveGrabAnimationState = ActiveGrabReferenceState.Animation;
        ActiveGrabRecognitionStrategyNameState = selection.Candidate.GripRecognitionStrategyName;
        UpdatePendingGrabCollisionProtection(selection.Grabbable);
        PublishOpticalPendingPresentation();

        return CurrentGrabbed;
    }

    /// <inheritdoc />
    public IGrabbable? BeginGrab(HandGrabInputSource inputSource)
    {
        if (CurrentGrabbed is not null || _pendingGrabState is not null)
        {
            return CurrentGrabbed;
        }

        _grabInputSource = inputSource;
        return Grab();
    }

    /// <inheritdoc />
    public bool TryGetCurrentGrabCandidate(out GrabCandidateObservation? candidate)
    {
        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            EnumerateDiscoverableGrabbables(),
            Side,
            ResolveQueryHandTransform(),
            DiscoveryRangeMetres);

        if (selection is null)
        {
            candidate = null;
            return false;
        }

        candidate = new GrabCandidateObservation(
            selection.Grabbable,
            selection.Candidate.Source,
            selection.Candidate.Animation,
            selection.Candidate.ValidatedReference
                ?? throw new InvalidOperationException("The observed grab candidate must carry a validated pose reference."),
            selection.Candidate.GripRecognitionStrategyName);
        return true;
    }

    /// <summary>
    /// Gets a side-effect-free measurement of the current best eligible candidate, including the acquisition
    /// distance and the direct BoneAttachment3D target used by the Movable commit gate. This diagnostic performs
    /// the same selection as <see cref="Grab" /> and does not initiate or alter a grab.
    /// </summary>
    public bool TryGetCurrentGrabCandidateMeasurement(out GrabCandidateMeasurement? measurement)
    {
        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            EnumerateDiscoverableGrabbables(),
            Side,
            ResolveQueryHandTransform(),
            DiscoveryRangeMetres);

        if (selection is null)
        {
            measurement = null;
            return false;
        }

        GrabPointCandidate candidate = selection.Candidate;
        measurement = new GrabCandidateMeasurement(
            selection.Grabbable,
            candidate.Source,
            candidate.Animation,
            candidate.AcquisitionDistance,
            candidate.GrabPointTransform,
            ResolveExpectedAttachmentTransform(candidate));
        return true;
    }

    /// <summary>
    /// Gets the direct hand-attachment transform currently required by a pending movable candidate. This is a
    /// read-only diagnostic of the actual 8 mm/5° commit gate, intended for runtime verification without
    /// substituting an IK target or modifying skeleton state.
    /// </summary>
    public bool TryGetPendingExpectedAttachmentTransform(out Transform3D expectedAttachmentTransform)
    {
        if (_pendingGrabState is not PendingGrabState pending || pending.Grabbable.Mobility != GrabbableMobility.Movable)
        {
            expectedAttachmentTransform = Transform3D.Identity;
            return false;
        }

        expectedAttachmentTransform = pending.CurrentSettlementTransform;
        return true;
    }

    /// <summary>
    /// Gets the approach destination most recently sent to the grab target provider for a pending Movable grab.
    /// This can differ from the current settlement destination while sub-tolerance observations accumulate.
    /// </summary>
    public bool TryGetPendingLastCommandedApproachTransform(out Transform3D approachTransform)
    {
        if (_pendingGrabState is not PendingGrabState pending || pending.Grabbable.Mobility != GrabbableMobility.Movable)
        {
            approachTransform = Transform3D.Identity;
            return false;
        }

        approachTransform = pending.LastCommandedApproachTransform;
        return true;
    }

    /// <inheritdoc />
    public bool CancelPendingGrab()
    {
        if (_pendingGrabState is null)
        {
            return false;
        }

        AbandonPendingGrab();
        return true;
    }

    /// <inheritdoc />
    public void Release()
    {
        if (CurrentGrabbed is null && _pendingGrabState is null)
        {
            return;
        }

        IGrabbable? grabbed = CurrentGrabbed;
        Vector3 releaseVelocity = ResolveReleaseVelocity(grabbed);
        CurrentGrabbed = null;
        _pendingGrabState = null;
        ClearActiveGrabInputState();
        _releaseVelocityTracker.Reset();
        ClearHeldMovableCollisionExceptions();
        ClearHeldCollisionProxies();
        RestoreGrabbedNodeParent();
        ClearPose();
        GrabTargetProvider?.ReleaseGrabTarget();
        grabbed?.ReleaseIfSupported();
        ApplyReleaseVelocity(grabbed, releaseVelocity);
    }

    private Transform3D ResolveHandTransform()
        => HandTargetNode is not null && IsInstanceValid(HandTargetNode)
            ? HandTargetNode.GlobalTransform
            : HandBoneAttachment is not null && IsInstanceValid(HandBoneAttachment)
            ? HandBoneAttachment.GlobalTransform
            : Transform3D.Identity;

    /// <summary>
    /// Transform used by acquisition queries: pending-grab refresh, eligibility observation, and candidate
    /// selection. It consumes the independent XR source intent sampled at the canonical pipeline epoch, so
    /// assisted or compensated physical-target movement cannot move the query; it falls back to the realised
    /// hand transform when no usable canonical source sample exists, for example in component fixtures without
    /// a wired default source (IK-005 TR19-TR21; INTR-002 recovery boundaries).
    /// </summary>
    private Transform3D ResolveQueryHandTransform()
    {
        return GrabTargetProvider is not null
            && IsInstanceValid(GrabTargetProvider)
            && GrabTargetProvider.TryGetSourceIntent(out IKTargetIntent sourceIntent)
            ? sourceIntent.WorldTransform
            : ResolveHandTransform();
    }

    private Transform3D ResolveApproachHandTarget(GrabPointCandidate candidate, Transform3D queryHandTransform)
    {
        if (HandTargetNode is null
            || !IsInstanceValid(HandTargetNode)
            || GrabTargetProvider is null
            || !IsInstanceValid(GrabTargetProvider)
            || HandBoneAttachment is null
            || !IsInstanceValid(HandBoneAttachment))
        {
            return candidate.HandTarget;
        }

        Transform3D targetToAttachment = queryHandTransform.AffineInverse() * HandBoneAttachment.GlobalTransform;
        Transform3D desiredAttachmentTransform = candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();
        return desiredAttachmentTransform * targetToAttachment.AffineInverse();
    }

    private IEnumerable<IGrabbable> EnumerateDiscoverableGrabbables()
    {
        SceneTree? tree = GetTree();
        if (tree is null)
        {
            yield break;
        }

        foreach (Node node in tree.GetNodesInGroup(GrabbableGroupName))
        {
            if (node is IGrabbable grabbable)
            {
                yield return grabbable;
            }
        }
    }

    private void AttachGrabbedNode(Node3D grabbedNode, GrabPointCandidate candidate)
    {
        BoneAttachment3D handBoneAttachment = HandBoneAttachment
            ?? throw new InvalidOperationException("Cannot attach a grabbed node without a hand bone attachment.");
        Node? previousParent = grabbedNode.GetParent();
        _attachmentState = new GrabAttachmentState(grabbedNode, previousParent, grabbedNode.GetIndex());

        Transform3D selectedGrabPointGlobal = candidate.GrabPointTransform;
        Transform3D grabPointInGrabbedSpace = grabbedNode.GlobalTransform.AffineInverse() * selectedGrabPointGlobal;
        Transform3D alignedGrabbedLocalTransform = candidate.GrabPointOffsetFromHand * grabPointInGrabbedSpace.AffineInverse();

        grabbedNode.Reparent(handBoneAttachment, keepGlobalTransform: true);
        grabbedNode.Transform = alignedGrabbedLocalTransform;
    }

    private void TryCommitPendingGrab()
    {
        if (_pendingGrabState is not PendingGrabState pending)
        {
            return;
        }

        // Physical relations (Immovable settling distance, approach compensation) read the realised target;
        // every acquisition query consumes the canonical-epoch source intent so assisted movement cannot move
        // the query (IK-005 TR19-TR21).
        Transform3D realisedTargetTransform = ResolveHandTransform();
        Transform3D sourceQueryTransform = ResolveQueryHandTransform();
        if (pending.Grabbable.Mobility == GrabbableMobility.Immovable)
        {
            GrabPointCandidate? refreshedCandidate = RefreshPendingGrabCandidate(
                pending,
                sourceQueryTransform,
                acquisitionToleranceMetres: 0.0f,
                requireInitialContent: false);
            if (refreshedCandidate is null)
            {
                AbandonPendingGrabFor(HandGrabAbandonmentReason.CandidateLoss);
                return;
            }
            float originalDistanceToCommit = realisedTargetTransform.Origin.DistanceTo(pending.ApproachHandTarget.Origin);
            bool originalCandidateSettled = originalDistanceToCommit <= GrabCommitDistanceMetres;
            float refreshedDistanceToCommit = refreshedCandidate is null
                ? float.PositiveInfinity
                : realisedTargetTransform.Origin.DistanceTo(
                    ResolveApproachHandTarget(refreshedCandidate, realisedTargetTransform).Origin);
            bool refreshedCandidateSettled = refreshedDistanceToCommit <= GrabCommitDistanceMetres;
            if (!originalCandidateSettled && !refreshedCandidateSettled)
            {
                return;
            }

            CommitImmovableGrab(pending, refreshedCandidate, originalCandidateSettled, refreshedCandidateSettled);
            return;
        }

        GrabPointCandidate? refreshedMovableCandidate = RefreshPendingGrabCandidate(
            pending,
            pending.RetainedContactHandTransform,
            PendingMovableGrabAcquisitionToleranceMetres,
            requireInitialContent: true);
        if (refreshedMovableCandidate is null)
        {
            AbandonPendingGrabFor(HandGrabAbandonmentReason.CandidateLoss);
            return;
        }

        GrabPointCandidate? currentEligibilityObservation = pending.Grabbable.GetGrabPoint(
            Side,
            sourceQueryTransform,
            PendingMovableGrabAcquisitionToleranceMetres);
        bool currentContentUnavailable = currentEligibilityObservation is not null
            && !currentEligibilityObservation.TryGetValidatedReference(out _, out _);
        float currentDistanceToRetainedContact = sourceQueryTransform.Origin.DistanceTo(
            refreshedMovableCandidate.GrabPointTransform.Origin);
        bool currentIdentityChanged = currentEligibilityObservation is not null
            && !currentContentUnavailable
            && !IsSamePendingCandidateIdentity(pending, currentEligibilityObservation, requireInitialContent: true);
        bool currentReachLost = currentEligibilityObservation is null
            && currentDistanceToRetainedContact > DiscoveryRangeMetres + PendingMovableGrabAcquisitionToleranceMetres;
        if (currentContentUnavailable || currentIdentityChanged || currentReachLost)
        {
            AbandonPendingGrabFor(HandGrabAbandonmentReason.CandidateLoss);
            return;
        }

        pending.CurrentEligibilityObservation = currentEligibilityObservation;

        // Pending-candidate collision protection follows the live pending grabbable: when the protected body
        // changes (a candidate switch during refresh), the previous body's protection reverts and the new
        // body's applies; an unchanged body is a no-op (INTR-002 pending approach protection).
        UpdatePendingGrabCollisionProtection(pending.Grabbable);

        if (HandBoneAttachment is null || !IsInstanceValid(HandBoneAttachment))
        {
            AbandonPendingGrab();
            return;
        }

        RefreshMovablePendingTarget(pending, refreshedMovableCandidate, realisedTargetTransform, sourceQueryTransform);
        TrackMovableAttachmentNonConvergence(pending);
        if (IsPendingCandidatePersistentlyMoving(pending))
        {
            // A persistently moving candidate keeps relocating the settlement destination the approach must
            // converge onto; distinguish that live chase from a stuck command (INTR-002 recovery boundaries).
            AbandonPendingGrabFor(HandGrabAbandonmentReason.MovingCandidate);
            return;
        }

        ulong ticksSinceImprovement = Engine.GetPhysicsFrames() - pending.LastImprovementPhysicsFrame;
        if (ticksSinceImprovement >= (ulong)Math.Max(1, MovableAttachmentNonConvergencePhysicsFrames))
        {
            // Bounded non-convergence: the direct-attachment residual stopped shrinking outside the commit gate,
            // so the commanded approach is unreachable or collision-limited. Abandon through the ordinary path —
            // the gate itself is never relaxed and no further compensation is commanded (IK-005 TR22).
            AbandonPendingGrabFor(HandGrabAbandonmentReason.NonConvergence);
            return;
        }

        if (!UpdateMovableAttachmentSettlement(pending))
        {
            return;
        }

        GrabPointCandidate commitCandidate = pending.CurrentSettlementCandidate;
        if (pending.Grabbable is not Node3D grabbedNode || !pending.Grabbable.Grab(commitCandidate))
        {
            LastPendingMovableGrabRejectedByFreshness = true;
            _pendingGrabState = null;
            ClearActiveGrabInputState();
            GrabTargetProvider?.ReleaseGrabTarget();
            return;
        }

        try
        {
            AttachGrabbedNode(grabbedNode, commitCandidate!);
            CreateHeldCollisionProxies(grabbedNode);
            // Revert the pending approach protection before the held exceptions apply so the identical
            // body pairs transition synchronously — no gap, no double application.
            ClearPendingGrabCollisionProtection();
            AddHeldMovableCollisionExceptions(grabbedNode);
            _releaseVelocityTracker.Reset(grabbedNode.GlobalPosition);
            // The AnimationTree must already be transitioning to the candidate pose before the modifier is told
            // to hand off pending assistance to committed authored ownership.
            SetPose(commitCandidate.Animation);
            CurrentGrabbed = pending.Grabbable;
            _pendingGrabState = null;
            ActiveGrabAnimationState = commitCandidate.Animation;
            ActiveGrabReferenceState = commitCandidate.ValidatedReference;
            ActiveGrabRecognitionStrategyNameState = commitCandidate.GripRecognitionStrategyName;
            PublishOpticalGrabPresentation();
            GrabTargetProvider?.BeginHeldCarry();
        }
        catch
        {
            ClearHeldMovableCollisionExceptions();
            ClearHeldCollisionProxies();
            RestoreGrabbedNodeParent();
            pending.Grabbable.ReleaseIfSupported();
            _pendingGrabState = null;
            ClearActiveGrabInputState();
            GrabTargetProvider?.ReleaseGrabTarget();
            throw;
        }
    }

    private GrabPointCandidate? RefreshPendingGrabCandidate(
        PendingGrabState pending,
        Transform3D currentHandTransform,
        float acquisitionToleranceMetres,
        bool requireInitialContent)
    {
        GrabPointCandidate? refreshedCandidate = pending.Grabbable.GetGrabPoint(
            Side,
            currentHandTransform,
            acquisitionToleranceMetres);
        return refreshedCandidate is not null
            && refreshedCandidate.TryGetValidatedReference(out _, out _)
            && IsSamePendingCandidateIdentity(pending, refreshedCandidate, requireInitialContent)
            ? refreshedCandidate
            : null;
    }

    private static bool IsSamePendingCandidateIdentity(
        PendingGrabState pending,
        GrabPointCandidate candidate,
        bool requireInitialContent)
        => ReferenceEquals(candidate.Source, pending.InitialCandidateSource)
            && (!requireInitialContent || ReferenceEquals(candidate.ValidatedReference, pending.InitialCandidateReference));

    private void RefreshMovablePendingTarget(
        PendingGrabState pending,
        GrabPointCandidate? refreshedCandidate,
        Transform3D realisedTargetTransform,
        Transform3D sourceQueryTransform)
    {
        if (refreshedCandidate is null)
        {
            return;
        }

        Transform3D proposedSettlementTransform = ResolveExpectedAttachmentTransform(refreshedCandidate);
        // The compensation relation is sampled from the realised target and the solved attachment — the frames
        // the prediction actually relates. The canonical source intent never enters the relation, and the
        // relation never enters an acquisition query (IK-005 TR19-TR21). The command may rebound beyond its
        // calibrated offset only within the bounded allowance, so a reach-limited residual cannot compound
        // into an unbounded moving-goal loop.
        Transform3D proposedApproachTransform = ResolveApproachHandTarget(refreshedCandidate, realisedTargetTransform);
        if (IsMovableApproachCommandUnbounded(proposedApproachTransform, proposedSettlementTransform, pending))
        {
            // The re-derived compensation would move the command beyond its calibrated offset or beyond the
            // arm's physical reach — the command chasing its own solver residual. Hold the last commanded
            // approach instead: the residual plateau is reported as bounded non-convergence, and no sweep
            // drags the body, acquisition query, or item along (IK-005 TR22).
            proposedApproachTransform = pending.LastCommandedApproachTransform;
        }
        if (!IsAttachmentTransformWithinTolerance(pending.CurrentSettlementTransform, proposedSettlementTransform))
        {
            // The candidate's expected attachment destination materially moved; the approach follows it and
            // non-convergence accounting restarts, distinguishing a live chase from a stuck command
            // (INTR-002 R17; IK-005 TR22). The chase recalibrates its command offset around the new
            // destination.
            pending.BestAttachmentResidualMetres = float.PositiveInfinity;
            pending.LastImprovementPhysicsFrame = Engine.GetPhysicsFrames();
            pending.InitialCommandOffsetFromDestinationMetres = proposedApproachTransform.Origin.DistanceTo(
                proposedSettlementTransform.Origin);
        }

        pending.CurrentSettlementCandidate = refreshedCandidate;
        pending.CurrentSettlementTransform = proposedSettlementTransform;
        if (IsAttachmentTransformWithinTolerance(pending.LastCommandedApproachTransform, proposedApproachTransform))
        {
            return;
        }

        pending.ConsecutiveAttachmentSettleFrames = 0;
        pending.ApproachHandTarget = proposedApproachTransform;
        pending.LastCommandedApproachTransform = proposedApproachTransform;
        // Retained contact records the independent canonical-epoch source intent at the moment the command was
        // refreshed; assisted physical-target movement can never move it.
        pending.RetainedContactHandTransform = sourceQueryTransform;
        GrabTargetProvider?.SetGrabTarget(proposedApproachTransform);
        ActiveGrabAnimationState = refreshedCandidate.Animation;
        ActiveGrabReferenceState = refreshedCandidate.ValidatedReference;
        ActiveGrabRecognitionStrategyNameState = refreshedCandidate.GripRecognitionStrategyName;
        PublishOpticalPendingPresentation();
    }

    /// <summary>
    /// Updates bounded non-convergence accounting from the direct-attachment residual: any improvement beyond
    /// <see cref="MovableAttachmentNonConvergenceImprovementMetres" /> below the best observed residual stamps
    /// the current physics tick, restarting the bounded interval; a plateau leaves the stamp behind
    /// (IK-005 TR22).
    /// </summary>
    private void TrackMovableAttachmentNonConvergence(PendingGrabState pending)
    {
        if (HandBoneAttachment is null || !IsInstanceValid(HandBoneAttachment))
        {
            return;
        }

        Transform3D actualAttachment = HandBoneAttachment.GlobalTransform;
        if (!IsFinite(actualAttachment) || !IsFinite(pending.CurrentSettlementTransform))
        {
            return;
        }

        float residual = pending.CurrentSettlementTransform.Origin.DistanceTo(actualAttachment.Origin);
        if (residual < pending.BestAttachmentResidualMetres - Mathf.Max(0f, MovableAttachmentNonConvergenceImprovementMetres))
        {
            pending.BestAttachmentResidualMetres = residual;
            pending.LastImprovementPhysicsFrame = Engine.GetPhysicsFrames();
        }
    }

    /// <summary>
    /// Tracks whether the pending candidate's physics body is persistently moving: a linear speed sample
    /// above <see cref="PendingMovableGrabMovingCandidateSpeedMetresPerSecond" /> starts (or continues) a
    /// physics-tick interval; any below-threshold sample resets it. The candidate counts as persistently
    /// moving once the interval spans
    /// <see cref="PendingMovableGrabMovingCandidatePhysicsFrames" /> authoritative physics ticks. Candidates
    /// without a physics body (for example <see cref="GrabbableNode" />) never count; frozen bodies measure
    /// zero speed and likewise never count.
    /// </summary>
    private bool IsPendingCandidatePersistentlyMoving(PendingGrabState pending)
    {
        if (pending.Grabbable is not RigidBody3D candidateBody || !IsInstanceValid(candidateBody))
        {
            pending.MovingCandidateSincePhysicsFrame = 0;
            return false;
        }

        float speedThreshold = Mathf.Max(0f, PendingMovableGrabMovingCandidateSpeedMetresPerSecond);
        if (candidateBody.LinearVelocity.LengthSquared() <= speedThreshold * speedThreshold)
        {
            pending.MovingCandidateSincePhysicsFrame = 0;
            return false;
        }

        ulong physicsFrame = Engine.GetPhysicsFrames();
        if (pending.MovingCandidateSincePhysicsFrame == 0)
        {
            pending.MovingCandidateSincePhysicsFrame = physicsFrame;
            return false;
        }

        return physicsFrame - pending.MovingCandidateSincePhysicsFrame
            >= (ulong)Math.Max(1, PendingMovableGrabMovingCandidatePhysicsFrames);
    }

    private bool UpdateMovableAttachmentSettlement(PendingGrabState pending)
    {
        if (HandBoneAttachment is null || !IsInstanceValid(HandBoneAttachment))
        {
            return false;
        }

        Transform3D actualAttachment = HandBoneAttachment.GlobalTransform;
        if (!IsAttachmentTransformWithinTolerance(pending.CurrentSettlementTransform, actualAttachment))
        {
            pending.ConsecutiveAttachmentSettleFrames = 0;
            return false;
        }

        pending.ConsecutiveAttachmentSettleFrames++;
        return pending.ConsecutiveAttachmentSettleFrames >= Math.Max(1, MovableAttachmentSettleProcessFrames);
    }

    private static Transform3D ResolveExpectedAttachmentTransform(GrabPointCandidate candidate)
        => candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse();

    /// <summary>
    /// Whether the re-derived Movable approach command exceeds its bounded compensation envelope: its offset
    /// from the settlement destination has rebounded beyond the calibrated offset plus the allowance, or the
    /// command sits beyond the arm's physical reach around the live shoulder. A stable offset — however large —
    /// stays bounded; only growth characteristic of the command chasing its own solver residual is unbounded
    /// (IK-005 TR22).
    /// </summary>
    private bool IsMovableApproachCommandUnbounded(
        Transform3D approach,
        Transform3D settlementDestination,
        PendingGrabState pending)
    {
        float proposedOffset = approach.Origin.DistanceTo(settlementDestination.Origin);
        return proposedOffset > pending.InitialCommandOffsetFromDestinationMetres + Mathf.Max(0f, MovableApproachCommandMaximumReboundMetres) || (pending.ReachSkeleton is { } skeleton
            && IsInstanceValid(skeleton)
            && pending.ShoulderBoneIndex >= 0
            && float.IsFinite(pending.ArmReachBoundMetres)
            && (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(pending.ShoulderBoneIndex).Origin)
            .DistanceTo(approach.Origin) > pending.ArmReachBoundMetres);
    }

    /// <summary>
    /// Resolves the skeleton-measured arm envelope used to bound the Movable approach command. Skeletons
    /// without the side's arm chain disable the reach bound.
    /// </summary>
    private bool TryResolvePendingReachBound(out Skeleton3D? skeleton, out int shoulderBoneIndex, out float armReachBoundMetres)
    {
        skeleton = HandBoneAttachment?.GetParent() as Skeleton3D;
        if (skeleton is null || !IsInstanceValid(skeleton))
        {
            skeleton = null;
            shoulderBoneIndex = -1;
            armReachBoundMetres = float.PositiveInfinity;
            return false;
        }

        StringName upperArmBoneName = Side == LimbSide.Left ? _leftUpperArmBoneName : _rightUpperArmBoneName;
        StringName lowerArmBoneName = Side == LimbSide.Left ? _leftLowerArmBoneName : _rightLowerArmBoneName;
        StringName handBoneName = Side == LimbSide.Left ? _leftHandBoneName : _rightHandBoneName;
        int upperArm = skeleton.FindBone(upperArmBoneName);
        int lowerArm = skeleton.FindBone(lowerArmBoneName);
        int hand = skeleton.FindBone(handBoneName);
        if (upperArm < 0 || lowerArm < 0 || hand < 0)
        {
            shoulderBoneIndex = -1;
            armReachBoundMetres = float.PositiveInfinity;
            return false;
        }

        shoulderBoneIndex = upperArm;
        armReachBoundMetres = skeleton.GetBoneGlobalRest(upperArm).Origin.DistanceTo(skeleton.GetBoneGlobalRest(lowerArm).Origin)
                              + skeleton.GetBoneGlobalRest(lowerArm).Origin.DistanceTo(skeleton.GetBoneGlobalRest(hand).Origin)
                              + Mathf.Max(0f, MovableApproachCommandReachMarginMetres);
        return true;
    }

    private bool IsAttachmentTransformWithinTolerance(Transform3D expected, Transform3D actual)
    {
        if (!IsFinite(expected) || !IsFinite(actual))
        {
            return false;
        }

        float positionTolerance = Math.Max(0f, MovableAttachmentPositionToleranceMetres);
        if (expected.Origin.DistanceSquaredTo(actual.Origin) > positionTolerance * positionTolerance)
        {
            return false;
        }

        Quaternion expectedRotation = new(expected.Basis.Orthonormalized());
        Quaternion actualRotation = new(actual.Basis.Orthonormalized());
        float absoluteDot = Mathf.Clamp(Mathf.Abs(expectedRotation.Dot(actualRotation)), 0f, 1f);
        float angularDifferenceRadians = 2f * MathF.Acos(absoluteDot);
        return angularDifferenceRadians <= Mathf.DegToRad(Math.Max(0f, MovableAttachmentOrientationToleranceDegrees));
    }

    private static bool IsFinite(Transform3D transform)
        => float.IsFinite(transform.Origin.X)
            && float.IsFinite(transform.Origin.Y)
            && float.IsFinite(transform.Origin.Z)
            && IsFinite(transform.Basis.X)
            && IsFinite(transform.Basis.Y)
            && IsFinite(transform.Basis.Z);

    private static bool IsFinite(Vector3 vector)
        => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);

    private static GrabPointCandidate? TryCommitGrabCandidate(
        PendingGrabState pending,
        GrabPointCandidate? refreshedCandidate,
        bool originalCandidateSettled,
        bool refreshedCandidateSettled)
        => originalCandidateSettled && pending.Grabbable.Grab(pending.Candidate)
            ? pending.Candidate
            : refreshedCandidateSettled && refreshedCandidate is not null && pending.Grabbable.Grab(refreshedCandidate)
            ? refreshedCandidate
            : null;

    private void UpdateHeldReleaseVelocity(double delta)
    {
        if (CurrentGrabbed is Node3D grabbedNode && IsInstanceValid(grabbedNode))
        {
            _releaseVelocityTracker.Update(grabbedNode.GlobalPosition, delta, ThrowVelocitySmoothingFactor);
        }
    }

    private Vector3 ResolveReleaseVelocity(IGrabbable? grabbable)
        => grabbable is { Mobility: GrabbableMobility.Movable } and RigidBody3D
            ? _releaseVelocityTracker.GetVelocity(
                ThrowMinimumReleaseSpeedMetresPerSecond,
                ThrowMaximumReleaseSpeedMetresPerSecond)
            : Vector3.Zero;

    private static void ApplyReleaseVelocity(IGrabbable? grabbable, Vector3 releaseVelocity)
    {
        if (grabbable is not RigidBody3D rigidBody || grabbable.Mobility != GrabbableMobility.Movable)
        {
            return;
        }

        rigidBody.LinearVelocity = releaseVelocity;
    }

    private void AddHeldMovableCollisionExceptions(Node3D grabbedNode)
    {
        if (grabbedNode is not PhysicsBody3D heldBody)
        {
            return;
        }

        ClearHeldMovableCollisionExceptions();

        foreach (PhysicsBody3D otherBody in EnumerateHeldMovableSelfCollisionBodies())
        {
            AddHeldMovableCollisionException(heldBody, otherBody);
        }

        if (HeldCollisionTarget is PhysicsBody3D heldCollisionBody && IsInstanceValid(heldCollisionBody))
        {
            foreach (PhysicsBody3D otherBody in EnumerateHeldMovableSelfCollisionBodies())
            {
                AddHeldMovableCollisionException(heldCollisionBody, otherBody);
            }
        }
    }

    private void CreateHeldCollisionProxies(Node3D grabbedNode)
    {
        ClearHeldCollisionProxies();

        if (HeldCollisionTarget is null || !IsInstanceValid(HeldCollisionTarget))
        {
            return;
        }

        List<HeldCollisionProxyShapeState> proxyShapes = [];
        foreach (CollisionShape3D originalShape in EnumerateDescendantCollisionShapes(grabbedNode))
        {
            if (originalShape.Disabled || originalShape.Shape is null)
            {
                continue;
            }

            uint shapeOwnerId = HeldCollisionTarget.CreateShapeOwner(originalShape);
            HeldCollisionTarget.ShapeOwnerAddShape(shapeOwnerId, originalShape.Shape);
            HeldCollisionTarget.ShapeOwnerSetTransform(
                shapeOwnerId,
                HeldCollisionTarget.GlobalTransform.AffineInverse() * originalShape.GlobalTransform);
            HeldCollisionTarget.ShapeOwnerSetDisabled(shapeOwnerId, false);
            bool wasDisabled = originalShape.Disabled;
            originalShape.Disabled = true;
            proxyShapes.Add(new HeldCollisionProxyShapeState(originalShape, shapeOwnerId, wasDisabled));
        }

        if (proxyShapes.Count > 0)
        {
            _heldCollisionProxyState = new HeldCollisionProxyState(HeldCollisionTarget, proxyShapes);
            HandDynamicBodyInteractionController.NotifyRuntimeShapeOwnersChanged(HeldCollisionTarget);
        }
    }

    private void ClearHeldCollisionProxies()
    {
        if (_heldCollisionProxyState is not HeldCollisionProxyState state)
        {
            return;
        }

        foreach (HeldCollisionProxyShapeState proxyShape in state.Shapes)
        {
            if (IsInstanceValid(proxyShape.OriginalShape))
            {
                proxyShape.OriginalShape.Disabled = proxyShape.WasDisabled;
            }

            if (IsInstanceValid(state.Target))
            {
                state.Target.RemoveShapeOwner(proxyShape.ShapeOwnerId);
            }
        }

        if (IsInstanceValid(state.Target))
        {
            HandDynamicBodyInteractionController.NotifyRuntimeShapeOwnersChanged(state.Target);
        }
        _heldCollisionProxyState = null;
    }

    private static IEnumerable<CollisionShape3D> EnumerateDescendantCollisionShapes(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is CollisionShape3D collisionShape)
            {
                yield return collisionShape;
            }

            foreach (CollisionShape3D descendant in EnumerateDescendantCollisionShapes(child))
            {
                yield return descendant;
            }
        }
    }

    private IEnumerable<PhysicsBody3D> EnumerateHeldMovableSelfCollisionBodies()
    {
        HashSet<PhysicsBody3D> yieldedBodies = [];
        if (HandTargetNode is PhysicsBody3D handTargetBody && yieldedBodies.Add(handTargetBody))
        {
            yield return handTargetBody;
        }

        DynamicPhysicalRig? rig = ResolvePhysicalRig();
        if (rig is null)
        {
            yield break;
        }

        StringName handBoneName = Side == LimbSide.Left ? _leftHandBoneName : _rightHandBoneName;
        StringName lowerArmBoneName = Side == LimbSide.Left ? _leftLowerArmBoneName : _rightLowerArmBoneName;

        foreach (PhysicsBody3D body in EnumerateRigBodies(rig, handBoneName, lowerArmBoneName))
        {
            if (yieldedBodies.Add(body))
            {
                yield return body;
            }
        }
    }

    private DynamicPhysicalRig? ResolvePhysicalRig()
        => PhysicalRig is not null && IsInstanceValid(PhysicalRig)
            ? PhysicalRig
            : HandBoneAttachment?.GetParent() is Skeleton3D skeleton
            ? skeleton.GetNodeOrNull<DynamicPhysicalRig>(nameof(DynamicPhysicalRig))
            : null;

    private static IEnumerable<PhysicsBody3D> EnumerateRigBodies(
        DynamicPhysicalRig rig,
        StringName handBoneName,
        StringName lowerArmBoneName)
    {
        foreach (PhysicsBody3D body in rig.GetGeneratedProxyBodiesForBone(handBoneName))
        {
            yield return body;
        }

        foreach (PhysicsBody3D body in rig.GetGeneratedProxyBodiesForBone(lowerArmBoneName))
        {
            yield return body;
        }

        foreach (PhysicsBody3D body in rig.GetGeneratedFingerProxyBodiesForHand(handBoneName))
        {
            yield return body;
        }
    }

    private void AddHeldMovableCollisionException(PhysicsBody3D heldBody, PhysicsBody3D otherBody)
    {
        if (heldBody == otherBody || !IsInstanceValid(otherBody))
        {
            return;
        }

        heldBody.AddCollisionExceptionWith(otherBody);
        otherBody.AddCollisionExceptionWith(heldBody);
        _heldMovableCollisionExceptions.Add(new CollisionExceptionPair(heldBody, otherBody));
    }

    private void ClearHeldMovableCollisionExceptions()
    {
        foreach (CollisionExceptionPair pair in _heldMovableCollisionExceptions)
        {
            if (IsInstanceValid(pair.HeldBody) && IsInstanceValid(pair.OtherBody))
            {
                pair.HeldBody.RemoveCollisionExceptionWith(pair.OtherBody);
                pair.OtherBody.RemoveCollisionExceptionWith(pair.HeldBody);
            }
        }

        _heldMovableCollisionExceptions.Clear();
    }

    /// <summary>
    /// Applies pending-candidate collision protection for the grabbable the approach is authorised to
    /// acquire: the candidate's physics body is exempted from the same-side hand proxies and hand IK target
    /// body — mirroring the held-movable exception set — and marked as approach-protected so the explicit
    /// hand interaction channel cannot push it while the approach runs (INTR-002 pending approach
    /// protection). Non-physics candidates skip gracefully; an unchanged protected body is a no-op.
    /// </summary>
    private void UpdatePendingGrabCollisionProtection(IGrabbable grabbable)
    {
        if (grabbable.Mobility != GrabbableMobility.Movable || grabbable is not PhysicsBody3D candidateBody)
        {
            ClearPendingGrabCollisionProtection();
            return;
        }

        if (_pendingGrabCollisionProtection is PendingGrabCollisionProtectionState current
            && ReferenceEquals(current.ProtectedBody, candidateBody))
        {
            return;
        }

        ClearPendingGrabCollisionProtection();

        List<CollisionExceptionPair> pairs = [];
        foreach (PhysicsBody3D otherBody in EnumerateHeldMovableSelfCollisionBodies())
        {
            if (candidateBody == otherBody || !IsInstanceValid(otherBody))
            {
                continue;
            }

            candidateBody.AddCollisionExceptionWith(otherBody);
            otherBody.AddCollisionExceptionWith(candidateBody);
            pairs.Add(new CollisionExceptionPair(candidateBody, otherBody));
        }

        RegisterApproachProtectedBody(candidateBody);
        _pendingGrabCollisionProtection = new PendingGrabCollisionProtectionState(candidateBody, pairs);
    }

    /// <summary>
    /// Fully reverts pending-candidate collision protection: exception pairs are removed and the approach
    /// protection mark is released. Commit transitions into the held exceptions by reverting here first, so
    /// the identical pairs are re-established synchronously by the held path without gaps or duplicates.
    /// </summary>
    private void ClearPendingGrabCollisionProtection()
    {
        if (_pendingGrabCollisionProtection is not PendingGrabCollisionProtectionState state)
        {
            return;
        }

        foreach (CollisionExceptionPair pair in state.Pairs)
        {
            if (IsInstanceValid(pair.HeldBody) && IsInstanceValid(pair.OtherBody))
            {
                pair.HeldBody.RemoveCollisionExceptionWith(pair.OtherBody);
                pair.OtherBody.RemoveCollisionExceptionWith(pair.HeldBody);
            }
        }

        if (IsInstanceValid(state.ProtectedBody))
        {
            UnregisterApproachProtectedBody(state.ProtectedBody);
        }

        _pendingGrabCollisionProtection = null;
    }

    /// <summary>
    /// Marks a body as approach-protected for this hand. The mark is reference-counted in node metadata so
    /// two hands pending on the same free body keep the channel suppression until both abandon or commit.
    /// </summary>
    private static void RegisterApproachProtectedBody(PhysicsBody3D body)
    {
        int references = ReadApproachProtectionReferences(body) + 1;
        body.SetMeta(ApproachProtectionReferenceMetaKey, references);
        if (references == 1)
        {
            body.AddToGroup(HandDynamicBodyInteractionController.ApproachProtectedBodyGroupName);
        }
    }

    private static void UnregisterApproachProtectedBody(PhysicsBody3D body)
    {
        int references = Mathf.Max(0, ReadApproachProtectionReferences(body) - 1);
        if (references == 0)
        {
            if (body.HasMeta(ApproachProtectionReferenceMetaKey))
            {
                body.RemoveMeta(ApproachProtectionReferenceMetaKey);
            }

            body.RemoveFromGroup(HandDynamicBodyInteractionController.ApproachProtectedBodyGroupName);
            return;
        }

        body.SetMeta(ApproachProtectionReferenceMetaKey, references);
    }

    private static int ReadApproachProtectionReferences(PhysicsBody3D body)
        => body.HasMeta(ApproachProtectionReferenceMetaKey)
            ? body.GetMeta(ApproachProtectionReferenceMetaKey).AsInt32()
            : 0;

    private void CommitImmovableGrab(
        PendingGrabState pending,
        GrabPointCandidate? refreshedCandidate,
        bool originalCandidateSettled,
        bool refreshedCandidateSettled)
    {
        GrabPointCandidate? commitCandidate = TryCommitGrabCandidate(
            pending,
            refreshedCandidate,
            originalCandidateSettled,
            refreshedCandidateSettled);
        if (commitCandidate is null)
        {
            _pendingGrabState = null;
            ClearActiveGrabInputState();
            GrabTargetProvider?.ReleaseGrabTarget();
            return;
        }

        SetPose(commitCandidate.Animation);
        CurrentGrabbed = pending.Grabbable;
        _pendingGrabState = null;
        ActiveGrabAnimationState = commitCandidate.Animation;
        ActiveGrabReferenceState = commitCandidate.ValidatedReference;
        ActiveGrabRecognitionStrategyNameState = commitCandidate.GripRecognitionStrategyName;
        PublishOpticalGrabPresentation();
    }

    private void AbandonPendingGrab()
    {
        _pendingGrabState = null;
        ClearActiveGrabInputState();
        GrabTargetProvider?.ReleaseGrabTarget();
    }

    /// <summary>
    /// Abandons the pending grab while recording why, so input layers and traces can distinguish bounded
    /// non-convergence from candidate loss without observing solver internals (IK-005 TR22).
    /// </summary>
    private void AbandonPendingGrabFor(HandGrabAbandonmentReason reason)
    {
        LastPendingGrabAbandonmentReason = reason;
        AbandonPendingGrab();
    }

    /// <summary>
    /// Clears the active grab's input provenance, candidate animation, optical presentation arbitration
    /// publication, and pending-candidate collision protection — the shared tail of every path that ends a
    /// grab intent or held grab, including hand teardown.
    /// </summary>
    private void ClearActiveGrabInputState()
    {
        ClearPendingGrabCollisionProtection();
        _grabInputSource = HandGrabInputSource.None;
        ActiveGrabAnimationState = null;
        ActiveGrabReferenceState = null;
        ActiveGrabRecognitionStrategyNameState = null;
        if (_opticalGrabPresentationOwner is OpticalGrabPresentationOwner owner)
        {
            _controller?.RevokeOpticalGrabHandoff(owner);
            if (TryResolveOpticalGrabArbiter() is { } arbiter)
            {
                _ = arbiter.TryClearOpticalGrab(owner);
            }
        }

        _opticalGrabPresentationOwner = null;
    }

    /// <summary>
    /// Publishes the held optical grab's per-hand presentation arbitration state at the commit transition — the
    /// single authoritative write point, because only the hand observes commit, release, and abandonment exactly
    /// (XR-002 TR30-TR31; INTR-003 TR19).
    /// </summary>
    private void PublishOpticalGrabPresentation()
    {
        if (_grabInputSource != HandGrabInputSource.Optical || CurrentGrabbed is null)
        {
            return;
        }

        if (_opticalGrabPresentationOwner is OpticalGrabPresentationOwner owner
            && TryResolveOpticalGrabArbiter() is { } arbiter)
        {
            if (_controller is HandPoseController controller && ActiveGrabReferenceState is GrabPoseReference reference)
            {
                controller.PublishOpticalGrabHandoff(Side, owner, reference);
            }

            if (!arbiter.TrySetOpticalGrabHeld(owner, ActiveGrabReferenceState))
            {
                _controller?.RevokeOpticalGrabHandoff(owner);
                _opticalGrabPresentationOwner = null;
            }
        }
    }

    /// <summary>
    /// Publishes optical-only pending contact assistance after the candidate has been stored. Controller-originated
    /// approaches intentionally retain ordinary presentation ownership.
    /// </summary>
    private void PublishOpticalPendingPresentation()
    {
        if (_grabInputSource != HandGrabInputSource.Optical || _pendingGrabState is null)
        {
            return;
        }

        _opticalGrabPresentationOwner ??= new OpticalGrabPresentationOwner(
            Side,
            GetInstanceId(),
            Interlocked.Increment(ref _nextOpticalGrabPresentationGeneration));
        if (TryResolveOpticalGrabArbiter() is { } arbiter)
        {
            arbiter.SetOpticalGrabPendingAssistance(_opticalGrabPresentationOwner.Value, ActiveGrabReferenceState);
        }
    }

    private IOpticalGrabPresentationArbiter? TryResolveOpticalGrabArbiter()
    {
        if (_opticalGrabArbiter is not null)
        {
            return _opticalGrabArbiter;
        }

        try
        {
            _opticalGrabArbiter = Game.Instance.GetService<XRManager>()?.OpticalGrabArbiter;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // No game singleton or service provider yet — arbitration publication is a fail-safe no-op.
            _opticalGrabArbiter = null;
        }

        return _opticalGrabArbiter;
    }

    private void RestoreGrabbedNodeParent()
    {
        if (_attachmentState is not GrabAttachmentState state || !IsInstanceValid(state.Node))
        {
            _attachmentState = null;
            return;
        }

        Node targetParent = state.PreviousParent is not null && IsInstanceValid(state.PreviousParent)
            ? state.PreviousParent
            : GetTree().CurrentScene ?? GetTree().Root;
        state.Node.Reparent(targetParent, keepGlobalTransform: true);
        if (state.PreviousParent is not null && IsInstanceValid(state.PreviousParent))
        {
            state.PreviousParent.MoveChild(state.Node, Mathf.Clamp(state.PreviousIndex, 0, state.PreviousParent.GetChildCount() - 1));
        }

        _attachmentState = null;
    }

    private sealed record GrabAttachmentState(Node3D Node, Node? PreviousParent, int PreviousIndex);

    private sealed record HeldCollisionProxyState(
        CollisionObject3D Target,
        IReadOnlyList<HeldCollisionProxyShapeState> Shapes);

    private sealed record HeldCollisionProxyShapeState(
        CollisionShape3D OriginalShape,
        uint ShapeOwnerId,
        bool WasDisabled);

    private readonly record struct CollisionExceptionPair(PhysicsBody3D HeldBody, PhysicsBody3D OtherBody);

    /// <summary>
    /// Live pending-candidate collision protection: the protected body and the exact exception pairs this
    /// hand applied, so reversion removes precisely what application added.
    /// </summary>
    private sealed record PendingGrabCollisionProtectionState(
        PhysicsBody3D ProtectedBody,
        IReadOnlyList<CollisionExceptionPair> Pairs);

    private sealed class PendingGrabState(
        IGrabbable grabbable,
        GrabPointCandidate candidate,
        Transform3D approachHandTarget,
        Transform3D expectedAttachmentTransform)
    {
        public IGrabbable Grabbable { get; } = grabbable;

        public GrabPointCandidate Candidate { get; } = candidate;

        public IGrabPoint InitialCandidateSource { get; } = candidate.Source;

        public GrabPoseReference InitialCandidateReference
        {
            get;
        } = candidate.ValidatedReference
            ?? throw new ArgumentException("Pending candidates must be content-validated.", nameof(candidate));

        public GrabPointCandidate? CurrentEligibilityObservation { get; set; } = candidate;

        public GrabPointCandidate CurrentSettlementCandidate { get; set; } = candidate;

        public Transform3D RetainedContactHandTransform { get; set; } = candidate.HandTransform;

        public Transform3D ApproachHandTarget { get; set; } = approachHandTarget;

        public Transform3D LastCommandedApproachTransform { get; set; } = approachHandTarget;

        public Transform3D CurrentSettlementTransform { get; set; } = expectedAttachmentTransform;

        /// <summary>Best (minimum) direct-attachment residual observed for the current destination.</summary>
        public float BestAttachmentResidualMetres { get; set; } = float.PositiveInfinity;

        /// <summary>
        /// Command offset from the settlement destination calibrated when the command was last (re)armed; the
        /// approach command may rebound beyond it only within the bounded allowance.
        /// </summary>
        public float InitialCommandOffsetFromDestinationMetres
        {
            get; set;
        }

        /// <summary>Skeleton supplying the live shoulder for the reach bound; <see langword="null" /> disables it.</summary>
        public Skeleton3D? ReachSkeleton
        {
            get; set;
        }

        /// <summary>Shoulder (upper-arm) bone index within <see cref="ReachSkeleton" />.</summary>
        public int ShoulderBoneIndex { get; set; } = -1;

        /// <summary>Arm-reach sphere radius around the live shoulder bounding the approach command.</summary>
        public float ArmReachBoundMetres { get; set; } = float.PositiveInfinity;

        /// <summary>Physics tick of the last bounded-interval restart through residual improvement.</summary>
        public ulong LastImprovementPhysicsFrame
        {
            get;
            set;
        } = Engine.GetPhysicsFrames();

        /// <summary>
        /// Physics tick where the candidate body's speed first exceeded the moving-candidate threshold, or
        /// zero while the candidate is not moving above the threshold.
        /// </summary>
        public ulong MovingCandidateSincePhysicsFrame
        {
            get;
            set;
        }

        public int ConsecutiveAttachmentSettleFrames
        {
            get;
            set;
        }
    }

    private sealed class ReleaseVelocityTracker
    {
        private Vector3 _lastPosition;
        private Vector3 _smoothedVelocity;
        private bool _hasPosition;
        private bool _hasVelocity;

        public void Reset()
        {
            _lastPosition = Vector3.Zero;
            _smoothedVelocity = Vector3.Zero;
            _hasPosition = false;
            _hasVelocity = false;
        }

        public void Reset(Vector3 position)
        {
            _lastPosition = position;
            _smoothedVelocity = Vector3.Zero;
            _hasPosition = true;
            _hasVelocity = false;
        }

        public void Update(Vector3 position, double delta, float smoothingFactor)
        {
            if (!_hasPosition)
            {
                Reset(position);
                return;
            }

            if (delta <= double.Epsilon)
            {
                _lastPosition = position;
                return;
            }

            Vector3 sampleVelocity = (position - _lastPosition) / (float)delta;
            float clampedSmoothingFactor = Mathf.Clamp(smoothingFactor, 0.0f, 1.0f);
            _smoothedVelocity = _hasVelocity
                ? _smoothedVelocity.Lerp(sampleVelocity, clampedSmoothingFactor)
                : sampleVelocity;
            _hasVelocity = true;
            _lastPosition = position;
        }

        public Vector3 GetVelocity(float minimumSpeed, float maximumSpeed)
        {
            if (!_hasVelocity)
            {
                return Vector3.Zero;
            }

            float speed = _smoothedVelocity.Length();
            if (speed <= Mathf.Max(0.0f, minimumSpeed))
            {
                return Vector3.Zero;
            }

            float maxSpeed = Mathf.Max(0.0f, maximumSpeed);
            return maxSpeed > 0.0f && speed > maxSpeed
                ? _smoothedVelocity.Normalized() * maxSpeed
                : _smoothedVelocity;
        }
    }
}

internal static class GrabbableReleaseExtensions
{
    public static void ReleaseIfSupported(this IGrabbable grabbable)
    {
        if (grabbable is IReleasableGrabbable releasable)
        {
            releasable.Release();
        }
    }
}
