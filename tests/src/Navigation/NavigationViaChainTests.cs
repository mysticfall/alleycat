using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Pure contracts of <see cref="NavigationViaChain"/> (NAV-001 TR18): the via chain concatenates per-segment
/// queries in order with junction deduplication and correct cumulative via distances; an empty via list is the
/// plain start→destination chain; and the station clamp keeps an unvisited via a binding route point while
/// releasing once the actor's realised distance passes through it. All inputs and expectations are independent
/// hand-computed values, never the production's own outputs.
/// </summary>
public sealed class NavigationViaChainTests
{
    private static Vector3[] StraightQuery(Vector3 from, Vector3 to)
        => [from, to];

    private static Vector3[] PortalQuery(Vector3 from, Vector3 to)
        // A string-pulled query that bends around (1, 1) whenever it lies between the endpoints.
        => from.Z < 1f && to.Z > 1f && from.X < 1f
            ? [from, new Vector3(1f, 0f, 1f), to]
            : [from, to];

    /// <summary>Straight segments concatenate in order and the shared junction points collapse.</summary>
    [Fact]
    public void Chain_ConcatenatesSegmentsInOrderAndDedupsJunctions()
    {
        (Vector3[] path, float[] viaDistances) = NavigationViaChain.Build(
            new Vector3(0f, 0f, 0f),
            [new Vector3(0f, 0f, -2f), new Vector3(2f, 0f, -2f)],
            new Vector3(2f, 0f, 0f),
            StraightQuery);

        // Each straight query returns [from, to]; the shared junction points collapse: start + 3 legs.
        Assert.Equal(
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 0f, -2f),
                new Vector3(2f, 0f, -2f),
                new Vector3(2f, 0f, 0f),
            ],
            path);
        Assert.Equal(2, viaDistances.Length);
        Assert.Equal(2f, viaDistances[0], 5);
        Assert.Equal(4f, viaDistances[1], 5);
    }

    /// <summary>Intermediate portal points accumulate into the via distances.</summary>
    [Fact]
    public void Chain_AccumulatesThroughIntermediatePortalPoints()
    {
        (Vector3[] path, float[] viaDistances) = NavigationViaChain.Build(
            new Vector3(0f, 0f, 0f),
            [new Vector3(0f, 0f, 2f)],
            new Vector3(0f, 0f, 4f),
            PortalQuery);

        // The first segment bends through (1, 1): start → (1,1) → via → destination.
        Assert.Equal(
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 1f),
            new Vector3(0f, 0f, 2f),
            new Vector3(0f, 0f, 4f),
        ], path);
        // via distance = |(0,0)→(1,1)| + |(1,1)→(0,2)| = 2·√2.
        Assert.Equal(2f * MathF.Sqrt(2f), viaDistances[0], 5);
    }

    /// <summary>An empty via list yields exactly the plain segment chain.</summary>
    [Fact]
    public void Chain_EmptyViaList_EqualsThePlainSegmentChain()
    {
        (Vector3[] withVias, float[] _) = NavigationViaChain.Build(
            new Vector3(1f, 0f, 1f), [], new Vector3(4f, 0f, 5f), PortalQuery);

        Assert.Equal([new Vector3(1f, 0f, 1f), new Vector3(4f, 0f, 5f)], withVias);
    }

    /// <summary>The station may not leapfrog an unvisited via.</summary>
    [Fact]
    public void Clamp_KeepsTheStationAtTheUnvisitedVia()
    {
        // The actor is 0.8 m along the route; the only unvisited via sits at 2.0 m; the planner wanted 3.5 m.
        float clamped = NavigationViaChain.ClampStationToBindingVia(3.5f, 0.8f, [2.0f]);

        Assert.Equal(2.0f, clamped, 5);
    }

    /// <summary>The clamp releases once the actor reaches the via.</summary>
    [Fact]
    public void Clamp_ReleasesOnceTheActorReachesTheVia()
    {
        // The realised distance has reached (passed through) the via: the clamp must not hold the station back.
        float clamped = NavigationViaChain.ClampStationToBindingVia(3.5f, 2.0f, [2.0f]);

        Assert.Equal(3.5f, clamped, 5);
    }

    /// <summary>Only the nearest unvisited via ahead binds.</summary>
    [Fact]
    public void Clamp_BindsOnlyTheNearestUnvisitedViaInOrder()
    {
        // Two unvisited vias at 2.0 m and 5.0 m; the actor at 3.0 m has passed the first (it is no longer
        // unvisited in practice — but even if still listed, the clamp binds at the next one ahead).
        float clamped = NavigationViaChain.ClampStationToBindingVia(6.0f, 3.0f, [2.0f, 5.0f]);

        Assert.Equal(5.0f, clamped, 5);
    }

    /// <summary>No unvisited vias leaves the station unchanged.</summary>
    [Fact]
    public void Clamp_NoUnvisitedViasLeavesTheStationUnchanged() => Assert.Equal(4.2f, NavigationViaChain.ClampStationToBindingVia(4.2f, 1.0f, []), 5);
}
