using AlleyCat.Common;
using AlleyCat.IK.Pose;
using AlleyCat.XR;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Runtime bridge that drives player IK targets from XR tracking data.
/// </summary>
[GlobalClass]
public partial class PlayerVRIK : Node3D
{
    private const float HeightEpsilon = 1e-4f;

    private Marker3D? _viewpoint;
    private CharacterBody3D? _headIKTarget;
    private CharacterBody3D? _rightHandIKTarget;
    private CharacterBody3D? _leftHandIKTarget;
    private Node3D? _rightFootIKTarget;
    private Node3D? _leftFootIKTarget;
    private Skeleton3D? _skeleton;

    private IKTargetBodyFollower? _headFollower;
    private IKTargetBodyFollower? _leftHandFollower;
    private IKTargetBodyFollower? _rightHandFollower;

    /// <summary>
    /// Avatar viewpoint marker representing eye-centre in avatar space.
    /// </summary>
    [Export]
    public Marker3D? Viewpoint
    {
        get;
        set;
    }

    /// <summary>
    /// Head IK target body.
    /// </summary>
    [Export]
    public CharacterBody3D? HeadIKTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Right-hand IK target body.
    /// </summary>
    [Export]
    public CharacterBody3D? RightHandIKTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Left-hand IK target body.
    /// </summary>
    [Export]
    public CharacterBody3D? LeftHandIKTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Right-foot IK target node.
    /// </summary>
    [Export]
    public Node3D? RightFootIKTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Left-foot IK target node.
    /// </summary>
    [Export]
    public Node3D? LeftFootIKTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Skeleton driven by IK modifiers.
    /// </summary>
    [Export]
    public Skeleton3D? Skeleton
    {
        get;
        set;
    }

    /// <summary>
    /// Maximum follow speed for the head target body.
    /// </summary>
    [Export]
    public float HeadTargetMaximumSpeed
    {
        get;
        set;
    } = 32.0f;

    /// <summary>
    /// Maximum follow speed for each hand target body.
    /// </summary>
    [Export]
    public float HandTargetMaximumSpeed
    {
        get;
        set;
    } = 28.0f;

    /// <summary>
    /// When true, enables IK processing. When false, skips all IK target updates.
    /// </summary>
    [Export]
    public bool Active
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Pose state machine driven from the XR bridge. When set, <c>_Process</c> builds a
    /// <see cref="PoseStateContext"/> per tick and invokes <see cref="PoseStateMachine.Tick"/>
    /// so hip reconciliation and animation bindings observe the same snapshot as the modifier
    /// pipeline. Leave unset to disable the pose-state layer entirely.
    /// </summary>
    [Export]
    public PoseStateMachine? PoseStateMachine
    {
        get;
        set;
    }

    /// <summary>
    /// Bone name used to resolve the hip-bone index supplied to the <see cref="PoseStateContext"/>.
    /// </summary>
    [Export]
    public StringName HipBoneName
    {
        get;
        set;
    } = new("Hips");

    /// <summary>
    /// Resolved head-bone index for compensation calculations.
    /// </summary>
    public int HeadBoneIndex
    {
        get;
        private set;
    } = -1;

    /// <summary>
    /// Resolved hip-bone index supplied to the pose-state context. <c>-1</c> when unresolved.
    /// </summary>
    public int HipBoneIndex
    {
        get;
        private set;
    } = -1;

    private IXROrigin? _origin;
    private IXRCamera? _camera;
    private IXRHandController? _rightHandController;
    private IXRHandController? _leftHandController;

    private bool _isBound;
    private Transform3D _viewpointLocalTransform = Transform3D.Identity;
    private Transform3D _viewpointLocalInverseTransform = Transform3D.Identity;
    private float _worldScale = 1.0f;

    private readonly PoseStateContextBuilder _poseContextBuilder = new();

