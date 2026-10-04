using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Route-section decisions of the NAV-001 corner-route runtime (User Requirement 8 follow-through): where the next
/// nonterminal section's station lies on a compiled route, and when the terminal approach owns the remainder. Pure
/// geometry only; no actor, animation or planner involvement.
/// </summary>
public sealed class NPCNavigationRouteSectionsTests
{
    private static readonly Transform3D _destination = new(Basis.Identity, new Vector3(0f, 0f, 4f));

    private static NPCNavigationRoutePlan StraightRoute()
        => NPCNavigationRoutePlan.Compile(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 4f)], 0, _destination, 1, 1, 0f);

    private static NPCNavigationRoutePlan BentRoute()
        => NPCNavigationRoutePlan.Compile(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 3f), new Vector3(3f, 0f, 3f)], 0,
            new Transform3D(Basis.Identity, new Vector3(3f, 0f, 3f)), 1, 1, 0f,
            new NPCNavigationRouteConfiguration { SignificantCornerRadians = 0.08726646f });

    /// <summary>A straight remainder within the terminal reach is one terminal plan.</summary>
    [Fact]
    public void Decide_StraightRemainderWithinTerminalReach_IsTerminal()
    {
        NPCNavigationRoutePlan route = StraightRoute();

        NPCNavigationLegDecision decision = NPCNavigationRouteSections.Decide(route, 1f, NPCNavigationRouteConfiguration.Default, travellingStart: false);

        Assert.True(decision.Terminal);
        Assert.Equal(route.TotalLength, decision.StationDistance);
        Assert.Equal(0, decision.SignificantCornersAhead);
    }

    /// <summary>A straight remainder beyond the terminal reach sections forward at the configured horizon.</summary>
    [Fact]
    public void Decide_StraightRemainderBeyondStraightMax_SectionsWithHorizonStation()
    {
        var configuration = new NPCNavigationRouteConfiguration
        {
            StraightTerminalMaxMetres = 1f,
            SectionHorizonMetres = 1.6f,
            MinSectionAdvanceMetres = 0.4f,
        };
        var route = NPCNavigationRoutePlan.Compile(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 8f)], 0,
            new Transform3D(Basis.Identity, new Vector3(0f, 0f, 8f)), 1, 1, 0f);

        NPCNavigationLegDecision decision = NPCNavigationRouteSections.Decide(route, 1f, configuration, travellingStart: true);

        Assert.False(decision.Terminal);
        Assert.Equal(2.6f, decision.StationDistance, 5);
        Assert.Equal(0, decision.SignificantCornersAhead);
    }

    /// <summary>A significant corner ahead pulls the station back before it so the next section redirects through it.</summary>
    [Fact]
    public void Decide_SignificantCornerAhead_PlacesStationBeforeIt()
    {
        NPCNavigationRoutePlan route = BentRoute();
        var configuration = new NPCNavigationRouteConfiguration
        {
            SectionHorizonMetres = 1.6f,
            CornerLeadMetres = 0.55f,
            MinSectionAdvanceMetres = 0.4f,
            SignificantCornerRadians = 0.08726646f,
        };

        NPCNavigationLegDecision decision = NPCNavigationRouteSections.Decide(route, 2f, configuration, travellingStart: true);

        Assert.False(decision.Terminal);
        Assert.Equal(2.45f, decision.StationDistance, 5);
        Assert.Equal(1, decision.SignificantCornersAhead);
        Assert.NotEqual(Vector2.Zero, decision.StationOutwardOffset);
    }

    /// <summary>A straight remainder is terminal from a standing start far beyond the travelling threshold.</summary>
    [Fact]
    public void Decide_StraightRemainder_TerminalReachDependsOnStart()
    {
        NPCNavigationRoutePlan route = StraightRoute();

        NPCNavigationLegDecision standing = NPCNavigationRouteSections.Decide(route, 1f, NPCNavigationRouteConfiguration.Default, travellingStart: false);
        NPCNavigationLegDecision travelling = NPCNavigationRouteSections.Decide(route, 1f, NPCNavigationRouteConfiguration.Default, travellingStart: true);

        Assert.True(standing.Terminal);
        Assert.False(travelling.Terminal);
    }

    /// <summary>A significant corner ahead keeps the route sectional: bends belong to nonterminal sections, never
    /// to the terminal plan that owns the stop, the Idle return and the final facing.</summary>
    [Fact]
    public void Decide_OneSignificantCornerAhead_StaysSectional()
    {
        var route = NPCNavigationRoutePlan.Compile(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 3f), new Vector3(1f, 0f, 3f)], 0,
            new Transform3D(Basis.Identity, new Vector3(1f, 0f, 3f)), 1, 1, 0f,
            new NPCNavigationRouteConfiguration { SignificantCornerRadians = 0.08726646f });

        // Only 1.4 m remain with the 90° corner still ahead: still a section, so the bend is turned while
        // travelling and the terminal approach is straight-only.
        NPCNavigationLegDecision decision = NPCNavigationRouteSections.Decide(route, 2.6f, NPCNavigationRouteConfiguration.Default, travellingStart: true);

        Assert.False(decision.Terminal);
        Assert.Equal(1, decision.SignificantCornersAhead);
    }

    /// <summary>Two significant corners ahead keep the route sectional no matter how short the remainder.</summary>
    [Fact]
    public void Decide_TwoSignificantCorners_NeverTerminalInSectionHorizon()
    {
        var route = NPCNavigationRoutePlan.Compile(
            [new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 2f), new Vector3(2f, 0f, 2f), new Vector3(2f, 0f, 4f)], 0,
            new Transform3D(Basis.Identity, new Vector3(2f, 0f, 4f)), 1, 1, 0f,
            new NPCNavigationRouteConfiguration { SignificantCornerRadians = 0.08726646f });

        NPCNavigationLegDecision decision = NPCNavigationRouteSections.Decide(route, 1f, NPCNavigationRouteConfiguration.Default, travellingStart: false);

        Assert.False(decision.Terminal);
        Assert.Equal(2, decision.SignificantCornersAhead);
    }

    /// <summary>A significant corner immediately ahead still leaves the station at least the minimum advance ahead.</summary>
    [Fact]
    public void Decide_StationNeverBehindMinimumAdvance()
    {
        NPCNavigationRoutePlan route = BentRoute();
        var configuration = new NPCNavigationRouteConfiguration
        {
            SectionHorizonMetres = 1.6f,
            CornerLeadMetres = 0.55f,
            MinSectionAdvanceMetres = 0.4f,
            SignificantCornerRadians = 0.08726646f,
        };

        NPCNavigationLegDecision decision = NPCNavigationRouteSections.Decide(route, 2.95f, configuration, travellingStart: true);

        Assert.False(decision.Terminal);
        Assert.InRange(decision.StationDistance, 3f, route.TotalLength);
    }
}
