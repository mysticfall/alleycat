using AlleyCat.Common;
using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Concrete pose state representing the all-fours entry and crawl flow.
/// </summary>
[GlobalClass]
public partial class AllFoursPoseState : PoseState, ICrouchingPoseTransitionSource
{
    private const float RestHeadHeightFloor = 1e-3f;

    /// <summary>
    /// Internal sub-state identifiers owned by <see cref="AllFoursPoseState"/>.
    /// </summary>
    public enum Phase
    {
        /// <summary>
        /// Entry phase where the state scrubs the all-fours enter animation.
        /// </summary>
        Transitioning,

        /// <summary>
        /// Steady crawl-hold phase after the player has moved far enough forward.
        /// </summary>
        Crawling,
    }

    /// <summary>
    /// Canonical identifier used by <see cref="AllFoursPoseState"/>.
    /// </summary>
    public static readonly StringName DefaultId = new("AllFours");

    /// <summary>
    /// Authored AnimationTree node used for the entry/transitioning phase.
    /// </summary>
    public static readonly StringName DefaultTransitioningAnimationStateName = new("AllFoursTransitioning");

    /// <summary>
    /// Authored AnimationTree node used for the steady crawl pose.
    /// </summary>
    public static readonly StringName DefaultCrawlingAnimationStateName = new("AllFours");

    private bool _warnedMissingSeekPath;
    private double _timeSinceEnter = -1.0;
    private HipLimitEnvelope? _snapshotHipOffsetEnvelope;
    private Vector3? _snapshotReferenceHipLocalPosition;

    /// <summary>
    /// Full parameter path of the <see cref="AnimationNodeTimeSeek"/> <c>seek_request</c>
    /// property inside the AnimationTree for the all-fours entry blend tree.
    /// </summary>
    [Export]
    public StringName SeekRequestParameter
    {
        get;
        set;
    } = new("parameters/AllFoursTransitioning/TimeSeek/seek_request");

    /// <summary>
    /// AnimationTree node name used while scrubbing the entry animation.
    /// </summary>
    [Export]
    public StringName TransitioningAnimationStateName
    {
        get;
        set;
    } = DefaultTransitioningAnimationStateName;

    /// <summary>
    /// AnimationTree node name used while holding the crawl pose.
    /// </summary>
    [Export]
    public StringName CrawlingAnimationStateName
    {
        get;
        set;
    } = DefaultCrawlingAnimationStateName;

    /// <summary>
    /// All-fours reference hip height along skeleton-local up, expressed as a ratio of rest head
    /// height.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ReferenceHipHeightRatio
    {
        get;
        set;
    } = 0.26f;

    /// <summary>
    /// All-fours reference forward shift at rest, expressed as a ratio of rest head height.
    /// Positive values move the reference along avatar-forward after that semantic direction has
    /// been resolved into skeleton-local space for this rig.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ReferenceForwardShiftRatio
    {
        get;
        set;
    } = 0.26f;

    /// <summary>
    /// Duration in seconds over which the all-fours reference blends from the captured source
    /// state's effective reference to the authored all-fours reference on entry.
    /// </summary>
    [Export(PropertyHint.Range, "0,5,0.01,or_greater")]
    public float TransitionBlendDurationSeconds
    {
        get;
        set;
    } = 0.5f;

    /// <summary>
    /// Transitioning-phase hip-offset limits applied while scrubbing into or back out of all-fours.
    /// These are blended from the source crouching state's effective envelope on entry so the
    /// all-fours/standing seam keeps clamp continuity.
    /// </summary>
    [Export]
    public OffsetLimits3D? TransitioningHipOffsetLimits
    {
        get;
        set;
    }

    /// <summary>
    /// Crawling-phase hip-offset limits applied once the state has settled into the crawl hold.
    /// When left unset, the transitioning limits continue to drive the crawling phase.
    /// </summary>
    [Export]
    public OffsetLimits3D? CrawlingHipOffsetLimits
    {
        get;
        set;
    }

    /// <summary>
    /// Entry-animation seek window start time in seconds.
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.0001,or_greater")]
    public float SeekWindowStartSeconds
    {
        get;
        set;
    } = 1.2f;

    /// <summary>
    /// Entry-animation seek window end time in seconds.
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.0001,or_greater")]
    public float SeekWindowEndSeconds
    {
        get;
        set;
    } = 3.5417f;

    /// <summary>
    /// Normalised forward offset from the skeleton-local origin where all-fours entry begins.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.001,or_greater")]
    public float EntryForwardOffsetThreshold
    {
        get;
        set;
    } = 0.42f;

    /// <summary>
    /// Normalised forward offset from the skeleton-local origin where entry transitions to crawl hold.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.001,or_greater")]
    public float CrawlForwardOffsetThreshold
    {
        get;
        set;
    } = 0.73f;

