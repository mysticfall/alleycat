using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for standing-continuum-to-kneeling transition gating semantics.
/// </summary>
public sealed class StandingToKneelingPoseTransitionTests
{
    private const float FullCrouchDepthRatio = 0.4f;
    private const float MinimumCrouchDepthBlend = 0.92f;
    private const float FullCrouchForwardOffsetRatio = 0.053f;
    private const float ArmingForwardOffsetFromFullCrouchRatio = 0.053f;
    private const float TriggerRetreatFromArmedPeakRatio = 0.020f;
    private const float NeutralReturnMaxOffsetRatio = 0.05f;

    /// <summary>
    /// Kneeling transition must be blocked until the crouch-depth gate is satisfied.
    /// </summary>
    [Fact]
    public void ShouldTransition_MidCrouchWithLargeForwardLean_DoesNotTransition()
    {
        bool shouldTransition = EvaluateStandingToKneeling(
            CreateTransform(0f, 1.50f, 0f),
            CreateTransform(0f, 1.22f, 0.16f),
            1.5f);

        Assert.False(shouldTransition);
    }

    /// <summary>
    /// Forward travel equal to the full-crouch baseline must not trigger kneeling by itself.
    /// </summary>
    [Fact]
    public void ShouldTransition_NearlyFullCrouchWithoutAdditionalForwardOffset_DoesNotTransition()
    {
        bool shouldTransition = EvaluateStandingToKneeling(
            CreateTransform(0f, 1.50f, 0f),
            CreateTransform(0f, 0.91f, 0.08f),
            1.5f);

        Assert.False(shouldTransition);
    }

    /// <summary>
    /// Near-full crouch plus extra forward travel beyond the arming threshold only arms the trigger.
    /// </summary>
    [Fact]
    public void ShouldTransition_EnteringArmedRegion_DoesNotTransitionImmediately()
    {
        bool shouldTransition = EvaluateStandingToKneeling(
            CreateTransform(0f, 1.50f, 0f),
            CreateTransform(0f, 0.91f, 0.16f),
            1.5f);

        Assert.False(shouldTransition);
    }

    /// <summary>
    /// Continuing further forward after arming updates the peak without firing the transition.
    /// </summary>
    [Fact]
    public void ShouldTransition_ArmedAndContinuingFurtherForward_DoesNotTransition()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;

