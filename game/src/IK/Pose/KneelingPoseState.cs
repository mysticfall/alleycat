using AlleyCat.Common;
using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Concrete pose state representing a kneeling posture.
/// Builds per-tick hip-limit frames using kneeling-position reference ratios
/// and smoothly blends from the source state's effective hip reference and
/// offset envelope when a transition source is available via
/// <see cref="ICrouchingPoseTransitionSource"/>.
/// </summary>
[GlobalClass]
public partial class KneelingPoseState : PoseState, ICrouchingPoseTransitionSource
{
    private const float RestHeadHeightFloor = 1e-3f;
    private const float SoftLimitBlendRangeFloor = 1e-4f;

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
    /// Scale applied to rotational hip compensation in kneeling posture.
    /// Blended from the source state's effective scale on entry when the source
    /// implements <see cref="ICrouchingPoseTransitionSource"/>.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float KneelingRotationCompensationScale
    {
        get;
        set;
    } = 0.1f;

    /// <summary>
    /// Blend-in range for limited-head behaviour, expressed as a ratio of rest head height.
    /// Small residuals inside this range only partially apply the limited head target so the
    /// first contact with the envelope does not pop abruptly.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.25,0.001,or_greater")]
    public float HeadLimitBlendRangeRatio
    {
        get;
        set;
    } = 0.02f;

    private double _timeSinceEnter = -1.0;
    private float? _snapshotRotationCompensationScale;
    private HipLimitEnvelope? _snapshotHipOffsetEnvelope;
    private Vector3? _snapshotReferenceHipLocalPosition;

    /// <summary>
    /// Initialises the state and seeds <see cref="PoseState.Id"/> with <see cref="DefaultId"/>.
    /// </summary>
    public KneelingPoseState()
    {
        Id = DefaultId;
        AnimationStateName = DefaultAnimationStateName;
    }

    /// <inheritdoc />
    public override void OnEnter(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _timeSinceEnter = 0.0;

        if (context.TransitionSourceState is ICrouchingPoseTransitionSource source)
        {
            _snapshotRotationCompensationScale = source.GetEffectiveRotationCompensationScale(context);
            _snapshotHipOffsetEnvelope = source.GetEffectiveHipOffsetEnvelope(context);
            _snapshotReferenceHipLocalPosition = source.GetEffectiveReferenceHipLocalPosition(context);
        }
        else
        {
            _snapshotRotationCompensationScale = null;
            _snapshotHipOffsetEnvelope = null;
            _snapshotReferenceHipLocalPosition = null;
        }
    }

    /// <inheritdoc />
    public override void OnUpdate(PoseStateContext context)
    {
        base.OnUpdate(context);
        _timeSinceEnter += context.Delta;
    }

    private float ComputeTransitionBlend()
        => _timeSinceEnter < 0.0 || TransitionBlendDurationSeconds <= 1e-4f
            ? 1.0f
            : (float)Mathf.Clamp(
                _timeSinceEnter / TransitionBlendDurationSeconds, 0.0, 1.0);

    private float ResolveEffectiveRotationCompensationScale()
    {
        float kneelingScale = Mathf.Clamp(KneelingRotationCompensationScale, 0f, 1f);

        float transitionBlend = ComputeTransitionBlend();
        return _snapshotRotationCompensationScale is null || transitionBlend >= 1.0f
            ? kneelingScale
            : Mathf.Lerp(_snapshotRotationCompensationScale.Value, kneelingScale, transitionBlend);
    }

    private HipLimitEnvelope? ResolveEffectiveHipOffsetEnvelope(HipLimitEnvelope? kneelingEnvelope)
    {
        float transitionBlend = ComputeTransitionBlend();
        if (transitionBlend >= 1.0f)
        {
            return kneelingEnvelope;
        }

        HipLimitEnvelope? sourceEnvelope = _snapshotHipOffsetEnvelope;
        return sourceEnvelope is null
            ? kneelingEnvelope
            : kneelingEnvelope is null
                ? sourceEnvelope
                : HipLimitEnvelope.Lerp(sourceEnvelope.Value, kneelingEnvelope.Value, transitionBlend);
    }

