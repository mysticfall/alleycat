using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Pure via-point chain arithmetic (NAV-001 TR18–19): concatenating per-segment navigation queries into one
/// ordered route polyline through the vias, with junction deduplication and each via's cumulative distance
/// along that polyline, and the station clamp that keeps an unvisited via a binding route point. No engine
/// calls — the caller supplies the segment query, so the ordering, dedup and clamp contracts are unit-testable.
/// </summary>
public static class NavigationViaChain
{
    /// <summary>Consecutive-chain junction tolerance: queries already start or end at the junction point, so
    /// only exact-duplicate removal (within float noise) is needed, never shape simplification.</summary>
    public const float JunctionToleranceMetres = 0.001f;

    /// <summary>
    /// Builds the ordered via chain: start → via 1 → … → via N → destination, where each segment is the
    /// caller's validated query (already ending within its own tolerance at the segment target). Consecutive
    /// duplicate junction points are dropped, and each via's cumulative distance along the concatenated
    /// polyline is recorded. An empty via list yields the plain start → destination chain.
    /// </summary>
    /// <param name="start">The request's navigation start position.</param>
    /// <param name="vias">The ordered via points (world positions; values only).</param>
    /// <param name="destination">The final destination.</param>
    /// <param name="querySegment">Returns one validated segment polyline from one point to the next.</param>
    /// <returns>The concatenated path and each via's distance along it (same order as
    /// <paramref name="vias"/>).</returns>
    public static (Vector3[] Path, float[] ViaDistances) Build(
        Vector3 start,
        IReadOnlyList<Vector3> vias,
        Vector3 destination,
        Func<Vector3, Vector3, Vector3[]> querySegment)
    {
        ArgumentNullException.ThrowIfNull(vias);
        ArgumentNullException.ThrowIfNull(querySegment);
        List<Vector3> path = [start];
        float[] viaDistances = new float[vias.Count];
        float cumulative = 0f;
        Vector3 cursor = start;
        for (int index = 0; index <= vias.Count; index++)
        {
            Vector3 target = index < vias.Count ? vias[index] : destination;
            foreach (Vector3 point in querySegment(cursor, target))
            {
                if (point.X == cursor.X && point.Z == cursor.Z && path.Count > 0)
                {
                    continue;
                }

                cumulative += PlanarDistance(cursor, point);
                path.Add(point);
                cursor = point;
            }

            if (index < vias.Count)
            {
                viaDistances[index] = cumulative;
            }
        }

        return ([.. path], viaDistances);
    }

    /// <summary>
    /// The station clamp for one route decision (TR18: vias are passed in order and never leapfrogged while
    /// unvisited): the section station may not advance past the nearest still-unvisited via's route distance.
    /// Once the actor's realised distance reaches the via (it is passing through it), the clamp releases and
    /// the next decision may station beyond it — the via's pass radius, not this clamp, is the criterion.
    /// </summary>
    /// <param name="stationDistance">The decision's station distance along the route.</param>
    /// <param name="realisedDistance">The actor's realised route distance.</param>
    /// <param name="unvisitedViaDistances">
    /// The route distances of the still-unvisited vias, in order (already advanced past the passed ones).
    /// </param>
    /// <returns>The clamped station distance.</returns>
    public static float ClampStationToBindingVia(float stationDistance, float realisedDistance, IReadOnlyList<float> unvisitedViaDistances)
    {
        ArgumentNullException.ThrowIfNull(unvisitedViaDistances);
        foreach (float viaDistance in unvisitedViaDistances)
        {
            if (viaDistance > realisedDistance + Mathf.Epsilon)
            {
                return MathF.Min(stationDistance, viaDistance);
            }
        }

        return stationDistance;
    }

    private static float PlanarDistance(Vector3 from, Vector3 to)
        => MathF.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Z - from.Z) * (to.Z - from.Z)));
}
