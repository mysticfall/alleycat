using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>A missed overlap fails only the active request and leaves idle and later commands usable.</summary>
    [Headless]
    [Fact]
    public Task TemporalOverrunLeavesLaterRequestsUsable() => RunNavigationOverrunAsync(false);

    /// <summary>A real provider exception at the same boundary retains its cause for each later command.</summary>
    [Headless]
    [Fact]
    public Task ProviderErrorAtOverlapRemainsLatched() => RunNavigationOverrunAsync(true);

    private static async Task RunNavigationOverrunAsync(bool providerError)
    {
        Vector3 start = new(.5f, 0, -.3f);
        await using NavigationFixedStepFixture fixture = await NavigationFixedStepFixture.CreateAsync(start, 0);
        MotionBricksNavigation nav = fixture.Navigation;
        var submitted = new List<long>();
        var handoffs = new List<MotionBricksNavigation.NativeHandoffObservation>();
        var played = new Dictionary<long, double>();
        fixture.Provider.Response = (context, _, count) => [.. Enumerable.Repeat(context[3], count)];
        nav.WindowSubmitted += (ticket, _, _) => submitted.Add(ticket);
        nav.NativeHandoffObserved = handoffs.Add;
        nav.PlaybackObserved = (ticket, _, frame) => played[ticket] = frame;
        Transform3D destination = new(Basis.Identity, NavigationFixedStepFixture.CornerGoal);
        Assert.Equal(NavigationDestinationResult.Accepted, nav.SetDestination(destination, []));
        await fixture.StepAsync();
        await fixture.ReleaseAsync(Assert.Single(submitted));
        await fixture.StepAsync();
        Assert.True(Assert.Single(handoffs).Accepted);

        // One 50-frame response supplies 49/30 seconds. Schedule a genuine early overlap and hold its
        // publication until the previous response has exhausted, still inside the incoming response.
        for (int tick = 0; tick < 100 && submitted.Count < 2; tick++)
        {
            await fixture.StepAsync();
        }
        Assert.Equal(2, submitted.Count);
        long expired = submitted[1];
        await fixture.Provider.WaitForStartAsync(); // In-flight overlap.
        for (int tick = 0; tick < 24; tick++)
        {
            await fixture.StepAsync();
        }
        fixture.Provider.FailNext = providerError;
        await fixture.ReleaseAsync(expired, alreadyStarted: true);
        await fixture.StepAsync();
        Assert.False(played.ContainsKey(expired));
        NavigationTerminalOutcomeRecord failed = Assert.Single(nav.TerminalOutcomes);
        if (providerError)
        {
            Assert.Equal(NavigationTerminalReason.MotionUnavailable, failed.Reason);
            Assert.Contains("Navigation fixture provider failure", nav.Failure);
        }
        else
        {
            MotionBricksNavigation.NativeHandoffObservation rejected = Assert.Single(handoffs, h => h.Request == expired);
            Assert.False(rejected.Accepted);
            Assert.Equal("native-overrun-safe-hold", rejected.Reason);
            Assert.NotEqual(NavigationTerminalReason.MotionUnavailable, failed.Reason);
            await fixture.StepAsync();
            Assert.Equal(3, submitted.Count);
            await fixture.ReleaseAsync(submitted[^1]);
            await fixture.StepAsync();
            Assert.Contains(handoffs, h => h.Request == submitted[^1] && h.Accepted && !nav.HasDestination);
            fixture.Provider.Response = null; // Later work has travel, unlike the isolated hold used to impose the overrun.
        }

        foreach (float z in new[] { -2.8f, -3, -3.2f })
        {
            int before = submitted.Count;
            Assert.Equal(NavigationDestinationResult.Accepted, nav.SetDestination(new(Basis.Identity, new(.5f, 0, z)), []));
            await fixture.StepAsync();
            if (providerError)
            {
                Assert.Equal(before, submitted.Count);
                Assert.Equal(NavigationTerminalReason.MotionUnavailable, nav.TerminalOutcomes[^1].Reason);
                Assert.Contains("Navigation fixture provider failure", nav.Failure);
            }
            else
            {
                Assert.Equal(before + 1, submitted.Count);
                await fixture.ReleaseAsync(submitted[^1]);
                await fixture.StepAsync();
                Assert.Contains(handoffs, h => h.Request == submitted[^1] && h.Accepted);
            }
        }

        // This authored tail covers a full 49/30-second response and enough time to observe its
        // successor play. It is not a reconstruction of the unavailable recorded command schedule.
        int submissionsAtTailStart = submitted.Count;
        int released = submissionsAtTailStart;
        Vector3 tailStart = fixture.Actor.GlobalPosition;
        Vector3 recoveryDirection = Vector3.Forward; // All three successor goals lie down the straight aisle.
        int outcomesAtTailStart = nav.TerminalOutcomes.Count;
        string retainedCause = nav.Failure;
        for (int tick = 0; tick < 2 * 60; tick++)
        {
            await fixture.StepAsync();
            if (providerError)
            {
                Assert.Equal(submissionsAtTailStart, submitted.Count);
                Assert.Equal(outcomesAtTailStart, nav.TerminalOutcomes.Count);
                Assert.Equal(retainedCause, nav.Failure);
                Assert.Equal(tailStart, fixture.Actor.GlobalPosition);
            }
            else if (released < submitted.Count)
            {
                await fixture.ReleaseAsync(submitted[released++]);
            }
        }
        if (providerError)
        {
            Assert.Equal(NavigationDestinationResult.Accepted, nav.SetDestination(destination, []));
            await fixture.StepAsync();
            Assert.Equal(outcomesAtTailStart + 1, nav.TerminalOutcomes.Count);
            Assert.Equal(NavigationTerminalReason.MotionUnavailable, nav.TerminalOutcomes[^1].Reason);
            Assert.Equal(retainedCause, nav.Failure);
            Assert.Equal(submissionsAtTailStart, submitted.Count);
        }
        else
        {
            Assert.Equal(outcomesAtTailStart, nav.TerminalOutcomes.Count);
            Assert.Equal("", nav.Failure);
            Assert.Contains(submitted.Skip(submissionsAtTailStart), ticket => played.TryGetValue(ticket, out double frame) && frame > 0 &&
                handoffs.Any(h => h.Request == ticket && h.Accepted));
            Assert.True((fixture.Actor.GlobalPosition - tailStart).Dot(recoveryDirection) > nav.DestinationReachedDistance);
        }
        Assert.False(played.ContainsKey(expired));
    }
}
