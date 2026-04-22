using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Transition from kneeling back to crouching when forward kneel offset returns near baseline.
/// </summary>
[GlobalClass]
public partial class KneelingToCrouchingPoseTransition : PoseTransition
{
    /// <summary>
    /// Forward offset baseline ratio at the fully crouched pose.
    /// </summary>
    [Export]
    public float FullCrouchForwardOffsetRatio
    {
        get;
        set;
    } = 0.053f;

    /// <summary>
    /// Maximum forward offset ratio from full-crouch baseline that still counts as crouching.
    /// </summary>
    [Export]
    public float MaximumForwardOffsetFromFullCrouchRatio
    {
        get;
        set;
    } = 0.020f;

    /// <inheritdoc />
    public override bool ShouldTransition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Evaluate(
            context.HeadTargetRestTransform,
            context.HeadTargetTransform,
            context.RestHeadHeight,
            FullCrouchForwardOffsetRatio,
            MaximumForwardOffsetFromFullCrouchRatio);
    }

    /// <summary>
    /// Pure transition predicate used by <see cref="ShouldTransition"/>.
    /// </summary>
    public static bool Evaluate(
        Transform3D headTargetRestTransform,
        Transform3D headTargetTransform,
        float restHeadHeight,
        float fullCrouchForwardOffsetRatio,
        float maximumForwardOffsetFromFullCrouchRatio)
    {
        float forwardFromFullCrouchRatio = KneelingPoseMetrics.ComputeForwardOffsetFromFullCrouchRatio(
            headTargetRestTransform,
            headTargetTransform,
            restHeadHeight,
            fullCrouchForwardOffsetRatio);

        float clampedMaximumForwardOffsetRatio = MathF.Max(0f, maximumForwardOffsetFromFullCrouchRatio);
        return forwardFromFullCrouchRatio <= clampedMaximumForwardOffsetRatio;
    }
}
