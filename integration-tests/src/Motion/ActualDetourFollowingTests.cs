using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>A native detour and a later outgoing request must both reach their owned arrival endpoints.</summary>
    [Headless]
    [Fact]
    public async Task NativeDetourCompletesAndOutgoingSuccessorRemainsResponsive()
    {
        Vector3 start = new(-1.5f, 0, -.4f);
        Vector3 goal = NavigationFixedStepFixture.CornerGoal;
        Vector3 corner = NavigationFixedStepFixture.Corner;
        Vector3 successor = new(.75f, 0, -3.85f);
        await using NavigationFixedStepFixture fixture = await NavigationFixedStepFixture.CreateAsync(start, -.8f);
        MotionBricksNavigation nav = fixture.Navigation;
        var tickets = new List<long>();
        int released = 0;
        bool outgoingFromCorner = false;
        bool finalNativeLegObserved = false;
        Vector3 previous = start;
        // This response models safe generated travel across the known physical corner, not model quality.
        // Its first and last poses are still the live submitted realised and conditioning endpoints.
        fixture.Provider.Response = (context, target, count) =>
        {
            Vector3 from = SomaMotionData.ModelToWorld * context[3][0].Origin;
            Vector3 to = SomaMotionData.ModelToWorld * target[0][0].Origin;
            Vector3 pivot = new(corner.X, (from.Y + to.Y) / 2, corner.Z);
            return [.. Enumerable.Range(0, count).Select(i =>
            {
                float fraction = i / (float)(count - 1);
                Transform3D[] pose = [.. context[3].Select((bone, joint) => SomaMotionData.Blend(bone, target[0][joint], fraction))];
                if (from.X < 0 && to.Z < -1)
                {
                    float division = from.DistanceTo(pivot) / (from.DistanceTo(pivot) + pivot.DistanceTo(to));
                    Vector3 world = fraction < division ? from.Lerp(pivot, fraction / division) :
                        pivot.Lerp(to, (fraction - division) / (1 - division));
                    pose[0].Origin = SomaMotionData.ModelToWorld.Inverse() * world;
                }
                return pose;
            })];
        };
        nav.WindowSubmitted += (ticket, context, _) =>
        {
            tickets.Add(ticket);
            if (tickets.Count == 1)
            {
                Assert.Contains(fixture.Route.PathPoints, p => p.IsEqualApprox(corner));
            }
            Assert.Equal(fixture.NavigationField<RealisedPoseHistory>("_history").Context().SelectMany(p => p),
                context.SelectMany(p => p));
        };

        static bool OnMap(Vector3 p)
        {
            return (p.X is >= -.005f and <= 1.005f && p.Z is >= -4.005f and <= .005f) ||
                (p.X is >= -3.005f and <= .005f && p.Z is >= -1.005f and <= .005f);
        }

        static bool SweepOnMap(Vector3 from, Vector3 to)
        {
            // Each rectangle is convex. A segment crossing the shared Z boundary must cross in the overlap.
            const float boundary = -1.005f;
            return ((from.Z < boundary) == (to.Z < boundary)) ||
                from.Lerp(to, (boundary - from.Z) / (to.Z - from.Z)).X >= -.005f;
        }
        Assert.False(OnMap(start.Lerp(goal, .5f)));

        async Task ObserveAsync(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                await fixture.StepAsync();
                Vector3 actual = fixture.Actor.GlobalPosition;
                Assert.True(OnMap(actual) && SweepOnMap(previous, actual),
                    $"The realised root left the authored corridor on {previous} → {actual}.");
                previous = actual;
                outgoingFromCorner |= actual.X > .05f && actual.Z < -1.25f;
                if (outgoingFromCorner && nav.CurrentStatus.ActiveRequest?.RequestID == 1)
                {
                    finalNativeLegObserved |= nav.GetCurrentNavigationPathIndex() == nav.GetCurrentNavigationPath().Length - 1;
                }
                if (released < tickets.Count)
                {
                    await fixture.ReleaseAsync(tickets[released++]);
                }
            }
        }

        Assert.Equal(NavigationDestinationResult.Accepted, nav.SetDestination(new(Basis.Identity, goal), []));
        await ObserveAsync(30 * 60);
        Assert.True(outgoingFromCorner);
        Assert.True(finalNativeLegObserved);
        Assert.Equal(NavigationTerminalReason.ArrivalSequenceCompleted, Assert.Single(nav.TerminalOutcomes).Reason);
        Assert.InRange(fixture.Actor.GlobalPosition.DistanceTo(goal), 0, nav.DestinationReachedDistance);
        await ObserveAsync(3 * 60);
        Assert.Equal(NavigationDestinationResult.Accepted, nav.SetDestination(new(Basis.Identity, successor), []));
        // Allow three full 49/30-second synthetic segments, then observe the original three-second tail.
        await ObserveAsync(6 * 60);
        Assert.Equal("settled-idle", nav.Phase);
        await ObserveAsync(3 * 60);
        Assert.Equal(2, nav.TerminalOutcomes.Count);
        Assert.All(nav.TerminalOutcomes, outcome => Assert.Equal(NavigationTerminalReason.ArrivalSequenceCompleted, outcome.Reason));
        Assert.Equal("settled-idle", nav.Phase);
        Assert.InRange(fixture.Actor.GlobalPosition.DistanceTo(successor), 0, nav.DestinationReachedDistance);
    }
}
