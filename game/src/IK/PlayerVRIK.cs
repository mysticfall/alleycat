using System.Globalization;
using System.Text;
using AlleyCat.Common;
using AlleyCat.IK.Pose;
using AlleyCat.UI;
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
    private Node3D? _headIKSolveTarget;
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
    /// Virtual head target consumed by downstream IK after hip-limit application.
    /// </summary>
    [Export]
    public Node3D? HeadIKSolveTarget
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
    /// When enabled, shows per-side hip clamp residuals in the debug overlay during play tests.
    /// </summary>
    [Export]
    public bool HipClampDebugOutputEnabled
    {
        get;
        set;
    }

    /// <summary>
    /// When enabled, shows current hip and reference positions for standing-reference tuning.
    /// </summary>
    [Export]
    public bool HipPositionReferenceDebugOutputEnabled
    {
        get;
        set;
    }

    /// <summary>
    /// When enabled, shows forward/back seam instrumentation for clamp, limited-head, and XR-origin correlation.
    /// </summary>
    [Export]
    public bool HipForwardBackSeamDebugOutputEnabled
    {
        get;
        set;
    }

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
    private bool _isDrivingDebugOverlay;
    private Transform3D _viewpointLocalTransform = Transform3D.Identity;
    private Transform3D _viewpointLocalInverseTransform = Transform3D.Identity;
    private float _worldScale = 1.0f;
    private Vector3 _mostRecentOriginCompensationDelta = Vector3.Zero;

    private readonly PoseStateContextBuilder _poseContextBuilder = new();
    private readonly StringBuilder _debugMessageBuilder = new();

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
        ApplyHeadSolveTargetTransform(limitedHeadTargetTransform: null);

        if (!Active || !_isBound)
        {
            ClearHipDebugMessage();
            return;
        }

        IXROrigin? origin = _origin;
        PoseStateMachine? stateMachine = PoseStateMachine;

        if (origin is null || _headFollower is null || _rightHandFollower is null || _leftHandFollower is null || !_isBound)
        {
            ClearHipDebugMessage();
            return;
        }

        origin.OriginNode.GlobalTransform = GlobalTransform;

        _headFollower.Follow(delta);
        _rightHandFollower.Follow(delta);
        _leftHandFollower.Follow(delta);

        ApplyHeadSolveTargetTransform(limitedHeadTargetTransform: null);

        if (stateMachine is null || !stateMachine.Active)
        {
            ClearHipDebugMessage();
            return;
        }

        Skeleton3D skeleton = GetResolvedSkeleton();
        PoseStateContext context = BuildPoseStateContext(skeleton, delta);
        PoseStateMachineTickResult tickResult = stateMachine.Tick(context);
        UpdateHipDebugMessage(tickResult.Context, tickResult);
    }

    private void OnEndStage(double delta)
    {
        if (!Active || !_isBound || _origin is null || _camera is null)
        {
            return;
        }

        Skeleton3D skeleton = GetResolvedSkeleton();

        Transform3D compensatedOriginTransform = ComputeCompensatedOriginTransform(
            skeleton,
            _camera.CameraNode,
            _origin.OriginNode);

        _origin.OriginNode.GlobalTransform = compensatedOriginTransform;
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

    private void ApplyHeadSolveTargetTransform(Transform3D? limitedHeadTargetTransform)
    {
        Node3D? headIKSolveTarget = _headIKSolveTarget;
        CharacterBody3D? headIKTarget = _headIKTarget;
        if (headIKSolveTarget is null || headIKTarget is null)
        {
            return;
        }

        headIKSolveTarget.GlobalTransform = limitedHeadTargetTransform ?? headIKTarget.GlobalTransform;
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

    private void UpdateHipDebugMessage(PoseStateContext context, PoseStateMachineTickResult tickResult)
    {
        if (!HipClampDebugOutputEnabled
            && !HipPositionReferenceDebugOutputEnabled
            && !HipForwardBackSeamDebugOutputEnabled)
        {
            ClearHipDebugMessage();
            return;
        }

        StringBuilder messageBuilder = _debugMessageBuilder;
        _ = messageBuilder.Clear();

        if (HipClampDebugOutputEnabled)
        {
            AppendHipClampDebugLine(messageBuilder, tickResult.ResidualHipOffset);
        }

        if (HipPositionReferenceDebugOutputEnabled)
        {
            if (tickResult.HipLocalPosition is not Vector3 hipLocalPosition || tickResult.ActiveState is null)
            {
                AppendHipPositionReferenceUnavailableLine(messageBuilder);
            }
            else
            {
                if (messageBuilder.Length > 0)
                {
                    _ = messageBuilder.Append('\n');
                }

                HipLimitFrame limitFrame = tickResult.ActiveState.BuildHipLimitFrame(context);
                AppendHipPositionReferenceDebugLine(
                    messageBuilder,
                    hipLocalPosition,
                    limitFrame.ReferenceHipLocalPosition,
                    context.RestHeadHeight);
            }
        }

        if (HipForwardBackSeamDebugOutputEnabled)
        {
            AppendHipForwardBackSeamDebugLine(
                messageBuilder,
                context,
                tickResult,
                _mostRecentOriginCompensationDelta);
        }

        _isDrivingDebugOverlay = this.SetDebugMessage(messageBuilder.ToString());
    }

    private void ClearHipDebugMessage()
    {
        if (!_isDrivingDebugOverlay)
        {
            return;
        }

        this.ClearDebugMessage();
        _isDrivingDebugOverlay = false;
    }

    private static void AppendHipClampDebugLine(StringBuilder messageBuilder, Vector3 residualHipOffset)
    {
        float left = Mathf.Max(-residualHipOffset.X, 0.0f);
        float right = Mathf.Max(residualHipOffset.X, 0.0f);
        float down = Mathf.Max(-residualHipOffset.Y, 0.0f);
        float up = Mathf.Max(residualHipOffset.Y, 0.0f);
        float forward = Mathf.Max(-residualHipOffset.Z, 0.0f);
        float back = Mathf.Max(residualHipOffset.Z, 0.0f);

        _ = messageBuilder.Append("Hip Clamp U:");
        _ = messageBuilder.Append(FormatFloat(up));
        _ = messageBuilder.Append(" D:");
        _ = messageBuilder.Append(FormatFloat(down));
        _ = messageBuilder.Append(" L:");
        _ = messageBuilder.Append(FormatFloat(left));
        _ = messageBuilder.Append(" R:");
        _ = messageBuilder.Append(FormatFloat(right));
        _ = messageBuilder.Append(" F:");
        _ = messageBuilder.Append(FormatFloat(forward));
        _ = messageBuilder.Append(" B:");
        _ = messageBuilder.Append(FormatFloat(back));
    }

    private static void AppendHipPositionReferenceUnavailableLine(StringBuilder messageBuilder)
    {
        AppendDebugLineSeparator(messageBuilder);

        _ = messageBuilder.Append("Hip Pos Ref: unavailable");
    }

    private static void AppendHipPositionReferenceDebugLine(
        StringBuilder messageBuilder,
        Vector3 hipLocalPosition,
        Vector3 referenceHipLocalPosition,
        float restHeadHeight)
    {
        Vector3 divergence = hipLocalPosition - referenceHipLocalPosition;

        _ = messageBuilder.Append("Hip Pos Cur:");
        _ = messageBuilder.Append(FormatVector3(hipLocalPosition));
        _ = messageBuilder.Append(" Ref:");
        _ = messageBuilder.Append(FormatVector3(referenceHipLocalPosition));
        _ = messageBuilder.Append(" Δ:");
        _ = messageBuilder.Append(FormatVector3(divergence));
        _ = messageBuilder.Append(" Δn:");

        if (Mathf.Abs(restHeadHeight) <= HeightEpsilon || !float.IsFinite(restHeadHeight))
        {
            _ = messageBuilder.Append("n/a");
            return;
        }

        _ = messageBuilder.Append(FormatVector3(divergence / restHeadHeight));
    }

    private static void AppendHipForwardBackSeamDebugLine(
        StringBuilder messageBuilder,
        PoseStateContext context,
        PoseStateMachineTickResult tickResult,
        Vector3 originCompensationDelta)
    {
        AppendDebugLineSeparator(messageBuilder);

        Vector3 appliedHipDelta = Vector3.Zero;
        if (tickResult.HipLocalPosition is Vector3 hipLocalPosition && tickResult.ActiveState is not null)
        {
            HipLimitFrame limitFrame = tickResult.ActiveState.BuildHipLimitFrame(context);
            appliedHipDelta = hipLocalPosition - limitFrame.ReferenceHipLocalPosition;
        }

        Vector3 desiredHipDelta = appliedHipDelta + tickResult.ResidualHipOffset;
        Vector3 limitedHeadDelta = Vector3.Zero;
        bool limitedHeadActive;
        if (tickResult.LimitedHeadTargetTransform is Transform3D limitedHeadTargetTransform)
        {
            limitedHeadActive = true;
            limitedHeadDelta = limitedHeadTargetTransform.Origin - context.HeadTargetTransform.Origin;
        }
        else
        {
            limitedHeadActive = false;
        }

        _ = messageBuilder.Append("Hip Seam Zapp:");
        _ = messageBuilder.Append(FormatFloat(appliedHipDelta.Z));
        _ = messageBuilder.Append(" Zdes:");
        _ = messageBuilder.Append(FormatFloat(desiredHipDelta.Z));
        _ = messageBuilder.Append(" Zres:");
        _ = messageBuilder.Append(FormatFloat(tickResult.ResidualHipOffset.Z));
        _ = messageBuilder.Append(" LH:");
        _ = messageBuilder.Append(limitedHeadActive ? 'Y' : 'N');
        _ = messageBuilder.Append(" ΔLH:");
        _ = messageBuilder.Append(FormatVector3(limitedHeadDelta));
        _ = messageBuilder.Append(" ΔOrg:");
        _ = messageBuilder.Append(FormatVector3(originCompensationDelta));
    }

    private static void AppendDebugLineSeparator(StringBuilder messageBuilder)
    {
        if (messageBuilder.Length > 0)
        {
            _ = messageBuilder.Append('\n');
        }
    }

    private static string FormatVector3(Vector3 value)
        => $"({FormatFloat(value.X)},{FormatFloat(value.Y)},{FormatFloat(value.Z)})";

    private static string FormatFloat(float value)
        => value.ToString("F3", CultureInfo.InvariantCulture);

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
        _headIKSolveTarget = HeadIKSolveTarget ?? this.RequireNode<Node3D>("IKTargets/HeadSolve");
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
        _poseContextBuilder.AnimationTree = PoseStateMachine?.AnimationTree;
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
