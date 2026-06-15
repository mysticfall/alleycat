using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// Godot node facade exposing BODY-004 Eyes look and blink control to scene consumers.
/// </summary>
[GlobalClass]
public sealed partial class EyesBehaviour : Node, IEyes
{
    private EyesController? _controller;
    private Node3D? _lookTarget;
    private bool _lookTargetExplicitlyAssigned;
    private bool _deferredRefreshScheduled;

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
        get => _controller?.LookTarget ?? _lookTarget;
        set => SetLookTarget(value);
    }

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
                AnimationTree = GetNodeOrNull<AnimationTree>("../AnimationTree");
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
            LookTarget = _lookTarget,
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

        _controller.Update(delta);
    }

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
        if (_controller is EyesController controller)
        {
            controller.LookTarget = target;
        }

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
        RefreshLookParametersDeferred();
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
        => _controller?.LookTarget is Node3D target && IsValidNode(target);
}