    /// <inheritdoc />
    public override HipLimitFrame BuildHipLimitFrame(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HipLimitSemanticFrame semanticFrame = HipLimitSemanticFrame.ReferenceRig;
        HipLimitEnvelope? kneelingEnvelope = HipLimitEnvelope.FromOffsetLimits(HipOffsetLimits, semanticFrame);
        HipLimitEnvelope? effectiveEnvelope = ResolveEffectiveHipOffsetEnvelope(kneelingEnvelope);
        Vector3 defaultReference = ResolveDefaultHipLocalReference(context);

        if (effectiveEnvelope is null)
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
        float transitionBlend = ComputeTransitionBlend();

        // Use the rig's avatar-relative semantic axes so the reference shift direction stays
        // independent of per-bone rest bases. The production rig carries its yaw flip on the
        // container rather than the hip bone, so hip-basis-derived axes would point opposite
        // to avatar-forward for this rig.
        Vector3 hipRestUpLocal = semanticFrame.UpLocal;
        Vector3 avatarForwardLocal = semanticFrame.AvatarForwardLocal;

        // Compute the kneeling target reference position from the kneeling ratios,
        // shifting along avatar-relative semantic axes for consistency with Standing.
        float kneelingReferenceHeight = safeRestHeadHeight * Mathf.Max(KneelingReferenceHipHeightRatio, 0f);
        float restHipHeight = defaultReference.Dot(hipRestUpLocal);
        float kneelingDownwardShift = Mathf.Max(restHipHeight - kneelingReferenceHeight, 0f);
        // The authored value is avatar-forward. This vector has already been resolved into the
        // skeleton-local frame used by the imported rig.
        float kneelingForwardShift = safeRestHeadHeight * Mathf.Max(KneelingReferenceForwardShiftRatio, 0f);
        Vector3 kneelingReference = defaultReference
            - (hipRestUpLocal * kneelingDownwardShift)
            + (avatarForwardLocal * kneelingForwardShift);

        // Blend from the snapshot of the source state's reference position to the kneeling
        // reference when a crouching transition source was captured on entry.
        Vector3 effectiveReference;
        if (_snapshotReferenceHipLocalPosition is null || transitionBlend >= 1.0f)
        {
            effectiveReference = kneelingReference;
        }
        else
        {
            Vector3 sourceReference = _snapshotReferenceHipLocalPosition.Value;
            effectiveReference = sourceReference.Lerp(kneelingReference, transitionBlend);
        }

        return new HipLimitFrame
        {
            ReferenceHipLocalPosition = effectiveReference,
            OffsetEnvelope = effectiveEnvelope,
        };
    }

    /// <inheritdoc />
    public override HipReconciliationTickResult? ResolveHipReconciliation(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (HipReconciliation is not HeadTrackingHipProfile headTrackingHipProfile)
        {
            return base.ResolveHipReconciliation(context);
        }

        float rotationCompensationScale = ResolveEffectiveRotationCompensationScale();

        HipReconciliationProfileResult? profileResult = headTrackingHipProfile.ComputeHipResult(
            context,
            rotationCompensationScale);

        return profileResult is null ? null : ApplyHipReconciliation(context, profileResult);
    }

    /// <inheritdoc />
    public float GetEffectiveRotationCompensationScale(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ResolveEffectiveRotationCompensationScale();
    }

    /// <inheritdoc />
    public HipLimitEnvelope? GetEffectiveHipOffsetEnvelope(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HipLimitSemanticFrame semanticFrame = HipLimitSemanticFrame.ReferenceRig;
        HipLimitEnvelope? kneelingEnvelope = HipLimitEnvelope.FromOffsetLimits(HipOffsetLimits, semanticFrame);
        return ResolveEffectiveHipOffsetEnvelope(kneelingEnvelope);
    }

    /// <inheritdoc />
    public Vector3 GetEffectiveReferenceHipLocalPosition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return BuildHipLimitFrame(context).ReferenceHipLocalPosition;
    }

    /// <inheritdoc />
    protected override HipReconciliationTickResult ApplyHipReconciliation(
        PoseStateContext context,
        HipReconciliationProfileResult profileResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profileResult);

        HipReconciliationTickResult tickResult = base.ApplyHipReconciliation(context, profileResult);
        if (!tickResult.LimitedHeadTargetTransform.HasValue)
        {
            return tickResult;
        }

        float safeRestHeadHeight = context.RestHeadHeight > RestHeadHeightFloor
            ? context.RestHeadHeight
            : 1f;
        float blendRange = Mathf.Max(HeadLimitBlendRangeRatio, 0f) * safeRestHeadHeight;
        if (blendRange <= SoftLimitBlendRangeFloor)
        {
            return tickResult;
        }

        float clampBlend = Mathf.Clamp(tickResult.ResidualFinalHipOffset.Length() / blendRange, 0f, 1f);
        if (clampBlend >= 1f)
        {
            return tickResult;
        }

        Transform3D currentHeadTargetTransform = context.HeadTargetTransform;
        Transform3D limitedHeadTargetTransform = tickResult.LimitedHeadTargetTransform.Value;

        return tickResult with
        {
            LimitedHeadTargetTransform = new Transform3D(
                currentHeadTargetTransform.Basis,
                currentHeadTargetTransform.Origin.Lerp(limitedHeadTargetTransform.Origin, clampBlend)),
        };
    }
}