    /// <summary>
    /// Normalised vertical offset from the skeleton-local origin above which crawl returns to transitioning.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.001,or_greater")]
    public float CrawlingVerticalReturnThreshold
    {
        get;
        set;
    } = 0.3f;

    /// <summary>
    /// Forward margin below <see cref="EntryForwardOffsetThreshold"/> that allows exit back to standing.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.001,or_greater")]
    public float ReturnForwardMargin
    {
        get;
        set;
    } = 0.05f;

    /// <summary>
    /// Gets the currently active internal phase.
    /// </summary>
    public Phase CurrentPhase
    {
        get;
        private set;
    } = Phase.Transitioning;

    /// <summary>
    /// Initialises the state and seeds <see cref="PoseState.Id"/>.
    /// </summary>
    public AllFoursPoseState()
    {
        Id = DefaultId;
        AnimationStateName = TransitioningAnimationStateName;
    }

    /// <inheritdoc />
    public override void OnEnter(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _timeSinceEnter = 0.0;
        CurrentPhase = Phase.Transitioning;

        if (context.TransitionSourceState is ICrouchingPoseTransitionSource source)
        {
            _snapshotHipOffsetEnvelope = source.GetEffectiveHipOffsetEnvelope(context);
            _snapshotReferenceHipLocalPosition = source.GetEffectiveReferenceHipLocalPosition(context);
        }
        else
        {
            _snapshotHipOffsetEnvelope = null;
            _snapshotReferenceHipLocalPosition = null;
        }

        EnsurePlaybackState(context.AnimationTree, TransitioningAnimationStateName);
        WriteSeekRequest(context.AnimationTree, ResolveForwardOffset(context));
    }

    /// <inheritdoc />
    public override void OnUpdate(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        float forwardOffset = ResolveForwardOffset(context);
        float verticalOffset = ResolveVerticalOffset(context);

        Phase nextPhase = ComputeNextPhase(
            CurrentPhase,
            forwardOffset,
            verticalOffset,
            CrawlForwardOffsetThreshold,
            CrawlingVerticalReturnThreshold);

        if (CurrentPhase == Phase.Crawling && nextPhase == Phase.Transitioning)
        {
            EnsurePlaybackState(context.AnimationTree, TransitioningAnimationStateName);
        }

        CurrentPhase = nextPhase;

        if (CurrentPhase == Phase.Transitioning)
        {
            EnsurePlaybackState(context.AnimationTree, TransitioningAnimationStateName);
            WriteSeekRequest(context.AnimationTree, forwardOffset);

            EnsurePlaybackState(
                context.AnimationTree,
                nextPhase == Phase.Crawling ? CrawlingAnimationStateName : TransitioningAnimationStateName);

            if (nextPhase == Phase.Crawling)
            {
                CurrentPhase = Phase.Crawling;
            }

            _timeSinceEnter += context.Delta;
            return;
        }

        EnsurePlaybackState(context.AnimationTree, CrawlingAnimationStateName);
        _timeSinceEnter += context.Delta;
    }

    /// <inheritdoc />
    public override string? BuildAnimationDebugMessage(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        float forwardOffset = ResolveForwardOffset(context);
        float verticalOffset = ResolveVerticalOffset(context);
        return $"AllFours: {CurrentPhase} forward={forwardOffset:F3} vertical={verticalOffset:F3}";
    }

    /// <summary>
    /// Maps the supplied forward offset into the authored entry seek window.
    /// </summary>
    public static float ComputeSeekTime(
        float forwardOffset,
        float entryForwardOffsetThreshold,
        float crawlForwardOffsetThreshold,
        float seekWindowStartSeconds,
        float seekWindowEndSeconds)
    {
        float safeRange = Mathf.Max(crawlForwardOffsetThreshold - entryForwardOffsetThreshold, 1e-4f);
        float seekBlend = Mathf.Clamp((forwardOffset - entryForwardOffsetThreshold) / safeRange, 0f, 1f);
        return Mathf.Lerp(seekWindowStartSeconds, seekWindowEndSeconds, seekBlend);
    }

    /// <summary>
    /// Resolves the next internal all-fours phase from the supplied forward and vertical metrics.
    /// </summary>
    public static Phase ComputeNextPhase(
        Phase currentPhase,
        float forwardOffset,
        float verticalOffset,
        float crawlForwardOffsetThreshold,
        float crawlingVerticalReturnThreshold)
    {
        float verticalReturnThreshold = Mathf.Max(crawlingVerticalReturnThreshold, 0f);
        return currentPhase == Phase.Crawling && verticalOffset > verticalReturnThreshold
            ? Phase.Transitioning
            : currentPhase == Phase.Transitioning
               && verticalOffset <= verticalReturnThreshold
               && forwardOffset > Mathf.Max(crawlForwardOffsetThreshold, 0f)
            ? Phase.Crawling
            : currentPhase;
    }

