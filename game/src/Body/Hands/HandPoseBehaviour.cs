using AlleyCat.IK;
using AlleyCat.Interaction;
using AlleyCat.UI;
using Godot;

namespace AlleyCat.Body.Hands;

/// <summary>
/// Godot node facade exposing BODY-001 Hands hand-pose control to scene consumers.
/// </summary>
[GlobalClass]
public sealed partial class HandPoseBehaviour : Node, IHand
{
    private const ulong PendingGrabDebugIntervalUsec = 750_000;
    private static readonly StringName _rightHandBoneName = new("RightHand");
    private static readonly StringName _leftHandBoneName = new("LeftHand");
    private static readonly StringName _rightLowerArmBoneName = new("RightLowerArm");
    private static readonly StringName _leftLowerArmBoneName = new("LeftLowerArm");

    private HandPoseController? _controller;
    private GrabAttachmentState? _attachmentState;
    private PendingGrabState? _pendingGrabState;
    private readonly ReleaseVelocityTracker _releaseVelocityTracker = new();
    private readonly List<CollisionExceptionPair> _heldMovableCollisionExceptions = [];
    private ulong _nextPendingGrabDebugTicksUsec;

    /// <summary>
    /// Gets or sets the animation tree controlled by this behaviour.
    /// </summary>
    [Export]
    public AnimationTree? AnimationTree
    {
        get; set;
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
        get; private set;
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
    /// Maximum hand-attachment distance from the selected hand target before a pending grab commits.
    /// </summary>
    [Export(PropertyHint.Range, "0.001,0.2,0.001,or_greater,suffix:m")]
    public float GrabCommitDistanceMetres { get; set; } = 0.025f;

    /// <summary>
    /// Minimum tracked release speed transferred to a released movable rigid body.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01,or_greater,suffix:m/s")]
    public float ThrowMinimumReleaseSpeedMetresPerSecond { get; set; } = 0.05f;

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
    /// Gets or sets whether grab execution diagnostics are posted through the global notification UI.
    /// </summary>
    [ExportGroup("Debug")]
    [Export]
    public bool DebugGrabOutput
    {
        get; set;
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
        if (AnimationTree is null)
        {
            GD.PushError($"{nameof(HandPoseBehaviour)} requires an AnimationTree reference or AnimationTree parent.");
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
        _controller?.Update(Side, delta);
        TryCommitPendingGrab();
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
        EmitGrabDebug($"Grab press: current={FormatGrabbable(CurrentGrabbed)}, pending={_pendingGrabState is not null}.");

        if (CurrentGrabbed is not null)
        {
            EmitGrabDebug($"Grab ignored: already holding {FormatGrabbable(CurrentGrabbed)}.");
            return CurrentGrabbed;
        }

        if (_pendingGrabState is not null)
        {
            EmitPendingGrabDebug(_pendingGrabState, force: true);
            TryCommitPendingGrab();
            return CurrentGrabbed;
        }

        Transform3D handTransform = ResolveHandTransform();
        var grabbables = EnumerateDiscoverableGrabbables().ToList();
        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            grabbables,
            Side,
            handTransform,
            DiscoveryRangeMetres);

        if (selection is null)
        {
            EmitGrabDebug($"Grab failed: no candidate. Discoverable={grabbables.Count}, range={DiscoveryRangeMetres:0.###}m, hand={FormatVector(handTransform.Origin)}.");
            return null;
        }

        EmitGrabDebug(
            $"Grab selected: {FormatGrabbable(selection.Grabbable)} mobility={selection.Grabbable.Mobility}, "
            + $"target={FormatVector(selection.Candidate.HandTarget.Origin)}, animation={FormatAnimation(selection.Candidate.Animation)}.");
        GrabTargetProvider?.SetGrabTarget(selection.Candidate.HandTarget);
        EmitGrabDebug($"Grab provider: active={GrabTargetProvider?.IsGrabOverrideActive.ToString() ?? "none"}, default={FormatProviderDefaultState()}.");
        _pendingGrabState = new PendingGrabState(selection.Grabbable, selection.Candidate);
        _nextPendingGrabDebugTicksUsec = 0;

        return CurrentGrabbed;
    }

    /// <inheritdoc />
    public void Release()
    {
        if (CurrentGrabbed is null && _pendingGrabState is null)
        {
            EmitGrabDebug("Release ignored: no current or pending grab.");
            return;
        }

        IGrabbable? grabbed = CurrentGrabbed;
        Vector3 releaseVelocity = ResolveReleaseVelocity(grabbed);
        EmitGrabDebug($"Release: current={FormatGrabbable(grabbed)}, pending={FormatGrabbable(_pendingGrabState?.Grabbable)}.");
        CurrentGrabbed = null;
        _pendingGrabState = null;
        _releaseVelocityTracker.Reset();
        ClearHeldMovableCollisionExceptions();
        RestoreGrabbedNodeParent();
        ClearPose();
        GrabTargetProvider?.ReleaseGrabTarget();
        grabbed?.ReleaseIfSupported();
        ApplyReleaseVelocity(grabbed, releaseVelocity);
        EmitGrabDebug($"Release complete: provider active={GrabTargetProvider?.IsGrabOverrideActive.ToString() ?? "none"}, default={FormatProviderDefaultState()}.");
    }

    private Transform3D ResolveHandTransform()
        => HandTargetNode is not null && IsInstanceValid(HandTargetNode)
            ? HandTargetNode.GlobalTransform
            : HandBoneAttachment is not null && IsInstanceValid(HandBoneAttachment)
            ? HandBoneAttachment.GlobalTransform
            : Transform3D.Identity;

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

        Transform3D currentHandTransform = ResolveHandTransform();
        float distanceToCommit = currentHandTransform.Origin.DistanceTo(pending.Candidate.HandTarget.Origin);
        if (distanceToCommit > GrabCommitDistanceMetres)
        {
            EmitPendingGrabDebug(pending, force: false, currentHandTransform, distanceToCommit);
            return;
        }

        EmitGrabDebug(
            $"Grab commit check: {FormatGrabbable(pending.Grabbable)} distance={distanceToCommit:0.###}m "
            + $"threshold={GrabCommitDistanceMetres:0.###}m mobility={pending.Grabbable.Mobility}.");

        if (pending.Grabbable.Mobility == GrabbableMobility.Immovable)
        {
            CommitImmovableGrab(pending);
            return;
        }

        if (HandBoneAttachment is null || !IsInstanceValid(HandBoneAttachment))
        {
            AbandonPendingGrab("Grab commit abandoned: movable grabbable needs a valid hand bone attachment.");
            return;
        }

        bool isNodeGrabbable = pending.Grabbable is Node3D;
        bool acceptedGrab = isNodeGrabbable && pending.Grabbable.Grab(pending.Candidate);
        if (pending.Grabbable is not Node3D grabbedNode || !acceptedGrab)
        {
            _pendingGrabState = null;
            GrabTargetProvider?.ReleaseGrabTarget();
            EmitGrabDebug(
                $"Grab commit failed: node={isNodeGrabbable}, accepted={acceptedGrab}, "
                + $"provider active={GrabTargetProvider?.IsGrabOverrideActive.ToString() ?? "none"}.");
            return;
        }

        try
        {
            AttachGrabbedNode(grabbedNode, pending.Candidate);
            AddHeldMovableCollisionExceptions(grabbedNode);
            _releaseVelocityTracker.Reset(grabbedNode.GlobalPosition);
            ApplyGrabPoseWithDiagnostics(pending.Candidate.Animation);
            CurrentGrabbed = pending.Grabbable;
            _pendingGrabState = null;
            GrabTargetProvider?.ClearGrabTargetImmediate();
        }
        catch
        {
            ClearHeldMovableCollisionExceptions();
            RestoreGrabbedNodeParent();
            pending.Grabbable.ReleaseIfSupported();
            _pendingGrabState = null;
            GrabTargetProvider?.ReleaseGrabTarget();
            throw;
        }

        EmitGrabDebug(
            $"Grab committed: movable {FormatGrabbable(CurrentGrabbed)}, provider active={GrabTargetProvider?.IsGrabOverrideActive.ToString() ?? "none"}, "
            + $"default={FormatProviderDefaultState()}.");
    }

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

