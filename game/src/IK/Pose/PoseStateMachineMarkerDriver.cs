using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Marker-driven pose-state-machine harness for photobooth and integration verification.
/// </summary>
/// <remarks>
/// This driver builds <see cref="PoseStateContext"/> snapshots from authored marker scenarios
/// so pose-state-machine flows can be validated without binding XR runtime services.
/// </remarks>
[GlobalClass]
public partial class PoseStateMachineMarkerDriver : Node3D
{
    private const string CameraMarkerName = "Camera";
    private const string LeftControllerMarkerName = "LeftController";
    private const string RightControllerMarkerName = "RightController";

    private readonly PoseStateContextBuilder _contextBuilder = new();

    private readonly PoseStateMachine _stateMachine = new();
    private Skeleton3D? _skeleton;
    private Node3D? _restViewpointMarker;
    private Node3D? _scenarioMarkersRoot;
    private Node3D? _leftControllerRestMarker;
    private Node3D? _rightControllerRestMarker;
    private AnimationTree? _animationTree;
    private Node3D? _activeScenario;

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
    /// Skeleton used to compute normalised local head offsets.
    /// </summary>
    [Export]
    public Skeleton3D? Skeleton
    {
        get;
        set;
    }

    /// <summary>
    /// Rest/calibration marker used as <see cref="PoseStateContext.ViewpointGlobalRest"/>.
    /// </summary>
    [Export]
    public Node3D? RestViewpointMarker
    {
        get;
        set;
    }

    /// <summary>
    /// Root node containing named scenario markers.
    /// </summary>
    [Export]
    public Node3D? ScenarioMarkersRoot
    {
        get;
        set;
    }

    /// <summary>
    /// Optional default marker for left-controller transform when a scenario does not define one.
    /// </summary>
    [Export]
    public Node3D? LeftControllerRestMarker
    {
        get;
        set;
    }

    /// <summary>
    /// Optional default marker for right-controller transform when a scenario does not define one.
    /// </summary>
    [Export]
    public Node3D? RightControllerRestMarker
    {
        get;
        set;
    }

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
    /// Default frame delta used by <see cref="ApplyScenario"/> and <see cref="TickActiveScenario"/>.
    /// </summary>
    [Export]
    public double TickDeltaSeconds
    {
        get;
        set;
    } = 1.0 / 60.0;

    /// <summary>
    /// Number of ticks performed by <see cref="ApplyScenario"/>.
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
    /// Currently selected scenario ID.
    /// </summary>
    public StringName CurrentScenarioId
    {
        get;
        private set;
    } = new();

    private int _headBoneIndex = -1;
    private int _hipBoneIndex = -1;

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
    /// Returns scenario names from <see cref="ScenarioMarkersRoot"/> in scene order.
    /// </summary>
    public string[] GetScenarioNames()
    {
        EnsureResolvedNodes();

        var names = new List<string>();
        for (int index = 0; index < _scenarioMarkersRoot!.GetChildCount(); index++)
        {
            if (_scenarioMarkersRoot.GetChild(index) is not Node3D marker)
            {
                continue;
            }

            names.Add(marker.Name.ToString());
        }

        return [.. names];
    }

    /// <summary>
    /// Returns the active pose-state ID, or empty when unresolved.
    /// </summary>
    public StringName GetCurrentStateId() => _stateMachine.CurrentState?.Id ?? new StringName();

    /// <summary>
    /// Returns whether an AnimationTree is currently bound to the internal state machine.
    /// </summary>
    public bool IsAnimationTreeBound() => _stateMachine.AnimationTree is not null;

    /// <summary>
    /// Returns whether the active state currently has a non-null animation binding.
    /// </summary>
    public bool CurrentStateHasAnimationBinding() => _stateMachine.CurrentState?.AnimationBinding is not null;

    /// <summary>
    /// Returns the current TimeSeek binding seek parameter path, or empty when unavailable.
    /// </summary>
    public StringName GetCurrentBindingSeekParameter()
        => (_stateMachine.CurrentState?.AnimationBinding as TimeSeekAnimationBinding)?.SeekRequestParameter
           ?? new StringName();

    /// <summary>
    /// Reads the standing-crouching seek-request parameter from the bound AnimationTree.
    /// </summary>
    public float GetStandingCrouchingSeekRequest()
        => _stateMachine.AnimationTree is { } tree
            ? tree.Get(new StringName("parameters/StandingCrouching/TimeSeek/seek_request")).AsSingle()
            : float.NaN;

