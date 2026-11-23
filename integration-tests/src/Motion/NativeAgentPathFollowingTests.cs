using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Native corner targets advance along the outgoing edge, not through the missing quadrant.</summary>
    [Headless]
    [Fact]
    public Task NativeCornerSubmitsOrderedPathTargets() => CheckNativeCornerAsync(false);

    /// <summary>An active native via leg supplies the conditioning path without a parallel route veto.</summary>
    [Headless]
    [Fact]
    public Task NativeCornerViaSubmitsOrderedPathTargets() => CheckNativeCornerAsync(true);

    private static async Task CheckNativeCornerAsync(bool via)
    {
        await using NavigationFixedStepFixture fixture = await NavigationFixedStepFixture.CreateAsync(NavigationFixedStepFixture.CornerStart, 0);
        MotionBricksNavigation nav = fixture.Navigation;
        Transform3D[][]? targets = null;
        nav.WindowSubmitted += (_, context, target) =>
        {
            targets = target;
            Assert.Equal(fixture.NavigationField<RealisedPoseHistory>("_history").Context().SelectMany(p => p),
                context.SelectMany(p => p));
        };
        Vector3 goal = NavigationFixedStepFixture.CornerGoal, viaPoint = NavigationFixedStepFixture.CornerVia;
        Assert.Equal(NavigationDestinationResult.Accepted, nav.SetDestination(new(Basis.Identity, goal), via ? [viaPoint] : []));
        await fixture.StepAsync();

        Assert.Equal("", nav.Failure);
        Assert.NotNull(targets);
        Assert.Equal(4, targets.Length);
        NavigationRouteSnapshot route = fixture.Route;
        Assert.True(nav.OwnsRoute(route));
        Assert.Equal(nav.GetCurrentNavigationPathIndex(), route.ActivePathIndex);
        Assert.Equal(via ? viaPoint : goal, nav.TargetPosition);
        Assert.True(route.PathPoints.Count >= 3);
        Assert.Equal(NavigationFixedStepFixture.Corner, route.PathPoints[1]);
        Assert.InRange(route.PathPoints[0].DistanceTo(NavigationFixedStepFixture.CornerStart), 0, .005f);
        Assert.Equal("forward", nav.ReviewedRole);
        ReviewedRoleSampler sampler = fixture.NavigationField<ReviewedRoleSampler>("_reviewed");
        Transform3D[][] source = sampler.Targets(nav.ObservationTime, 49.0 / 30, Basis.Identity, Vector3.Zero);
        // The authored corner and native leg endpoint define the outgoing edge independently of the adapter.
        Vector3 corner = NavigationFixedStepFixture.Corner;
        Vector3 outgoingEdge = (nav.TargetPosition - corner).Normalized();
        float distanceToCorner = route.PathPoints[0].DistanceTo(corner);
        float priorStation = distanceToCorner;
        for (int i = 0; i < targets.Length; i++)
        {
            Vector3 world = SomaMotionData.ModelToWorld * targets[i][0].Origin;
            float expectedStation = sampler.ForwardReferenceSpeed * ((49f + i) / 30);
            float actualStation = distanceToCorner + (world - corner).Dot(outgoingEdge);
            Assert.True(actualStation > priorStation);
            Assert.InRange(Math.Abs(actualStation - expectedStation), 0, .00001f);
            priorStation = actualStation;
            Vector3 expected = corner + (outgoingEdge * (expectedStation - distanceToCorner));
            Assert.True(expected.DistanceTo(new(world.X, 0, world.Z)) < .0001f,
                $"Expected {expected}, observed {world}; native path: {string.Join(", ", route.PathPoints)}.");
            Assert.Equal(source[i][0].Origin.Y, targets[i][0].Origin.Y);
            Assert.Equal(source[i][1..], targets[i][1..]);
        }
    }
}
