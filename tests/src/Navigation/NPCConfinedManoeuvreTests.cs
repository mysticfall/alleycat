using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCConfinedManoeuvreTests
{
    // Package endpoint displacement of the ANIM-002 side-step clips (sr/sl Root |X| in metres).
    private const float SideStepEndpoint = 0.42499688f;
    // Package endpoint yaw magnitude of the ANIM-002 pivot clips (±π/4).
    private const float PivotYaw = Mathf.Pi / 4f;
    // A ~90° terminal turn, clearly beyond both absorption capacities.
    private const float QuarterTurn = Mathf.Pi / 2f;
    // Existing generic per-segment planar correction bound driving the curvature-safe yaw absorption capacity.
    private const float PlanarCorrectionLimit = 0.08f;
    // Existing yaw tolerance (2°) below which no pivot clip plays.
    private const float PureConvergenceThreshold = 0.034906585f;

    private static float YawAbsorptionCapacity => NPCSideStep.YawAbsorptionCapacity(SideStepEndpoint, PlanarCorrectionLimit);

    private static NPCConfinedManoeuvrePlan Select(Vector2 offset, float residualYaw)
        => NPCConfinedManoeuvre.Select(
            offset,
            residualYaw,
            SideStepEndpoint,
            YawAbsorptionCapacity,
            PlanarCorrectionLimit,
            PivotYaw,
            PureConvergenceThreshold);

    [Fact]
    public void BeyondAbsorptionRightTurn_ComposesRightStepChainThenRightPivotChain()
    {
        // This beyond-capacity reported vector is 0.2 m mostly +X lateral with a ~90° right terminal yaw — beyond both the
        // pivot planar absorption and the side step's curvature-safe yaw absorption. The programme chains the composed
        // pivot: a 90° remainder is exactly two pr45 clips with zero synthetic rotation.
        NPCConfinedManoeuvrePlan plan = Select(new Vector2(0.18984336f, 0.06523204f), -QuarterTurn);

        Assert.Equal(NPCConfinedManoeuvreKind.SideStepThenPivot, plan.Kind);
        Assert.True(plan.PlaysSideStepClip);
        Assert.True(plan.ComposesPivotAfterSideStep);
        Assert.Equal(NPCSideStepKind.StepRight, plan.SideStep.Kind);
        Assert.Equal(StandingLocomotionCapability.ConfinedSideStepRight, plan.SideStep.StepCapability);
        Assert.Equal(1, plan.SideStep.ChainedStepCount);
        Assert.Equal(-YawAbsorptionCapacity, plan.SideStep.AbsorbedYawRadians, 7);
        Assert.Equal(NPCTurnInPlaceKind.PivotRight, plan.Turn.Kind);
        Assert.Equal(StandingLocomotionCapability.ConfinedPivotRight, plan.Turn.PivotCapability);
        Assert.True(plan.Turn.PlaysPivotClip);
        Assert.Equal(2, plan.Turn.ChainedPivotCount);
        Assert.Equal(-PivotYaw, plan.Turn.PivotYawRadians, 7);
        Assert.Equal(-QuarterTurn + YawAbsorptionCapacity + (2f * PivotYaw), plan.Turn.ConvergenceYawRadians, 7);
        // The ordered plan covers exactly the requested terminal yaw: step absorption + pivot chain + convergence.
        // Six digits: the decomposition round-trips the clamp and pivot subtraction in 32-bit float arithmetic.
        Assert.Equal(
            -QuarterTurn,
            plan.SideStep.AbsorbedYawRadians + plan.Turn.AuthoredYawRadians + plan.Turn.ConvergenceYawRadians,
            6);
    }

    [Fact]
    public void BeyondAbsorptionLeftTurn_ComposesLeftStepChainThenLeftPivotChain()
    {
        NPCConfinedManoeuvrePlan plan = Select(new Vector2(-0.18984336f, 0.06523204f), QuarterTurn);

        Assert.Equal(NPCConfinedManoeuvreKind.SideStepThenPivot, plan.Kind);
        Assert.Equal(NPCSideStepKind.StepLeft, plan.SideStep.Kind);
        Assert.Equal(StandingLocomotionCapability.ConfinedSideStepLeft, plan.SideStep.StepCapability);
        Assert.Equal(YawAbsorptionCapacity, plan.SideStep.AbsorbedYawRadians, 7);
        Assert.Equal(NPCTurnInPlaceKind.PivotLeft, plan.Turn.Kind);
        Assert.Equal(StandingLocomotionCapability.ConfinedPivotLeft, plan.Turn.PivotCapability);
        Assert.Equal(2, plan.Turn.ChainedPivotCount);
        Assert.Equal(PivotYaw, plan.Turn.PivotYawRadians, 7);
        Assert.Equal(QuarterTurn - YawAbsorptionCapacity - (2f * PivotYaw), plan.Turn.ConvergenceYawRadians, 7);
    }

    [Fact]
    public void HalfTurn_ComposesFourChainedPivots()
    {
        NPCConfinedManoeuvrePlan plan = Select(new Vector2(0.2f, 0.01f), -Mathf.Pi);

        Assert.Equal(NPCConfinedManoeuvreKind.SideStepThenPivot, plan.Kind);
        Assert.Equal(4, plan.Turn.ChainedPivotCount);
        // The step absorbs its curvature-safe capacity first, so the pivot chain's convergence remainder is that
        // absorbed amount back — within the half-pivot bound, never a synthetic rotation beyond it.
        Assert.Equal(-Mathf.Pi + YawAbsorptionCapacity + (4f * PivotYaw), plan.Turn.ConvergenceYawRadians, 6);
        Assert.True(Mathf.Abs(plan.Turn.ConvergenceYawRadians) <= (PivotYaw * 0.5f) + 0.000001f);
    }

    [Fact]
    public void WithinAbsorptionYaw_KeepsSingleSideStepWithoutComposition()
    {
        NPCConfinedManoeuvrePlan plan = Select(new Vector2(0.18984336f, 0.06523204f), -0.15f);

        Assert.Equal(NPCConfinedManoeuvreKind.SideStep, plan.Kind);
        Assert.False(plan.ComposesPivotAfterSideStep);
        Assert.True(plan.PlaysSideStepClip);
        Assert.False(plan.Turn.PlaysPivotClip);
        Assert.Equal(-0.15f, plan.SideStep.AbsorbedYawRadians, 7);
        Assert.Equal(0f, plan.SideStep.SettlementYawRadians, 7);
    }

    [Fact]
    public void AtTheExactAbsorptionBoundary_DoesNotCompose()
    {
        NPCConfinedManoeuvrePlan plan = Select(new Vector2(0.2f, 0.01f), -YawAbsorptionCapacity);

        Assert.Equal(NPCConfinedManoeuvreKind.SideStep, plan.Kind);
        Assert.False(plan.ComposesPivotAfterSideStep);
        Assert.Equal(-YawAbsorptionCapacity, plan.SideStep.AbsorbedYawRadians, 7);
        Assert.Equal(0f, plan.SideStep.SettlementYawRadians, 7);
    }

    [Fact]
    public void BeyondAbsorptionButWithinPureConvergenceThreshold_DoesNotCompose()
    {
        // A beyond-capacity remainder that the existing convergence tolerance already absorbs composes no pivot;
        // settlement owns the sliver, as required by confined absorption.
        NPCConfinedManoeuvrePlan plan = Select(new Vector2(0.2f, 0.01f), -(YawAbsorptionCapacity + 0.01f));

        Assert.Equal(NPCConfinedManoeuvreKind.SideStep, plan.Kind);
        Assert.False(plan.ComposesPivotAfterSideStep);
        Assert.False(plan.Turn.PlaysPivotClip);
        Assert.Equal(NPCTurnInPlaceKind.PureConvergence, plan.Turn.Kind);
        Assert.Equal(-0.01f, plan.Turn.ConvergenceYawRadians, 7);
    }

    [Fact]
    public void JustBeyondAbsorptionAndThreshold_Composes()
    {
        float composingYaw = -(YawAbsorptionCapacity + PureConvergenceThreshold + 0.001f);
        NPCConfinedManoeuvrePlan plan = Select(new Vector2(0.2f, 0.01f), composingYaw);

        Assert.Equal(NPCConfinedManoeuvreKind.SideStepThenPivot, plan.Kind);
        Assert.Equal(NPCTurnInPlaceKind.PivotRight, plan.Turn.Kind);
    }

    [Fact]
    public void MostlyForwardRequest_PreservesTheShortBoutRegime()
    {
        foreach (Vector2 forwardish in new[] { new Vector2(0.05f, 0.4f), new Vector2(-0.1f, 0.3f), Vector2.Zero })
        {
            NPCConfinedManoeuvrePlan plan = Select(forwardish, -QuarterTurn);

            Assert.Equal(NPCConfinedManoeuvreKind.None, plan.Kind);
            Assert.False(plan.PlaysSideStepClip);
            Assert.False(plan.ComposesPivotAfterSideStep);
        }
    }

    [Fact]
    public void ArbitraryLateralDistance_ComposesThroughTheChainedRepetitions()
    {
        // The composition locus chains for any mostly-lateral distance — the former
        // endpoint-plus-budget band no longer bounds the regime. A beyond-band request with an outstanding yaw
        // composes its step chain then its pivot chain instead of falling through to travel or blocking.
        // 0.63 m / 0.425 = 1.48 → still a single step (the chain starts beyond 1.5 endpoints), now unbounded.
        NPCConfinedManoeuvrePlan beyondFormerBand = Select(new Vector2(0.63f, 0.01f), -QuarterTurn);
        NPCConfinedManoeuvrePlan lateralChain = Select(new Vector2(-0.946435f, 0.03213787f), -0.12529877f);
        NPCConfinedManoeuvrePlan longLateral = Select(new Vector2(-2.0f, 0.02f), QuarterTurn);

        Assert.Equal(NPCConfinedManoeuvreKind.SideStepThenPivot, beyondFormerBand.Kind);
        Assert.Equal(1, beyondFormerBand.SideStep.ChainedStepCount);
        // The stuck lateral fixture's yaw sits inside the curvature-safe absorption: a pure step chain, no pivot.
        Assert.Equal(NPCConfinedManoeuvreKind.SideStep, lateralChain.Kind);
        Assert.Equal(2, lateralChain.SideStep.ChainedStepCount);
        Assert.Equal(-0.12529877f, lateralChain.SideStep.AbsorbedYawRadians, 6);
        Assert.Equal(NPCConfinedManoeuvreKind.SideStepThenPivot, longLateral.Kind);
        Assert.Equal(5, longLateral.SideStep.ChainedStepCount);
        Assert.Equal(2, longLateral.Turn.ChainedPivotCount);
    }

    [Fact]
    public void NonFiniteInputs_ReturnNone()
    {
        Assert.Equal(NPCConfinedManoeuvreKind.None, Select(new Vector2(float.NaN, 0f), -QuarterTurn).Kind);
        Assert.Equal(NPCConfinedManoeuvreKind.None, Select(new Vector2(0.2f, 0.01f), float.NaN).Kind);
        Assert.Equal(
            NPCConfinedManoeuvreKind.None,
            NPCConfinedManoeuvre.Select(
                new Vector2(0.2f, 0.01f),
                -QuarterTurn,
                float.NaN,
                YawAbsorptionCapacity,
                PlanarCorrectionLimit,
                PivotYaw,
                PureConvergenceThreshold).Kind);
    }

    [Fact]
    public void Thresholds_DeriveFromPackageEndpointsAndExistingCapacitiesOnly()
    {
        // The composition boundary is the curvature-safe absorption capacity itself: 2·asin(bound / (2·endpoint)),
        // built from the package side-step endpoint displacement and the existing per-segment planar bound.
        Assert.Equal(2f * Mathf.Asin(PlanarCorrectionLimit / (2f * SideStepEndpoint)), YawAbsorptionCapacity, 7);
        Assert.True(YawAbsorptionCapacity > 0f);
        // The composed pivot chain carries authored package yaw (±π/4 per clip) with the remainder within half a
        // pivot converging through settlement.
        NPCConfinedManoeuvrePlan halfTurn = Select(new Vector2(0.2f, 0.01f), -Mathf.Pi);
        Assert.Equal(-PivotYaw, halfTurn.Turn.PivotYawRadians, 7);
        Assert.True(Mathf.Abs(halfTurn.Turn.ConvergenceYawRadians) <= (PivotYaw * 0.5f) + 0.000001f);
        // Straddling the capacity boundary flips the decision and nothing else.
        Assert.Equal(
            NPCConfinedManoeuvreKind.SideStep,
            Select(new Vector2(0.2f, 0.01f), YawAbsorptionCapacity - 0.001f).Kind);
        Assert.Equal(
            NPCConfinedManoeuvreKind.SideStepThenPivot,
            Select(new Vector2(0.2f, 0.01f), YawAbsorptionCapacity + PureConvergenceThreshold + 0.001f).Kind);
    }
}
