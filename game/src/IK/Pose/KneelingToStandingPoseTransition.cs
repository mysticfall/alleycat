using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Transition from kneeling back to the standing continuum using an armed forward-retreat trigger.
/// </summary>
/// <remarks>
/// After this transition fires, it becomes fully inert until the head returns close to the
/// neutral forward baseline. This "fired-until-neutral-return" gate prevents double-triggering
/// across the overlapping forward region shared with
/// <see cref="StandingToKneelingPoseTransition"/>. The opposite-direction transition is also
/// locked out on a fire: once either direction fires, both stay inert until the forward-only
/// neutral-return gate clears, preventing same-tick ping-pong within the overlap region.
/// </remarks>
[GlobalClass]
public partial class KneelingToStandingPoseTransition : PoseTransition
{
    /// <summary>
    /// Transitional AnimationTree state that plays the authored kneeling exit clip.
    /// </summary>
    [Export]
    public StringName TransitionStateName
    {
        get;
        set;
    } = new("KneelingExit");

    /// <summary>
    /// Forward offset baseline ratio at the fully crouched pose.
    /// </summary>
    [Export]
    public float FullCrouchForwardOffsetRatio
    {
        get;
        set;
    } = -0.027f;

    /// <summary>
    /// Forward offset ratio past <see cref="FullCrouchForwardOffsetRatio"/> that arms the exit trigger.
    /// </summary>
    [Export]
    public float ArmingForwardOffsetFromFullCrouchRatio
    {
        get;
        set;
    } = 0.200f;

    /// <summary>
    /// Minimum retreat from the armed peak forward offset ratio required to fire the transition.
    /// </summary>
    [Export]
    public float TriggerRetreatFromArmedPeakRatio
    {
        get;
        set;
    } = 0.040f;

    /// <summary>
    /// Maximum absolute forward-offset ratio (relative to the full-crouch forward baseline)
    /// treated as "close to neutral" when clearing the fired-until-neutral-return gate.
    /// </summary>
    /// <remarks>
    /// The comparison uses the same forward-axis metric as arming/retreat/firing
    /// (see <see cref="KneelingPoseMetrics.ComputeForwardOffsetFromFullCrouchRatio"/>), so the
    /// gate is independent of vertical head descent. The absolute value is used so a small
    /// overshoot in either direction from the pose neutral baseline still counts as a
    /// "neutral return".
    /// </remarks>
    [Export]
    public float NeutralReturnMaxOffsetRatio
    {
        get;
        set;
    } = 0.05f;

    private bool _isForwardRetreatArmed;
    private float _armedPeakForwardOffsetRatio;
    private bool _firedSinceNeutralReturn;

    /// <inheritdoc />
    public override bool ShouldTransition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        (bool shouldTransition, bool isArmed, float armedPeakForwardOffsetRatio, bool firedSinceNeutralReturn) = Evaluate(
            context.HeadTargetRestTransform,
            context.HeadTargetTransform,
            context.RestHeadHeight,
            FullCrouchForwardOffsetRatio,
            ArmingForwardOffsetFromFullCrouchRatio,
            TriggerRetreatFromArmedPeakRatio,
            NeutralReturnMaxOffsetRatio,
            _isForwardRetreatArmed,
            _armedPeakForwardOffsetRatio,
            _firedSinceNeutralReturn);

        _isForwardRetreatArmed = isArmed;
        _armedPeakForwardOffsetRatio = armedPeakForwardOffsetRatio;
        _firedSinceNeutralReturn = firedSinceNeutralReturn;
        return shouldTransition;
    }

    /// <summary>
    /// Pure transition predicate and trigger-state advance used by <see cref="ShouldTransition"/>.
    /// </summary>
    /// <remarks>
    /// While <paramref name="firedSinceNeutralReturn"/> is <c>true</c>, the transition remains
    /// fully inert regardless of head motion. The flag clears only once the absolute
    /// forward-axis offset from the pose neutral baseline (computed from
    /// <paramref name="fullCrouchForwardOffsetRatio"/>) is at or below
    /// <paramref name="neutralReturnMaxOffsetRatio"/>, at which point the armed/peak trigger
    /// state is allowed to evolve again from a clean baseline. Vertical head descent does not
    /// influence this gate, so deep-crouch/kneel poses can still return to neutral purely by
    /// straightening the head on the forward axis.
    /// </remarks>
    public static (bool ShouldTransition, bool IsArmed, float ArmedPeakForwardOffsetRatio, bool FiredSinceNeutralReturn) Evaluate(
        Transform3D headTargetRestTransform,
        Transform3D headTargetTransform,
        float restHeadHeight,
        float fullCrouchForwardOffsetRatio,
        float armingForwardOffsetFromFullCrouchRatio,
        float triggerRetreatFromArmedPeakRatio,
        float neutralReturnMaxOffsetRatio,
        bool isArmed,
        float armedPeakForwardOffsetRatio,
        bool firedSinceNeutralReturn)
    {
        float forwardFromFullCrouchRatio = KneelingPoseMetrics.ComputeForwardOffsetFromFullCrouchRatio(
            headTargetRestTransform,
            headTargetTransform,
            restHeadHeight,
            fullCrouchForwardOffsetRatio);

        if (firedSinceNeutralReturn)
        {
            float neutralThreshold = MathF.Max(0f, neutralReturnMaxOffsetRatio);
            if (MathF.Abs(forwardFromFullCrouchRatio) > neutralThreshold)
            {
                return (false, false, 0f, true);
            }
        }

        float armingForwardOffsetRatio = MathF.Max(0f, armingForwardOffsetFromFullCrouchRatio);
        if (!isArmed)
        {
            return forwardFromFullCrouchRatio < armingForwardOffsetRatio
                ? (false, false, 0f, false)
                : (false, true, forwardFromFullCrouchRatio, false);
        }

        armedPeakForwardOffsetRatio = MathF.Max(armedPeakForwardOffsetRatio, forwardFromFullCrouchRatio);

        float retreatFromPeakRatio = armedPeakForwardOffsetRatio - forwardFromFullCrouchRatio;
        float requiredRetreatRatio = MathF.Max(0f, triggerRetreatFromArmedPeakRatio);
        if (retreatFromPeakRatio < requiredRetreatRatio)
        {
            return (false, true, armedPeakForwardOffsetRatio, false);
        }

        return (true, false, 0f, false);
    }

    /// <inheritdoc />
    public override void OnTransitionEnter(PoseStateContext context)
    {
        MarkFiredAndClearTriggerState();
        base.OnTransitionEnter(context);
    }

    /// <inheritdoc />
    public override void OnTransitionExit(PoseStateContext context)
    {
        MarkFiredAndClearTriggerState();
        base.OnTransitionExit(context);
    }

    /// <inheritdoc />
    public override void OnAnotherTransitionFired(PoseStateContext context) =>
        MarkFiredAndClearTriggerState();

    private void MarkFiredAndClearTriggerState()
    {
        _isForwardRetreatArmed = false;
        _armedPeakForwardOffsetRatio = 0f;
        _firedSinceNeutralReturn = true;
    }

    /// <inheritdoc />
    protected override StringName TransitionAnimationStateName => TransitionStateName;
}