    /// <inheritdoc />
    public override void _Ready()
    {
        EnsureResolvedNodes();
        EnsureFollowers();

        InsertStageModifiers();
    }

    /// <summary>
    /// Binds XR runtime abstractions once and calibrates world scale.
    /// </summary>
    public virtual bool TryBind(IXRRuntime runtime)
        => TryBind(
            runtime.Origin,
            runtime.Camera,
            runtime.RightHandController,
            runtime.LeftHandController);

    /// <summary>
    /// Binds XR abstractions once and calibrates world scale.
    /// </summary>
    public bool TryBind(
        IXROrigin origin,
        IXRCamera camera,
        IXRHandController rightHandController,
        IXRHandController leftHandController)
    {
        if (_isBound)
        {
            return true;
        }

        _origin = origin;
        _camera = camera;
        _rightHandController = rightHandController;
        _leftHandController = leftHandController;

        EnsureResolvedNodes();
        EnsureFollowers();

        CalibrateWorldScaleOnce();
        _isBound = true;
        return true;
    }

    private void InsertStageModifiers()
    {
        Skeleton3D skeleton = GetResolvedSkeleton();

        StageModifier beginModifier = new()
        {
            Name = "VRIKBeginStage",
            Callback = OnBeginStage,
        };

        StageModifier endModifier = new()
        {
            Name = "VRIKEndStage",
            Callback = OnEndStage,
        };

        skeleton.AddChild(beginModifier);
        skeleton.MoveChild(beginModifier, 0);

        skeleton.AddChild(endModifier);
        skeleton.MoveChild(endModifier, skeleton.GetChildCount() - 1);
    }

    private void CalibrateWorldScaleOnce()
    {
        IXROrigin origin = _origin ?? throw new InvalidOperationException("PlayerVRIK origin not bound.");
        IXRCamera camera = _camera ?? throw new InvalidOperationException("PlayerVRIK camera not bound.");

        Node3D originNode = origin.OriginNode;

        Skeleton3D skeleton = GetResolvedSkeleton();

        Transform3D headBoneRest = skeleton.GetBoneGlobalRest(HeadBoneIndex);
        Transform3D restViewpoint = headBoneRest * _viewpointLocalTransform;

        float avatarRestViewpointHeight = Mathf.Abs(restViewpoint.Origin.Y);
        float xrCameraHeight = Mathf.Abs(originNode.ToLocal(camera.CameraNode.GlobalPosition).Y);

        bool calibrated = TryCalibrateWorldScale(
            avatarRestViewpointHeight,
            xrCameraHeight,
            HeightEpsilon,
            origin.WorldScale,
            out float calibratedScale);

        if (!calibrated)
        {
            GD.PushWarning(
                $"Skipping XR world-scale calibration due to near-zero height values (avatar={avatarRestViewpointHeight:F5}, xr={xrCameraHeight:F5}).");
            _worldScale = origin.WorldScale;
            return;
        }

        origin.WorldScale = calibratedScale;
        _worldScale = calibratedScale;
    }

    private void OnBeginStage(double delta)
    {
        if (!Active || !_isBound)
        {
            return;
        }

        IXROrigin? origin = _origin;
        PoseStateMachine? stateMachine = PoseStateMachine;

        if (origin is null || _headFollower is null || _rightHandFollower is null || _leftHandFollower is null ||
            stateMachine is null || !_isBound)
        {
            return;
        }

        origin.OriginNode.GlobalTransform = GlobalTransform;

        _headFollower.Follow(delta);
        _rightHandFollower.Follow(delta);
        _leftHandFollower.Follow(delta);

        Skeleton3D skeleton = GetResolvedSkeleton();
        PoseStateContext context = BuildPoseStateContext(skeleton, delta);
        stateMachine.Tick(context);
    }

