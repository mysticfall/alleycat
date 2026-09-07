using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCSideStepTests
{
    // Package endpoint displacement of the ANIM-002 side-step clips (sr/sl Root |X| in metres).
    private const float SideStepEndpoint = 0.42499688f;
    // Package endpoint yaw magnitude of the ANIM-002 pivot clips (±π/4).
    private const float PivotYaw = Mathf.Pi / 4f;

    // Existing generic per-segment planar correction bound; also the chained longitudinal drift budget per clip.
    private const float PlanarCorrectionLimit = 0.08f;

    private static NPCSideStepPlan Select(Vector2 offset, float yaw, float capacity = PivotYaw)
        => NPCSideStep.Select(offset, yaw, SideStepEndpoint, capacity, PlanarCorrectionLimit);

    [Fact]
    public void MostlyLateralRightRequest_SelectsOneRightSideStep()
    {
        // The user-reported short mostly-lateral request: 0.2 m dominated by +X with a ~71° right terminal turn.
        NPCSideStepPlan plan = Select(new Vector2(0.18984336f, 0.06523204f), -1.2398256f);

        Assert.Equal(NPCSideStepKind.StepRight, plan.Kind);
        Assert.Equal(StandingLocomotionCapability.ConfinedSideStepRight, plan.StepCapability);
        Assert.True(plan.PlaysSideStepClip);
        Assert.Equal(new Vector2(0.18984336f, 0.06523204f), plan.AbsorbedPlanarSemantic);
    }

    [Fact]
    public void MostlyLateralLeftRequest_SelectsOneLeftSideStep()
    {
        NPCSideStepPlan plan = Select(new Vector2(-0.3f, 0.05f), 0.2f);

        Assert.Equal(NPCSideStepKind.StepLeft, plan.Kind);
        Assert.Equal(StandingLocomotionCapability.ConfinedSideStepLeft, plan.StepCapability);
    }

    [Fact]
    public void ForwardDominantOrDiagonalRequests_ReturnNone()
    {
        foreach (Vector2 forwardish in new[] { new Vector2(0.05f, 0.4f), new Vector2(-0.1f, 0.3f), new Vector2(0.05f, 0.05f) })
        {
            NPCSideStepPlan plan = Select(forwardish, 0f);
            Assert.Equal(NPCSideStepKind.None, plan.Kind);
            Assert.False(plan.PlaysSideStepClip);
            Assert.Null(plan.StepCapability);
            Assert.Equal(0, plan.ChainedStepCount);
        }
    }

    [Fact]
    public void ChainPlanning_PlansNearestWholeRepetitionsForAnyLateralDistance()
    {
        // The former endpoint-plus-budget band is replaced by chaining —
        // N = round(distance / endpoint) repetitions, so the final step's dispersed remainder never exceeds half an
        // endpoint in either direction. Distances beyond any former bound still classify.
        (Vector2 Offset, int ExpectedSteps)[] chains =
        [
            (new Vector2(0.2f, 0.01f), 1),
            (new Vector2(0.43f, 0.01f), 1),
            (new Vector2(0.55422264f, 0.022288084f), 1),
            (new Vector2(0.61f, 0.01f), 1),
            // The stuck lateral fixture: 0.947 m left-lateral chains exactly two steps.
            (new Vector2(-0.946435f, 0.03213787f), 2),
            (new Vector2(0.8f, 0f), 2),
            (new Vector2(2.0f, 0.02f), 5),
            (new Vector2(2.5f, 0f), 6),
            (new Vector2(5.0f, 0f), 12),
        ];
        foreach ((Vector2 offset, int expectedSteps) in chains)
        {
            NPCSideStepPlan plan = Select(offset, 0f);
            Assert.True(plan.PlaysSideStepClip, $"Offset {offset} did not classify as a side-step chain.");
            Assert.Equal(expectedSteps, plan.ChainedStepCount);
            if (expectedSteps >= 2)
            {
                // Only the chaining regime guarantees the half-endpoint bound; a single step keeps the
                // in-band dispersal semantics for the whole clip-versus-request remainder.
                float endpointSign = plan.Kind == NPCSideStepKind.StepRight ? 1f : -1f;
                float chainedRemainder = offset.X - (expectedSteps * endpointSign * SideStepEndpoint);
                Assert.True(
                    Mathf.Abs(chainedRemainder) <= (SideStepEndpoint * 0.5f) + 0.000001f,
                    $"Chained remainder {chainedRemainder:F7} for {offset} escaped the half-endpoint bound.");
            }
        }
    }

    [Fact]
    public void ChainPlanning_ExactEndpointMultiplesCarryZeroRemainder()
    {
        NPCSideStepPlan twoExact = Select(new Vector2(2f * SideStepEndpoint, 0f), 0f);

        Assert.Equal(2, twoExact.ChainedStepCount);
        Vector2 clipEndpoint = new(SideStepEndpoint, 0f);
        Vector2 remainder = twoExact.AbsorbedPlanarSemantic - (clipEndpoint * twoExact.ChainedStepCount);
        Assert.Equal(0f, remainder.Length(), 6);
    }

    [Fact]
    public void ChainEligibility_DeferSteepDiagonalsToTravelAndKeepNearLateralChains()
    {
        // The chain's longitudinal drift budget is the existing per-segment planar correction bound per chained
        // clip: near-lateral requests of any distance chain, while steeper X-dominant diagonals belong to travel
        // (which turns toward the displacement and walks it through ordinary travel).
        NPCSideStepPlan steepDiagonal = Select(new Vector2(1.8042767f, 1.0658197f), -1.0372293f);
        NPCSideStepPlan mediumSkew = Select(new Vector2(0.65f, 0.3f), 0f);
        NPCSideStepPlan nearLateral = Select(new Vector2(0.946435f, 0.03213787f), -0.12529877f);

        Assert.Equal(NPCSideStepKind.None, steepDiagonal.Kind);
        Assert.Equal(NPCSideStepKind.None, mediumSkew.Kind);
        Assert.Equal(NPCSideStepKind.StepRight, nearLateral.Kind);
        Assert.Equal(2, nearLateral.ChainedStepCount);
        // The budget scales with the chain: five chained clips carry five per-segment bounds of drift.
        NPCSideStepPlan longMildSkew = Select(new Vector2(2.0f, 0.35f), 0f);
        Assert.Equal(NPCSideStepKind.StepRight, longMildSkew.Kind);
        Assert.Equal(5, longMildSkew.ChainedStepCount);
        // Single steps keep the curvature-safe band semantics: any lateral-dominant shape within one step classifies.
        NPCSideStepPlan singleSteep = Select(new Vector2(0.44f, 0.43f), 0f);
        Assert.Equal(NPCSideStepKind.StepRight, singleSteep.Kind);
        Assert.Equal(1, singleSteep.ChainedStepCount);
    }

    [Fact]
    public void EffectivelyZeroOrNonFiniteRequests_ReturnNone()
    {
        Assert.Equal(NPCSideStepKind.None, Select(Vector2.Zero, 1f).Kind);
        Assert.Equal(NPCSideStepKind.None, Select(new Vector2(float.NaN, 0f), 1f).Kind);
        Assert.Equal(NPCSideStepKind.None, NPCSideStep.Select(new Vector2(0.1f, 0f), 1f, 0f, PivotYaw, PlanarCorrectionLimit).Kind);
        Assert.Equal(NPCSideStepKind.None, NPCSideStep.Select(new Vector2(0.1f, 0f), 1f, float.NaN, PivotYaw, PlanarCorrectionLimit).Kind);
    }

    [Fact]
    public void SmallYawResidual_IsFullyAbsorbedByTheSideStepChain()
    {
        NPCSideStepPlan plan = Select(new Vector2(0.2f, 0.01f), 0.3f);

        Assert.Equal(0.3f, plan.AbsorbedYawRadians, 7);
        Assert.Equal(0f, plan.SettlementYawRadians, 7);
        Assert.Equal(0.3f, plan.TotalYawRadians, 7);
    }

    [Fact]
    public void YawResidualBeyondAbsorptionCapacity_ClampsAndDefersRemainderWithoutComposing()
    {
        // A pivot chain composes only beyond capacity; a single confined manoeuvre defers the remainder to settlement.
        float capacity = NPCSideStep.YawAbsorptionCapacity(SideStepEndpoint, 0.08f);
        Assert.True(capacity > 0f);
        NPCSideStepPlan plan = NPCSideStep.Select(
            new Vector2(0.2f, 0.01f),
            -1.2398256f,
            SideStepEndpoint,
            capacity,
            PlanarCorrectionLimit);

        Assert.Equal(-capacity, plan.AbsorbedYawRadians, 7);
        Assert.Equal(-1.2398256f + capacity, plan.SettlementYawRadians, 7);
        Assert.Equal(-1.2398256f, plan.TotalYawRadians, 7);
    }

    [Fact]
    public void ZeroYawResidual_AbsorbsNothing()
    {
        NPCSideStepPlan plan = Select(new Vector2(0.2f, 0.01f), 0f);

        Assert.Equal(0f, plan.AbsorbedYawRadians, 7);
        Assert.Equal(0f, plan.SettlementYawRadians, 7);
    }

    [Fact]
    public void YawAbsorptionCapacity_IsCurvatureSafeAgainstExistingPlanarCorrectionBound()
    {
        // Absorbing yaw during a lateral displacement curves the actor-relative path; the capacity keeps the
        // worst-case induced deviation (2·L·sin(φ/2)) within the existing per-segment planar correction bound.
        float capacity = NPCSideStep.YawAbsorptionCapacity(SideStepEndpoint, 0.08f);
        Assert.True(
            SideStepEndpoint * 2f * Mathf.Sin(capacity / 2f) <= 0.08f + 0.000001f,
            "Absorption capacity must keep the induced planar deviation inside the per-segment correction bound.");
        Assert.True(capacity > 0.1f);
        Assert.True(capacity < Mathf.Pi / 4f);
        Assert.Equal(0f, NPCSideStep.YawAbsorptionCapacity(0f, 0.08f), 7);
        Assert.Equal(0f, NPCSideStep.YawAbsorptionCapacity(SideStepEndpoint, -1f), 7);
        Assert.Equal(0f, NPCSideStep.YawAbsorptionCapacity(SideStepEndpoint, float.NaN), 7);
        Assert.Equal(
            Mathf.Pi,
            NPCSideStep.YawAbsorptionCapacity(0.01f, 0.08f),
            5);
    }
}
