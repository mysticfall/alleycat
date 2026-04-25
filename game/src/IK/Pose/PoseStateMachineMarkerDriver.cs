using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Lightweight node wrapper that drives a <see cref="PoseStateMachine"/> from externally provided
/// IK target transforms.
/// </summary>
[GlobalClass]
public partial class PoseStateMachineMarkerDriver : Node3D
{
    private readonly PoseStateContextBuilder _contextBuilder = new();
    private readonly PoseStateMachine _stateMachine = new();

    private Skeleton3D? _skeleton;
    private AnimationTree? _animationTree;
    private int _headBoneIndex = -1;
    private int _hipBoneIndex = -1;

    /// <summary>
    /// Pose states available to the internal pose state machine.
    /// </summary>
    [Export]
    public PoseState[] States
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Transition edges available to the internal pose state machine.
    /// </summary>
    [Export]
    public PoseTransition[] Transitions
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Initial state ID for the internal pose state machine.
    /// </summary>
    [Export]
    public StringName InitialStateId
    {
        get;
        set;
    } = new("Standing");

    /// <summary>
    /// Optional AnimationTree driven by the pose state machine.
    /// </summary>
    [Export]
    public AnimationTree? AnimationTree
    {
        get;
        set;
    }

    /// <summary>
    /// Skeleton used to compute normalised local head offsets and resolve bone indices.
    /// </summary>
    [Export]
    public Skeleton3D? Skeleton
    {
        get;
        set;
    }

    /// <summary>
    /// Head rest/reference transform used for crouch and kneel metrics.
    /// </summary>
    [Export]
    public Transform3D HeadTargetRestTransform
    {
        get;
        set;
    } = Transform3D.Identity;

    /// <summary>
    /// World-scale value injected into the built pose context.
    /// </summary>
    [Export]
    public float WorldScale
    {
        get;
        set;
    } = 1.0f;

    /// <summary>
    /// Default frame delta used when tick calls receive non-positive delta.
    /// </summary>
    [Export]
    public double TickDeltaSeconds
    {
        get;
        set;
    } = 1.0 / 60.0;

    /// <summary>
    /// Default number of ticks applied when tick calls receive non-positive
    /// tick count.
    /// </summary>
    [Export]
    public int TickCountPerApply
    {
        get;
        set;
    } = 2;

    /// <summary>
    /// Bone name exported into <see cref="PoseStateContext.HeadBoneIndex"/>.
    /// </summary>
    [Export]
    public StringName HeadBoneName
    {
        get;
        set;
    } = new("Head");

    /// <summary>
    /// Bone name exported into <see cref="PoseStateContext.HipBoneIndex"/>.
    /// </summary>
    [Export]
    public StringName HipBoneName
    {
        get;
        set;
    } = new("Hips");

    /// <summary>
    /// When true, enables pose target processing. When false, skips pose target ticks.
    /// </summary>
    [Export]
    public bool Active
    {
        get;
        set;
    } = true;

    /// <inheritdoc />
    public override void _Ready()
    {
        EnsureResolvedNodes();
        ResolveBoneIndices();
        ConfigureStateMachine();
        _stateMachine.EnsureInitialStateResolved();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (IsInstanceValid(_stateMachine))
        {
            _stateMachine.Free();
        }
    }

    /// <summary>
    /// Updates the stored head rest/reference transform used for subsequent ticks.
    /// </summary>
    public void SetHeadTargetRestTransform(Transform3D headTargetRestTransform)
        => HeadTargetRestTransform = headTargetRestTransform;

    /// <summary>
    /// Ticks the pose state machine using externally supplied IK target transforms.
    /// </summary>
    public void TickPoseTargets(
        Transform3D headTargetTransform,
        Transform3D leftHandTargetTransform,
        Transform3D rightHandTargetTransform,
        Transform3D leftFootTargetTransform,
        Transform3D rightFootTargetTransform,
        int tickCount = -1,
        double delta = -1.0)
    {
        TickPoseTargets(
            headTargetTransform,
            leftHandTargetTransform,
            rightHandTargetTransform,
            leftFootTargetTransform,
            rightFootTargetTransform,
            HeadTargetRestTransform,
            tickCount,
            delta);
    }