    private void OnEndStage(double delta)
    {
        if (!Active || !_isBound || _origin is null || _camera is null)
        {
            return;
        }

        Skeleton3D skeleton = GetResolvedSkeleton();

        _origin.OriginNode.GlobalTransform = ComputeCompensatedOriginTransform(
            skeleton,
            _camera.CameraNode,
            _origin.OriginNode);
    }

    /// <summary>
    /// Solves the compensated XR origin transform that aligns the current physical head target
    /// to the virtual head pose while preserving the physical-head local offset under the prior
    /// origin transform.
    /// </summary>
    public Transform3D ComputeCompensatedOriginTransform(
        Skeleton3D skeleton,
        Camera3D camera,
        Node3D origin)
    {
        Transform3D physicalHeadPose = camera.GlobalTransform * _viewpointLocalInverseTransform;
        Transform3D virtualHeadPose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(HeadBoneIndex);
        Transform3D localPose = origin.GlobalTransform.Inverse() * physicalHeadPose;

        return virtualHeadPose * localPose.Inverse();
    }

    private Transform3D BuildHeadTargetTransform()
    {
        IXRCamera? camera = _camera;

        return camera is null
            ? _headIKTarget?.GlobalTransform ?? Transform3D.Identity
            : camera.CameraNode.GlobalTransform * _viewpointLocalInverseTransform;
    }

    private Transform3D BuildRightHandTargetTransform()
        => _rightHandController?.HandPositionNode.GlobalTransform
           ?? _rightHandIKTarget?.GlobalTransform
           ?? Transform3D.Identity;

    private Transform3D BuildLeftHandTargetTransform()
        => _leftHandController?.HandPositionNode.GlobalTransform
           ?? _leftHandIKTarget?.GlobalTransform
           ?? Transform3D.Identity;

    private static bool TryCalibrateWorldScale(
        float avatarRestViewpointHeight,
        float xrCameraHeight,
        float minimumHeight,
        float currentWorldScale,
        out float calibratedScale)
    {
        if (avatarRestViewpointHeight <= minimumHeight || xrCameraHeight <= minimumHeight)
        {
            calibratedScale = currentWorldScale;
            return false;
        }

        float candidateScale = avatarRestViewpointHeight / xrCameraHeight;
        if (!float.IsFinite(candidateScale) || candidateScale <= minimumHeight)
        {
            calibratedScale = currentWorldScale;
            return false;
        }

        calibratedScale = candidateScale;
        return true;
    }

    private sealed partial class StageModifier : SkeletonModifier3D
    {
        public Action<double>? Callback
        {
            get;
            set;
        }

        public override void _ProcessModificationWithDelta(double delta)
            => Callback?.Invoke(delta);
    }

    private Skeleton3D GetResolvedSkeleton()
        => _skeleton ?? throw new InvalidOperationException("PlayerVRIK skeleton not resolved before use.");

    private void EnsureResolvedNodes()
    {
        if (_skeleton is not null)
        {
            return;
        }

        _viewpoint = Viewpoint ?? this.RequireNode<Marker3D>("Female_export/GeneralSkeleton/Head/Viewpoint");
        _headIKTarget = HeadIKTarget ?? this.RequireNode<CharacterBody3D>("IKTargets/Head");
        _rightHandIKTarget = RightHandIKTarget ?? this.RequireNode<CharacterBody3D>("IKTargets/RightHand");
        _leftHandIKTarget = LeftHandIKTarget ?? this.RequireNode<CharacterBody3D>("IKTargets/LeftHand");
        _rightFootIKTarget = RightFootIKTarget ?? GetNodeOrNull<Node3D>("IKTargets/RightFoot");
        _leftFootIKTarget = LeftFootIKTarget ?? GetNodeOrNull<Node3D>("IKTargets/LeftFoot");
        _skeleton = Skeleton ?? this.RequireNode<Skeleton3D>("Female_export/GeneralSkeleton");

        HeadBoneIndex = _skeleton.FindBone("Head");
        if (HeadBoneIndex < 0)
        {
            throw new InvalidOperationException($"Unable to resolve Head bone on skeleton '{_skeleton.Name}'.");
        }

        HipBoneIndex = _skeleton.FindBone(HipBoneName);
        // HipBoneIndex may remain -1 when the skeleton does not expose a hips bone; consumers
        // (for example HipReconciliationModifier) already guard for the unresolved case.

        _viewpointLocalTransform = _viewpoint.Transform;
        _viewpointLocalInverseTransform = _viewpointLocalTransform.Inverse();
    }

