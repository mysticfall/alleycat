using System.Runtime.CompilerServices;
using Godot;

namespace AlleyCat.Body.Hands;

/// <summary>
/// Controls BODY-001 Hands hand-pose blend parameters on an <see cref="AnimationTree"/>.
/// </summary>
public sealed class HandPoseController
{
    private static readonly ConditionalWeakTable<AnimationTree, HandPoseController> _controllersByTree = [];

    private readonly HandChannel _left = new(LimbSide.Left);
    private readonly HandChannel _right = new(LimbSide.Right);

    /// <summary>
    /// Gets the shared hand-pose controller for an animation tree.
    /// </summary>
    /// <remarks>
    /// The left and right hand behaviours in the character scene share a single <see cref="AnimationTree"/>. Reusing one
    /// controller preserves both channel states and prevents the later hand node from resetting the earlier hand's blend.
    /// </remarks>
    public static HandPoseController GetOrCreate(AnimationTree animationTree)
        => _controllersByTree.GetValue(
            animationTree ?? throw new ArgumentNullException(nameof(animationTree)),
            static tree => new HandPoseController(tree));

    /// <summary>
    /// Initialises a controller bound to the supplied animation tree.
    /// </summary>
    /// <param name="animationTree">AnimationTree containing the BODY-001 Hands blend nodes.</param>
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
    /// Gets or sets the target left hand pose animation; <see langword="null"/> clears the override.
    /// </summary>
    public Animation? LeftHandPose
    {
        get => _left.TargetPose;
        set => SetHandPose(LimbSide.Left, value, immediate: false);
    }

    /// <summary>
    /// Gets or sets the target right hand pose animation; <see langword="null"/> clears the override.
    /// </summary>
    public Animation? RightHandPose
    {
        get => _right.TargetPose;
        set => SetHandPose(LimbSide.Right, value, immediate: false);
    }

    /// <summary>
    /// Gets or sets the clamped left effective hand-pose blend weight.
    /// </summary>
    public float LeftHandPoseWeight
    {
        get => _left.TargetWeight;
        set => SetHandPoseWeight(LimbSide.Left, value);
    }

    /// <summary>
    /// Gets or sets the clamped right effective hand-pose blend weight.
    /// </summary>
    public float RightHandPoseWeight
    {
        get => _right.TargetWeight;
        set => SetHandPoseWeight(LimbSide.Right, value);
    }

    /// <summary>
    /// Gets the currently applied left hand pose after transition state has settled.
    /// </summary>
    public Animation? CurrentLeftHandPose => _left.CurrentPose;

    /// <summary>
    /// Gets the currently applied right hand pose after transition state has settled.
    /// </summary>
    public Animation? CurrentRightHandPose => _right.CurrentPose;

    /// <summary>
    /// Sets or clears a hand pose, optionally overriding the weight and bypassing smoothing.
    /// </summary>
    public void SetHandPose(LimbSide side, Animation? pose, float? weight = null, bool immediate = false)
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
    public void ClearHandPose(LimbSide side, bool immediate = false)
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

    /// <summary>
    /// Advances smooth transition state for only the requested hand side.
    /// </summary>
    public void Update(LimbSide side, double deltaSeconds)
    {
        float delta = (float)Math.Max(0.0, deltaSeconds);
        UpdateChannel(GetChannel(side), delta);
    }

    private void SetHandPoseWeight(LimbSide side, float value)
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

    private void ApplyPoseNode(HandChannel channel, Animation? pose)
    {
        AnimationNodeAnimation poseNode = ResolvePoseNode(channel.Side);
        StringName animationName = ResolveAnimationName(pose);
        EnsureAnimationAvailable(animationName, pose);
        poseNode.Animation = animationName;
    }

    private AnimationNodeAnimation ResolvePoseNode(LimbSide side)
    {
        return AnimationTree.TreeRoot is not AnimationNodeBlendTree rootTree
            ? throw new InvalidOperationException("Hand poses require the AnimationTree root to be an AnimationNodeBlendTree.")
            : rootTree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(side)) as AnimationNodeAnimation
            ?? throw new InvalidOperationException($"AnimationTree is missing the {side} hand pose AnimationNodeAnimation.");
    }

    private static float ResolveTargetBlend(HandChannel channel)
        => channel.TargetPose is null ? 0f : channel.TargetWeight;

    private void EnsureAnimationAvailable(StringName animationName, Animation? pose)
    {
        if (pose is null)
        {
            return;
        }

        AnimationPlayer? player = ResolveAnimationPlayer();
        if (player is null || player.HasAnimation(animationName))
        {
            return;
        }

        StringName libraryName = new(string.Empty);
        AnimationLibrary library = player.HasAnimationLibrary(libraryName)
            ? player.GetAnimationLibrary(libraryName)
            : new AnimationLibrary();

        if (!player.HasAnimationLibrary(libraryName))
        {
            _ = player.AddAnimationLibrary(libraryName, library);
        }

        _ = library.AddAnimation(animationName, pose);
    }

    private AnimationPlayer? ResolveAnimationPlayer()
    {
        NodePath animPlayerPath = AnimationTree.AnimPlayer;
        return animPlayerPath.IsEmpty
            ? null
            : AnimationTree.GetNodeOrNull<AnimationPlayer>(animPlayerPath);
    }

    private static StringName ResolveAnimationName(Animation? pose)
    {
        return pose is null
            ? new StringName(HandPoseAnimationTreePaths.ResetAnimationName)
            : string.IsNullOrWhiteSpace(pose.ResourceName)
            ? new StringName(pose.ResourcePath.GetFile().GetBaseName())
            : new StringName(pose.ResourceName);
    }

    private HandChannel GetChannel(LimbSide side) => side == LimbSide.Left ? _left : _right;

    private sealed class HandChannel(LimbSide side)
    {
        public LimbSide Side { get; } = side;

        public Animation? TargetPose
        {
            get; set;
        }

        public Animation? CurrentPose
        {
            get; set;
        }

        public Animation? PendingPose
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
