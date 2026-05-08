using Godot;

namespace AlleyCat.Body.Hands;

/// <summary>
/// Godot node facade exposing BODY-001 Hands hand-pose control to scene consumers.
/// </summary>
[GlobalClass]
public sealed partial class HandPoseBehaviour : Node, IHand
{
    private HandPoseController? _controller;

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

    /// <inheritdoc />
    public Resource? Pose
    {
        get => Side == LimbSide.Left ? LeftHandPose : RightHandPose;
        set => SetPose(value);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Resource? CurrentPose => Side == LimbSide.Left ? CurrentLeftHandPose : CurrentRightHandPose;

    /// <summary>
    /// Gets or sets the target left hand pose resource.
    /// </summary>
    [Export]
    public Resource? LeftHandPose
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
    /// Gets or sets the target right hand pose resource.
    /// </summary>
    [Export]
    public Resource? RightHandPose
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
    public Resource? CurrentLeftHandPose => _controller?.CurrentLeftHandPose;

    /// <summary>
    /// Gets the currently applied right hand pose after transition state has settled.
    /// </summary>
    public Resource? CurrentRightHandPose => _controller?.CurrentRightHandPose;

    private Resource? _leftHandPose;
    private Resource? _rightHandPose;
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

        _controller = new HandPoseController(AnimationTree)
        {
            TransitionDuration = _transitionDuration,
            LeftHandPoseWeight = _leftHandPoseWeight,
            RightHandPoseWeight = _rightHandPoseWeight
        };
        _controller.SetHandPose(LimbSide.Left, _leftHandPose, immediate: true);
        _controller.SetHandPose(LimbSide.Right, _rightHandPose, immediate: true);
    }

    /// <inheritdoc />
    public override void _Process(double delta) => _controller?.Update(delta);

    /// <summary>
    /// Sets or clears the pose for this hand, optionally overriding the weight and bypassing smoothing.
    /// </summary>
    public void SetPose(Resource? pose, float? weight = null, bool immediate = false)
        => SetHandPose(Side, pose, weight, immediate);

    /// <summary>
    /// Sets or clears a hand pose for the requested side, optionally overriding the weight and bypassing smoothing.
    /// </summary>
    public void SetHandPose(LimbSide side, Resource? pose, float? weight = null, bool immediate = false)
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
}