    private PoseStateContext BuildPoseStateContext(Skeleton3D skeleton, double delta)
    {
        // Head target rest in world space: head-bone global rest multiplied by the viewpoint
        // marker's local transform inside the head bone. Matches the calibration reference used
        // by CalibrateWorldScaleOnce.
        Transform3D headBoneRest = skeleton.GetBoneGlobalRest(HeadBoneIndex);
        Transform3D headTargetRestTransform = skeleton.GlobalTransform * headBoneRest * _viewpointLocalTransform;

        // Current IK target transforms in world space.
        Transform3D headTargetTransform = _headIKTarget?.GlobalTransform ?? Transform3D.Identity;
        Transform3D rightHandTargetTransform = _rightHandIKTarget?.GlobalTransform ?? Transform3D.Identity;
        Transform3D leftHandTargetTransform = _leftHandIKTarget?.GlobalTransform ?? Transform3D.Identity;
        Transform3D rightFootTargetTransform = _rightFootIKTarget?.GlobalTransform ?? Transform3D.Identity;
        Transform3D leftFootTargetTransform = _leftFootIKTarget?.GlobalTransform ?? Transform3D.Identity;

        _poseContextBuilder.HeadTargetTransform = headTargetTransform;
        _poseContextBuilder.HeadTargetRestTransform = headTargetRestTransform;
        _poseContextBuilder.RightHandTargetTransform = rightHandTargetTransform;
        _poseContextBuilder.LeftHandTargetTransform = leftHandTargetTransform;
        _poseContextBuilder.RightFootTargetTransform = rightFootTargetTransform;
        _poseContextBuilder.LeftFootTargetTransform = leftFootTargetTransform;
        _poseContextBuilder.WorldScale = _worldScale;
        _poseContextBuilder.Skeleton = skeleton;
        _poseContextBuilder.HipBoneIndex = HipBoneIndex;
        _poseContextBuilder.HeadBoneIndex = HeadBoneIndex;
        _poseContextBuilder.Delta = delta;
        _poseContextBuilder.ClearAuxiliarySignals();

        return _poseContextBuilder.Build();
    }

    private void EnsureFollowers()
    {
        if (_headFollower is not null && _rightHandFollower is not null && _leftHandFollower is not null)
        {
            return;
        }

        CharacterBody3D headTarget = _headIKTarget
                                     ?? throw new InvalidOperationException(
                                         "PlayerVRIK head target not resolved before follower setup.");
        CharacterBody3D rightHandTarget = _rightHandIKTarget
                                          ?? throw new InvalidOperationException(
                                              "PlayerVRIK right-hand target not resolved before follower setup.");
        CharacterBody3D leftHandTarget = _leftHandIKTarget
                                         ?? throw new InvalidOperationException(
                                             "PlayerVRIK left-hand target not resolved before follower setup.");

        _headFollower = new IKTargetBodyFollower(headTarget, BuildHeadTargetTransform)
        {
            MaximumSpeed = HeadTargetMaximumSpeed,
        };

        _rightHandFollower = new IKTargetBodyFollower(rightHandTarget, BuildRightHandTargetTransform)
        {
            MaximumSpeed = HandTargetMaximumSpeed,
        };

        _leftHandFollower = new IKTargetBodyFollower(leftHandTarget, BuildLeftHandTargetTransform)
        {
            MaximumSpeed = HandTargetMaximumSpeed,
        };
    }
}
