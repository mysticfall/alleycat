using AlleyCat.Navigation;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>Allocation-free stationary confirmation coverage for NAV-TAC-006 through NAV-TAC-008.</summary>
public sealed class NPCTerminalConfirmationTests
{
    /// <summary>Verifies boundary entry itself is not counted and success occurs on confirmation frame four.</summary>
    [Fact]
    public void FourInToleranceFrames_CompleteOnlyOnFourthObservation()
    {
        NPCTerminalConfirmation confirmation = default;
        confirmation.Begin();

        Assert.Equal(NPCTerminalConfirmationResult.Pending, confirmation.Observe(true));
        Assert.Equal(NPCTerminalConfirmationResult.Pending, confirmation.Observe(true));
        Assert.Equal(NPCTerminalConfirmationResult.Pending, confirmation.Observe(true));
        Assert.Equal(3, confirmation.ConfirmedFrameCount);
        Assert.True(confirmation.IsActive);

        Assert.Equal(NPCTerminalConfirmationResult.Completed, confirmation.Observe(true));
        Assert.Equal(NPCTerminalConfirmation.RequiredFrameCount, confirmation.ConfirmedFrameCount);
        Assert.False(confirmation.IsActive);
        Assert.Equal(NPCTerminalConfirmationResult.Pending, confirmation.Observe(false));
    }

    /// <summary>Verifies every confirmation frame fails immediately when its fresh measurement is outside tolerance.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LeavingTolerance_FailsImmediatelyOnEveryConfirmationFrame(int failingFrame)
    {
        NPCTerminalConfirmation confirmation = default;
        confirmation.Begin();

        for (int frame = 1; frame < failingFrame; frame++)
        {
            Assert.Equal(NPCTerminalConfirmationResult.Pending, confirmation.Observe(true));
        }

        Assert.Equal(NPCTerminalConfirmationResult.Failed, confirmation.Observe(false));
        Assert.Equal(failingFrame - 1, confirmation.ConfirmedFrameCount);
        Assert.False(confirmation.IsActive);
    }

    /// <summary>Verifies zero margins are accepted while the next representable error outside tolerance is rejected.</summary>
    [Fact]
    public void ExactToleranceEdges_AreInclusiveAndMarginsAreExact()
    {
        var tolerances = new NavigationTerminalTolerances(0.001f, 0.01f);
        var error = new NavigationPlanarYawError(0.001f, 0.01f);
        NavigationSignedMargins margins = tolerances.CalculateMargins(error);
        NPCTerminalBoundaryMeasurement measurement = Measurement(error, tolerances, margins);

        Assert.Equal(0f, margins.PlanarDistance);
        Assert.Equal(0f, margins.YawRadians);
        Assert.True(measurement.IsWithinTolerance);

        NavigationPlanarYawError outside = error with
        {
            YawRadians = float.BitIncrement(error.YawRadians)
        };
        NavigationSignedMargins outsideMargins = tolerances.CalculateMargins(outside);
        Assert.False(Measurement(outside, tolerances, outsideMargins).IsWithinTolerance);
        Assert.True(outsideMargins.YawRadians < 0f);
    }

    /// <summary>Verifies the scalar confirmation state performs no managed allocations while advancing.</summary>
    [Fact]
    public void ConfirmationObservations_AllocateNoMemory()
    {
        NPCTerminalConfirmation confirmation = default;
        confirmation.Begin();
        _ = confirmation.Observe(true);
        confirmation.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            confirmation.Begin();
            _ = confirmation.Observe(true);
            _ = confirmation.Observe(true);
            _ = confirmation.Observe(true);
            _ = confirmation.Observe(true);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    private static NPCTerminalBoundaryMeasurement Measurement(
        NavigationPlanarYawError error,
        NavigationTerminalTolerances tolerances,
        NavigationSignedMargins margins)
    {
        var occurrence = new NPCReleaseOccurrence(7, 3, 0.8f, NPCAnimationPhaseLane.Left, "release_l");
        return new(
            new NavigationRequestIdentity(2, 1),
            4,
            7,
            occurrence,
            10,
            10,
            new NavigationOutcomeTransform(1f, 0f, 2f, 0.2f),
            new NavigationOutcomeTransform(1f, 0f, 2f, 0.2f),
            error,
            tolerances,
            margins);
    }
}
