using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// Godot node facade exposing BODY-004 Eyes look and blink control to scene consumers.
/// </summary>
[GlobalClass]
public partial class EyesBehaviour : Node, IEyes
{
    private const float DefaultSaccadeIntervalSeconds = 1f;
    private const float DefaultSaccadeSpeedMetresPerSecond = 0.6f;
    private const float DefaultSaccadeAmplitudeMetres = 0.075f;

    private EyesController? _controller;
    private Node3D? _lookTarget;
    private bool _lookTargetExplicitlyAssigned;
    private bool _deferredRefreshScheduled;
    private readonly RandomNumberGenerator _saccadeRandom = new();
    private Vector3 _saccadeAnchorGlobalPosition = new(0f, 0f, -1f);
    private Vector3 _currentSaccadeOffsetGlobal;
    private Vector3 _targetSaccadeOffsetGlobal;
    private float _timeUntilSaccadeAnchorPoll;
    private bool _hasSaccadeAnchor;

    /// <summary>
    /// Gets or sets the animation tree controlled by this behaviour.
    /// </summary>
    [ExportGroup("Targets")]
    [Export]
    public AnimationTree? AnimationTree
    {
        get;
        set
        {
            AnimationTree? previousTree = field;
            field = value;
            if (_controller is not null && !AreSameNode(previousTree, value))
            {
                _controller = null;
            }

            TryInitialiseController();
            ScheduleDeferredRefresh();
        }
    }

    /// <summary>
    /// Gets or sets the viewpoint or head marker used as the eye origin.
    /// </summary>
    [Export]
    public Node3D? EyeOrigin
    {
        get;
        set
        {
            field = value;
            ScheduleDeferredRefresh();
        }
    }

    /// <inheritdoc />
    [Export]
    public Node3D? LookTarget
    {
        get => _lookTarget;
        set => SetLookTarget(value);
    }

    /// <summary>
    /// Gets or sets how often the gaze anchor is re-polled for saccades, in seconds.
    /// </summary>
    [ExportGroup("Saccades")]
    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public float SaccadeInterval
    {
        get;
        set => field = Mathf.Max(0f, value);
    } = DefaultSaccadeIntervalSeconds;

    /// <summary>
    /// Gets or sets how quickly the active saccade offset moves towards a new offset, in metres per second.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.005,or_greater")]
    public float SaccadeSpeed
    {
        get;
        set => field = Mathf.Max(0f, value);
    } = DefaultSaccadeSpeedMetresPerSecond;

    /// <summary>
    /// Gets or sets the maximum saccade offset amplitude around the gaze anchor, in metres.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.25,0.001,or_greater")]
    public float SaccadeAmplitude
    {
        get;
        set
        {
            field = Mathf.Max(0f, value);
            if (Mathf.IsZeroApprox(field))
            {
                _currentSaccadeOffsetGlobal = Vector3.Zero;
                _targetSaccadeOffsetGlobal = Vector3.Zero;
            }
        }
    } = DefaultSaccadeAmplitudeMetres;

    /// <summary>
    /// Gets or sets the maximum horizontal eye angle in degrees.
    /// </summary>
    [ExportGroup("Look")]
    [Export(PropertyHint.Range, "1,90,0.5")]
    public float MaxHorizontalAngleDegrees
    {
        get => _controller?.MaxHorizontalAngleDegrees ?? _maxHorizontalAngleDegrees;
        set
        {
            _maxHorizontalAngleDegrees = Mathf.Max(0.1f, value);
            if (_controller is EyesController controller)
            {
                controller.MaxHorizontalAngleDegrees = _maxHorizontalAngleDegrees;
            }
        }
    }

    /// <summary>
    /// Gets or sets the maximum vertical eye angle in degrees.
    /// </summary>
    [Export(PropertyHint.Range, "1,90,0.5")]
    public float MaxVerticalAngleDegrees
    {
        get => _controller?.MaxVerticalAngleDegrees ?? _maxVerticalAngleDegrees;
        set
        {
            _maxVerticalAngleDegrees = Mathf.Max(0.1f, value);
            if (_controller is EyesController controller)
            {
                controller.MaxVerticalAngleDegrees = _maxVerticalAngleDegrees;
            }
        }
    }

