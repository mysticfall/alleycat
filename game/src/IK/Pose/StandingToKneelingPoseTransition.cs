using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Transition from the standing continuum to kneeling, gated by crouch depth and forward lean.
/// </summary>
/// <remarks>
/// The forward metric is authored relative to the fully crouched forward baseline rather than the
/// standing baseline, so kneeling only becomes reachable from a sufficiently lowered standing pose.
/// </remarks>
[GlobalClass]
public partial class StandingToKneelingPoseTransition : PoseTransition
{
    /// <summary>
    /// Crouch depth ratio (normalised by rest head height) considered fully crouched.
    /// </summary>
    [Export]
    public float FullCrouchDepthRatio
    {
        get;
        set;
    } = 0.4f;

    /// <summary>
    /// Minimum crouch depth blend required before kneeling is allowed.
    /// </summary>
    [Export]
    public float MinimumCrouchDepthBlend
    {
        get;
        set;
    } = 0.92f;

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
    /// Minimum forward offset ratio past <see cref="FullCrouchForwardOffsetRatio"/> to trigger kneel.
    /// </summary>
    [Export]
    public float MinimumForwardOffsetFromFullCrouchRatio
    {
        get;
        set;
    } = 0.027f;

    /// <inheritdoc />
    public override bool ShouldTransition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Evaluate(
            context.HeadTargetRestTransform,
            context.HeadTargetTransform,
            context.RestHeadHeight,
            FullCrouchDepthRatio,
            MinimumCrouchDepthBlend,
            FullCrouchForwardOffsetRatio,
            MinimumForwardOffsetFromFullCrouchRatio);
    }

    /// <summary>
    /// Pure transition predicate used by <see cref="ShouldTransition"/>.
    /// </summary>
    public static bool Evaluate(
        Transform3D headTargetRestTransform,
        Transform3D headTargetTransform,
        float restHeadHeight,
        float fullCrouchDepthRatio,
        float minimumCrouchDepthBlend,
        float fullCrouchForwardOffsetRatio,
        float minimumForwardOffsetFromFullCrouchRatio)
    {
        float crouchDepthRatio = KneelingPoseMetrics.ComputeCrouchDepthRatio(
            headTargetRestTransform,
            headTargetTransform,
            restHeadHeight);
        float crouchDepthBlend = KneelingPoseMetrics.ComputeCrouchDepthBlend(
            crouchDepthRatio,
            fullCrouchDepthRatio);
        if (crouchDepthBlend < Mathf.Clamp(minimumCrouchDepthBlend, 0f, 1f))
        {
            return false;
        }

        float forwardFromFullCrouchRatio = KneelingPoseMetrics.ComputeForwardOffsetFromFullCrouchRatio(
            headTargetRestTransform,
            headTargetTransform,
            restHeadHeight,
            fullCrouchForwardOffsetRatio);

        float requiredForwardOffsetRatio = MathF.Max(0f, minimumForwardOffsetFromFullCrouchRatio);
        return forwardFromFullCrouchRatio >= requiredForwardOffsetRatio;
    }
}