    /// <summary>
    /// Computes the all-fours reference hip position from the rest hip, semantic up axis, and
    /// semantic avatar-forward axis in skeleton-local space.
    /// </summary>
    public static Vector3 ComputeReferenceHipLocalPosition(
        Vector3 hipLocalRest,
        Vector3 hipRestUpLocal,
        Vector3 avatarForwardLocal,
        float restHeadHeight,
        float referenceHipHeightRatio,
        float referenceForwardShiftRatio)
    {
        float safeRestHeadHeight = restHeadHeight > RestHeadHeightFloor
            ? restHeadHeight
            : 1f;
        Vector3 safeHipRestUpLocal = TryNormaliseAxis(hipRestUpLocal, Vector3.Up);
        Vector3 safeAvatarForwardLocal = TryNormaliseAxis(avatarForwardLocal, Vector3.Back);

        float referenceHeight = safeRestHeadHeight * Mathf.Max(referenceHipHeightRatio, 0f);
        float restHipHeight = hipLocalRest.Dot(safeHipRestUpLocal);
        float downwardShift = Mathf.Max(restHipHeight - referenceHeight, 0f);
        float forwardShift = safeRestHeadHeight * Mathf.Max(referenceForwardShiftRatio, 0f);

        return hipLocalRest
            - (safeHipRestUpLocal * downwardShift)
            + (safeAvatarForwardLocal * forwardShift);
    }

    /// <summary>
    /// Rebases a profile result from one absolute hip baseline to another while keeping limited-
    /// head reconstruction internally consistent.
    /// </summary>
    public static HipReconciliationProfileResult RebaseProfileResult(
        HipReconciliationProfileResult profileResult,
        Vector3 sourceReferenceHipLocalPosition,
        Vector3 targetReferenceHipLocalPosition)
    {
        ArgumentNullException.ThrowIfNull(profileResult);

        Vector3 baselineShift = targetReferenceHipLocalPosition - sourceReferenceHipLocalPosition;
        return baselineShift.IsZeroApprox()
            ? profileResult
            : CreateRebasedProfileResult(profileResult, baselineShift);
    }

    /// <summary>
    /// Resolves the authored hip-offset envelope for the active all-fours phase.
    /// </summary>
    public static HipLimitEnvelope? ResolvePhaseHipOffsetEnvelope(
        Phase currentPhase,
        HipLimitEnvelope? transitioningEnvelope,
        HipLimitEnvelope? crawlingEnvelope)
        => currentPhase == Phase.Crawling
            ? crawlingEnvelope ?? transitioningEnvelope
            : transitioningEnvelope ?? crawlingEnvelope;

    /// <summary>
    /// Blends from a captured source reference into the authored all-fours target reference.
    /// </summary>
    public static Vector3 ResolveEffectiveReferenceHipLocalPosition(
        Vector3? sourceReferenceHipLocalPosition,
        Vector3 targetReferenceHipLocalPosition,
        float transitionBlend)
        => sourceReferenceHipLocalPosition is null || transitionBlend >= 1.0f
            ? targetReferenceHipLocalPosition
            : sourceReferenceHipLocalPosition.Value.Lerp(targetReferenceHipLocalPosition, transitionBlend);

    /// <summary>
    /// Blends from a captured source envelope into the authored envelope for the active all-fours
    /// phase.
    /// </summary>
    public static HipLimitEnvelope? ResolveEffectiveHipOffsetEnvelope(
        HipLimitEnvelope? sourceEnvelope,
        HipLimitEnvelope? authoredEnvelope,
        float transitionBlend)
        => transitionBlend >= 1.0f
            ? authoredEnvelope
            : sourceEnvelope is null
            ? authoredEnvelope
            : authoredEnvelope is null
                ? sourceEnvelope
                : HipLimitEnvelope.Lerp(sourceEnvelope.Value, authoredEnvelope.Value, transitionBlend);

    /// <inheritdoc />
    public override HipLimitFrame BuildHipLimitFrame(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Vector3 defaultReference = ResolveDefaultHipLocalReference(context);
        HipLimitSemanticFrame semanticFrame = HipLimitSemanticFrame.ReferenceRig;
        HipLimitEnvelope? authoredEnvelope = ResolveAuthoredHipOffsetEnvelope(semanticFrame);
        Vector3 targetReference = ComputeReferenceHipLocalPosition(
            defaultReference,
            semanticFrame.UpLocal,
            semanticFrame.AvatarForwardLocal,
            context.RestHeadHeight,
            ReferenceHipHeightRatio,
            ReferenceForwardShiftRatio);

        float transitionBlend = ComputeTransitionBlend();
        Vector3 effectiveReference = ResolveEffectiveReferenceHipLocalPosition(
            _snapshotReferenceHipLocalPosition,
            targetReference,
            transitionBlend);
        HipLimitEnvelope? effectiveEnvelope = ResolveEffectiveHipOffsetEnvelope(
            _snapshotHipOffsetEnvelope,
            authoredEnvelope,
            transitionBlend);

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

        HipReconciliationProfileResult? profileResult = headTrackingHipProfile.ComputeHipResult(context);
        if (profileResult is null)
        {
            return null;
        }

        HipReconciliationProfileResult rebasedProfileResult = RebaseProfileResult(
            profileResult,
            ResolveDefaultHipLocalReference(context),
            BuildHipLimitFrame(context).ReferenceHipLocalPosition);

        return ApplyHipReconciliation(context, rebasedProfileResult);
    }