    /// <summary>
    /// Gets or sets the smoothing time in seconds used for visible eye movement.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01,or_greater")]
    public float LookSmoothingTime
    {
        get => _controller?.LookSmoothingTime ?? _lookSmoothingTime;
        set
        {
            _lookSmoothingTime = Mathf.Max(0f, value);
            if (_controller is EyesController controller)
            {
                controller.LookSmoothingTime = _lookSmoothingTime;
            }

            ScheduleDeferredRefresh();
        }
    }

    /// <summary>
    /// Gets or sets the minimum random interval between blinks.
    /// </summary>
    [ExportGroup("Blink")]
    [Export(PropertyHint.Range, "0,30,0.1,or_greater")]
    public float MinimumBlinkInterval
    {
        get => _controller?.MinimumBlinkInterval ?? _minimumBlinkInterval;
        set
        {
            _minimumBlinkInterval = Mathf.Max(0f, value);
            if (_controller is EyesController controller)
            {
                controller.MinimumBlinkInterval = _minimumBlinkInterval;
            }
        }
    }

    /// <summary>
    /// Gets or sets the maximum random interval between blinks.
    /// </summary>
    [Export(PropertyHint.Range, "0,30,0.1,or_greater")]
    public float MaximumBlinkInterval
    {
        get => _controller?.MaximumBlinkInterval ?? _maximumBlinkInterval;
        set
        {
            _maximumBlinkInterval = Mathf.Max(0f, value);
            if (_controller is EyesController controller)
            {
                controller.MaximumBlinkInterval = _maximumBlinkInterval;
            }
        }
    }

    /// <summary>
    /// Gets or sets the duration of a blink in seconds.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,2,0.01,or_greater")]
    public float BlinkDuration
    {
        get => _controller?.BlinkDuration ?? _blinkDuration;
        set
        {
            _blinkDuration = Mathf.Max(0.01f, value);
            if (_controller is EyesController controller)
            {
                controller.BlinkDuration = _blinkDuration;
            }
        }
    }

    private float _maxHorizontalAngleDegrees = 35f;
    private float _maxVerticalAngleDegrees = 25f;
    private float _lookSmoothingTime = 0.08f;
    private float _minimumBlinkInterval = 2.5f;
    private float _maximumBlinkInterval = 6f;
    private float _blinkDuration = 0.3f;

    /// <inheritdoc />
    public override void _EnterTree()
        => SetProcess(true);

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        if (what is (int)NotificationProcess)
        {
            ProcessLookAndBlink(GetProcessDeltaTime());
        }
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        _saccadeRandom.Randomize();
        AnimationTree ??= GetParentOrNull<AnimationTree>();
        EyeOrigin ??= GetParentOrNull<Node3D>();
        TryInitialiseController();
        if (_controller is null)
        {
            GD.PushWarning($"{nameof(EyesBehaviour)} '{Name}' is waiting for an AnimationTree binding.");
            return;
        }

