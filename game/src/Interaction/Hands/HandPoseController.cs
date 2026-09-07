using System.Runtime.CompilerServices;
using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.Interaction.Hands;

/// <summary>
/// Controls BODY-001 Hands hand-pose blend parameters on an <see cref="AnimationTree"/>.
/// </summary>
public sealed class HandPoseController
{
    private const float BlendSnapTolerance = 0.0001f;
    private const float TimeSnapTolerance = 0.000001f;

    private static readonly ConditionalWeakTable<AnimationTree, HandPoseController> _controllersByTree = [];

    private static readonly Dictionary<OpticalGrabPresentationOwner, WeakReference<HandPoseController>>
        _controllersByHandoffPublisher = [];

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
        if (channel.HandoffReference is Animation handoffReference && !ReferenceEquals(handoffReference, pose))
        {
            // A replacement must not inherit the old publisher's readiness. Its legitimate publisher must register
            // the new generation/reference before modifier writes can relinquish.
            RevokeOpticalGrabHandoff(channel);
        }

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
            ResetTransition(channel);
            WriteChannel(channel);
            return;
        }

        if (ReferenceEquals(channel.CurrentPose, pose))
        {
            channel.PendingPose = null;
            BeginTransition(channel, ResolveTargetBlend(channel));
            WriteChannel(channel);
            return;
        }

        if (channel.CurrentPose is null)
        {
            ApplyPoseNode(channel, pose);
            channel.CurrentPose = pose;
            BeginTransition(channel, ResolveTargetBlend(channel));
        }
        else
        {
            channel.PendingPose = pose;
            BeginTransition(channel, 0f);
        }

        WriteChannel(channel);
    }

    /// <summary>
    /// Clears a hand pose for the requested side.
    /// </summary>
    public void ClearHandPose(LimbSide side, bool immediate = false)
        => SetHandPose(side, null, immediate: immediate);

    /// <summary>
    /// Registers the held optical publisher whose exact authored pose this controller is transitioning into.
    /// </summary>
    /// <remarks>
    /// The publication is deliberately controller-owned: only the hand behaviour which owns the AnimationTree path
    /// may register it, and the modifier can subsequently read only <see cref="HandPoseHandoffReadiness"/>.
    /// </remarks>
    internal void PublishOpticalGrabHandoff(
        LimbSide side,
        OpticalGrabPresentationOwner publisher,
        GrabPoseReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (publisher.Side != side || reference.Side != side)
        {
            throw new ArgumentException("The optical handoff publisher and reference must belong to the controlled hand side.");
        }

        HandChannel channel = GetChannel(side);
        RevokeOpticalGrabHandoff(channel);
        channel.HandoffPublisher = publisher;
        channel.HandoffReference = reference.Animation;
        _controllersByHandoffPublisher[publisher] = new WeakReference<HandPoseController>(this);
    }

    /// <summary>Revokes this controller's handoff publication for the supplied owner, if it is still current.</summary>
    internal void RevokeOpticalGrabHandoff(OpticalGrabPresentationOwner publisher)
    {
        HandChannel channel = GetChannel(publisher.Side);
        if (channel.HandoffPublisher != publisher)
        {
            return;
        }

        RevokeOpticalGrabHandoff(channel);
    }

    /// <summary>
    /// Reads the current handoff readiness for an exact publisher identity. A missing, stale, or collected publisher
    /// fails closed and never grants modifier suppression.
    /// </summary>
    internal static bool TryGetOpticalGrabHandoffReadiness(
        OpticalGrabPresentationOwner publisher,
        out HandPoseHandoffReadiness readiness)
    {
        readiness = default;
        if (!_controllersByHandoffPublisher.TryGetValue(publisher, out WeakReference<HandPoseController>? weakController)
            || !weakController.TryGetTarget(out HandPoseController? controller))
        {
            _ = _controllersByHandoffPublisher.Remove(publisher);
            return false;
        }

        return controller.TryCreateOpticalGrabHandoffReadiness(publisher, out readiness);
    }

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
        float targetBlend = channel.PendingPose is null ? ResolveTargetBlend(channel) : 0f;
        if (TransitionDuration <= 0f)
        {
            channel.TargetBlend = targetBlend;
            channel.CurrentBlend = targetBlend;
            ResetTransition(channel);
        }
        else
        {
            BeginTransition(channel, targetBlend);
        }

        WriteChannel(channel);
    }

    private void InitialiseChannel(HandChannel channel, bool immediate)
    {
        channel.TargetWeight = 1f;
        channel.TargetBlend = 0f;
        ResetTransition(channel);
        if (immediate)
        {
            channel.CurrentBlend = 0f;
        }

        ApplyPoseNode(channel, null);
        WriteChannel(channel);
    }

    private void UpdateChannel(HandChannel channel, float delta)
    {
        float remainingDelta = delta;
        while (true)
        {
            if (!IsBlendSettled(channel))
            {
                AdvanceTransition(channel, remainingDelta, out remainingDelta);
            }

            if (!IsBlendSettled(channel))
            {
                break;
            }

            channel.CurrentBlend = channel.TargetBlend;
            if (!TryActivateSettledPose(channel))
            {
                break;
            }

            if (remainingDelta <= 0f || IsBlendSettled(channel))
            {
                break;
            }
        }

        WriteChannel(channel);
    }

    private static void AdvanceTransition(HandChannel channel, float delta, out float remainingDelta)
    {
        if (TransitionDurationIsInstant(channel))
        {
            channel.TransitionElapsed = 0f;
            channel.CurrentBlend = channel.TargetBlend;
            remainingDelta = delta;
            return;
        }

        float previousElapsed = channel.TransitionElapsed;
        float remainingTransitionTime = channel.TransitionDuration - previousElapsed;
        channel.TransitionElapsed = delta + TimeSnapTolerance >= remainingTransitionTime
            ? channel.TransitionDuration
            : previousElapsed + delta;
        float progress = Mathf.Clamp(channel.TransitionElapsed / channel.TransitionDuration, 0f, 1f);
        channel.CurrentBlend = channel.TransitionStartBlend + ((channel.TargetBlend - channel.TransitionStartBlend) * progress);
        remainingDelta = Mathf.Max(0f, delta - remainingTransitionTime);
    }

    private bool TryActivateSettledPose(HandChannel channel)
    {
        if (channel.PendingPose is null && (channel.TargetPose is not null || channel.CurrentPose is null))
        {
            return false;
        }

        ApplyPoseNode(channel, channel.PendingPose);
        channel.CurrentPose = channel.PendingPose;
        channel.PendingPose = null;
        BeginTransition(channel, ResolveTargetBlend(channel));
        return true;
    }

    private static bool IsBlendSettled(HandChannel channel)
        => Mathf.Abs(channel.CurrentBlend - channel.TargetBlend) <= BlendSnapTolerance;

    private void BeginTransition(HandChannel channel, float targetBlend)
    {
        channel.TransitionStartBlend = channel.CurrentBlend;
        channel.TransitionElapsed = 0f;
        channel.TargetBlend = targetBlend;
        channel.TransitionDuration = Math.Max(0f, TransitionDuration);
    }

    private static bool TransitionDurationIsInstant(HandChannel channel) => channel.TransitionDuration <= 0f;

    private static void ResetTransition(HandChannel channel)
    {
        channel.TransitionStartBlend = channel.CurrentBlend;
        channel.TransitionElapsed = 0f;
    }

    private void WriteChannel(HandChannel channel) => AnimationTree.Set(HandPoseAnimationTreePaths.GetHandBlendParameter(channel.Side), channel.CurrentBlend);

    private void ApplyPoseNode(HandChannel channel, Animation? pose)
    {
        AnimationNodeAnimation poseNode = ResolvePoseNode(channel.Side);
        StringName animationName = pose is null
            ? new StringName(HandPoseAnimationTreePaths.ResetAnimationName)
            : EnsureAnimationAvailable(pose);
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

    /// <summary>
    /// Resolves an AnimationPlayer key for the exact pose instance. An existing authored key may be reused only
    /// when it already maps to that instance; name/path equality alone is an alias and must never select another
    /// candidate resource.
    /// </summary>
    private StringName EnsureAnimationAvailable(Animation pose)
    {
        AnimationPlayer? player = ResolveAnimationPlayer();
        StringName preferredName = ResolvePreferredAnimationName(pose);
        if (player is null)
        {
            return preferredName;
        }

        StringName libraryName = new(string.Empty);
        AnimationLibrary library = player.HasAnimationLibrary(libraryName)
            ? player.GetAnimationLibrary(libraryName)
            : new AnimationLibrary();

        if (!player.HasAnimationLibrary(libraryName))
        {
            _ = player.AddAnimationLibrary(libraryName, library);
        }

        if (!player.HasAnimation(preferredName))
        {
            _ = library.AddAnimation(preferredName, pose);
            return preferredName;
        }

        if (ReferenceEquals(player.GetAnimation(preferredName), pose))
        {
            return preferredName;
        }

        // A colliding authored key belongs to a different resource. Keep it untouched and register this exact
        // instance under a deterministic, process-unique key. Instance IDs are identity, not authored content.
        StringName instanceName = new($"__alleycat_hand_pose_{pose.GetInstanceId()}");
        if (!player.HasAnimation(instanceName))
        {
            _ = library.AddAnimation(instanceName, pose);
        }
        else if (!ReferenceEquals(player.GetAnimation(instanceName), pose))
        {
            throw new InvalidOperationException(
                $"AnimationPlayer key '{instanceName}' unexpectedly aliases another Animation instance.");
        }

        return instanceName;
    }

    private AnimationPlayer? ResolveAnimationPlayer()
    {
        NodePath animPlayerPath = AnimationTree.AnimPlayer;
        return animPlayerPath.IsEmpty
            ? null
            : AnimationTree.GetNodeOrNull<AnimationPlayer>(animPlayerPath);
    }

    private static StringName ResolvePreferredAnimationName(Animation pose)
    {
        if (!string.IsNullOrWhiteSpace(pose.ResourceName))
        {
            return new StringName(pose.ResourceName);
        }

        string pathName = pose.ResourcePath.GetFile().GetBaseName();
        return string.IsNullOrWhiteSpace(pathName)
            ? new StringName($"__alleycat_hand_pose_{pose.GetInstanceId()}")
            : new StringName(pathName);
    }

    private HandChannel GetChannel(LimbSide side) => side == LimbSide.Left ? _left : _right;

    private bool TryCreateOpticalGrabHandoffReadiness(
        OpticalGrabPresentationOwner publisher,
        out HandPoseHandoffReadiness readiness)
    {
        HandChannel channel = GetChannel(publisher.Side);
        Animation? reference = channel.HandoffReference;
        if (channel.HandoffPublisher != publisher || reference is null)
        {
            readiness = default;
            return false;
        }

        float intendedWeight = channel.TargetWeight;
        bool evaluatedReady = IsHandoffPoseEvaluated(channel, reference, intendedWeight);
        readiness = new HandPoseHandoffReadiness(publisher.Generation, reference, intendedWeight, evaluatedReady);
        return true;
    }

    private bool IsHandoffPoseEvaluated(HandChannel channel, Animation reference, float intendedWeight)
    {
        if (!ReferenceEquals(channel.TargetPose, reference)
            || !ReferenceEquals(channel.CurrentPose, reference)
            || channel.PendingPose is not null
            || Mathf.Abs(channel.CurrentBlend - intendedWeight) > BlendSnapTolerance)
        {
            return false;
        }

        float evaluatedWeight = AnimationTree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(channel.Side)).AsSingle();
        return Mathf.Abs(evaluatedWeight - intendedWeight) <= BlendSnapTolerance
            && ResolveAnimationPlayer() is AnimationPlayer player
            && AnimationTree.TreeRoot is AnimationNodeBlendTree tree
            && tree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(channel.Side)) is AnimationNodeAnimation poseNode
            && player.HasAnimation(poseNode.Animation)
            && ReferenceEquals(player.GetAnimation(poseNode.Animation), reference);
    }

    private void RevokeOpticalGrabHandoff(HandChannel channel)
    {
        if (channel.HandoffPublisher is OpticalGrabPresentationOwner publisher
            && _controllersByHandoffPublisher.TryGetValue(publisher, out WeakReference<HandPoseController>? weakController)
            && weakController.TryGetTarget(out HandPoseController? controller)
            && ReferenceEquals(controller, this))
        {
            _ = _controllersByHandoffPublisher.Remove(publisher);
        }

        channel.HandoffPublisher = null;
        channel.HandoffReference = null;
    }

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

        public float TransitionStartBlend
        {
            get; set;
        }

        public float TransitionElapsed
        {
            get; set;
        }

        public float TransitionDuration
        {
            get; set;
        }

        public OpticalGrabPresentationOwner? HandoffPublisher
        {
            get; set;
        }

        public Animation? HandoffReference
        {
            get; set;
        }
    }
}