    /// <summary>
    /// Selects a scenario by marker name.
    /// </summary>
    public bool SetScenario(StringName scenarioId)
    {
        EnsureResolvedNodes();

        if (_scenarioMarkersRoot!.GetNodeOrNull<Node3D>(new NodePath(scenarioId.ToString())) is not { } marker)
        {
            GD.PushWarning(
                $"{nameof(PoseStateMachineMarkerDriver)} could not resolve scenario marker '{scenarioId}'.");
            return false;
        }

        _activeScenario = marker;
        CurrentScenarioId = scenarioId;
        return true;
    }

    /// <summary>
    /// Applies a named scenario and ticks the state machine.
    /// </summary>
    public bool ApplyScenario(StringName scenarioId)
        => SetScenario(scenarioId) && TickActiveScenario();

    /// <summary>
    /// Ticks the current scenario once or multiple times.
    /// </summary>
    public bool TickActiveScenario(int tickCount = -1, double delta = -1.0)
    {
        EnsureResolvedNodes();

        if (_activeScenario is null)
        {
            GD.PushWarning(
                $"{nameof(PoseStateMachineMarkerDriver)} has no active scenario. Call {nameof(SetScenario)} first.");
            return false;
        }

        int steps = tickCount > 0 ? tickCount : TickCountPerApply;
        double stepDelta = delta > 0.0 ? delta : TickDeltaSeconds;

        for (int step = 0; step < steps; step++)
        {
            _stateMachine.Tick(BuildContext(_activeScenario, stepDelta));
        }

        return true;
    }

    private PoseStateContext BuildContext(Node3D scenario, double delta)
    {
        Transform3D cameraTransform = ResolveScenarioMarkerTransform(scenario, CameraMarkerName, scenario.GlobalTransform);
        Transform3D leftControllerTransform = ResolveScenarioMarkerTransform(
            scenario,
            LeftControllerMarkerName,
            _leftControllerRestMarker?.GlobalTransform ?? Transform3D.Identity);
        Transform3D rightControllerTransform = ResolveScenarioMarkerTransform(
            scenario,
            RightControllerMarkerName,
            _rightControllerRestMarker?.GlobalTransform ?? Transform3D.Identity);

        _contextBuilder.CameraTransform = cameraTransform;
        _contextBuilder.LeftControllerTransform = leftControllerTransform;
        _contextBuilder.RightControllerTransform = rightControllerTransform;
        _contextBuilder.ViewpointGlobalRest = _restViewpointMarker!.GlobalTransform;
        _contextBuilder.WorldScale = WorldScale;
        _contextBuilder.Skeleton = _skeleton;
        _contextBuilder.HeadBoneIndex = _headBoneIndex;
        _contextBuilder.HipBoneIndex = _hipBoneIndex;
        _contextBuilder.Delta = delta;
        _contextBuilder.ClearAuxiliarySignals();

        return _contextBuilder.Build();
    }

    private static Transform3D ResolveScenarioMarkerTransform(Node3D scenario, StringName markerName, Transform3D fallback)
        => scenario.GetNodeOrNull<Node3D>(new NodePath(markerName.ToString()))?.GlobalTransform ?? fallback;

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
        _restViewpointMarker ??= RestViewpointMarker;
        _scenarioMarkersRoot ??= ScenarioMarkersRoot;
        _leftControllerRestMarker ??= LeftControllerRestMarker;
        _rightControllerRestMarker ??= RightControllerRestMarker;
        _animationTree ??= AnimationTree;

        _skeleton ??= GetNodeOrNull<Skeleton3D>(new NodePath("../Subject/Female/Female_export/GeneralSkeleton"));
        _restViewpointMarker ??= GetNodeOrNull<Node3D>(new NodePath("../Markers/PoseStateMachine/RestViewpoint"));
        _scenarioMarkersRoot ??= GetNodeOrNull<Node3D>(new NodePath("../Markers/PoseStateMachine/Scenarios"));
        _leftControllerRestMarker ??= GetNodeOrNull<Node3D>(new NodePath("../Markers/PoseStateMachine/ControllerRestLeft"));
        _rightControllerRestMarker ??= GetNodeOrNull<Node3D>(new NodePath("../Markers/PoseStateMachine/ControllerRestRight"));
        _animationTree ??= GetNodeOrNull<AnimationTree>(new NodePath("../Subject/Female/AnimationTree"));

        if (_skeleton is null)
        {
            throw new InvalidOperationException(
                $"{nameof(PoseStateMachineMarkerDriver)} requires {nameof(Skeleton)}.");
        }

        if (_restViewpointMarker is null)
        {
            throw new InvalidOperationException(
                $"{nameof(PoseStateMachineMarkerDriver)} requires {nameof(RestViewpointMarker)}.");
        }

        if (_scenarioMarkersRoot is null)
        {
            throw new InvalidOperationException(
                $"{nameof(PoseStateMachineMarkerDriver)} requires {nameof(ScenarioMarkersRoot)}.");
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