        SetProcess(true);
    }

    private void TryInitialiseController()
    {
        if (IsInsideTree())
        {
            if (!IsValidNode(AnimationTree))
            {
                AnimationTree? resolvedAnimationTree = GetNodeOrNull<AnimationTree>("../AnimationTree");
                if (resolvedAnimationTree is not null)
                {
                    AnimationTree = resolvedAnimationTree;
                }
            }

            if (!IsValidNode(EyeOrigin))
            {
                EyeOrigin = ResolveConventionEyeOrigin();
            }

            Node3D? conventionLookTarget = ResolveConventionLookTarget();
            if (!_lookTargetExplicitlyAssigned && conventionLookTarget is not null && !AreSameNode(_lookTarget, conventionLookTarget))
            {
                SetResolvedLookTarget(conventionLookTarget, explicitlyAssigned: false);
            }
            else if (!IsValidNode(_lookTarget))
            {
                SetResolvedLookTarget(null, explicitlyAssigned: false);
            }
        }

        AnimationTree? animationTree = AnimationTree;
        if (_controller is not null || animationTree is null || !IsInstanceValid(animationTree))
        {
            return;
        }

        _controller = new EyesController(animationTree)
        {
            MaxHorizontalAngleDegrees = _maxHorizontalAngleDegrees,
            MaxVerticalAngleDegrees = _maxVerticalAngleDegrees,
            LookSmoothingTime = _lookSmoothingTime,
            MinimumBlinkInterval = _minimumBlinkInterval,
            MaximumBlinkInterval = _maximumBlinkInterval,
            BlinkDuration = _blinkDuration,
        };
        SetProcess(true);
    }

    /// <inheritdoc />
    public override void _Process(double delta)
        => ProcessLookAndBlink(delta);

    private void ProcessLookAndBlink(double delta)
    {
        if (_controller is not null && !IsInstanceValid(_controller.AnimationTree))
        {
            _controller = null;
            AnimationTree = IsInsideTree() ? GetNodeOrNull<AnimationTree>("../AnimationTree") : null;
        }

        TryInitialiseController();
        if (_controller is null)
        {
            return;
        }

        if (EyeOrigin is Node3D eyeOrigin)
        {
            _controller.EyeOriginGlobalTransform = IsValidNode(eyeOrigin) && eyeOrigin.IsInsideTree()
                ? eyeOrigin.GlobalTransform
                : Transform3D.Identity;
        }

        Vector3 lookPoint = UpdateSaccadeLookPoint(delta);
        _controller.Update(delta, lookPoint);
    }

    /// <summary>
    /// Resolves the current gaze anchor in world space before presentation saccade offsets are applied.
    /// </summary>
    protected virtual Vector3 ResolveWorldLookPoint()
    {
        if (_lookTarget is Node3D lookTarget && IsValidNode(lookTarget) && lookTarget.IsInsideTree())
        {
            return lookTarget.GlobalPosition;
        }

        Transform3D eyeOriginTransform = ResolveEyeOriginGlobalTransform();
        return eyeOriginTransform.Origin - eyeOriginTransform.Basis.Z.Normalized();
    }

    private Vector3 UpdateSaccadeLookPoint(double deltaSeconds)
    {
        float delta = (float)Math.Max(0.0, deltaSeconds);
        if (!_hasSaccadeAnchor || _timeUntilSaccadeAnchorPoll <= 0f)
        {
            _saccadeAnchorGlobalPosition = ResolveWorldLookPoint();
            _hasSaccadeAnchor = true;
            _timeUntilSaccadeAnchorPoll = SaccadeInterval;
            _targetSaccadeOffsetGlobal = ResolveNextSaccadeOffset();
        }
        else
        {
            _timeUntilSaccadeAnchorPoll -= delta;
        }

        _currentSaccadeOffsetGlobal = SaccadeAmplitude <= 0f || SaccadeSpeed <= 0f
            ? Vector3.Zero
            : _currentSaccadeOffsetGlobal.MoveToward(
                _targetSaccadeOffsetGlobal,
                SaccadeSpeed * delta);

        return _saccadeAnchorGlobalPosition + _currentSaccadeOffsetGlobal;
    }

    internal Vector3 ResolveSaccadedLookPointForTesting(double deltaSeconds) => UpdateSaccadeLookPoint(deltaSeconds);

    private Vector3 ResolveNextSaccadeOffset()
    {
        float amplitude = SaccadeAmplitude;
        if (amplitude <= 0f)
        {
            return Vector3.Zero;
        }

        float angle = _saccadeRandom.RandfRange(0f, Mathf.Tau);
        float radius = Mathf.Sqrt(_saccadeRandom.Randf()) * amplitude;
        Transform3D eyeOriginTransform = ResolveEyeOriginGlobalTransform();
        Vector3 right = eyeOriginTransform.Basis.X.Normalized();
        Vector3 up = eyeOriginTransform.Basis.Y.Normalized();
        return (right * (Mathf.Cos(angle) * radius)) + (up * (Mathf.Sin(angle) * radius));
    }

    private Transform3D ResolveEyeOriginGlobalTransform()
        => EyeOrigin is Node3D eyeOrigin && IsValidNode(eyeOrigin) && eyeOrigin.IsInsideTree()
            ? eyeOrigin.GlobalTransform
            : Transform3D.Identity;

    /// <inheritdoc />
    public void SetLookTarget(Node3D? target)
        => SetResolvedLookTarget(target, explicitlyAssigned: target is not null);

    private void SetResolvedLookTarget(Node3D? target, bool explicitlyAssigned)
    {
        if (AreSameNode(_lookTarget, target) && _lookTargetExplicitlyAssigned == explicitlyAssigned)
        {
            return;
        }

        _lookTarget = target;
        _lookTargetExplicitlyAssigned = explicitlyAssigned;
        _hasSaccadeAnchor = false;
        ScheduleDeferredRefresh();
    }

    private static bool IsValidNode(Node? node) => node is not null && IsInstanceValid(node);

    private static bool AreSameNode(Node? left, Node? right)
        => left is null
            ? right is null
            : right is not null && IsInstanceValid(left) && IsInstanceValid(right) && left.GetInstanceId() == right.GetInstanceId();

    private Node3D? ResolveConventionLookTarget()
    {
        Node? ancestor = this;
        while (ancestor is not null)
        {
            if (ancestor.FindChild("LookTarget", recursive: false, owned: false) is Node3D lookTarget)
            {
                return lookTarget;
            }

            ancestor = ancestor.GetParent();
        }

        return null;
    }

    private Node3D? ResolveConventionEyeOrigin()
    {
        Node? ancestor = this;
        while (ancestor is not null)
        {
            Node3D? eyeOrigin = ResolveEyeOriginUnder(ancestor);
            if (eyeOrigin is not null)
            {
                return eyeOrigin;
            }

            ancestor = ancestor.GetParent();
        }

        return null;
    }

    private static Node3D? ResolveEyeOriginUnder(Node root)
    {
        Skeleton3D? skeleton = ResolveSingleSkeleton(root);
        return skeleton?.GetNodeOrNull<Node3D>("Head/Viewpoint");
    }

    private static Skeleton3D? ResolveSingleSkeleton(Node root)
    {
        Skeleton3D? match = null;
        foreach (Node child in root.GetChildren())
        {
            if (child is Skeleton3D skeleton)
            {
                if (match is not null)
                {
                    return null;
                }

                match = skeleton;
            }

            Skeleton3D? childMatch = ResolveSingleSkeleton(child);
            if (childMatch is null)
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = childMatch;
        }

        return match;
    }

    /// <inheritdoc />
    public void ClearLookTarget() => SetLookTarget(null);

    /// <summary>
    /// Refreshes eye parameters once after late scene overrides or test-driven target moves.
    /// </summary>
    public void RefreshLookParametersDeferred()
    {
        _deferredRefreshScheduled = false;
        if (!IsInsideTree())
        {
            return;
        }

        ProcessLookAndBlink(0d);
    }

    private void ScheduleDeferredRefresh()
    {
        if (_deferredRefreshScheduled || !IsInsideTree())
        {
            return;
        }

        _deferredRefreshScheduled = true;
        _ = CallDeferred(MethodName.RefreshLookParametersDeferred);
    }

    /// <summary>
    /// Starts a blink immediately through the runtime controller path.
    /// </summary>
    public void TriggerBlink() => _controller?.TriggerBlink();

    /// <summary>
    /// Gets the horizontal look seek time currently written by the runtime controller.
    /// </summary>
    public float GetHorizontalLookSeekTime()
        => _controller?.AnimationTree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle()
            ?? EyesLookMath.NeutralSeekTimeSeconds;

    /// <summary>
    /// Gets the vertical look seek time currently written by the runtime controller.
    /// </summary>
    public float GetVerticalLookSeekTime()
        => _controller?.AnimationTree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle()
            ?? EyesLookMath.NeutralSeekTimeSeconds;

    /// <summary>
    /// Gets whether the runtime controller currently has a valid look target.
    /// </summary>
    public bool HasRuntimeLookTarget()
        => IsValidNode(_lookTarget);
}
