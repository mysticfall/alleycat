using AlleyCat.Common;
using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Concrete pose state representing the standing-to-crouching continuum.
/// </summary>
/// <remarks>
/// The standing pose family owns the full standing-to-crouching range through a single framework-
/// level state. The state itself scrubs the shared <c>StandingCrouching</c> AnimationTree state
/// across that continuum, while
/// <see cref="HeadTrackingHipProfile"/> provides the matching hip reconciliation behaviour.
/// </remarks>
[GlobalClass]
public partial class StandingPoseState : PoseState, ICrouchingPoseTransitionSource
{
    private const float RestHeadHeightFloor = 1e-3f;
    private const float RatioFloor = 1e-3f;
    private const float SoftLimitBlendRangeFloor = 1e-4f;

    private readonly record struct TransitionBoundTranslationMask(
        bool Up,
        bool Down,
        bool Left,
        bool Right,
        bool Forward,
        bool Back);

    private bool _warnedMissingSeekPath;
    private double _timeSinceEnter = -1.0;
    private PoseState? _snapshotTransitionSourceState;
    private float? _snapshotRotationCompensationScale;
    private HipLimitEnvelope? _snapshotHipOffsetEnvelope;
    private Vector3? _snapshotReferenceHipLocalPosition;

    /// <summary>
    /// Canonical identifier used by <see cref="StandingPoseState"/>.
    /// </summary>
    public static readonly StringName DefaultId = new("Standing");

    /// <summary>
    /// Default steady-state AnimationTree node used by the standing continuum.
    /// </summary>
    public static readonly StringName DefaultAnimationStateName = new("StandingCrouching");

    /// <summary>
    /// Full parameter path of the <see cref="AnimationNodeTimeSeek"/> <c>seek_request</c>
    /// property inside the AnimationTree.
    /// </summary>
    [Export]
    public StringName SeekRequestParameter
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Duration in seconds of the standing-crouching seek clip.
    /// </summary>
    [Export]
    public float ClipDurationSeconds
    {
        get;
        set;
    } = 1.0f;

    /// <summary>
    /// Standing-envelope limits used at the upright end of the continuum.
    /// </summary>
    [Export]
    public OffsetLimits3D? UprightHipOffsetLimits
    {
        get;
        set;
    }

    /// <summary>
    /// Crouched-envelope limits used near the kneeling end of the continuum.
    /// </summary>
    [Export]
    public OffsetLimits3D? CrouchedHipOffsetLimits
    {
        get;
        set;
    }

    /// <summary>
    /// Blend-in range for limited-head behaviour, expressed as a ratio of rest head height.
    /// Small residuals inside this range only partially apply the limited head target so the
    /// first contact with the crouch envelope does not pop abruptly.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.25,0.001,or_greater")]
    public float HeadLimitBlendRangeRatio
    {
        get;
        set;
    } = 0.02f;

    /// <summary>
    /// Scale applied to rotational hip compensation at full crouch. The standing continuum lerps
    /// towards this value as crouch depth approaches 1.0 so lean-back head tilt does not drive an
    /// excessive forward origin shift under the tight crouched envelope.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float FullCrouchRotationCompensationScale
    {
        get;
        set;
    } = 0.1f;

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
    /// Positive values move the reference along avatar-forward after that semantic direction has
    /// been resolved into skeleton-local space for this rig. On the current reference rig, the
    /// imported skeleton is yaw-flipped under its container, so avatar-forward resolves to
    /// skeleton-local +Z. Negative values are not supported by the runtime and clamp to zero.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")]
    public float FullCrouchReferenceForwardShiftRatio
    {
        get;
        set;
    } = 0.04f;

    /// <summary>
    /// Duration in seconds over which the standing continuum blends from a captured crouching
    /// source snapshot back into its own reference, envelope, and rotation continuum.
    /// </summary>
    [Export(PropertyHint.Range, "0,5,0.01,or_greater")]
    public float TransitionBlendDurationSeconds
    {
        get;
        set;
    } = 0.5f;

