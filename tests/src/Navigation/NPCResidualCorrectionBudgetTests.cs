using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCResidualCorrectionBudgetTests
{
    [Fact]
    public void BeginRoute_CreatesRevisionedTravelScope()
    {
        NPCResidualCorrectionBudget budget = new();

        budget.BeginRoute(7, 11, 1.5f, 0.8f);

        Assert.Equal(7, budget.DestinationGeneration);
        Assert.Equal(11, budget.RouteRevision);
        Assert.Equal(NPCResidualCorrectionScope.Travel, budget.Scope);
        Assert.Equal(1.5f, budget.RemainingPlanar);
        Assert.Equal(0.8f, budget.RemainingYaw);
    }

    [Fact]
    public void ReenteringSameScope_DoesNotResetPerFrameAccounting()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 1f, 1f);
        budget.AccountRealised(new Vector2(0.25f, 0f), new Vector2(0.25f, 0f), 0.2f, 0.2f);

        budget.BeginScope(NPCResidualCorrectionScope.Travel);

        Assert.Equal(0.25f, budget.RealisedPlanar);
        Assert.Equal(0.2f, budget.RealisedYaw);
    }

    [Fact]
    public void NewLifecycleScope_RetainsOneCumulativeBoutBudget()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 1f, 1f);
        budget.AccountRealised(new Vector2(0.8f, 0f), new Vector2(0.8f, 0f), 0.7f, 0.7f);

        budget.BeginScope(NPCResidualCorrectionScope.Exit);

        Assert.Equal(0.8f, budget.RealisedPlanar);
        Assert.Equal(0.7f, budget.RealisedYaw);
        Assert.Equal(0.2f, budget.RemainingPlanar, 5);
        Assert.Equal(0.3f, budget.RemainingYaw, 5);
    }

    [Fact]
    public void Accounting_ChargesOnlyRealisedSyntheticCorrection()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 1f, 1f);

        budget.AccountRealised(new Vector2(0.4f, 0f), new Vector2(0.1f, 0f), 0.4f, 0.1f);

        Assert.Equal(0.1f, budget.RealisedPlanar, 5);
        Assert.Equal(0.1f, budget.RealisedYaw, 5);
    }

    [Fact]
    public void AuthoredRootWithoutSyntheticRequest_DoesNotConsumeBudget()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 1f, 1f);

        budget.AccountRealised(Vector2.Zero, new Vector2(0f, -0.3f), 0f, 0.4f);

        Assert.Equal(0f, budget.RealisedPlanar);
        Assert.Equal(0f, budget.RealisedYaw);
    }

    [Fact]
    public void TerminalReservation_RejectsForecastBeyondCapacity()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 1f, 1f);

        Assert.False(budget.ReserveTerminal(1.1f, 0.5f, 1f, 1f));
        Assert.False(budget.HasTerminalReservation);
        Assert.True(budget.ReserveTerminal(0.8f, 0.5f, 1f, 1f));
    }

    [Fact]
    public void TerminalReservation_UsesConfiguredCapacityBeyondLowerGenericLimit()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 0.08f, 0.20f, 1f);

        Assert.Equal(0.08f, budget.GenericPlanarLimit, 5);
        Assert.Equal(0.20f, budget.TerminalPlanarLimit, 5);
        Assert.Equal(0.20f, budget.PlanarLimit, 5);
        Assert.Equal(0.08f, budget.LimitPlanarRequest(new Vector2(0.20f, 0f)).Length(), 5);
        Assert.True(budget.ReserveTerminal(0.19f, 0.5f, 0.20f, 1f));
        Assert.Equal(0.19f, budget.ReservedTerminalPlanar, 5);

        NPCResidualCorrectionBudget rejected = new();
        rejected.BeginRoute(1, 1, 0.08f, 0.20f, 1f);

        Assert.False(rejected.ReserveTerminal(0.201f, 0.5f, 0.20f, 1f));
        Assert.False(rejected.HasTerminalReservation);
    }

    [Fact]
    public void TerminalReservation_RemainingCapacityAccountsForPriorTerminalCorrection()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 0.08f, 0.20f, 1f);
        Assert.True(budget.ReserveTerminal(0.20f, 0f, 0.20f, 1f));
        budget.AccountTerminalRealised(new Vector2(0.02f, 0f), new Vector2(0.02f, 0f), 0f, 0f);

        Assert.Equal(0.18f, budget.RemainingPlanar, 5);
        Assert.Equal(0.18f, budget.RemainingTerminalPlanarCapacity, 5);
        Assert.Equal(0.18f, budget.RemainingReservedTerminalPlanar, 5);
    }

    [Fact]
    public void RecordedTerminalReservation_ChargesFullResidualForObservabilityWithoutACapacityGate()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 0.08f, 0.20f, 1f);

        // Travel exit-entry commitments record their full forecast residual even beyond the
        // terminal capacity — shaping plus unbounded settlement land the request; the ledger stays observational.
        budget.RecordTerminal(0.58f, 0.4f);

        Assert.True(budget.HasTerminalReservation);
        Assert.Equal(0.58f, budget.ReservedTerminalPlanar, 5);
        Assert.Equal(0.4f, budget.ReservedTerminalYaw, 5);
        Assert.Equal(0f, budget.RealisedReservedTerminalPlanar, 5);
        Assert.Equal(0.58f, budget.RemainingReservedTerminalPlanar, 5);
        budget.AccountTerminalRealised(new Vector2(0.2f, 0f), new Vector2(0.2f, 0f), 0.1f, 0.1f);
        Assert.Equal(0.2f, budget.RealisedReservedTerminalPlanar, 5);
    }

    [Fact]
    public void TerminalActualCorrection_AccountsWithinReservationAndRouteLedger()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 0.08f, 0.20f, 1f);
        Assert.True(budget.ReserveTerminal(0.10f, 0f, 0.20f, 1f));

        budget.AccountTerminalRealised(new Vector2(0.15f, 0f), new Vector2(0.15f, 0f), 0f, 0f);

        Assert.Equal(0.10f, budget.RealisedPlanar, 5);
        Assert.Equal(0.10f, budget.RealisedTerminalPlanar, 5);
        Assert.Equal(0.10f, budget.RealisedReservedTerminalPlanar, 5);
        Assert.Equal(0f, budget.RemainingReservedTerminalPlanar, 5);

        budget.AccountTerminalRealised(new Vector2(0.01f, 0f), new Vector2(0.01f, 0f), 0f, 0f);

        Assert.Equal(0.10f, budget.RealisedPlanar, 5);
        Assert.True(budget.RemainingPlanar > 0f);
    }

    [Fact]
    public void SameDestinationRouteRevision_PreservesTerminalActualAccounting()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 0.08f, 0.20f, 1f);
        Assert.True(budget.ReserveTerminal(0.10f, 0f, 0.20f, 1f));
        budget.AccountTerminalRealised(new Vector2(0.05f, 0f), new Vector2(0.05f, 0f), 0f, 0f);

        budget.BeginRoute(1, 2, 0.08f, 0.20f, 1f);

        Assert.False(budget.HasTerminalReservation);
        Assert.Equal(0.05f, budget.RealisedPlanar, 5);
        Assert.Equal(0.05f, budget.RealisedTerminalPlanar, 5);
        Assert.Equal(0.15f, budget.RemainingTerminalPlanarCapacity, 5);
    }

    [Fact]
    public void SameDestinationRouteRevision_InvalidatesReservationAndPreservesRealisedAccounting()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 1f, 1f);
        budget.AccountRealised(new Vector2(0.2f, 0f), new Vector2(0.2f, 0f), 0.3f, 0.3f);
        Assert.True(budget.ReserveTerminal(0.5f, 0.5f, 1f, 1f));

        budget.BeginRoute(1, 2, 1f, 1f);
        Assert.False(budget.HasTerminalReservation);
        Assert.Equal(2, budget.RouteRevision);
        Assert.Equal(0.2f, budget.RealisedPlanar, 5);
        Assert.Equal(0.3f, budget.RealisedYaw, 5);
        Assert.Equal(0.8f, budget.RemainingPlanar, 5);
        Assert.Equal(0.7f, budget.RemainingYaw, 5);
    }

    [Fact]
    public void CancelTerminalReservation_PreservesNonResettingRealisedLedger()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 0.03f, 1f);
        budget.AccountRealised(new Vector2(0.01f, 0f), new Vector2(0.01f, 0f), 0.2f, 0.2f);
        Assert.True(budget.ReserveTerminal(0.015f, 0.1f, budget.RemainingPlanar, budget.RemainingYaw));

        budget.CancelTerminalReservation();

        Assert.False(budget.HasTerminalReservation);
        Assert.Equal(0f, budget.ReservedTerminalPlanar);
        Assert.Equal(0f, budget.ReservedTerminalYaw);
        Assert.Equal(0.01f, budget.RealisedPlanar, 5);
        Assert.Equal(0.02f, budget.RemainingPlanar, 5);
        Assert.Equal(0.2f, budget.RealisedYaw, 5);
    }

    [Fact]
    public void NewDestinationAndCancellation_ResetRealisedAccounting()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 1f, 1f);
        budget.AccountRealised(new Vector2(0.2f, 0f), new Vector2(0.2f, 0f), 0.3f, 0.3f);

        budget.BeginRoute(2, 2, 1f, 1f);
        Assert.Equal(0f, budget.RealisedPlanar);
        Assert.Equal(0f, budget.RealisedYaw);

        budget.Invalidate();
        Assert.Equal(NPCResidualCorrectionScope.None, budget.Scope);
        Assert.Equal(0, budget.DestinationGeneration);
        Assert.Equal(0f, budget.RemainingPlanar);
    }
}
