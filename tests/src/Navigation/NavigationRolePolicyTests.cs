using AlleyCat.Navigation;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>Approved NAV semantic choices, independent of guide generation and source-window output.</summary>
public sealed class NavigationRolePolicyTests
{
    private static NavigationRolePolicy.Input Input(double forward, double left, double? remaining = null, double facing = 0,
        long generation = 1, long route = 1) => new(forward, left, remaining ?? Math.Sqrt((forward * forward) + (left * left)),
        Math.Sqrt((forward * forward) + (left * left)), facing * Math.PI / 180, 0, 0, false, facing == 0, generation, route);

    /// <summary>Nearby cardinal relocations use their direct source roles.</summary>
    [Fact]
    public void NearbyCardinalDisplacementsRetainFacingAndSelectDirectRoles()
    {
        foreach ((double forward, double left, string? expected) in new[] { (1d, 0d, "forward"), (-1d, 0d, "backward"), (0d, 1d, "left_noncross"), (0d, -1d, "right_noncross") })
        {
            NavigationRolePolicy.Decision decision = NavigationRolePolicy.Select(Input(forward, left), new(), null);
            Assert.Equal(expected, decision.Role);
            Assert.True(decision.Positioning);
        }
    }

    /// <summary>A short mesh edge does not turn a long journey into persistent side travel.</summary>
    [Fact]
    public void WholeRemainingJourneyAndFacingDistinguishForwardTravelFromNearbyMeshEdges()
    {
        foreach (NavigationRolePolicy.Input? input in new[] { Input(0, 1, 8), Input(-8, 0), Input(4, 4), Input(0, 1, facing: 90) })
        {
            NavigationRolePolicy.Decision decision = NavigationRolePolicy.Select(input, new(), null);
            Assert.Equal("forward", decision.Role);
            Assert.False(decision.Positioning);
        }
        Assert.Equal("left_noncross", NavigationRolePolicy.Select(Input(0, 1, 1), new(), null).Role);
    }

    /// <summary>Only short facing-preserving lateral-dominant oblique displacement may cross.</summary>
    [Fact]
    public void CrossingIsRestrictedToBoundedFacingPreservingObliqueRelocation()
    {
        Assert.Equal("left_cross", NavigationRolePolicy.Select(Input(.25, .433), new(), null).Role);
        Assert.Equal("right_cross", NavigationRolePolicy.Select(Input(.25, -.433), new(), null).Role);
        foreach (NavigationRolePolicy.Input? input in new[] { Input(.5, .5), Input(.5, .866), Input(.25, .433, facing: 15), Input(0, .5) })
        {
            Assert.DoesNotContain("_cross", NavigationRolePolicy.Select(input, new(), null).Role);
        }
    }

    /// <summary>Role stability is bounded and never owns a replacement request.</summary>
    [Fact]
    public void BoundedHysteresisDoesNotSurviveReplacementOrRouteChange()
    {
        NavigationRolePolicy.Decision previous = NavigationRolePolicy.Select(Input(0, 1.4), new(), null);
        Assert.True(NavigationRolePolicy.Select(Input(0, 1.6), new(), previous).Positioning);
        Assert.False(NavigationRolePolicy.Select(Input(0, 1.8), new(), previous).Positioning);
        Assert.False(NavigationRolePolicy.Select(Input(0, 1.6, generation: 2), new(), previous).Positioning);
        Assert.False(NavigationRolePolicy.Select(Input(0, 1.6, route: 2), new(), previous).Positioning);
        Assert.Equal("left_noncross", NavigationRolePolicy.Select(Input(.4, .9), new(), previous).Role);
        Assert.Equal("right_noncross", NavigationRolePolicy.Select(Input(0, -1), new(), previous).Role);
        NavigationRolePolicy.Input terminal = Input(0, 0, facing: 90) with
        {
            PositionReached = true
        };
        Assert.Equal("left_turn", NavigationRolePolicy.Select(terminal, new(), previous).Role);
    }

    /// <summary>Final facing does not require a loop or a compulsory turn before a short direct adjustment.</summary>
    [Fact]
    public void ShortCompleteAdjustmentRetainsDisplacementRoleDespiteFinalFacing()
    {
        foreach ((double forward, double left, string expected) in new[]
                 { (.12, 0d, "forward"), (-.12, 0d, "backward"), (0d, .12, "left_noncross"), (0d, -.12, "right_noncross") })
        {
            NavigationRolePolicy.Decision decision = NavigationRolePolicy.Select(Input(forward, left, facing: 137), new(), null);
            Assert.Equal(expected, decision.Role);
            Assert.True(decision.Positioning);
        }
        NavigationRolePolicy.Input marked = Input(-.085682116, .079475045, .116868645, 137.15);
        NavigationRolePolicy.Decision local = NavigationRolePolicy.Select(marked, new(), null);
        Assert.Equal("backward", local.Role);
        Assert.True(local.Positioning);
        Assert.Equal("left_turn", NavigationRolePolicy.Select(marked with
        {
            PositionReached = true
        }, new(), local).Role);
    }

    /// <summary>The configured whole-journey boundary is not expanded by facing hysteresis or a tiny mesh edge.</summary>
    [Fact]
    public void ShortEligibilityUsesBothWholeDistancesAndDoesNotExpandAngularHysteresis()
    {
        var settings = new NavigationRolePolicy.Settings(ShortMoveDistance: .2);
        NavigationRolePolicy.Decision shortMove = NavigationRolePolicy.Select(Input(-.2, 0, facing: 137), settings, null);
        Assert.True(shortMove.Positioning);
        foreach (NavigationRolePolicy.Input input in new[]
                 { Input(-.21, 0, facing: 137), Input(-.12, 0, remaining: 3, facing: 137), Input(-.3, 0, remaining: .12, facing: 137) })
        {
            NavigationRolePolicy.Decision route = NavigationRolePolicy.Select(input, settings, shortMove);
            Assert.Equal("forward", route.Role);
            Assert.False(route.Positioning);
        }
        NavigationRolePolicy.Decision nearby = NavigationRolePolicy.Select(Input(-1, 0, facing: 30), settings, null);
        Assert.True(NavigationRolePolicy.Select(Input(-1, 0, facing: 40), settings, nearby).Positioning);
        Assert.False(NavigationRolePolicy.Select(Input(-1, 0, facing: 46), settings, nearby).Positioning);
        Assert.False(NavigationRolePolicy.Select(Input(-1, 0, facing: 40, generation: 2), settings, nearby).Positioning);
    }
}