    /// <summary>
    /// Ticks the pose state machine using externally supplied IK target transforms and an explicit
    /// head rest/reference transform.
    /// </summary>
    public void TickPoseTargets(
        Transform3D headTargetTransform,
        Transform3D leftHandTargetTransform,
        Transform3D rightHandTargetTransform,
        Transform3D leftFootTargetTransform,
        Transform3D rightFootTargetTransform,
        Transform3D headTargetRestTransform,
        int tickCount = -1,
        double delta = -1.0)
    {
        if (!Active)
        {
            return;
        }

        EnsureResolvedNodes();

        int steps = tickCount > 0 ? tickCount : TickCountPerApply;
        double stepDelta = delta > 0.0 ? delta : TickDeltaSeconds;

        for (int step = 0; step < steps; step++)
        {
            PoseStateContext context = BuildContext(
                headTargetTransform,
                leftHandTargetTransform,
                rightHandTargetTransform,
                leftFootTargetTransform,
                rightFootTargetTransform,
                headTargetRestTransform,
                stepDelta);
            _stateMachine.Tick(context);
        }
    }

    /// <summary>
    /// Returns the active pose-state ID, or empty when unresolved.
    /// </summary>
    public StringName GetCurrentStateId() => _stateMachine.CurrentState?.Id ?? new StringName();

    /// <summary>
    /// Returns the internally driven <see cref="PoseStateMachine"/> so test scenes can wire
    /// dependent consumers such as <see cref="HipReconciliationModifier"/> to the same pending
    /// hip target producer.
    /// </summary>
    public PoseStateMachine GetDrivenStateMachine() => _stateMachine;

    /// <summary>
    /// Returns whether an AnimationTree is currently bound to the internal state machine.
    /// </summary>
    public bool IsAnimationTreeBound() => _stateMachine.AnimationTree is not null;

    /// <summary>
    /// Returns whether the active state currently has a non-null animation binding.
    /// </summary>
    public bool CurrentStateHasAnimationBinding() => _stateMachine.CurrentState?.AnimationBinding is not null;

    /// <summary>
    /// Returns the current standing-crouching seek binding parameter path, or empty when unavailable.
    /// </summary>
    public StringName GetCurrentBindingSeekParameter()
        => (_stateMachine.CurrentState?.AnimationBinding as StandingCrouchingSeekAnimationBinding)?.SeekRequestParameter
           ?? new StringName();

    private PoseStateContext BuildContext(
        Transform3D headTargetTransform,
        Transform3D leftHandTargetTransform,
        Transform3D rightHandTargetTransform,
        Transform3D leftFootTargetTransform,
        Transform3D rightFootTargetTransform,
        Transform3D headTargetRestTransform,
        double delta)
    {
        _contextBuilder.HeadTargetTransform = headTargetTransform;
        _contextBuilder.LeftHandTargetTransform = leftHandTargetTransform;
        _contextBuilder.RightHandTargetTransform = rightHandTargetTransform;
        _contextBuilder.LeftFootTargetTransform = leftFootTargetTransform;
        _contextBuilder.RightFootTargetTransform = rightFootTargetTransform;
        _contextBuilder.HeadTargetRestTransform = headTargetRestTransform;
        _contextBuilder.WorldScale = WorldScale;
        _contextBuilder.Skeleton = _skeleton;
        _contextBuilder.HeadBoneIndex = _headBoneIndex;
        _contextBuilder.HipBoneIndex = _hipBoneIndex;
        _contextBuilder.Delta = delta;
        _contextBuilder.ClearAuxiliarySignals();

        return _contextBuilder.Build();
    }

    private void ResolveBoneIndices()
    {
        _headBoneIndex = _skeleton!.FindBone(HeadBoneName);
        if (_headBoneIndex < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(PoseStateMachineMarkerDriver)} could not resolve head bone '{HeadBoneName}'.");
        }

        _hipBoneIndex = _skeleton.FindBone(HipBoneName);
        if (_hipBoneIndex < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(PoseStateMachineMarkerDriver)} could not resolve hip bone '{HipBoneName}'.");
        }
    }

    private void EnsureResolvedNodes()
    {
        _skeleton ??= Skeleton;
        _animationTree ??= AnimationTree;

        if (_skeleton is null)
        {
            throw new InvalidOperationException(
                $"{nameof(PoseStateMachineMarkerDriver)} requires {nameof(Skeleton)}.");
        }
    }

    private void ConfigureStateMachine()
    {
        _stateMachine.States = States;
        _stateMachine.Transitions = Transitions;
        _stateMachine.InitialStateId = InitialStateId;
        _stateMachine.AnimationTree = _animationTree;
    }
}
