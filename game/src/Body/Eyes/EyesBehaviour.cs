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

    /// <summary>
    /// Gets or sets the skeleton node used as the root of eye blend-shape filter paths.
    /// </summary>
    [ExportGroup("Blend Shape Filters")]
    [Export]
    public Skeleton3D? Skeleton
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the mesh nodes that expose eye blend shapes controlled by this behaviour.
    /// </summary>
    [Export]
    public MeshInstance3D[] EyeBlendShapeMeshes
    {
        get; set;
    } = [];

    /// <summary>
    /// Gets or sets the animation tree controlled by this behaviour.
    /// </summary>
    [ExportGroup("Targets")]
    [Export]
    public AnimationTree? AnimationTree
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the viewpoint or head marker used as the eye origin.
    /// </summary>
    [Export]
    public Node3D? EyeOrigin
    {
        get; set;
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
    public override void _Ready()
    {
        AnimationTree ??= GetParentOrNull<AnimationTree>();
        EyeOrigin ??= GetParentOrNull<Node3D>();
        if (AnimationTree is null)
        {
            GD.PushError($"{nameof(EyesBehaviour)} requires an AnimationTree reference or AnimationTree parent.");
            return;
        }

        ConfigureEyeBlendFilters();

        _controller = new EyesController(AnimationTree)
        {
            LookTarget = _lookTarget,
            MaxHorizontalAngleDegrees = _maxHorizontalAngleDegrees,
            MaxVerticalAngleDegrees = _maxVerticalAngleDegrees,
            LookSmoothingTime = _lookSmoothingTime,
            MinimumBlinkInterval = _minimumBlinkInterval,
            MaximumBlinkInterval = _maximumBlinkInterval,
            BlinkDuration = _blinkDuration,
        };
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_controller is null)
        {
            return;
        }

        if (EyeOrigin is Node3D eyeOrigin)
        {
            _controller.EyeOriginGlobalTransform = eyeOrigin.GlobalTransform;
        }

        _controller.Update(delta);
    }

    /// <inheritdoc />
    public void SetLookTarget(Node3D? target)
    {
        _lookTarget = target;
        if (_controller is EyesController controller)
        {
            controller.LookTarget = target;
        }
    }

    /// <inheritdoc />
    public void ClearLookTarget() => SetLookTarget(null);

    /// <summary>
    /// Starts a blink immediately through the runtime controller path.
    /// </summary>
    public void TriggerBlink() => _controller?.TriggerBlink();

    private void ConfigureEyeBlendFilters()
    {
        if (Skeleton is null || EyeBlendShapeMeshes.Length == 0)
        {
            GD.PushWarning(
                $"{nameof(EyesBehaviour)} has no configured eye blend-shape skeleton or meshes; existing filters remain unchanged.");
            return;
        }

        if (AnimationTree?.TreeRoot is not AnimationNodeBlendTree root)
        {
            GD.PushError($"{nameof(EyesBehaviour)} requires an AnimationNodeBlendTree root to configure eye filters.");
            return;
        }

        AnimationTree.TreeRoot = (AnimationRootNode)root.Duplicate(true);
        root = (AnimationNodeBlendTree)AnimationTree.TreeRoot;

        IReadOnlyList<string> meshNodeNames = GetMeshNodeNames(EyeBlendShapeMeshes);
        string skeletonNodeName = Skeleton.Name.ToString();

        SetFilterPaths(
            root.GetNode(EyesAnimationTreePaths.HorizontalLookBlendNode),
            EyesAnimationTreePaths.BuildHorizontalLookBlendShapeFilterPaths(skeletonNodeName, meshNodeNames));
        SetFilterPaths(
            root.GetNode(EyesAnimationTreePaths.VerticalLookBlendNode),
            EyesAnimationTreePaths.BuildVerticalLookBlendShapeFilterPaths(skeletonNodeName, meshNodeNames));
        SetFilterPaths(
            root.GetNode(EyesAnimationTreePaths.BlinkOneShotNode),
            EyesAnimationTreePaths.BuildBlinkBlendShapeFilterPaths(skeletonNodeName, meshNodeNames));
    }

    private static IReadOnlyList<string> GetMeshNodeNames(IReadOnlyList<MeshInstance3D> meshes)
    {
        string[] meshNodeNames = new string[meshes.Count];
        for (int index = 0; index < meshes.Count; index++)
        {
            meshNodeNames[index] = meshes[index].Name.ToString();
        }

        return meshNodeNames;
    }

    private static void SetFilterPaths(AnimationNode node, IReadOnlyList<NodePath> filterPaths)
    {
        node.FilterEnabled = true;
        for (int index = 0; index < filterPaths.Count; index++)
        {
            node.SetFilterPath(filterPaths[index], true);
        }
    }
}
