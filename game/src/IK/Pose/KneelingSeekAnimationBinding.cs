using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Animation binding that drives the kneeling seek clip from forward travel past full crouch.
/// </summary>
[GlobalClass]
public partial class KneelingSeekAnimationBinding : AnimationBinding
{
    private bool _warnedMissingTree;
    private bool _warnedMissingSeekPath;
    private bool _warnedMissingPlayback;

    /// <summary>
    /// Full parameter path to the kneeling <c>seek_request</c> property.
    /// </summary>
    [Export]
    public StringName SeekRequestParameter
    {
        get;
        set;
    } = new("parameters/Kneeling/TimeSeek/seek_request");

    /// <summary>
    /// Parameter path for the root state-machine playback object.
    /// </summary>
    [Export]
    public StringName PlaybackParameter
    {
        get;
        set;
    } = new("parameters/playback");

    /// <summary>
    /// Target AnimationTree state while the owning pose state is active.
    /// </summary>
    [Export]
    public StringName TargetStateName
    {
        get;
        set;
    } = new("Kneeling");

    /// <summary>
    /// Duration in seconds of the kneeling seek clip.
    /// </summary>
    [Export]
    public float ClipDurationSeconds
    {
        get;
        set;
    } = 1.0f;

    /// <summary>
    /// Forward offset baseline ratio for the fully crouched pose.
    /// </summary>
    [Export]
    public float FullCrouchForwardOffsetRatio
    {
        get;
        set;
    } = 0.053f;

    /// <summary>
    /// Maximum additional forward travel ratio from full crouch to full kneel seek.
    /// </summary>
    [Export]
    public float MaximumKneelForwardRangeRatio
    {
        get;
        set;
    } = 0.093f;

    /// <inheritdoc />
    public override void Apply(AnimationTree tree, PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (tree is null)
        {
            if (!_warnedMissingTree)
            {
                GD.PushWarning(
                    $"{nameof(KneelingSeekAnimationBinding)}.{nameof(Apply)} called with null AnimationTree; skipping.");
                _warnedMissingTree = true;
            }

            return;
        }

        float forwardFromFullCrouchRatio = KneelingPoseMetrics.ComputeForwardOffsetFromFullCrouchRatio(
            context.HeadTargetRestTransform,
            context.HeadTargetTransform,
            context.RestHeadHeight,
            FullCrouchForwardOffsetRatio);

        float kneelBlend = KneelingPoseMetrics.ComputeKneelSeekBlend(
            forwardFromFullCrouchRatio,
            MaximumKneelForwardRangeRatio);

        WriteSeekRequest(tree, kneelBlend);
        MaybeTravel(tree);
    }

    private void WriteSeekRequest(AnimationTree tree, float poseBlend)
    {
        if (SeekRequestParameter.IsEmpty)
        {
            if (!_warnedMissingSeekPath)
            {
                GD.PushWarning(
                    $"{nameof(KneelingSeekAnimationBinding)}.{nameof(SeekRequestParameter)} is empty; skipping seek writes.");
                _warnedMissingSeekPath = true;
            }

            return;
        }

        float seekTime = poseBlend * ClipDurationSeconds;
        tree.Set(SeekRequestParameter, seekTime);
    }

    private void MaybeTravel(AnimationTree tree)
    {
        if (PlaybackParameter.IsEmpty || TargetStateName.IsEmpty)
        {
            return;
        }

        AnimationNodeStateMachinePlayback? playback =
            tree.Get(PlaybackParameter).As<AnimationNodeStateMachinePlayback>();
        if (playback is null)
        {
            if (!_warnedMissingPlayback)
            {
                GD.PushWarning(
                    $"{nameof(KneelingSeekAnimationBinding)} could not resolve playback object at '{PlaybackParameter}'.");
                _warnedMissingPlayback = true;
            }

            return;
        }

        playback.Travel(TargetStateName);
    }
}
