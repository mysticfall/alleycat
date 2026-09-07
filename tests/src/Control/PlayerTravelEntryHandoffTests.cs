using AlleyCat.Control.Locomotion;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

public sealed class PlayerTravelEntryHandoffTests
{
    private const float FrameDelta = 1f / 60f;

    [Fact]
    public void Begin_MatchesForwardEntryTerminalSpeedWithBoundedDeparture()
    {
        PlayerTravelEntryHandoff handoff = new();

        // i2f ends at 0.698 m/s; wf sustains 0.927 m/s per rate unit, so the matched rate 0.753 is lifted to
        // the 20% slow-departure floor of 0.8 (NPC entry-handoff bounding).
        handoff.Begin(entryTerminalSpeed: 0.698103845119476f, sustainedUnitSpeed: 0.927339623119109f, holdSeconds: 0.15f);

        Assert.True(handoff.Active);
        Assert.Equal(0.8f, handoff.Rate, 4);
    }

    [Fact]
    public void Begin_MatchesBackwardEntryTerminalSpeedWithoutDeparture()
    {
        PlayerTravelEntryHandoff handoff = new();

        // i2b ends at 0.922 m/s against wb's 0.927 m/s unit speed, so the handoff rate stays at the matched
        // value instead of stepping to the demanded full-stick rate.
        handoff.Begin(entryTerminalSpeed: 0.921742856502533f, sustainedUnitSpeed: 0.927339623119109f, holdSeconds: 0.15f);

        Assert.Equal(0.994f, handoff.Rate, 3);
    }

    [Fact]
    public void Begin_CollapsesFasterMatchesToUnitRate()
    {
        PlayerTravelEntryHandoff handoff = new();

        // A matched rate above unit rate never overdrives the boundary frame: the one-sided slow-departure
        // bound collapses it to unit rate (mirroring the accepted NPC formula).
        handoff.Begin(entryTerminalSpeed: 1.2f, sustainedUnitSpeed: 0.927339623119109f, holdSeconds: 0.15f);

        Assert.True(handoff.Active);
        Assert.Equal(1f, handoff.Rate, 5);
    }

    [Fact]
    public void Begin_FallsBackToUnitRateWithoutSustainedSpeed()
    {
        PlayerTravelEntryHandoff handoff = new();

        handoff.Begin(entryTerminalSpeed: 0.9f, sustainedUnitSpeed: 0f, holdSeconds: 0.15f);

        Assert.Equal(1f, handoff.Rate, 5);
    }

    [Fact]
    public void Step_HoldsMatchedRateThroughTheHoldWindow()
    {
        PlayerTravelEntryHandoff handoff = new();
        handoff.Begin(entryTerminalSpeed: 0.698103845119476f, sustainedUnitSpeed: 0.927339623119109f, holdSeconds: 0.15f);

        float published = 1f;
        for (int frame = 0; frame < 8; frame++)
        {
            published = handoff.Step(demandedRate: 1f, FrameDelta);
        }

        // Eight frames cover ~0.133 s of the 0.15 s hold, so the matched rate is still owning the boundary.
        Assert.True(handoff.Active);
        Assert.Equal(0.8f, published, 4);
    }

    [Fact]
    public void Step_RampsMonotonicallyTowardDemandedRateAfterHold()
    {
        PlayerTravelEntryHandoff handoff = new();
        handoff.Begin(entryTerminalSpeed: 0.698103845119476f, sustainedUnitSpeed: 0.927339623119109f, holdSeconds: 0.02f);

        float previous = handoff.Rate;
        int rampFrames = 0;
        while (handoff.Active && rampFrames < 600)
        {
            float published = handoff.Step(demandedRate: 1f, FrameDelta);
            Assert.True(published >= previous - 0.00001f);
            previous = published;
            rampFrames++;
        }

        Assert.False(handoff.Active);
        Assert.Equal(1f, previous, 5);
        Assert.True(rampFrames > 1);
    }

    [Fact]
    public void Step_EndsWindowOnConvergenceAndPassesDemandThroughAfterwards()
    {
        PlayerTravelEntryHandoff handoff = new();
        handoff.Begin(entryTerminalSpeed: 0.921742856502533f, sustainedUnitSpeed: 0.927339623119109f, holdSeconds: 0f);

        int frames = 0;
        while (handoff.Active && frames < 600)
        {
            _ = handoff.Step(demandedRate: 0.95f, FrameDelta);
            frames++;
        }

        float after = handoff.Step(demandedRate: 0.9f, FrameDelta);

        Assert.False(handoff.Active);
        Assert.Equal(0.9f, after, 5);
    }

    [Fact]
    public void Cancel_ReturnsDemandImmediately()
    {
        PlayerTravelEntryHandoff handoff = new();
        handoff.Begin(entryTerminalSpeed: 0.698103845119476f, sustainedUnitSpeed: 0.927339623119109f, holdSeconds: 0.15f);

        handoff.Cancel();
        float published = handoff.Step(demandedRate: 1f, FrameDelta);

        Assert.False(handoff.Active);
        Assert.Equal(1f, published, 5);
    }

    [Fact]
    public void Step_GuardsNonFiniteDeltaAndDemand()
    {
        PlayerTravelEntryHandoff handoff = new();
        handoff.Begin(entryTerminalSpeed: 0.698103845119476f, sustainedUnitSpeed: 0.927339623119109f, holdSeconds: 0f);

        Assert.Equal(0.8f, handoff.Step(demandedRate: float.NaN, 0f), 4);
        Assert.True(handoff.Active);
        Assert.Equal(0.8f, handoff.Step(demandedRate: float.NaN, FrameDelta), 4);
    }
}
