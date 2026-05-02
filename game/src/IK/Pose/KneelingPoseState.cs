using AlleyCat.Common;
using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Concrete pose state representing a kneeling posture.
/// Builds per-tick hip-limit frames using kneeling-position reference ratios
/// with configurable transition blending so the hip reference does not jump
/// abruptly when this state is entered.
/// </summary>
[GlobalClass]
public partial class KneelingPoseState : PoseState
{
    private const float RestHeadHeightFloor = 1e-3f;

    /// <summary>
    /// Canonical identifier used by <see cref="KneelingPoseState"/>.
    /// </summary>
    public static readonly StringName DefaultId = new("Kneeling");

    /// <summary>
    /// Default steady-state AnimationTree node used by the kneeling posture.
    /// </summary>
    public static readonly StringName DefaultAnimationStateName = new("Kneeling");

    /// <summary>
    /// Explicit kneeling hip-offset limits applied relative to the kneeling state's rest-derived
    /// reference.
    /// </summary>
    [Export]
    public OffsetLimits3D? HipOffsetLimits
    {
        get;
        set;
    }

    /// <summary>
    /// Rest-pose kneeling reference hip height along skeleton-local up, expressed as a ratio of
    /// rest head height.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float KneelingReferenceHipHeightRatio
    {
        get;
        set;
    } = 0.16f;

    /// <summary>
    /// Forward reference shift at rest kneeling pose, expressed as a ratio of rest head height.
    /// Positive values move the reference along avatar-forward after that semantic direction has
    /// been resolved into skeleton-local space for this rig.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float KneelingReferenceForwardShiftRatio
    {
        get;
        set;
    } = 0.09f;

    /// <summary>
    /// Duration in seconds over which the hip reference position smoothly transitions from the
    /// source ratios to the kneeling ratios when this state is entered.
    /// </summary>
    [Export(PropertyHint.Range, "0,5,0.01,or_greater")]
    public float TransitionBlendDurationSeconds
    {
        get;
        set;
    } = 0.5f;

    /// <summary>
    /// Source hip height ratio used at the start of a transition into this state.
    /// This should match the full-crouch reference height ratio of the standing state
    /// to ensure continuity.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float TransitionSourceHipHeightRatio
    {
        get;
        set;
    } = 0.21f;

    /// <summary>
    /// Source forward shift ratio used at the start of a transition into this state.
    /// This should match the full-crouch forward shift ratio of the standing state
    /// to ensure continuity.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")]
    public float TransitionSourceForwardShiftRatio
    {
        get;
        set;
    } = 0.04f;

    private double _timeSinceEnter = -1.0;

    /// <summary>
    /// Initialises the state and seeds <see cref="PoseState.Id"/> with <see cref="DefaultId"/>.
    /// </summary>
    public KneelingPoseState()
    {
        Id = DefaultId;
        AnimationStateName = DefaultAnimationStateName;
    }

    /// <inheritdoc />
    public override void OnEnter(PoseStateContext context) => _timeSinceEnter = 0.0;

    /// <inheritdoc />
    public override void OnUpdate(PoseStateContext context)
    {
        base.OnUpdate(context);
        _timeSinceEnter += context.Delta;
    }

    /// <inheritdoc />
    public override HipLimitFrame BuildHipLimitFrame(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HipLimitSemanticFrame semanticFrame = HipLimitSemanticFrame.ReferenceRig;
        HipLimitEnvelope? offsetEnvelope = HipLimitEnvelope.FromOffsetLimits(HipOffsetLimits, semanticFrame);
        Vector3 defaultReference = ResolveDefaultHipLocalReference(context);

        if (offsetEnvelope is null)
        {
            return new HipLimitFrame
            {
                ReferenceHipLocalPosition = defaultReference,
            };
        }

        float safeRestHeadHeight = context.RestHeadHeight > RestHeadHeightFloor
            ? context.RestHeadHeight
            : 1f;

        // Compute transition blend: when OnEnter has not been called (e.g. in tests),
        // the timer is negative and we use the full kneeling reference immediately.
        float transitionBlend = _timeSinceEnter < 0.0 || TransitionBlendDurationSeconds <= 1e-4f
            ? 1.0f
            : (float)Mathf.Clamp(
                _timeSinceEnter / TransitionBlendDurationSeconds, 0.0, 1.0);

        float effectiveHipHeightRatio = Mathf.Lerp(
            TransitionSourceHipHeightRatio, KneelingReferenceHipHeightRatio, transitionBlend);
        float effectiveForwardShiftRatio = Mathf.Lerp(
            TransitionSourceForwardShiftRatio, KneelingReferenceForwardShiftRatio, transitionBlend);

        float kneelingReferenceHeight = safeRestHeadHeight * Mathf.Max(effectiveHipHeightRatio, 0f);
        float restHipHeight = defaultReference.Dot(semanticFrame.UpLocal);
        float downwardShift = Mathf.Max(restHipHeight - kneelingReferenceHeight, 0f);
        // The authored value is avatar-forward. This vector has already been resolved into the
        // skeleton-local frame used by the imported rig.
        float forwardShift = safeRestHeadHeight * Mathf.Max(effectiveForwardShiftRatio, 0f);

        return new HipLimitFrame
        {
            ReferenceHipLocalPosition = defaultReference
                - (semanticFrame.UpLocal * downwardShift)
                + (semanticFrame.AvatarForwardLocal * forwardShift),
            OffsetEnvelope = offsetEnvelope,
        };
    }
}
