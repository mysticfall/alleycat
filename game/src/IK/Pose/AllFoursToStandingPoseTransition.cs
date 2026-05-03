using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Transition from all-fours back to the standing continuum while the all-fours state is in its transitioning phase.
/// </summary>
[GlobalClass]
public partial class AllFoursToStandingPoseTransition : PoseTransition
{
    /// <summary>
    /// AnimationTree node name that identifies the all-fours transitioning phase.
    /// </summary>
    [Export]
    public StringName TransitioningStateName
    {
        get;
        set;
    } = AllFoursPoseState.DefaultTransitioningAnimationStateName;

    /// <summary>
    /// Normalised forward offset baseline from the skeleton-local origin used for all-fours entry.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.001,or_greater")]
    public float EntryForwardOffsetThreshold
    {
        get;
        set;
    } = 0.42f;

    /// <summary>
    /// Additional margin below <see cref="EntryForwardOffsetThreshold"/> that permits standing return.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.001,or_greater")]
    public float ReturnForwardMargin
    {
        get;
        set;
    } = 0.05f;

    /// <inheritdoc />
    public override bool ShouldTransition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        AnimationTree? tree = context.AnimationTree;
        if (tree is null || context.Skeleton is null)
        {
            return false;
        }

        AnimationNodeStateMachinePlayback? playback = ResolvePlayback(tree);
        if (playback is null || playback.GetCurrentNode() != TransitioningStateName)
        {
            return false;
        }

        float forwardOffset = AllFoursPoseMetrics.ComputeNormalizedForwardOffsetFromSkeletonOrigin(
            context.Skeleton.GlobalTransform,
            context.HeadTargetTransform,
            context.RestHeadHeight);

        return Evaluate(
            isTransitioningPhaseActive: true,
            forwardOffset,
            EntryForwardOffsetThreshold,
            ReturnForwardMargin);
    }

    /// <summary>
    /// Pure return predicate used by <see cref="ShouldTransition"/>.
    /// </summary>
    public static bool Evaluate(
        bool isTransitioningPhaseActive,
        float forwardOffset,
        float entryForwardOffsetThreshold,
        float returnForwardMargin)
    {
        if (!isTransitioningPhaseActive)
        {
            return false;
        }

        float returnThreshold = entryForwardOffsetThreshold - Mathf.Max(returnForwardMargin, 0f);
        return forwardOffset < returnThreshold;
    }

    /// <inheritdoc />
    protected override StringName TransitionAnimationStateName => StandingPoseState.DefaultAnimationStateName;
}
