using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>An accepted route to an unmodelled wall visibly turns and travels before truthful obstruction.</summary>
    [Headless]
    [Fact]
    public async Task WallRequestRealisesOwnedTravelAndTurningBeforeObstruction()
    {
        Vector3 start = new(.5f, 0, -.4f);
        const float front = -1.1f;
        await using NavigationFixedStepFixture fixture = await NavigationFixedStepFixture.CreateAsync(start, 1.1f, new(.5f, .5f, front - .1f));
        MotionBricksNavigation nav = fixture.Navigation;
        float heading = nav.RecordingAnatomicalHeading!.Value;
        float maximumTurn = 0, minimumZ = start.Z;
        var tickets = new List<(long Ticket, long? Owner, string Role)>();
        var played = new HashSet<long>();
        nav.WindowSubmitted += (ticket, _, _) => tickets.Add((ticket, nav.CurrentStatus.ActiveRequest?.RequestID, nav.ReviewedRole));
        nav.PlaybackObserved = (ticket, _, _) => _ = played.Add(ticket);
        Assert.Equal(NavigationDestinationResult.Accepted,
            nav.SetDestination(new(Basis.Identity, new(.5f, 0, -3.4f)), []));
        long request = nav.CurrentStatus.ActiveRequest!.Value.RequestID;
        int released = 0;
        for (int i = 0; i < Math.Ceiling(4.845948319 / NavigationFixedStepFixture.Delta); i++)
        {
            await fixture.StepAsync();
            maximumTurn = Math.Max(maximumTurn, Math.Abs(Mathf.Wrap(nav.RecordingAnatomicalHeading!.Value - heading, -Mathf.Pi, Mathf.Pi)));
            minimumZ = Math.Min(minimumZ, fixture.Actor.GlobalPosition.Z);
            if (released < tickets.Count)
            {
                await fixture.ReleaseAsync(tickets[released++].Ticket);
            }
        }
        Assert.Contains(tickets, t => t.Owner == request && t.Role == "forward" && played.Contains(t.Ticket));
        Assert.True(start.Z - minimumZ > nav.DestinationReachedDistance);
        Assert.True(maximumTurn > Mathf.DegToRad(nav.FacingToleranceDegrees));
        float slop = ProjectSettings.GetSetting("physics/jolt_physics_3d/simulation/penetration_slop").AsSingle();
        Assert.True(minimumZ - .1f >= front - slop);
        Assert.Contains("static obstruction", nav.Failure);
        Assert.Equal(NavigationTerminalReason.ConvergenceFailed, Assert.Single(nav.TerminalOutcomes).Reason);
        Assert.Equal("settled-idle", nav.Phase);
    }
}
