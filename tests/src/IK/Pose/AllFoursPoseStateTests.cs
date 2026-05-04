using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for all-fours state seek mapping and internal phase changes.
/// </summary>
public sealed class AllFoursPoseStateTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>
    /// The entry seek mapping should span the authored seek window across the authored forward range.
    /// </summary>
    [Fact]
    public void ComputeSeekTime_MapsForwardRangeOntoAuthoredWindow()
    {
        float startSeek = AllFoursPoseState.ComputeSeekTime(0.42f, 0.42f, 0.73f, 1.2f, 3.5417f);
        float midpointSeek = AllFoursPoseState.ComputeSeekTime(0.575f, 0.42f, 0.73f, 1.2f, 3.5417f);
        float endSeek = AllFoursPoseState.ComputeSeekTime(0.73f, 0.42f, 0.73f, 1.2f, 3.5417f);

        Assert.Equal(1.2f, startSeek, precision: 4);
        Assert.InRange(midpointSeek, 2.36f, 2.39f);
        Assert.Equal(3.5417f, endSeek, precision: 4);
    }

    /// <summary>
    /// The all-fours head trigger metric reads semantic avatar-forward in skeleton-local space even
    /// when the solved skeleton is yaw-rotated in world space.
    /// </summary>
    [Fact]
    public void ComputeNormalizedForwardOffsetFromSkeletonOrigin_UsesSemanticAvatarForwardAxis()
    {
        Transform3D skeletonGlobalTransform = new(
            new Basis(Vector3.Up, Mathf.Pi),
            new Vector3(1.2f, 0.4f, -0.8f));
        Transform3D headTargetTransform = new(
            Basis.Identity,
            skeletonGlobalTransform * new Vector3(0f, 0.3f, 0.70f));

        float forwardOffset = AllFoursPoseMetrics.ComputeNormalizedForwardOffsetFromSkeletonOrigin(
            skeletonGlobalTransform,
            headTargetTransform,
            restHeadHeight: 1.0f);

        Assert.Equal(0.70f, forwardOffset, precision: 4);
    }

    /// <summary>
    /// Forward travel past the crawl threshold should move the state from transitioning into crawling.
    /// </summary>
    [Fact]
    public void ComputeNextPhase_ForwardOffsetPastCrawlThreshold_EntersCrawling()
    {
        AllFoursPoseState.Phase nextPhase = AllFoursPoseState.ComputeNextPhase(
            AllFoursPoseState.Phase.Transitioning,
            forwardOffset: 0.75f,
            verticalOffset: 0.20f,
            crawlForwardOffsetThreshold: 0.73f,
            crawlingVerticalReturnThreshold: 0.30f);

        Assert.Equal(AllFoursPoseState.Phase.Crawling, nextPhase);
    }

    /// <summary>
    /// Raising the head vertically while crawling should return the state to its transitioning phase.
    /// </summary>
    [Fact]
    public void ComputeNextPhase_CrawlingVerticalRise_ReturnsToTransitioning()
    {
        AllFoursPoseState.Phase nextPhase = AllFoursPoseState.ComputeNextPhase(
            AllFoursPoseState.Phase.Crawling,
            forwardOffset: 0.75f,
            verticalOffset: 0.35f,
            crawlForwardOffsetThreshold: 0.73f,
            crawlingVerticalReturnThreshold: 0.30f);

        Assert.Equal(AllFoursPoseState.Phase.Transitioning, nextPhase);
    }

    /// <summary>
    /// After a vertical-return bounce back into transitioning, a still-high head must not
    /// immediately re-enter crawling just because forward offset remains beyond the crawl gate.
    /// </summary>
    [Fact]
    public void ComputeNextPhase_TransitioningWhileHeadStillHigh_DoesNotImmediatelyReenterCrawling()
    {
        AllFoursPoseState.Phase nextPhase = AllFoursPoseState.ComputeNextPhase(
            AllFoursPoseState.Phase.Transitioning,
            forwardOffset: 0.90f,
            verticalOffset: 0.35f,
            crawlForwardOffsetThreshold: 0.73f,
            crawlingVerticalReturnThreshold: 0.30f);

        Assert.Equal(AllFoursPoseState.Phase.Transitioning, nextPhase);
    }

    /// <summary>
    /// All-fours reference hip placement uses the authored ratios in semantic up and avatar-forward space.
    /// </summary>
    [Fact]
    public void ComputeReferenceHipLocalPosition_UsesAuthoredAllFoursRatios()
    {
        Vector3 reference = AllFoursPoseState.ComputeReferenceHipLocalPosition(
            hipLocalRest: new Vector3(0f, 0.95f, 0f),
            hipRestUpLocal: Vector3.Up,
            avatarForwardLocal: Vector3.Back,
            restHeadHeight: 1.6f,
            referenceHipHeightRatio: 0.26f,
            referenceForwardShiftRatio: 0.26f);

        AssertClose(new Vector3(0f, 0.416f, 0.416f), reference);
    }

    /// <summary>
    /// Rebasing the profile result onto the all-fours reference moves the zero-offset hip target
    /// away from rest so entry is not anchored to the standing baseline.
    /// </summary>
    [Fact]
    public void RebaseProfileResult_ZeroHeadOffset_AnchorsAllFoursToStateReferenceInsteadOfRest()
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Vector3 allFoursReference = AllFoursPoseState.ComputeReferenceHipLocalPosition(
            hipRest,
            Vector3.Up,
            Vector3.Back,
            restHeadHeight: 1.6f,
            referenceHipHeightRatio: 0.26f,
            referenceForwardShiftRatio: 0.26f);
        HipReconciliationProfileResult profileResult = HeadTrackingHipProfile.ComputeHipResult(
            hipRest,
            hipRestUpLocal: Vector3.Up,
            hipRestForwardLocal: Vector3.Forward,
            hipRestLateralLocal: Vector3.Right,
            restHeadLocalTransform: new Transform3D(Basis.Identity, new Vector3(0f, 1.65f, 0f)),
            currentHeadLocalTransform: new Transform3D(Basis.Identity, new Vector3(0f, 1.65f, 0f)),
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 0.0f,
            verticalPositionWeight: 1.0f,
            lateralPositionWeight: 1.0f,
            forwardPositionWeight: 1.0f,
            minimumAlignmentWeight: 1.0f);

        HipReconciliationTickResult oldAnchoredTick = PoseState.ApplyHipLimitFrame(
            profileResult,
            new HipLimitFrame
            {
                ReferenceHipLocalPosition = allFoursReference,
            },
            restHeadHeight: 1.6f,
            skeletonGlobalTransform: Transform3D.Identity);
        HipReconciliationProfileResult rebasedProfileResult = AllFoursPoseState.RebaseProfileResult(
            profileResult,
            hipRest,
            allFoursReference);
        HipReconciliationTickResult rebasedTick = PoseState.ApplyHipLimitFrame(
            rebasedProfileResult,
            new HipLimitFrame
            {
                ReferenceHipLocalPosition = allFoursReference,
            },
            restHeadHeight: 1.6f,
            skeletonGlobalTransform: Transform3D.Identity);

        AssertClose(hipRest, oldAnchoredTick.AppliedHipLocalPosition);
        AssertClose(allFoursReference, rebasedTick.AppliedHipLocalPosition);
        AssertClose(Vector3.Zero, rebasedTick.DesiredFinalHipOffset);
    }

    /// <summary>
    /// Rebasing also shifts limited-head reconstruction to the all-fours baseline so clamp
    /// recovery remains internally consistent.
    /// </summary>
    [Fact]
    public void RebaseProfileResult_ShiftsHeadLimitBaselineWithHipTarget()
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Transform3D restHeadLocalTransform = new(Basis.Identity, new Vector3(0f, 1.65f, 0f));
        Transform3D currentHeadLocalTransform = new(Basis.Identity, new Vector3(0f, 1.05f, -0.20f));
        Vector3 allFoursReference = AllFoursPoseState.ComputeReferenceHipLocalPosition(
            hipRest,
            Vector3.Up,
            Vector3.Back,
            restHeadHeight: 1.6f,
            referenceHipHeightRatio: 0.26f,
            referenceForwardShiftRatio: 0.26f);
        HipReconciliationProfileResult rebasedProfileResult = AllFoursPoseState.RebaseProfileResult(
            HeadTrackingHipProfile.ComputeHipResult(
                hipRest,
                hipRestUpLocal: Vector3.Up,
                hipRestForwardLocal: Vector3.Forward,
                hipRestLateralLocal: Vector3.Right,
                restHeadLocalTransform: restHeadLocalTransform,
                currentHeadLocalTransform: currentHeadLocalTransform,
                headRotationDisplacementLocal: Vector3.Zero,
                rotationCompensationWeight: 0.0f,
                verticalPositionWeight: 1.0f,
                lateralPositionWeight: 1.0f,
                forwardPositionWeight: 1.0f,
                minimumAlignmentWeight: 1.0f),
            hipRest,
            allFoursReference);

        HipReconciliationTickResult tickResult = PoseState.ApplyHipLimitFrame(
            rebasedProfileResult,
            new HipLimitFrame
            {
                ReferenceHipLocalPosition = allFoursReference,
                OffsetEnvelope = new HipLimitEnvelope(1.0f, 0.20f, 1.0f, 1.0f, 0.05f, 1.0f),
            },
            restHeadHeight: 1.6f,
            skeletonGlobalTransform: Transform3D.Identity);

        Assert.True(tickResult.LimitedHeadTargetTransform.HasValue);
        Transform3D limitedHeadTargetTransform = tickResult.LimitedHeadTargetTransform.Value;
        Vector3 limitedHeadOffset = limitedHeadTargetTransform.Origin - restHeadLocalTransform.Origin;

        AssertClose(new Vector3(0f, 0.096f, 0.336f), tickResult.AppliedHipLocalPosition);
        AssertClose(new Vector3(0f, -0.32f, -0.08f), limitedHeadOffset);
    }

    /// <summary>
    /// All-fours forward placement is owned by the state reference baseline; once the player has
    /// entered the authored all-fours pose, head-tracking forward gain should not add the same
    /// forward travel on top of that baseline again.
    /// </summary>
    [Fact]
    public void RebaseProfileResult_AllFoursReferencePose_DoesNotAddExtraForwardHipShift()
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Transform3D restHeadLocalTransform = new(Basis.Identity, new Vector3(0f, 1.65f, 0f));
        Transform3D currentHeadLocalTransform = new(Basis.Identity, new Vector3(0f, 1.05f, 0.42f));
        Vector3 allFoursReference = AllFoursPoseState.ComputeReferenceHipLocalPosition(
            hipRest,
            Vector3.Up,
            Vector3.Back,
            restHeadHeight: 1.6f,
            referenceHipHeightRatio: 0.26f,
            referenceForwardShiftRatio: 0.26f);
        HipReconciliationProfileResult rebasedProfileResult = AllFoursPoseState.RebaseProfileResult(
            HeadTrackingHipProfile.ComputeHipResult(
                hipRest,
                hipRestUpLocal: Vector3.Up,
                hipRestForwardLocal: Vector3.Forward,
                hipRestLateralLocal: Vector3.Right,
                restHeadLocalTransform: restHeadLocalTransform,
                currentHeadLocalTransform: currentHeadLocalTransform,
                headRotationDisplacementLocal: Vector3.Zero,
                rotationCompensationWeight: 0.0f,
                verticalPositionWeight: 0.1f,
                lateralPositionWeight: 0.5f,
                forwardPositionWeight: 0.0f,
                minimumAlignmentWeight: 1.0f),
            hipRest,
            allFoursReference);

        HipReconciliationTickResult tickResult = PoseState.ApplyHipLimitFrame(
            rebasedProfileResult,
            new HipLimitFrame
            {
                ReferenceHipLocalPosition = allFoursReference,
            },
            restHeadHeight: 1.6f,
            skeletonGlobalTransform: Transform3D.Identity);

        float forwardDeltaFromReference = (tickResult.AppliedHipLocalPosition - allFoursReference)
            .Dot(Vector3.Back);

        AssertClose(new Vector3(0f, 0.356f, 0.416f), tickResult.AppliedHipLocalPosition);
        Assert.True(
            Mathf.Abs(forwardDeltaFromReference) <= Tolerance,
            $"Expected all-fours reference pose to avoid extra forward hip shift, got {forwardDeltaFromReference}.");
    }

    /// <summary>
    /// On the transition tick, all-fours must expose the source state's envelope and reference so
    /// standing-to-all-fours entry keeps clamp continuity before blending towards its authored
    /// transitioning limits.
    /// </summary>
    [Fact]
    public void ResolveEffectiveTransitionValues_SourceSnapshot_PreservesContinuityThenBlendsTowardsAuthoredLimits()
    {
        HipLimitEnvelope sourceEnvelope = new(0.15f, 0.07f, 0.02f, 0.02f, 0.08f, 0.05f);
        HipLimitEnvelope authoredEnvelope = new(0.05f, 0.02f, 0.01f, 0.01f, 0.03f, 0.02f);
        Vector3 sourceReference = new(0f, 0.62f, 0.18f);
        Vector3 targetReference = new(0f, 0f, 0.416f);

        HipLimitEnvelope? transitionTickEnvelope = AllFoursPoseState.ResolveEffectiveHipOffsetEnvelope(
            sourceEnvelope,
            authoredEnvelope,
            transitionBlend: 0f);
        Vector3 transitionTickReference = AllFoursPoseState.ResolveEffectiveReferenceHipLocalPosition(
            sourceReference,
            targetReference,
            transitionBlend: 0f);

        Assert.True(transitionTickEnvelope.HasValue);
        AssertEnvelopeEquals(
            sourceEnvelope,
            transitionTickEnvelope.Value);
        AssertClose(sourceReference, transitionTickReference);

        HipLimitEnvelope? blendedEnvelope = AllFoursPoseState.ResolveEffectiveHipOffsetEnvelope(
            sourceEnvelope,
            authoredEnvelope,
            transitionBlend: 0.5f);
        Vector3 blendedReference = AllFoursPoseState.ResolveEffectiveReferenceHipLocalPosition(
            sourceReference,
            targetReference,
            transitionBlend: 0.5f);

        Assert.True(blendedEnvelope.HasValue);
        AssertEnvelopeEquals(
            new HipLimitEnvelope(0.10f, 0.045f, 0.015f, 0.015f, 0.055f, 0.035f),
            blendedEnvelope.Value);
        AssertClose(new Vector3(0f, 0.31f, 0.298f), blendedReference);
    }

    /// <summary>
    /// The crawling phase can expose its own authored offset envelope instead of reusing the
    /// transitioning one.
    /// </summary>
    [Fact]
    public void ResolvePhaseHipOffsetEnvelope_CrawlingPhase_UsesCrawlingEnvelope()
    {
        HipLimitEnvelope? envelope = AllFoursPoseState.ResolvePhaseHipOffsetEnvelope(
            AllFoursPoseState.Phase.Crawling,
            new HipLimitEnvelope(0.05f, 0.02f, 0.01f, 0.01f, 0.03f, 0.02f),
            new HipLimitEnvelope(0.02f, 0.01f, 0.005f, 0.005f, 0.01f, 0.01f));

        Assert.True(envelope.HasValue);
        AssertEnvelopeEquals(
            new HipLimitEnvelope(0.02f, 0.01f, 0.005f, 0.005f, 0.01f, 0.01f),
            envelope.Value);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        float delta = (expected - actual).Length();
        Assert.True(delta <= Tolerance, $"Expected {expected}, got {actual} (|delta|={delta}).");
    }

    private static void AssertEnvelopeEquals(HipLimitEnvelope expected, HipLimitEnvelope actual)
    {
        AssertLimitEquals(expected.Up, actual.Up);
        AssertLimitEquals(expected.Down, actual.Down);
        AssertLimitEquals(expected.Left, actual.Left);
        AssertLimitEquals(expected.Right, actual.Right);
        AssertLimitEquals(expected.Forward, actual.Forward);
        AssertLimitEquals(expected.Back, actual.Back);
    }

    private static void AssertLimitEquals(float? expected, float? actual)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (expected.HasValue && actual.HasValue)
        {
            Assert.InRange(actual.Value, expected.Value - Tolerance, expected.Value + Tolerance);
        }
    }

}