    /// <summary>
    /// Initialises the state and seeds <see cref="PoseState.Id"/> with <see cref="DefaultId"/>.
    /// </summary>
    public StandingPoseState()
    {
        Id = DefaultId;
        AnimationStateName = DefaultAnimationStateName;
    }

    /// <summary>
    /// Computes the normalised 0..1 standing-crouching pose blend from the rest and current head Y
    /// values, normalised by rest head height.
    /// </summary>
    public static float ComputePoseBlend(
        float restHeadY,
        float currentHeadY,
        float restHeadHeight,
        float fullCrouchReferenceHipHeightRatio)
    {
        float safeRestHeadHeight = restHeadHeight > RestHeadHeightFloor
            ? restHeadHeight
            : 1f;
        float safeFullCrouchDepthRatio = fullCrouchReferenceHipHeightRatio > RatioFloor
            ? fullCrouchReferenceHipHeightRatio
            : RatioFloor;
        float safeFullCrouchDepthMetres = safeRestHeadHeight * safeFullCrouchDepthRatio;

        float descent = restHeadY - currentHeadY;
        float ratio = descent / safeFullCrouchDepthMetres;
        return float.IsFinite(ratio) ? Mathf.Clamp(ratio, 0f, 1f) : 0f;
    }

    /// <inheritdoc />
    public override void Start(AnimationTree tree)
    {
        base.Start(tree);
        WriteSeekRequest(tree, poseBlend: 0f);
    }