    /// <inheritdoc />
    public float GetEffectiveRotationCompensationScale(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return 1f;
    }

    /// <inheritdoc />
    public HipLimitEnvelope? GetEffectiveHipOffsetEnvelope(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return BuildHipLimitFrame(context).OffsetEnvelope;
    }

    /// <inheritdoc />
    public Vector3 GetEffectiveReferenceHipLocalPosition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return BuildHipLimitFrame(context).ReferenceHipLocalPosition;
    }

    private static float ResolveForwardOffset(PoseStateContext context)
        => context.Skeleton is null
            ? 0f
            : AllFoursPoseMetrics.ComputeNormalizedForwardOffsetFromSkeletonOrigin(
                context.Skeleton.GlobalTransform,
                context.HeadTargetTransform,
                context.RestHeadHeight);

    private static float ResolveVerticalOffset(PoseStateContext context)
        => context.Skeleton is null
            ? 0f
            : AllFoursPoseMetrics.ComputeNormalizedVerticalOffsetFromSkeletonOrigin(
                context.Skeleton.GlobalTransform,
                context.HeadTargetTransform,
                context.RestHeadHeight);

    private static Vector3 TryNormaliseAxis(Vector3 axis, Vector3 fallback)
        => axis.LengthSquared() > Mathf.Epsilon
            ? axis.Normalized()
            : fallback;

    private static HipReconciliationProfileResult CreateRebasedProfileResult(
        HipReconciliationProfileResult profileResult,
        Vector3 baselineShift)
        => profileResult with
        {
            DesiredHipLocalPosition = profileResult.DesiredHipLocalPosition + baselineShift,
            HeadTargetLimit = profileResult.HeadTargetLimit is null
                ? null
                : profileResult.HeadTargetLimit with
                {
                    HipRestLocalPosition = profileResult.HeadTargetLimit.HipRestLocalPosition + baselineShift,
                },
        };

    private float ComputeTransitionBlend()
        => _timeSinceEnter < 0.0 || TransitionBlendDurationSeconds <= 1e-4f
            ? 1.0f
            : (float)Mathf.Clamp(_timeSinceEnter / TransitionBlendDurationSeconds, 0.0, 1.0);

    private HipLimitEnvelope? ResolveAuthoredHipOffsetEnvelope(HipLimitSemanticFrame semanticFrame)
    {
        HipLimitEnvelope? transitioningEnvelope = HipLimitEnvelope.FromOffsetLimits(
            TransitioningHipOffsetLimits,
            semanticFrame);
        HipLimitEnvelope? crawlingEnvelope = HipLimitEnvelope.FromOffsetLimits(
            CrawlingHipOffsetLimits,
            semanticFrame);

        return ResolvePhaseHipOffsetEnvelope(CurrentPhase, transitioningEnvelope, crawlingEnvelope);
    }

    private void EnsurePlaybackState(AnimationTree? tree, StringName targetState)
    {
        if (tree is null || targetState.IsEmpty)
        {
            return;
        }

        AnimationNodeStateMachinePlayback? playback = ResolvePlayback(tree);
        if (playback is null || playback.GetCurrentNode() == targetState)
        {
            return;
        }

        playback.Travel(targetState);
        tree.Advance(0.0);
    }

    private void WriteSeekRequest(AnimationTree? tree, float forwardOffset)
    {
        if (tree is null)
        {
            return;
        }

        if (SeekRequestParameter.IsEmpty)
        {
            if (!_warnedMissingSeekPath)
            {
                GD.PushWarning(
                    $"{nameof(AllFoursPoseState)}.{nameof(SeekRequestParameter)} is empty; skipping seek writes.");
                _warnedMissingSeekPath = true;
            }

            return;
        }

        float seekTime = ComputeSeekTime(
            forwardOffset,
            EntryForwardOffsetThreshold,
            CrawlForwardOffsetThreshold,
            SeekWindowStartSeconds,
            SeekWindowEndSeconds);
        tree.Set(SeekRequestParameter, seekTime);
    }
}
