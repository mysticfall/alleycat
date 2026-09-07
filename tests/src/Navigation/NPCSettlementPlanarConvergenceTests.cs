using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCSettlementPlanarConvergenceTests
{
    private const float Delta = 1f / 60f;

    [Fact]
    public void CalculateRequestedDelta_AtNumericalTolerance_ClearsStalePlanarSmoothing()
    {
        Vector2 smoothedVelocity = new(0.3f, -0.2f);
        Vector2 acceleration = new(-1f, 0.5f);

        Vector2 requested = NPCSettlementPlanarConvergence.CalculateRequestedDelta(
            new Vector2(NPCAnimationProgramme.NumericalConvergenceThreshold, 0f),
            new Vector2(0.03f, 0f),
            ref smoothedVelocity,
            ref acceleration,
            Delta);

        Assert.Equal(Vector2.Zero, requested);
        Assert.Equal(Vector2.Zero, smoothedVelocity);
        Assert.Equal(Vector2.Zero, acceleration);
    }

    [Fact]
    public void IsWithinNumericalTolerance_AcceptsExactPlanarBoundary()
    {
        Assert.True(NPCSettlementPlanarConvergence.IsWithinNumericalTolerance(
            new Vector2(NPCAnimationProgramme.NumericalConvergenceThreshold, 0f)));
    }

    [Fact]
    public void ClearSmoothing_AtNumericalTolerance_ResetsStalePlanarState()
    {
        Vector2 smoothedVelocity = new(0.3f, -0.2f);
        Vector2 acceleration = new(-1f, 0.5f);

        NPCSettlementPlanarConvergence.ClearSmoothing(ref smoothedVelocity, ref acceleration);

        Assert.Equal(Vector2.Zero, smoothedVelocity);
        Assert.Equal(Vector2.Zero, acceleration);
    }

    [Fact]
    public void ClampRequestedDelta_CrossingRequest_StopsExactlyAtEndpoint()
    {
        Vector2 requested = NPCSettlementPlanarConvergence.ClampRequestedDelta(
            new Vector2(0.02f, 0f),
            new Vector2(0.05f, 0f));

        Assert.Equal(new Vector2(0.02f, 0f), requested);
    }

    [Fact]
    public void ClampRequestedDelta_AwayFromEndpoint_ReturnsZero()
    {
        Vector2 requested = NPCSettlementPlanarConvergence.ClampRequestedDelta(
            new Vector2(0.02f, 0f),
            new Vector2(-0.01f, 0f));

        Assert.Equal(Vector2.Zero, requested);
    }

    [Fact]
    public void ResolveTaperedVelocity_LargeResidual_StartsBriskBeyondTheRetiredSlowCap()
    {
        Vector2 target = NPCSettlementPlanarConvergence.ResolveTaperedVelocity(
            new Vector2(0.3f, 0f),
            NPCSettlementPlanarConvergence.MaximumRate);

        // The former slow cap was min(rate, 0.03); the tapered profile must start far beyond it.
        Assert.Equal(NPCSettlementPlanarConvergence.MaximumRate, target.X, 5);
        Assert.True(target.X > 0.2f, $"Taper start {target.X:F5} was not brisk.");
    }

    [Fact]
    public void ResolveTaperedVelocity_SmallResidual_DeceleratesBelowTheCeiling()
    {
        Vector2 target = NPCSettlementPlanarConvergence.ResolveTaperedVelocity(
            new Vector2(0.01f, 0f),
            NPCSettlementPlanarConvergence.MaximumRate);

        float expected = Mathf.Sqrt(2f * NPCSettlementPlanarConvergence.TaperAcceleration * 0.01f);
        Assert.Equal(expected, target.X, 5);
        Assert.True(target.X < NPCSettlementPlanarConvergence.MaximumRate);
    }

    [Fact]
    public void ResolveTaperedVelocity_AnyResidualDirection_PreservesErrorDirection()
    {
        var error = new Vector2(-0.12f, 0.05f);

        Vector2 target = NPCSettlementPlanarConvergence.ResolveTaperedVelocity(
            error,
            NPCSettlementPlanarConvergence.MaximumRate);

        Assert.True(target.Dot(error) > 0f);
        Assert.True(target.X < 0f);
        Assert.True(target.Y > 0f);
    }

    [Fact]
    public void ResolveTaperedVelocity_WithinNumericalTolerance_ReturnsZero()
    {
        Vector2 target = NPCSettlementPlanarConvergence.ResolveTaperedVelocity(
            new Vector2(NPCAnimationProgramme.NumericalConvergenceThreshold * 0.5f, 0f),
            NPCSettlementPlanarConvergence.MaximumRate);

        Assert.Equal(Vector2.Zero, target);
    }

    [Fact]
    public void TaperedConvergence_ResolvesLargeResidualQuicklyWithoutCrossingOrMagnitudeCap()
    {
        var error = new Vector2(0.3f, 0f);
        Vector2 smoothedVelocity = Vector2.Zero;
        Vector2 acceleration = Vector2.Zero;
        float peakSpeed = 0f;
        List<float> approachSpeeds = [];

        int frames = 0;
        while (error.Length() > NPCAnimationProgramme.NumericalConvergenceThreshold && frames < 240)
        {
            Vector2 target = NPCSettlementPlanarConvergence.ResolveTaperedVelocity(
                error,
                NPCSettlementPlanarConvergence.MaximumRate);
            Vector2 requested = NPCSettlementPlanarConvergence.CalculateRequestedDelta(
                error,
                target,
                ref smoothedVelocity,
                ref acceleration,
                Delta);
            Assert.True(
                requested.Dot(new Vector2(1f, 0f)) >= -0.000001f,
                $"Frame {frames} crossed the endpoint: error={error}, requested={requested}.");
            error -= requested;
            float speed = smoothedVelocity.Length();
            peakSpeed = Math.Max(peakSpeed, speed);
            if (error.Length() < 0.05f)
            {
                approachSpeeds.Add(speed);
            }

            frames++;
        }

        // Brisk: 0.3 m resolves well inside a 1.5 s tail (braking curve from 0.3 m at 2 m/s² plus the ceiling).
        Assert.True(frames <= 90, $"Tapered settlement of 0.3 m took {frames} frames ({frames * Delta:F3}s).");
        Assert.True(error.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold);
        Assert.True(peakSpeed > 0.2f, $"Convergence never exceeded a brisk speed: peak={peakSpeed:F5}.");
        // Smooth taper: the final approach decelerates monotonically into the tolerance.
        for (int index = 1; index < approachSpeeds.Count; index++)
        {
            Assert.True(
                approachSpeeds[index] <= approachSpeeds[index - 1] + 0.0001f,
                $"Approach speed increased at tail index {index}: {string.Join(", ", approachSpeeds)}.");
        }
    }

    [Fact]
    public void TerminalAccounting_ChargesRealisedCorrectionWithinReservationForObservability()
    {
        NPCResidualCorrectionBudget budget = new();
        budget.BeginRoute(1, 1, 0.08f, 0.20f, 1f);
        budget.RecordTerminal(0.20f, 0f);
        Vector2 endpointBounded = NPCSettlementPlanarConvergence.ClampRequestedDelta(
            new Vector2(0.02f, 0f),
            new Vector2(0.05f, 0f));

        // Settlement requests are not capped by the reservation, but realised correction still
        // charges the ledger within it.
        budget.AccountTerminalRealised(endpointBounded, endpointBounded, 0f, 0f);

        Assert.Equal(new Vector2(0.02f, 0f), endpointBounded);
        Assert.Equal(0.02f, budget.RealisedPlanar, 5);
        Assert.Equal(0.02f, budget.RealisedReservedTerminalPlanar, 5);
        Assert.Equal(0.18f, budget.RemainingReservedTerminalPlanar, 5);
    }
}