        EmitGrabDebug($"Grab collision exceptions: movable={heldBody.Name}, pairs={_heldMovableCollisionExceptions.Count}.");
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

    private void CommitImmovableGrab(PendingGrabState pending)
    {
        if (!pending.Grabbable.Grab(pending.Candidate))
        {
            _pendingGrabState = null;
            GrabTargetProvider?.ReleaseGrabTarget();
            EmitGrabDebug($"Grab commit failed: immovable {FormatGrabbable(pending.Grabbable)} rejected candidate.");
            return;
        }

        ApplyGrabPoseWithDiagnostics(pending.Candidate.Animation);
        CurrentGrabbed = pending.Grabbable;
        _pendingGrabState = null;
        EmitGrabDebug(
            $"Grab committed: immovable {FormatGrabbable(CurrentGrabbed)}, provider active={GrabTargetProvider?.IsGrabOverrideActive.ToString() ?? "none"}.");
    }

    private void ApplyGrabPoseWithDiagnostics(Animation animation)
    {
        EmitAnimationDiagnostics(animation, "before");
        try
        {
            SetPose(animation);
        }
        catch (Exception exception)
        {
            EmitGrabDebug($"Grab animation failed: {FormatAnimation(animation)} error={exception.GetType().Name}: {exception.Message}");
            throw;
        }

        EmitAnimationDiagnostics(animation, "after");
    }

