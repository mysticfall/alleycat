using AlleyCat.Common;
using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Concrete pose state representing a kneeling posture.
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
    /// Full-crouch reference hip height along skeleton-local up, expressed as a ratio of rest head
    /// height.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float FullCrouchReferenceHipHeightRatio
    {
        get;
        set;
    } = 0.21f;

    /// <summary>
    /// Forward reference shift at full crouch, expressed as a ratio of rest head height.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float FullCrouchReferenceForwardShiftRatio
    {
        get;
        set;
    } = 0.04f;

    /// <summary>
    /// Initialises the state and seeds <see cref="PoseState.Id"/> with <see cref="DefaultId"/>.
    /// </summary>
    public KneelingPoseState()
    {
        Id = DefaultId;
        AnimationStateName = DefaultAnimationStateName;
    }

    /// <inheritdoc />
    public override HipLimitFrame BuildHipLimitFrame(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HipLimitEnvelope? offsetEnvelope = HipLimitEnvelope.FromOffsetLimits(HipOffsetLimits);
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
        float fullCrouchReferenceHeight = safeRestHeadHeight * Mathf.Max(FullCrouchReferenceHipHeightRatio, 0f);
        float restHipHeight = defaultReference.Y;
        float downwardShift = Mathf.Max(restHipHeight - fullCrouchReferenceHeight, 0f);
        float forwardShift = safeRestHeadHeight * Mathf.Max(FullCrouchReferenceForwardShiftRatio, 0f);

        return new HipLimitFrame
        {
            ReferenceHipLocalPosition = defaultReference
                - (Vector3.Up * downwardShift)
                + (Vector3.Forward * forwardShift),
            OffsetEnvelope = offsetEnvelope,
        };
    }
}
