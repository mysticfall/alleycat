using Godot;

namespace AlleyCat.Animation;

/// <summary>
/// Controls ANIM-001 post-pipeline hand pose parameters on an <see cref="AnimationTree"/>.
/// </summary>
public sealed class HandPoseController
{
    private readonly HandChannel _left = new(HandPoseSide.Left);
    private readonly HandChannel _right = new(HandPoseSide.Right);

    /// <summary>
    /// Initialises a controller bound to the supplied animation tree.
    /// </summary>
    /// <param name="animationTree">AnimationTree containing the ANIM-001 blend nodes.</param>
    public HandPoseController(AnimationTree animationTree)
    {
        AnimationTree = animationTree ?? throw new ArgumentNullException(nameof(animationTree));
        InitialiseChannel(_left, immediate: true);
        InitialiseChannel(_right, immediate: true);
    }

    /// <summary>
    /// Gets the controlled animation tree.
    /// </summary>
    public AnimationTree AnimationTree
    {
        get;
    }

    /// <summary>
    /// Gets or sets the duration used to smooth hand-pose activation changes.
    /// </summary>
    public float TransitionDuration { get; set; } = 0.2f;

    /// <summary>
    /// Gets or sets the target left hand pose resource; <see langword="null"/> clears the override.
    /// </summary>
    public Resource? LeftHandPose
    {
        get => _left.TargetPose;
        set => SetHandPose(HandPoseSide.Left, value, immediate: false);
    }

    /// <summary>
    /// Gets or sets the target right hand pose resource; <see langword="null"/> clears the override.
    /// </summary>
    public Resource? RightHandPose
    {
        get => _right.TargetPose;
        set => SetHandPose(HandPoseSide.Right, value, immediate: false);
    }

    /// <summary>
    /// Gets or sets the clamped left effective hand-pose blend weight.
    /// </summary>
    public float LeftHandPoseWeight
    {
        get => _left.TargetWeight;
        set => SetHandPoseWeight(HandPoseSide.Left, value);
    }

    /// <summary>
    /// Gets or sets the clamped right effective hand-pose blend weight.
    /// </summary>
    public float RightHandPoseWeight
    {
        get => _right.TargetWeight;
        set => SetHandPoseWeight(HandPoseSide.Right, value);
    }

    /// <summary>
    /// Gets the currently applied left hand pose after transition state has settled.
    /// </summary>
    public Resource? CurrentLeftHandPose => _left.CurrentPose;

    /// <summary>
    /// Gets the currently applied right hand pose after transition state has settled.
    /// </summary>
    public Resource? CurrentRightHandPose => _right.CurrentPose;

    /// <summary>
    /// Sets or clears a hand pose, optionally overriding the weight and bypassing smoothing.
    /// </summary>
    public void SetHandPose(HandPoseSide side, Resource? pose, float? weight = null, bool immediate = false)
    {
        HandChannel channel = GetChannel(side);
        channel.TargetPose = pose;
        if (weight.HasValue)
        {
            channel.TargetWeight = Mathf.Clamp(weight.Value, 0f, 1f);
        }

        if (immediate || TransitionDuration <= 0f)
        {
            ApplyPoseNode(channel, pose);
            channel.CurrentPose = pose;
            channel.PendingPose = null;
            channel.TargetBlend = ResolveTargetBlend(channel);
            channel.CurrentBlend = channel.TargetBlend;
            WriteChannel(channel);
            return;
        }

        if (ReferenceEquals(channel.CurrentPose, pose))
        {
            channel.TargetBlend = ResolveTargetBlend(channel);
            WriteChannel(channel);
            return;
        }

        if (channel.CurrentPose is null)
        {
            ApplyPoseNode(channel, pose);
            channel.CurrentPose = pose;
            channel.TargetBlend = ResolveTargetBlend(channel);
        }
        else
        {
            channel.PendingPose = pose;
            channel.TargetBlend = 0f;
        }

        WriteChannel(channel);
    }

