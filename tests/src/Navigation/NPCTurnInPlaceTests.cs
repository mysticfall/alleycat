using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCTurnInPlaceTests
{
    private const float PivotYaw = MathF.PI / 4f;
    private const float ConvergenceThreshold = 0.034906585f;

    [Fact]
    public void PositiveResidual_SelectsLeftPivotChainCarryingAuthoredPositiveYaw()
    {
        // 1.2 rad / (π/4) = 1.53 → rounds to two chained pl45 clips; the −0.371 rad remainder converges in
        // settlement within the half-pivot bound.
        NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(1.2f, PivotYaw, ConvergenceThreshold);

        Assert.Equal(NPCTurnInPlaceKind.PivotLeft, plan.Kind);
        Assert.Equal(StandingLocomotionCapability.ConfinedPivotLeft, plan.PivotCapability);
        Assert.Equal(2, plan.ChainedPivotCount);
        Assert.Equal(PivotYaw, plan.PivotYawRadians, 7);
        Assert.Equal(1.2f - (2f * PivotYaw), plan.ConvergenceYawRadians, 7);
        Assert.True(plan.PlaysPivotClip);
    }

    [Fact]
    public void NegativeResidual_SelectsRightPivotChainCarryingAuthoredNegativeYaw()
    {
        NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(-1.2398256f, PivotYaw, ConvergenceThreshold);

        Assert.Equal(NPCTurnInPlaceKind.PivotRight, plan.Kind);
        Assert.Equal(StandingLocomotionCapability.ConfinedPivotRight, plan.PivotCapability);
        Assert.Equal(2, plan.ChainedPivotCount);
        Assert.Equal(-PivotYaw, plan.PivotYawRadians, 7);
        Assert.Equal(-1.2398256f + (2f * PivotYaw), plan.ConvergenceYawRadians, 7);
        Assert.True(plan.PlaysPivotClip);
    }

    [Fact]
    public void QuarterTurn_PlansExactlyTwoChainedPivotsWithZeroRemainder()
    {
        NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(MathF.PI / 2f, PivotYaw, ConvergenceThreshold);

        Assert.Equal(2, plan.ChainedPivotCount);
        Assert.Equal(0f, plan.ConvergenceYawRadians, 7);
        Assert.Equal(MathF.PI / 2f, plan.AuthoredYawRadians, 7);
    }

    [Fact]
    public void HalfTurn_PlansExactlyFourChainedPivotsWithZeroRemainder()
    {
        NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(-MathF.PI, PivotYaw, ConvergenceThreshold);

        Assert.Equal(NPCTurnInPlaceKind.PivotRight, plan.Kind);
        Assert.Equal(4, plan.ChainedPivotCount);
        Assert.Equal(0f, plan.ConvergenceYawRadians, 7);
        Assert.Equal(-MathF.PI, plan.AuthoredYawRadians, 7);
    }

    [Fact]
    public void ChainedPlans_KeepTheSignedRemainderWithinHalfAPivot()
    {
        foreach (float residual in new[] { 0.5f, 1.2f, 1.5f, 2.6f, -0.9f, -2.9f, MathF.PI - 0.01f })
        {
            NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(residual, PivotYaw, ConvergenceThreshold);

            Assert.True(plan.ChainedPivotCount >= 1);
            Assert.True(
                Mathf.Abs(plan.ConvergenceYawRadians) <= (PivotYaw * 0.5f) + 0.000001f,
                $"Remainder {plan.ConvergenceYawRadians:F7} for residual {residual:F7} escaped the half-pivot bound.");
        }
    }

    [Fact]
    public void BelowHalfAPivot_RoundsToASingleClip()
    {
        foreach (float residual in new[] { 0.5f, -0.7f })
        {
            NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(residual, PivotYaw, ConvergenceThreshold);

            Assert.Equal(1, plan.ChainedPivotCount);
            Assert.Equal(residual, (plan.PivotYawRadians * plan.ChainedPivotCount) + plan.ConvergenceYawRadians, 6);
        }
    }

    [Fact]
    public void ResidualAtOrBelowThreshold_SelectsPureConvergenceWithoutAClip()
    {
        NPCTurnInPlacePlan within = NPCTurnInPlace.Select(0.02f, PivotYaw, ConvergenceThreshold);
        NPCTurnInPlacePlan boundary = NPCTurnInPlace.Select(-ConvergenceThreshold, PivotYaw, ConvergenceThreshold);

        Assert.Equal(NPCTurnInPlaceKind.PureConvergence, within.Kind);
        Assert.Null(within.PivotCapability);
        Assert.Equal(0f, within.PivotYawRadians, 7);
        Assert.Equal(0.02f, within.ConvergenceYawRadians, 7);
        Assert.False(within.PlaysPivotClip);
        Assert.Equal(0, within.ChainedPivotCount);

        Assert.Equal(NPCTurnInPlaceKind.PureConvergence, boundary.Kind);
        Assert.Null(boundary.PivotCapability);
        Assert.Equal(-ConvergenceThreshold, boundary.ConvergenceYawRadians, 7);
    }

    [Fact]
    public void HeadingOnlyResidual_DecomposesEntirelyIntoAuthoredChainedYaw_WithoutAnyTranslationRequest()
    {
        foreach (float residual in new[] { 1.2f, -1.2398256f, 2.6f, -MathF.PI + 0.01f })
        {
            NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(residual, PivotYaw, ConvergenceThreshold);

            Assert.Equal(
                residual,
                plan.AuthoredYawRadians + plan.ConvergenceYawRadians,
                6);
            Assert.True(plan.PivotCapability is null
                or StandingLocomotionCapability.ConfinedPivotLeft
                or StandingLocomotionCapability.ConfinedPivotRight);
            Assert.Equal(Vector2.Zero, plan.AbsorbedPlanarSemantic);
        }
    }

    [Fact]
    public void NonFiniteResidual_FallsBackToPureConvergenceWithNoMotion()
    {
        NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(float.NaN, PivotYaw, ConvergenceThreshold);

        Assert.Equal(NPCTurnInPlaceKind.PureConvergence, plan.Kind);
        Assert.Null(plan.PivotCapability);
        Assert.Equal(0f, plan.PivotYawRadians, 7);
        Assert.Equal(0f, plan.ConvergenceYawRadians, 7);
    }

    [Fact]
    public void SmallPlanarOffset_RidesTheFirstPivotAsAbsorbedResidual_InsteadOfComposing()
    {
        // Under confined cross-DOF absorption, 45° of yaw plus 5 cm forward is ONE pivot with the offset
        // dispersed mid-phase, never pivot-then-side-step.
        NPCTurnInPlacePlan forwardOffset = NPCTurnInPlace.Select(
            -PivotYaw,
            new Vector2(0f, 0.05f),
            PivotYaw,
            ConvergenceThreshold);
        NPCTurnInPlacePlan leftOffset = NPCTurnInPlace.Select(
            PivotYaw,
            new Vector2(-0.04f, 0.02f),
            PivotYaw,
            ConvergenceThreshold);

        Assert.Equal(NPCTurnInPlaceKind.PivotRight, forwardOffset.Kind);
        Assert.Equal(StandingLocomotionCapability.ConfinedPivotRight, forwardOffset.PivotCapability);
        Assert.Equal(new Vector2(0f, 0.05f), forwardOffset.AbsorbedPlanarSemantic);
        Assert.Equal(0f, forwardOffset.ConvergenceYawRadians, 7);

        Assert.Equal(NPCTurnInPlaceKind.PivotLeft, leftOffset.Kind);
        Assert.Equal(new Vector2(-0.04f, 0.02f), leftOffset.AbsorbedPlanarSemantic);

        // The absorbed offset never disturbs the yaw decomposition: the full request is still the pivot chain plus
        // bounded convergence, with no translation request authored by the plan.
        NPCTurnInPlacePlan withOffset = NPCTurnInPlace.Select(
            -1.2398256f,
            new Vector2(0f, 0.05f),
            PivotYaw,
            ConvergenceThreshold);
        Assert.Equal(
            -1.2398256f,
            withOffset.AuthoredYawRadians + withOffset.ConvergenceYawRadians,
            7);
    }

    [Fact]
    public void HeadingOnlyRequest_AbsorbsZeroPlanarOffset()
    {
        NPCTurnInPlacePlan plan = NPCTurnInPlace.Select(-1.2398256f, PivotYaw, ConvergenceThreshold);

        Assert.Equal(NPCTurnInPlaceKind.PivotRight, plan.Kind);
        Assert.Equal(Vector2.Zero, plan.AbsorbedPlanarSemantic);
    }

    [Fact]
    public void PivotPlanarAbsorptionCapacity_DerivesFromExistingCorrectionLimitsOnly()
    {
        Assert.Equal(0.08f, NPCTurnInPlace.PivotPlanarAbsorptionCapacity(0.08f, 0.2f), 7);
        Assert.Equal(0.05f, NPCTurnInPlace.PivotPlanarAbsorptionCapacity(0.08f, 0.05f), 7);
        Assert.Equal(0f, NPCTurnInPlace.PivotPlanarAbsorptionCapacity(-1f, 0.2f), 7);
        Assert.Equal(0f, NPCTurnInPlace.PivotPlanarAbsorptionCapacity(0.08f, float.NaN), 7);
    }
}