        Assert.False(EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.16f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio));

        bool shouldTransition = EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.18f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio);

        Assert.False(shouldTransition);
        Assert.True(isArmed);
        Assert.True(armedPeakForwardOffsetRatio > ArmingForwardOffsetFromFullCrouchRatio);
    }

    /// <summary>
    /// Kneel triggers once the pose was armed by deep forward travel and then retreats from its peak.
    /// </summary>
    [Fact]
    public void ShouldTransition_ArmedPeakRetreat_Transitions()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;

        Assert.False(EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.16f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio));

        bool shouldTransition = EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.125f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio);

        Assert.True(shouldTransition);
    }

    /// <summary>
    /// Retreating from an armed peak by less than the configured amount must not fire kneel.
    /// </summary>
    [Fact]
    public void ShouldTransition_ArmedRetreatBelowRequiredDistance_DoesNotTransition()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;

        Assert.False(EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.16f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio));

        bool shouldTransition = EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.135f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio);

        Assert.False(shouldTransition);
        Assert.True(isArmed);
    }

    /// <summary>
    /// Retreating from an armed peak by the configured distance fires even while still stooping forward.
    /// </summary>
    [Fact]
    public void ShouldTransition_ArmedRetreatWhileStillForward_Transitions()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;

        Assert.False(EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.18f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio));

        bool shouldTransition = EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.145f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio);

        Assert.True(shouldTransition);
        Assert.False(isArmed);
        Assert.Equal(0f, armedPeakForwardOffsetRatio);
    }

    /// <summary>
    /// Once kneel entry has fired, the transition must remain fully inert regardless of any
    /// subsequent armed-forward or armed-retreat head motion while the head is still
    /// forward-away from the pose neutral baseline.
    /// </summary>
    [Fact]
    public void ShouldTransition_AfterFiring_StaysInertEvenOnArmedForwardRetreatCycle()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;
        bool firedSinceNeutralReturn = true;

        // Both poses keep |forwardFromFullCrouch| strictly above NeutralReturnMaxOffsetRatio, so
        // the forward-only gate must hold across an armed-forward-then-retreat cycle.
        bool armingAttempt = EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.20f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn);

        Assert.False(armingAttempt);
        Assert.False(isArmed);
        Assert.Equal(0f, armedPeakForwardOffsetRatio);
        Assert.True(firedSinceNeutralReturn);

        bool retreatAttempt = EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.16f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn);

        Assert.False(retreatAttempt);
        Assert.False(isArmed);
        Assert.True(firedSinceNeutralReturn);
    }

    /// <summary>
    /// After firing, oscillating the head forward and back while still away from neutral must not
    /// re-arm or re-fire the transition.
    /// </summary>
    [Fact]
    public void ShouldTransition_AfterFiring_DoesNotRearmOnForwardBackOscillation()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;
        bool firedSinceNeutralReturn = true;

        // Both oscillation extrema keep |forwardFromFullCrouch| strictly above the neutral
        // threshold — the gate must hold.
        for (int i = 0; i < 4; i++)
        {
            Assert.False(EvaluateStandingToKneeling(
                rest,
                CreateTransform(0f, 0.91f, 0.20f),
                1.5f,
                ref isArmed,
                ref armedPeakForwardOffsetRatio,
                ref firedSinceNeutralReturn));
            Assert.True(firedSinceNeutralReturn);

            Assert.False(EvaluateStandingToKneeling(
                rest,
                CreateTransform(0f, 0.91f, 0.16f),
                1.5f,
                ref isArmed,
                ref armedPeakForwardOffsetRatio,
                ref firedSinceNeutralReturn));
            Assert.True(firedSinceNeutralReturn);
        }
    }

    /// <summary>
    /// The fired-until-neutral-return gate is independent of vertical head descent. A deep-crouch
    /// pose with near-zero forward offset from the pose neutral baseline must be able to clear the
    /// gate, because the vertical head drop alone must never block the neutral return.
    /// </summary>
    [Fact]
    public void ShouldTransition_AfterFiring_DeepCrouchWithNearZeroForwardOffset_ClearsGate()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;
        bool firedSinceNeutralReturn = true;

        // Head is dropped deeply (y=0.91, full crouch depth) but forward offset from the
        // full-crouch baseline is near zero. Under the previous Length()-based gate this would
        // remain blocked indefinitely; the forward-only gate must clear.
        Assert.False(EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.08f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn));

        Assert.False(firedSinceNeutralReturn);
    }

    /// <summary>
    /// The fired-until-neutral-return gate clears once the forward-axis offset returns within the
    /// configured threshold, allowing the armed-forward-retreat trigger to rearm.
    /// </summary>
    [Fact]
    public void ShouldTransition_AfterFiring_RearmsOnlyAfterNeutralReturnWithinThreshold()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;
        bool firedSinceNeutralReturn = true;

        // Head still forward-away from the pose neutral baseline — gate holds.
        Assert.False(EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.16f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn));
        Assert.True(firedSinceNeutralReturn);

        // Head returns close to the pose neutral baseline on the forward axis — the gate clears.
        // (Head height also returns, so the crouch-depth gate blocks firing on this tick.)
        Assert.False(EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 1.49f, 0.01f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn));
        Assert.False(firedSinceNeutralReturn);

        // A subsequent crouched armed forward pose can now arm the trigger again.
        Assert.False(EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.16f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn));
        Assert.True(isArmed);

        bool shouldReenter = EvaluateStandingToKneeling(
            rest,
            CreateTransform(0f, 0.91f, 0.125f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn);

        Assert.True(shouldReenter);
    }

    /// <summary>
    /// Kneel seek blend is computed from forward offset relative to full-crouch baseline.
    /// </summary>
    [Fact]
    public void ComputeKneelSeekBlend_UsesFullCrouchForwardBaseline()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        Transform3D camera = CreateTransform(0f, 0.91f, 0.16f);

        float forwardFromFullCrouchRatio = KneelingPoseMetrics.ComputeForwardOffsetFromFullCrouchRatio(
            rest,
            camera,
            restHeadHeight: 1.5f,
            fullCrouchForwardOffsetRatio: FullCrouchForwardOffsetRatio);

        Assert.InRange(forwardFromFullCrouchRatio, 0.053f, 0.054f);

        float kneelBlend = KneelingPoseMetrics.ComputeKneelSeekBlend(
            forwardFromFullCrouchRatio,
            maximumKneelForwardRangeRatio: 0.093f);

        Assert.InRange(kneelBlend, 0.55f, 0.58f);
    }

    /// <summary>
    /// Forward trigger threshold uses rest-head-height normalised ratios rather than absolute metres.
    /// </summary>
    [Fact]
    public void ShouldTransition_UsesForwardRatioAcrossDifferentRestHeights()
    {
        Transform3D restShort = CreateTransform(0f, 1.50f, 0f);
        Transform3D restTall = CreateTransform(0f, 1.80f, 0f);
        bool shortIsArmed = false;
        float shortPeakForwardOffsetRatio = 0f;
        bool tallIsArmed = false;
        float tallPeakForwardOffsetRatio = 0f;

        Assert.False(EvaluateStandingToKneeling(
            restShort,
            CreateTransform(0f, 1.22f, 0.16f),
            1.5f,
            minimumCrouchDepthBlend: 0f,
            isArmed: ref shortIsArmed,
            armedPeakForwardOffsetRatio: ref shortPeakForwardOffsetRatio));
        Assert.False(EvaluateStandingToKneeling(
            restTall,
            CreateTransform(0f, 1.52f, 0.192f),
            1.8f,
            minimumCrouchDepthBlend: 0f,
            isArmed: ref tallIsArmed,
            armedPeakForwardOffsetRatio: ref tallPeakForwardOffsetRatio));

        bool shortCharacterTransitions = EvaluateStandingToKneeling(
            restShort,
            CreateTransform(0f, 1.22f, 0.125f),
            1.5f,
            minimumCrouchDepthBlend: 0f,
            isArmed: ref shortIsArmed,
            armedPeakForwardOffsetRatio: ref shortPeakForwardOffsetRatio);
        bool tallCharacterTransitions = EvaluateStandingToKneeling(
            restTall,
            CreateTransform(0f, 1.52f, 0.15f),
            1.8f,
            minimumCrouchDepthBlend: 0f,
            isArmed: ref tallIsArmed,
            armedPeakForwardOffsetRatio: ref tallPeakForwardOffsetRatio);

        Assert.True(shortCharacterTransitions);
        Assert.True(tallCharacterTransitions);
    }

    /// <summary>
    /// Crouch gate uses rest-height ratios so scaled avatars preserve near-full crouch behaviour.
    /// </summary>
    [Fact]
    public void ShouldTransition_UsesCrouchDepthRatioAcrossDifferentRestHeights()
    {
        Transform3D restShort = CreateTransform(0f, 1.50f, 0f);
        Transform3D restTall = CreateTransform(0f, 1.80f, 0f);
        bool shortIsArmed = false;
        float shortPeakForwardOffsetRatio = 0f;
        bool tallIsArmed = false;
        float tallPeakForwardOffsetRatio = 0f;
        Assert.False(EvaluateStandingToKneeling(
            restShort,
            CreateTransform(0f, 0.93f, 0.16f),
            1.5f,
            isArmed: ref shortIsArmed,
            armedPeakForwardOffsetRatio: ref shortPeakForwardOffsetRatio));
        Assert.False(EvaluateStandingToKneeling(
            restTall,
            CreateTransform(0f, 1.116f, 0.192f),
            1.8f,
            isArmed: ref tallIsArmed,
            armedPeakForwardOffsetRatio: ref tallPeakForwardOffsetRatio));

        bool shortCharacterTransitions = EvaluateStandingToKneeling(
            restShort,
            CreateTransform(0f, 0.93f, 0.125f),
            1.5f,
            isArmed: ref shortIsArmed,
            armedPeakForwardOffsetRatio: ref shortPeakForwardOffsetRatio);
        bool tallCharacterTransitions = EvaluateStandingToKneeling(
            restTall,
            CreateTransform(0f, 1.116f, 0.15f),
            1.8f,
            isArmed: ref tallIsArmed,
            armedPeakForwardOffsetRatio: ref tallPeakForwardOffsetRatio);

        Assert.True(shortCharacterTransitions);
        Assert.True(tallCharacterTransitions);
    }



    /// <summary>
    /// Kneeling does not return to crouching until the forward-retreat trigger has been armed.
    /// </summary>
    [Fact]
    public void KneelingToStandingShouldTransition_WithoutArming_DoesNotTransition()
    {
        bool shouldTransition = EvaluateKneelingToStanding(
            CreateTransform(0f, 1.50f, 0f),
            CreateTransform(0f, 0.93f, 0.10f),
            1.5f);

        Assert.False(shouldTransition);
    }

    /// <summary>
    /// Kneeling returns to crouching once a deep-forward armed exit path retreats from its peak.
    /// </summary>
    [Fact]
    public void KneelingToStandingShouldTransition_ArmedRetreat_Transitions()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;

        Assert.False(EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.93f, 0.18f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio));

        bool shouldTransition = EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.93f, 0.10f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio);

        Assert.True(shouldTransition);
    }

    /// <summary>
    /// Kneeling returns to crouching while still forward once the retreat distance is large enough.
    /// </summary>
    [Fact]
    public void KneelingToStandingShouldTransition_ArmedRetreatWhileStillForward_Transitions()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;

        Assert.False(EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.93f, 0.18f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio));

        bool shouldTransition = EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.93f, 0.14f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio);

        Assert.True(shouldTransition);
    }

    /// <summary>
    /// Kneeling remains active until the retreat from the armed peak reaches the configured distance.
    /// </summary>
    [Fact]
    public void KneelingToStandingShouldTransition_ArmedRetreatBelowRequiredDistance_DoesNotTransition()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;

        Assert.False(EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.93f, 0.18f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio));

        bool shouldTransition = EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.93f, 0.16f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio);

        Assert.False(shouldTransition);
        Assert.True(isArmed);
    }

    /// <summary>
    /// Exit to standing is promptly available from the kneeling forward region — a kneel entry does
    /// not need a full reset before a forward-then-retreat cycle can fire the exit transition.
    /// </summary>
    [Fact]
    public void KneelingToStandingShouldTransition_CanFirePromptlyAfterKneelEntry()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;
        bool firedSinceNeutralReturn = false;

        // Head in the overlapping forward region directly after kneel entry already satisfies the
        // arming threshold; no full-reset cycle is required.
        Assert.False(EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.91f, 0.17f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn));
        Assert.True(isArmed);

        bool shouldExitOnRetreat = EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.91f, 0.13f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn);

        Assert.True(shouldExitOnRetreat);
    }

    /// <summary>
    /// Once kneeling-to-standing fires, the transition remains inert until the head returns close
    /// to the neutral forward baseline — it cannot immediately rearm on another forward-retreat
    /// cycle.
    /// </summary>
    [Fact]
    public void KneelingToStandingShouldTransition_AfterFiring_StaysInertUntilNeutralReturn()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;
        bool firedSinceNeutralReturn = true;

        // Head still forward-away from the pose neutral baseline — gate holds.
        Assert.False(EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.93f, 0.18f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn));
        Assert.False(isArmed);

        Assert.False(EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.93f, 0.16f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn));
        Assert.True(firedSinceNeutralReturn);

        // Forward offset returns within the neutral-return threshold; gate clears.
        Assert.False(EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.93f, 0.08f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn));
        Assert.False(firedSinceNeutralReturn);
    }

    /// <summary>
    /// Kneeling-to-standing must clear its fired gate from a deep-kneel pose whose vertical head
    /// descent is large but whose forward offset from the pose neutral baseline is near zero.
    /// </summary>
    [Fact]
    public void KneelingToStandingShouldTransition_AfterFiring_DeepKneelWithNearZeroForwardOffset_ClearsGate()
    {
        Transform3D rest = CreateTransform(0f, 1.50f, 0f);
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;
        bool firedSinceNeutralReturn = true;

        // Head is dropped to kneel height (y=0.60) but forward offset from the full-crouch
        // baseline is near zero. Under the previous Length()-based gate this would stay blocked
        // indefinitely because the vertical component alone already exceeds NeutralReturnMaxOffsetRatio.
        Assert.False(EvaluateKneelingToStanding(
            rest,
            CreateTransform(0f, 0.60f, 0.08f),
            1.5f,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn));

        Assert.False(firedSinceNeutralReturn);
    }

    private static bool EvaluateStandingToKneeling(
        Transform3D rest,
        Transform3D head,
        float restHeadHeight,
        ref bool isArmed,
        ref float armedPeakForwardOffsetRatio,
        ref bool firedSinceNeutralReturn,
        float minimumCrouchDepthBlend = MinimumCrouchDepthBlend)
    {
        (bool shouldTransition, bool nextIsArmed, float nextArmedPeakForwardOffsetRatio, bool nextFiredSinceNeutralReturn) = StandingToKneelingPoseTransition.Evaluate(
            rest,
            head,
            restHeadHeight,
            FullCrouchDepthRatio,
            minimumCrouchDepthBlend,
            FullCrouchForwardOffsetRatio,
            ArmingForwardOffsetFromFullCrouchRatio,
            TriggerRetreatFromArmedPeakRatio,
            NeutralReturnMaxOffsetRatio,
            isArmed,
            armedPeakForwardOffsetRatio,
            firedSinceNeutralReturn);

        isArmed = nextIsArmed;
        armedPeakForwardOffsetRatio = nextArmedPeakForwardOffsetRatio;
        firedSinceNeutralReturn = nextFiredSinceNeutralReturn;
        return shouldTransition;
    }

    private static bool EvaluateStandingToKneeling(
        Transform3D rest,
        Transform3D head,
        float restHeadHeight,
        ref bool isArmed,
        ref float armedPeakForwardOffsetRatio,
        float minimumCrouchDepthBlend = MinimumCrouchDepthBlend)
    {
        bool firedSinceNeutralReturn = false;
        return EvaluateStandingToKneeling(
            rest,
            head,
            restHeadHeight,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn,
            minimumCrouchDepthBlend);
    }

    private static bool EvaluateStandingToKneeling(
        Transform3D rest,
        Transform3D head,
        float restHeadHeight,
        float minimumCrouchDepthBlend = MinimumCrouchDepthBlend)
    {
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;
        return EvaluateStandingToKneeling(
            rest,
            head,
            restHeadHeight,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            minimumCrouchDepthBlend);
    }

    private static bool EvaluateKneelingToStanding(
        Transform3D rest,
        Transform3D head,
        float restHeadHeight,
        ref bool isArmed,
        ref float armedPeakForwardOffsetRatio,
        ref bool firedSinceNeutralReturn)
    {
        (bool shouldTransition, bool nextIsArmed, float nextArmedPeakForwardOffsetRatio, bool nextFiredSinceNeutralReturn) = KneelingToStandingPoseTransition.Evaluate(
            rest,
            head,
            restHeadHeight,
            FullCrouchForwardOffsetRatio,
            ArmingForwardOffsetFromFullCrouchRatio,
            TriggerRetreatFromArmedPeakRatio,
            NeutralReturnMaxOffsetRatio,
            isArmed,
            armedPeakForwardOffsetRatio,
            firedSinceNeutralReturn);

        isArmed = nextIsArmed;
        armedPeakForwardOffsetRatio = nextArmedPeakForwardOffsetRatio;
        firedSinceNeutralReturn = nextFiredSinceNeutralReturn;
        return shouldTransition;
    }

    private static bool EvaluateKneelingToStanding(
        Transform3D rest,
        Transform3D head,
        float restHeadHeight,
        ref bool isArmed,
        ref float armedPeakForwardOffsetRatio)
    {
        bool firedSinceNeutralReturn = false;
        return EvaluateKneelingToStanding(
            rest,
            head,
            restHeadHeight,
            ref isArmed,
            ref armedPeakForwardOffsetRatio,
            ref firedSinceNeutralReturn);
    }

    private static bool EvaluateKneelingToStanding(Transform3D rest, Transform3D head, float restHeadHeight)
    {
        bool isArmed = false;
        float armedPeakForwardOffsetRatio = 0f;
        return EvaluateKneelingToStanding(rest, head, restHeadHeight, ref isArmed, ref armedPeakForwardOffsetRatio);
    }

    private static Transform3D CreateTransform(float x, float y, float z)
        => new(new Basis(new Vector3(-1f, 0f, 0f), Vector3.Up, new Vector3(0f, 0f, -1f)), new Vector3(x, y, z));
}