    /// <summary>
    /// Clears a hand pose for the requested side.
    /// </summary>
    public void ClearHandPose(HandPoseSide side, bool immediate = false)
        => SetHandPose(side, null, immediate: immediate);

    /// <summary>
    /// Advances smooth transition state by the supplied frame delta.
    /// </summary>
    public void Update(double deltaSeconds)
    {
        float delta = (float)Math.Max(0.0, deltaSeconds);
        UpdateChannel(_left, delta);
        UpdateChannel(_right, delta);
    }

    private void SetHandPoseWeight(HandPoseSide side, float value)
    {
        HandChannel channel = GetChannel(side);
        channel.TargetWeight = Mathf.Clamp(value, 0f, 1f);
        channel.TargetBlend = ResolveTargetBlend(channel);
        WriteChannel(channel);
    }

    private void InitialiseChannel(HandChannel channel, bool immediate)
    {
        channel.TargetWeight = 1f;
        channel.TargetBlend = 0f;
        if (immediate)
        {
            channel.CurrentBlend = 0f;
        }

        ApplyPoseNode(channel, null);
        WriteChannel(channel);
    }

    private void UpdateChannel(HandChannel channel, float delta)
    {
        if (Mathf.IsEqualApprox(channel.CurrentBlend, channel.TargetBlend))
        {
            if (channel.PendingPose is not null || (channel.TargetPose is null && channel.CurrentPose is not null))
            {
                ApplyPoseNode(channel, channel.PendingPose);
                channel.CurrentPose = channel.PendingPose;
                channel.PendingPose = null;
                channel.TargetBlend = ResolveTargetBlend(channel);
            }

            WriteChannel(channel);
            return;
        }

        float transitionScale = Mathf.Max(channel.CurrentBlend, channel.TargetBlend);
        float step = TransitionDuration <= 0f ? transitionScale : delta / TransitionDuration * transitionScale;
        channel.CurrentBlend = Mathf.MoveToward(channel.CurrentBlend, channel.TargetBlend, step);
        WriteChannel(channel);
    }

    private void WriteChannel(HandChannel channel) => AnimationTree.Set(HandPoseAnimationTreePaths.GetHandBlendParameter(channel.Side), channel.CurrentBlend);

    private void ApplyPoseNode(HandChannel channel, Resource? pose)
    {
        AnimationNodeAnimation poseNode = ResolvePoseNode(channel.Side);
        poseNode.Animation = ResolveAnimationName(pose);
    }

    private AnimationNodeAnimation ResolvePoseNode(HandPoseSide side)
    {
        return AnimationTree.TreeRoot is not AnimationNodeBlendTree rootTree
            ? throw new InvalidOperationException("Hand poses require the AnimationTree root to be an AnimationNodeBlendTree.")
            : rootTree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(side)) as AnimationNodeAnimation
            ?? throw new InvalidOperationException($"AnimationTree is missing the {side} hand pose AnimationNodeAnimation.");
    }

    private static float ResolveTargetBlend(HandChannel channel)
        => channel.TargetPose is null ? 0f : channel.TargetWeight;

    private static StringName ResolveAnimationName(Resource? pose)
    {
        return pose is null
            ? new StringName(HandPoseAnimationTreePaths.ResetAnimationName)
            : string.IsNullOrWhiteSpace(pose.ResourceName)
            ? new StringName(pose.ResourcePath.GetFile().GetBaseName())
            : new StringName(pose.ResourceName);
    }

    private HandChannel GetChannel(HandPoseSide side) => side == HandPoseSide.Left ? _left : _right;

    private sealed class HandChannel(HandPoseSide side)
    {
        public HandPoseSide Side { get; } = side;

        public Resource? TargetPose
        {
            get; set;
        }

        public Resource? CurrentPose
        {
            get; set;
        }

        public Resource? PendingPose
        {
            get; set;
        }

        public float TargetWeight { get; set; } = 1f;

        public float CurrentBlend
        {
            get; set;
        }

        public float TargetBlend
        {
            get; set;
        }
    }
}
