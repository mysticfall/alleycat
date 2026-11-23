using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Short rearward travel must realise useful progress before turning, without a wide forward loop.</summary>
    [Headless]
    [Fact]
    public Task ShortAdjustmentSubmitsTranslationAndFacingBeforeExternalIdle() => CheckLocalAdjustmentAsync(1.3f, 10.858264787);

    /// <summary>Opposite terminal facing must not restart translation after the short adjustment is reached.</summary>
    [Headless]
    [Fact]
    public Task OppositeFacingDoesNotRepeatUnproductiveTranslation() => CheckLocalAdjustmentAsync(3.10f, 25.096799003);

    private static async Task CheckLocalAdjustmentAsync(float yaw, double observationSeconds)
    {
        Vector3 start = new(.5f, 0, -2);
        Vector3 goal = start + new Vector3(0, 0, .25f);
        await using NavigationFixedStepFixture fixture = await NavigationFixedStepFixture.CreateAsync(start, 0);
        MotionBricksNavigation nav = fixture.Navigation;
        var windows = new List<(long Ticket, MotionBricksNavigation.RoleSubmission? Role, float Remaining, bool Arrival)>();
        nav.WindowSubmitted += (ticket, _, _) => windows.Add((ticket, nav.SubmittedRole,
            fixture.Actor.GlobalPosition.DistanceTo(goal), nav.SourceRateCurveIntent?.Arrival == true));
        Assert.Equal(NavigationDestinationResult.Accepted, nav.SetDestination(new(new Basis(Vector3.Up, yaw), goal), []));
        int released = 0;
        bool reachedBeforeSecondSubmission = false;
        float maximumYawChange = 0;
        double excursion = start.DistanceTo(goal) + nav.DestinationReachedDistance;
        for (int i = 0; i < Math.Ceiling(observationSeconds / NavigationFixedStepFixture.Delta); i++)
        {
            await fixture.StepAsync();
            Assert.InRange(fixture.Actor.GlobalPosition.DistanceTo(start), 0, excursion);
            maximumYawChange = Math.Max(maximumYawChange, Math.Abs(fixture.Actor.GlobalRotation.Y));
            if (windows.Count == 1 && fixture.Actor.GlobalPosition.DistanceTo(goal) <= nav.DestinationReachedDistance)
            {
                reachedBeforeSecondSubmission = true;
            }
            if (released < windows.Count)
            {
                await fixture.ReleaseAsync(windows[released++].Ticket);
            }
        }
        Assert.NotEmpty(windows);
        Assert.Equal("backward", windows[0].Role?.SelectedRole);
        Assert.False(windows[0].Arrival);
        Assert.True(reachedBeforeSecondSubmission, "The first translation must retain useful progress until actual position is reached.");
        int turn = windows.FindIndex(w => w.Role?.SelectedRole.EndsWith("_turn", StringComparison.Ordinal) == true);
        int idle = windows.FindIndex(w => w.Arrival);
        Assert.True(turn > 0 && idle > turn, $"Required travel, turn and arrival order: {string.Join(", ", windows.Select(w => (w.Role?.SelectedRole, w.Remaining, w.Arrival)))}");
        Assert.NotNull(windows[turn].Role);
        Assert.True(windows[turn].Role!.Decision.Inputs.PositionReached);
        Assert.InRange(windows[turn].Remaining, 0, nav.DestinationReachedDistance);
        Assert.DoesNotContain(windows, w => w.Role is { SelectedRole: "forward", Decision.Positioning: false });
        Assert.DoesNotContain(windows.Skip(turn), w => w.Role?.SelectedRole == "backward");
        Assert.True(maximumYawChange > Mathf.DegToRad(nav.FacingToleranceDegrees));
        Assert.Equal(NavigationTerminalReason.ArrivalSequenceCompleted, Assert.Single(nav.TerminalOutcomes).Reason);
    }
}