    /// <inheritdoc />
    public override void OnEnter(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _timeSinceEnter = 0.0;

        if (context.TransitionSourceState is ICrouchingPoseTransitionSource source)
        {
            _snapshotTransitionSourceState = context.TransitionSourceState as PoseState;
            _snapshotRotationCompensationScale = source.GetEffectiveRotationCompensationScale(context);
            _snapshotHipOffsetEnvelope = source.GetEffectiveHipOffsetEnvelope(context);
            _snapshotReferenceHipLocalPosition = source.GetEffectiveReferenceHipLocalPosition(context);
        }
        else
        {
            _snapshotTransitionSourceState = null;
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

    /// <inheritdoc />
    public override HipLimitFrame BuildHipLimitFrame(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HipLimitFrame standingFrame = BuildStandingHipLimitFrame(context);
        TransitionBoundTranslationMask standingTransitionBoundTranslationMask = BuildStandingTransitionBoundTranslationMask();
        float transitionBlend = ComputeTransitionBlend();
        if ((_snapshotReferenceHipLocalPosition is null && _snapshotHipOffsetEnvelope is null) || transitionBlend >= 1.0f)
        {
            return standingFrame;
        }

        Vector3 blendedReferenceHipLocalPosition = (_snapshotReferenceHipLocalPosition ?? standingFrame.ReferenceHipLocalPosition)
            .Lerp(standingFrame.ReferenceHipLocalPosition, transitionBlend);
        HipLimitEnvelope? blendedOffsetEnvelope = ResolveTransitionEnvelope(standingFrame.OffsetEnvelope, transitionBlend);
        HipLimitBounds? blendedAbsoluteBounds = ResolveTransitionAbsoluteBounds(
            standingFrame.AbsoluteBounds,
            standingFrame.ReferenceHipLocalPosition,
            blendedReferenceHipLocalPosition,
            standingTransitionBoundTranslationMask,
            context.RestHeadHeight,
            transitionBlend);

        return standingFrame with
        {
            ReferenceHipLocalPosition = blendedReferenceHipLocalPosition,
            AbsoluteBounds = blendedAbsoluteBounds,
            OffsetEnvelope = blendedOffsetEnvelope,
        };
    }

    private HipLimitFrame BuildStandingHipLimitFrame(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Skeleton3D? skeleton = context.Skeleton;
        Vector3 defaultReference = ResolveDefaultHipLocalReference(context);
        HipLimitSemanticFrame semanticFrame = HipLimitSemanticFrame.ReferenceRig;

        HipLimitEnvelope? uprightEnvelope = HipLimitEnvelope.FromOffsetLimits(UprightHipOffsetLimits, semanticFrame);
        HipLimitEnvelope? crouchedEnvelope = HipLimitEnvelope.FromOffsetLimits(CrouchedHipOffsetLimits, semanticFrame);
        if (uprightEnvelope is null && crouchedEnvelope is null)
        {
            return new HipLimitFrame
            {
                ReferenceHipLocalPosition = defaultReference,
            };
        }

        if (skeleton is null || context.HipBoneIndex < 0 || context.HipBoneIndex >= skeleton.GetBoneCount())
        {
            HipLimitEnvelope fallbackEnvelope = ResolveFallbackEnvelope(
                uprightEnvelope,
                crouchedEnvelope,
                ComputePoseBlend(context));
            return new HipLimitFrame
            {
                ReferenceHipLocalPosition = defaultReference,
                OffsetEnvelope = fallbackEnvelope,
            };
        }

        Transform3D hipGlobalRest = skeleton.GetBoneGlobalRest(context.HipBoneIndex);
        Vector3 hipLocalRest = hipGlobalRest.Origin;
        // Use the rig's avatar-relative semantic axes so the reference shift direction stays
        // independent of the per-bone rest basis. The production rig carries its yaw flip on
        // the container rather than the hip bone, so a hip-basis-derived forward would point
        // opposite to avatar-forward for this rig.
        Vector3 hipRestUpLocal = semanticFrame.UpLocal;
        Vector3 avatarForwardLocal = semanticFrame.AvatarForwardLocal;
        return ComputeHipLimitFrame(
            hipLocalRest,
            hipRestUpLocal,
            avatarForwardLocal,
            context.HeadTargetRestTransform.Origin.Y,
            context.HeadTargetTransform.Origin.Y,
            context.RestHeadHeight,
            FullCrouchReferenceHipHeightRatio,
            FullCrouchReferenceForwardShiftRatio,
            uprightEnvelope,
            crouchedEnvelope);
    }

    /// <inheritdoc />
    public override HipReconciliationTickResult? ResolveHipReconciliation(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HipReconciliationTickResult? standingTickResult;
        if (HipReconciliation is not HeadTrackingHipProfile headTrackingHipProfile)
        {
            standingTickResult = base.ResolveHipReconciliation(context);
            return BlendTransitionSourceTickResult(context, standingTickResult);
        }

        HipReconciliationProfileResult? profileResult = headTrackingHipProfile.ComputeHipResult(
            context,
            ResolveEffectiveRotationCompensationScale(context));

        standingTickResult = profileResult is null ? null : ApplyHipReconciliation(context, profileResult);
        return BlendTransitionSourceTickResult(context, standingTickResult);
    }

    /// <inheritdoc />
    protected override void ApplyAnimation(PoseStateContext context)
    {
        if (context.AnimationTree == null)
        {
            return;
        }

        float poseBlend = ComputePoseBlend(context);

        WriteSeekRequest(context.AnimationTree, poseBlend);
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

    /// <summary>
    /// Computes the standing-family hip-limit frame for the current continuum position.
    /// The supplied axes and envelopes must already represent avatar/character semantic directions
    /// resolved into skeleton-local space for the active rig.
    /// </summary>
    public static HipLimitFrame ComputeHipLimitFrame(
        Vector3 hipLocalRest,
        Vector3 hipRestUpLocal,
        Vector3 avatarForwardLocal,
        float restHeadY,
        float currentHeadY,
        float restHeadHeight,
        float fullCrouchReferenceHipHeightRatio,
        float fullCrouchReferenceForwardShiftRatio,
        HipLimitEnvelope? uprightEnvelope,
        HipLimitEnvelope? crouchedEnvelope)
    {
        float poseBlend = ComputePoseBlend(
            restHeadY,
            currentHeadY,
            restHeadHeight,
            fullCrouchReferenceHipHeightRatio);

        float safeRestHeadHeight = restHeadHeight > RestHeadHeightFloor
            ? restHeadHeight
            : 1f;
        Vector3 safeHipRestUpLocal = TryNormaliseAxis(hipRestUpLocal, Vector3.Up);
        Vector3 safeAvatarForwardLocal = TryNormaliseAxis(avatarForwardLocal, Vector3.Back);
        Vector3 referenceHipLocalPosition = ComputeReferenceHipLocalPosition(
            hipLocalRest,
            safeHipRestUpLocal,
            safeAvatarForwardLocal,
            safeRestHeadHeight,
            fullCrouchReferenceHipHeightRatio,
            fullCrouchReferenceForwardShiftRatio,
            poseBlend);

        HipLimitEnvelope? interpolatedEnvelope = InterpolateEnvelopeSides(
            uprightEnvelope,
            crouchedEnvelope,
            poseBlend);
        HipLimitBounds? absoluteBounds = ResolveStandingAbsoluteBounds(
            hipLocalRest,
            safeHipRestUpLocal,
            safeAvatarForwardLocal,
            safeRestHeadHeight,
            fullCrouchReferenceHipHeightRatio,
            fullCrouchReferenceForwardShiftRatio,
            poseBlend,
            uprightEnvelope,
            crouchedEnvelope,
            referenceHipLocalPosition);

        return new HipLimitFrame
        {
            ReferenceHipLocalPosition = referenceHipLocalPosition,
            AbsoluteBounds = absoluteBounds,
            OffsetEnvelope = interpolatedEnvelope,
        };
    }

    /// <summary>
    /// Computes the standing family's effective rotation-compensation scale for the supplied pose
    /// blend.
    /// </summary>
    public static float ComputeRotationCompensationScale(
        float poseBlend,
        float fullCrouchRotationCompensationScale)
        => Mathf.Lerp(
            1f,
            Mathf.Clamp(fullCrouchRotationCompensationScale, 0f, 1f),
            Mathf.Clamp(poseBlend, 0f, 1f));

    private static Vector3 TryNormaliseAxis(Vector3 axis, Vector3 fallback)
        => axis.LengthSquared() > Mathf.Epsilon
            ? axis.Normalized()
            : fallback;

    private static HipLimitBounds CreateAbsoluteBounds(
        Vector3 referenceHipLocalPosition,
        HipLimitEnvelope envelope,
        float restHeadHeight)
    {
        float safeRestHeadHeight = restHeadHeight > RestHeadHeightFloor
            ? restHeadHeight
            : 1f;

        return new HipLimitBounds(
            envelope.Up.HasValue ? referenceHipLocalPosition.Y + (Mathf.Max(envelope.Up.Value, 0f) * safeRestHeadHeight) : null,
            envelope.Down.HasValue ? referenceHipLocalPosition.Y - (Mathf.Max(envelope.Down.Value, 0f) * safeRestHeadHeight) : null,
            envelope.Left.HasValue ? referenceHipLocalPosition.X - (Mathf.Max(envelope.Left.Value, 0f) * safeRestHeadHeight) : null,
            envelope.Right.HasValue ? referenceHipLocalPosition.X + (Mathf.Max(envelope.Right.Value, 0f) * safeRestHeadHeight) : null,
            envelope.Forward.HasValue ? referenceHipLocalPosition.Z - (Mathf.Max(envelope.Forward.Value, 0f) * safeRestHeadHeight) : null,
            envelope.Back.HasValue ? referenceHipLocalPosition.Z + (Mathf.Max(envelope.Back.Value, 0f) * safeRestHeadHeight) : null);
    }

    private static HipLimitBounds LerpBounds(HipLimitBounds from, HipLimitBounds to, float weight)
        => new(
            LerpOptionalBound(from.Up, to.Up, weight),
            LerpOptionalBound(from.Down, to.Down, weight),
            LerpOptionalBound(from.Left, to.Left, weight),
            LerpOptionalBound(from.Right, to.Right, weight),
            LerpOptionalBound(from.Forward, to.Forward, weight),
            LerpOptionalBound(from.Back, to.Back, weight));

    private static HipLimitBounds TranslateBounds(HipLimitBounds bounds, Vector3 delta, TransitionBoundTranslationMask translationMask)
        => new(
            translationMask.Up ? TranslateOptionalBound(bounds.Up, delta.Y) : bounds.Up,
            translationMask.Down ? TranslateOptionalBound(bounds.Down, delta.Y) : bounds.Down,
            translationMask.Left ? TranslateOptionalBound(bounds.Left, delta.X) : bounds.Left,
            translationMask.Right ? TranslateOptionalBound(bounds.Right, delta.X) : bounds.Right,
            translationMask.Forward ? TranslateOptionalBound(bounds.Forward, delta.Z) : bounds.Forward,
            translationMask.Back ? TranslateOptionalBound(bounds.Back, delta.Z) : bounds.Back);

    private static float? TranslateOptionalBound(float? value, float delta)
        => value.HasValue ? value.Value + delta : null;

    private static float? LerpOptionalBound(float? from, float? to, float weight)
        => (from, to) switch
        {
            (null, null) => null,
            ({ } fromValue, null) => weight >= 1f ? null : fromValue,
            (null, { } toValue) => weight <= 0f ? null : toValue,
            ({ } fromValue, { } toValue) => Mathf.Lerp(fromValue, toValue, Mathf.Clamp(weight, 0f, 1f)),
        };

    private static HipLimitEnvelope ResolveFallbackEnvelope(
        HipLimitEnvelope? uprightEnvelope,
        HipLimitEnvelope? crouchedEnvelope,
        float poseBlend)
        => InterpolateEnvelopeSides(uprightEnvelope, crouchedEnvelope, poseBlend)
           ?? uprightEnvelope
           ?? crouchedEnvelope
           ?? default;

    private static Vector3 ComputeReferenceHipLocalPosition(
        Vector3 hipLocalRest,
        Vector3 safeHipRestUpLocal,
        Vector3 safeHipRestForwardLocal,
        float safeRestHeadHeight,
        float fullCrouchReferenceHipHeightRatio,
        float fullCrouchReferenceForwardShiftRatio,
        float poseBlend)
    {
        float fullCrouchReferenceHeight = safeRestHeadHeight * Mathf.Max(fullCrouchReferenceHipHeightRatio, 0f);
        float restHipHeight = hipLocalRest.Dot(safeHipRestUpLocal);
        float downwardShift = Mathf.Max(restHipHeight - fullCrouchReferenceHeight, 0f) * poseBlend;
        // The authored value is avatar-forward. This vector has already been resolved into the
        // skeleton-local frame used by the imported rig.
        float forwardShift = safeRestHeadHeight * Mathf.Max(fullCrouchReferenceForwardShiftRatio, 0f) * poseBlend;
        return hipLocalRest
            - (safeHipRestUpLocal * downwardShift)
            + (safeHipRestForwardLocal * forwardShift);
    }

    private static HipLimitEnvelope? InterpolateEnvelopeSides(
        HipLimitEnvelope? uprightEnvelope,
        HipLimitEnvelope? crouchedEnvelope,
        float poseBlend)
        => (uprightEnvelope, crouchedEnvelope) switch
        {
            (null, null) => null,
            ({ } upright, null) => upright,
            (null, { } crouched) => crouched,
            ({ } upright, { } crouched) => HipLimitEnvelope.Lerp(upright, crouched, poseBlend),
        };

    private static HipLimitBounds? ResolveStandingAbsoluteBounds(
        Vector3 hipLocalRest,
        Vector3 safeHipRestUpLocal,
        Vector3 safeHipRestForwardLocal,
        float safeRestHeadHeight,
        float fullCrouchReferenceHipHeightRatio,
        float fullCrouchReferenceForwardShiftRatio,
        float poseBlend,
        HipLimitEnvelope? uprightEnvelope,
        HipLimitEnvelope? crouchedEnvelope,
        Vector3 currentReferenceHipLocalPosition)
    {
        if (uprightEnvelope is null && crouchedEnvelope is null)
        {
            return null;
        }

        Vector3 crouchedReferenceHipLocalPosition = ComputeReferenceHipLocalPosition(
            hipLocalRest,
            safeHipRestUpLocal,
            safeHipRestForwardLocal,
            safeRestHeadHeight,
            fullCrouchReferenceHipHeightRatio,
            fullCrouchReferenceForwardShiftRatio,
            poseBlend: 1f);

        return new HipLimitBounds(
            ResolveUpperBound(
                uprightEnvelope?.Up,
                crouchedEnvelope?.Up,
                hipLocalRest.Y,
                crouchedReferenceHipLocalPosition.Y,
                currentReferenceHipLocalPosition.Y,
                safeRestHeadHeight,
                poseBlend),
            ResolveLowerBound(
                uprightEnvelope?.Down,
                crouchedEnvelope?.Down,
                hipLocalRest.Y,
                crouchedReferenceHipLocalPosition.Y,
                currentReferenceHipLocalPosition.Y,
                safeRestHeadHeight,
                poseBlend),
            ResolveLowerBound(
                uprightEnvelope?.Left,
                crouchedEnvelope?.Left,
                hipLocalRest.X,
                crouchedReferenceHipLocalPosition.X,
                currentReferenceHipLocalPosition.X,
                safeRestHeadHeight,
                poseBlend),
            ResolveUpperBound(
                uprightEnvelope?.Right,
                crouchedEnvelope?.Right,
                hipLocalRest.X,
                crouchedReferenceHipLocalPosition.X,
                currentReferenceHipLocalPosition.X,
                safeRestHeadHeight,
                poseBlend),
            ResolveLowerBound(
                uprightEnvelope?.Forward,
                crouchedEnvelope?.Forward,
                hipLocalRest.Z,
                crouchedReferenceHipLocalPosition.Z,
                currentReferenceHipLocalPosition.Z,
                safeRestHeadHeight,
                poseBlend),
            ResolveUpperBound(
                uprightEnvelope?.Back,
                crouchedEnvelope?.Back,
                hipLocalRest.Z,
                crouchedReferenceHipLocalPosition.Z,
                currentReferenceHipLocalPosition.Z,
                safeRestHeadHeight,
                poseBlend));
    }

    private static float? ResolveUpperBound(
        float? uprightLimit,
        float? crouchedLimit,
        float uprightReference,
        float crouchedReference,
        float currentReference,
        float normalisationDistance,
        float poseBlend)
        => ResolveBound(
            uprightLimit,
            crouchedLimit,
            uprightReference,
            crouchedReference,
            currentReference,
            normalisationDistance,
            poseBlend,
            static (reference, limit, distance) => reference + (Mathf.Max(limit, 0f) * distance));

    private static float? ResolveLowerBound(
        float? uprightLimit,
        float? crouchedLimit,
        float uprightReference,
        float crouchedReference,
        float currentReference,
        float normalisationDistance,
        float poseBlend)
        => ResolveBound(
            uprightLimit,
            crouchedLimit,
            uprightReference,
            crouchedReference,
            currentReference,
            normalisationDistance,
            poseBlend,
            static (reference, limit, distance) => reference - (Mathf.Max(limit, 0f) * distance));

    private static float? ResolveBound(
        float? uprightLimit,
        float? crouchedLimit,
        float uprightReference,
        float crouchedReference,
        float currentReference,
        float normalisationDistance,
        float poseBlend,
        Func<float, float, float, float> applyLimit)
        => (uprightLimit, crouchedLimit) switch
        {
            (null, null) => null,
            ({ } limit, null) => applyLimit(uprightReference, limit, normalisationDistance),
            (null, { } limit) => applyLimit(crouchedReference, limit, normalisationDistance),
            ({ } upright, { } crouched) => applyLimit(
                currentReference,
                Mathf.Lerp(upright, crouched, poseBlend),
                normalisationDistance),
        };

    private float ComputePoseBlend(PoseStateContext context)
        => ComputePoseBlend(
            context.HeadTargetRestTransform.Origin.Y,
            context.HeadTargetTransform.Origin.Y,
            context.RestHeadHeight,
            FullCrouchReferenceHipHeightRatio);

    private TransitionBoundTranslationMask BuildStandingTransitionBoundTranslationMask()
    {
        HipLimitSemanticFrame semanticFrame = HipLimitSemanticFrame.ReferenceRig;
        HipLimitEnvelope? uprightEnvelope = HipLimitEnvelope.FromOffsetLimits(UprightHipOffsetLimits, semanticFrame);
        HipLimitEnvelope? crouchedEnvelope = HipLimitEnvelope.FromOffsetLimits(CrouchedHipOffsetLimits, semanticFrame);

        return new TransitionBoundTranslationMask(
            Up: uprightEnvelope?.Up is not null && crouchedEnvelope?.Up is not null,
            Down: uprightEnvelope?.Down is not null && crouchedEnvelope?.Down is not null,
            Left: uprightEnvelope?.Left is not null && crouchedEnvelope?.Left is not null,
            Right: uprightEnvelope?.Right is not null && crouchedEnvelope?.Right is not null,
            Forward: uprightEnvelope?.Forward is not null && crouchedEnvelope?.Forward is not null,
            Back: uprightEnvelope?.Back is not null && crouchedEnvelope?.Back is not null);
    }

    private float ComputeTransitionBlend()
        => _timeSinceEnter < 0.0 || TransitionBlendDurationSeconds <= 1e-4f
            ? 1.0f
            : (float)Mathf.Clamp(_timeSinceEnter / TransitionBlendDurationSeconds, 0.0, 1.0);

    private HipLimitEnvelope? ResolveTransitionEnvelope(HipLimitEnvelope? standingEnvelope, float transitionBlend)
    {
        if (transitionBlend >= 1.0f)
        {
            return standingEnvelope;
        }

        HipLimitEnvelope? sourceEnvelope = _snapshotHipOffsetEnvelope;
        return sourceEnvelope is null
            ? standingEnvelope
            : standingEnvelope is null
                ? sourceEnvelope
                : HipLimitEnvelope.Lerp(sourceEnvelope.Value, standingEnvelope.Value, transitionBlend);
    }

    private HipLimitBounds? ResolveTransitionAbsoluteBounds(
        HipLimitBounds? standingAbsoluteBounds,
        Vector3 standingReferenceHipLocalPosition,
        Vector3 effectiveReferenceHipLocalPosition,
        TransitionBoundTranslationMask standingTransitionBoundTranslationMask,
        float restHeadHeight,
        float transitionBlend)
    {
        HipLimitBounds? sourceAbsoluteBounds = _snapshotHipOffsetEnvelope is null || _snapshotReferenceHipLocalPosition is null
            ? null
            : CreateAbsoluteBounds(_snapshotReferenceHipLocalPosition.Value, _snapshotHipOffsetEnvelope.Value, restHeadHeight);

        return (sourceAbsoluteBounds, standingAbsoluteBounds) switch
        {
            (null, null) => null,
            ({ } sourceBounds, null) => transitionBlend >= 1.0f ? null : sourceBounds,
            (null, { } targetBounds) => TranslateBounds(
                targetBounds,
                effectiveReferenceHipLocalPosition - standingReferenceHipLocalPosition,
                standingTransitionBoundTranslationMask),
            ({ } sourceBounds, { } targetBounds) => LerpBounds(sourceBounds, targetBounds, transitionBlend),
        };
    }

    private HipReconciliationTickResult? BlendTransitionSourceTickResult(
        PoseStateContext context,
        HipReconciliationTickResult? standingTickResult)
    {
        float transitionBlend = ComputeTransitionBlend();
        if (standingTickResult is null || _snapshotTransitionSourceState is null || transitionBlend >= 1.0f)
        {
            return standingTickResult;
        }

        HipReconciliationTickResult? sourceTickResult = _snapshotTransitionSourceState.ResolveHipReconciliation(context);
        return sourceTickResult is null
            ? standingTickResult
            : BlendTickResults(
                sourceTickResult,
                standingTickResult,
                context.HeadTargetTransform,
                transitionBlend);
    }

    private static HipReconciliationTickResult BlendTickResults(
        HipReconciliationTickResult source,
        HipReconciliationTickResult target,
        Transform3D currentHeadTargetTransform,
        float blend)
        => new()
        {
            AppliedHipLocalPosition = source.AppliedHipLocalPosition.Lerp(target.AppliedHipLocalPosition, blend),
            DesiredFinalHipOffset = source.DesiredFinalHipOffset.Lerp(target.DesiredFinalHipOffset, blend),
            AppliedFinalHipOffset = source.AppliedFinalHipOffset.Lerp(target.AppliedFinalHipOffset, blend),
            LimitedHeadTargetTransform = BlendLimitedHeadTargetTransform(
                source.LimitedHeadTargetTransform,
                target.LimitedHeadTargetTransform,
                currentHeadTargetTransform,
                blend),
        };

    private static Transform3D? BlendLimitedHeadTargetTransform(
        Transform3D? source,
        Transform3D? target,
        Transform3D currentHeadTargetTransform,
        float blend)
    {
        if (!source.HasValue && !target.HasValue)
        {
            return null;
        }

        Transform3D from = source ?? currentHeadTargetTransform;
        Transform3D to = target ?? currentHeadTargetTransform;

        return new Transform3D(
            currentHeadTargetTransform.Basis,
            from.Origin.Lerp(to.Origin, blend));
    }

    private float ResolveEffectiveRotationCompensationScale(PoseStateContext context)
    {
        float standingScale = ComputeStandingRotationCompensationScale(context);
        float transitionBlend = ComputeTransitionBlend();
        return _snapshotRotationCompensationScale is null || transitionBlend >= 1.0f
            ? standingScale
            : Mathf.Lerp(_snapshotRotationCompensationScale.Value, standingScale, transitionBlend);
    }

    /// <inheritdoc />
    public float GetEffectiveRotationCompensationScale(PoseStateContext context)
        => ResolveEffectiveRotationCompensationScale(context);

    /// <inheritdoc />
    public HipLimitEnvelope? GetEffectiveHipOffsetEnvelope(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return BuildHipLimitFrame(context).OffsetEnvelope;
    }

    /// <inheritdoc />
    public Vector3 GetEffectiveReferenceHipLocalPosition(PoseStateContext context)
        => BuildHipLimitFrame(context).ReferenceHipLocalPosition;

    private float ComputeStandingRotationCompensationScale(PoseStateContext context)
        => ComputeRotationCompensationScale(
            ComputePoseBlend(context),
            FullCrouchRotationCompensationScale);

    private void WriteSeekRequest(AnimationTree tree, float poseBlend)
    {
        if (SeekRequestParameter.IsEmpty)
        {
            if (!_warnedMissingSeekPath)
            {
                GD.PushWarning(
                    $"{nameof(StandingPoseState)}.{nameof(SeekRequestParameter)} is empty; skipping seek writes.");
                _warnedMissingSeekPath = true;
            }

            return;
        }

        float seekTime = Mathf.Clamp(poseBlend, 0f, 1f) * ClipDurationSeconds;
        tree.Set(SeekRequestParameter, seekTime);
    }
}
