using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Transition into all-fours using an armed-then-continue-forward trigger.
/// </summary>
[GlobalClass]
public partial class AllFoursEntryPoseTransition : PoseTransition
{
    /// <summary>
    /// AnimationTree state entered immediately when the transition fires.
    /// </summary>
    [Export]
    public StringName TransitionStateName
    {
        get;
        set;
    } = AllFoursPoseState.DefaultTransitioningAnimationStateName;

    /// <summary>
    /// Normalised forward offset from the skeleton-local origin that arms the all-fours trigger.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.001,or_greater")]
    public float ArmingForwardOffsetThreshold
    {
        get;
        set;
    } = 0.42f;

    /// <summary>
    /// Additional forward travel required after arming before the transition fires.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.001,or_greater")]
    public float ContinueForwardMargin
    {
        get;
        set;
    } = 0.06f;

    private bool _isArmed;
    private float _armedForwardOffsetRatio;

    /// <inheritdoc />
    public override bool ShouldTransition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        float forwardOffset = context.Skeleton is null
            ? 0f
            : AllFoursPoseMetrics.ComputeNormalizedForwardOffsetFromSkeletonOrigin(
                context.Skeleton.GlobalTransform,
                context.HeadTargetTransform,
                context.RestHeadHeight);

        (bool shouldTransition, bool isArmed, float armedForwardOffsetRatio) = Evaluate(
            forwardOffset,
            ArmingForwardOffsetThreshold,
            ContinueForwardMargin,
            _isArmed,
            _armedForwardOffsetRatio);

        _isArmed = isArmed;
        _armedForwardOffsetRatio = armedForwardOffsetRatio;
        return shouldTransition;
    }

    /// <summary>
    /// Pure trigger predicate and trigger-state advance used by <see cref="ShouldTransition"/>.
    /// </summary>
    public static (bool ShouldTransition, bool IsArmed, float ArmedForwardOffsetRatio) Evaluate(
        float forwardOffset,
        float armingForwardOffsetThreshold,
        float continueForwardMargin,
        bool isArmed,
        float armedForwardOffsetRatio)
    {
        float requiredArmingThreshold = Mathf.Max(armingForwardOffsetThreshold, 0f);
        if (forwardOffset < requiredArmingThreshold)
        {
            return (false, false, 0f);
        }

        if (!isArmed)
        {
            return (false, true, forwardOffset);
        }

        float requiredForwardOffset = armedForwardOffsetRatio + Mathf.Max(continueForwardMargin, 0f);
        return forwardOffset < requiredForwardOffset
            ? (false, true, armedForwardOffsetRatio)
            : (true, false, 0f);
    }

    /// <inheritdoc />
    public override void OnTransitionEnter(PoseStateContext context)
    {
        ClearTriggerState();
        base.OnTransitionEnter(context);
    }

    /// <inheritdoc />
    public override void OnTransitionExit(PoseStateContext context)
    {
        ClearTriggerState();
        base.OnTransitionExit(context);
    }

    /// <inheritdoc />
    public override void OnAnotherTransitionFired(PoseStateContext context) => ClearTriggerState();

    private void ClearTriggerState()
    {
        _isArmed = false;
        _armedForwardOffsetRatio = 0f;
    }

    /// <inheritdoc />
    protected override StringName TransitionAnimationStateName => TransitionStateName;
}