    private void EmitPendingGrabDebug(
        PendingGrabState pending,
        bool force,
        Transform3D? currentHandTransform = null,
        float? distanceToCommit = null)
    {
        if (!DebugGrabOutput)
        {
            return;
        }

        ulong now = Time.GetTicksUsec();
        if (!force && now < _nextPendingGrabDebugTicksUsec)
        {
            return;
        }

        _nextPendingGrabDebugTicksUsec = now + PendingGrabDebugIntervalUsec;
        Transform3D current = currentHandTransform ?? ResolveHandTransform();
        float distance = distanceToCommit ?? current.Origin.DistanceTo(pending.Candidate.HandTarget.Origin);
        EmitGrabDebug(
            $"Grab pending: {FormatGrabbable(pending.Grabbable)} target={FormatVector(pending.Candidate.HandTarget.Origin)}, "
            + $"current={FormatVector(current.Origin)}, distance={distance:0.###}m/{GrabCommitDistanceMetres:0.###}m.");
    }

    private void EmitAnimationDiagnostics(Animation animation, string phase)
    {
        if (!DebugGrabOutput)
        {
            return;
        }

        string animationName = ResolveAnimationName(animation).ToString();
        AnimationPlayer? player = ResolveAnimationPlayer();
        bool registered = player?.HasAnimation(new StringName(animationName)) ?? false;
        string poseNodeState = ResolvePoseNodeAnimationName();
        EmitGrabDebug(
            $"Grab animation {phase}: resource={FormatAnimation(animation)}, name={animationName}, "
            + $"player={(player is null ? "missing" : "ok")}, registered={registered}, poseNode={poseNodeState}.");
    }

    private void EmitGrabDebug(string message)
    {
        if (!DebugGrabOutput)
        {
            return;
        }

        _ = this.PostNotification($"Grab: {message}", 4.0);
    }

    private void AbandonPendingGrab(string reason)
    {
        _pendingGrabState = null;
        GrabTargetProvider?.ReleaseGrabTarget();
        EmitGrabDebug($"{reason} Provider active={GrabTargetProvider?.IsGrabOverrideActive.ToString() ?? "none"}.");
    }

    private string FormatProviderDefaultState()
        => GrabTargetProvider is null
            ? "none"
            : GrabTargetProvider.DefaultProvider is not null && IsInstanceValid(GrabTargetProvider.DefaultProvider)
            ? "available"
            : "missing";

    private string ResolvePoseNodeAnimationName()
        => AnimationTree?.TreeRoot is AnimationNodeBlendTree rootTree
            ? rootTree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(Side)) is AnimationNodeAnimation poseNode
                ? poseNode.Animation.ToString()
                : "node-missing"
            : "tree-missing";

    private AnimationPlayer? ResolveAnimationPlayer()
    {
        if (AnimationTree is null)
        {
            return null;
        }

        NodePath animPlayerPath = AnimationTree.AnimPlayer;
        return animPlayerPath.IsEmpty
            ? null
            : AnimationTree.GetNodeOrNull<AnimationPlayer>(animPlayerPath);
    }

    private static StringName ResolveAnimationName(Animation? pose)
        => pose is null
            ? new StringName(HandPoseAnimationTreePaths.ResetAnimationName)
            : string.IsNullOrWhiteSpace(pose.ResourceName)
            ? new StringName(pose.ResourcePath.GetFile().GetBaseName())
            : new StringName(pose.ResourceName);

    private static string FormatGrabbable(IGrabbable? grabbable)
        => grabbable is Node node
            ? $"{node.Name}"
            : grabbable?.GetType().Name ?? "none";

    private static string FormatAnimation(Animation? animation)
        => animation is null
            ? "none"
            : !string.IsNullOrWhiteSpace(animation.ResourceName)
            ? animation.ResourceName
            : !string.IsNullOrWhiteSpace(animation.ResourcePath)
            ? animation.ResourcePath.GetFile()
            : "unnamed";

    private static string FormatVector(Vector3 value) => $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";

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

    private readonly record struct CollisionExceptionPair(PhysicsBody3D HeldBody, PhysicsBody3D OtherBody);

    private sealed record PendingGrabState(IGrabbable Grabbable, GrabPointCandidate Candidate);

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
