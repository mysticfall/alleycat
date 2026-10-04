using System.Collections.ObjectModel;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>Route-geometry tuning used by NPC sequence planning.</summary>
public sealed record NPCNavigationRouteConfiguration
{
    public static NPCNavigationRouteConfiguration Default { get; } = new();
    public float CornerAnticipationDistance { get; init; } = 0.8f;
    public float ForwardAcceleration { get; init; } = 1.8f;

    /// <summary>Lookahead of one nonterminal route section along the compiled route, in metres (about one walk
    /// cycle plus margin, so quantised walk/arc redirects re-aim at least once per cycle).</summary>
    public float SectionHorizonMetres { get; init; } = 1.6f;

    /// <summary>Remaining route distance at or below which a route with no significant corner ahead is planned as
    /// one terminal plan from a standing (Idle) start, in metres (the arrival families' natural reach).</summary>
    public float StraightTerminalMaxMetres { get; init; } = 8f;

    /// <summary>Remaining route distance at or below which a route with no significant corner ahead is planned as
    /// the terminal approach from a travelling start, in metres: short enough that a straight walk tail plus the
    /// exit lands the arrival within tolerance without stopping.</summary>
    public float TravellingStraightTerminalMetres { get; init; } = 1.8f;

    /// <summary>Distance before a significant corner at which a section ends, in metres, so the next section
    /// redirects through — and clears the outside of — the corner instead of inside it.</summary>
    public float CornerLeadMetres { get; init; } = 0.55f;

    /// <summary>Outward offset applied to the station that precedes a significant corner, in metres: a native arc
    /// of the turn's fixed radius cuts inside the corner vertex, so the actor is guided wide of it to keep the
    /// turn's inside on navigable ground.</summary>
    public float CornerOutsideOffsetMetres { get; init; } = 0.25f;

    /// <summary>Distance past a significant corner at which the through-corner section's station lands on the
    /// outgoing leg, in metres, so the corner is turned while travelling rather than crawled under.</summary>
    public float CornerExitMetres { get; init; } = 0.35f;

    /// <summary>Smallest advance of one section's station beyond the realised route position, in metres.</summary>
    public float MinSectionAdvanceMetres { get; init; } = 0.4f;

    /// <summary>Compiled-route corner turn magnitude at or above which the corner is significant for section
    /// placement, in radians (string-pulled portal jinks stay far below it).</summary>
    public float SignificantCornerRadians { get; init; } = 0.08726646f;

    /// <summary>Most consecutive holding sections (no coherent route sample) before the request ends through a
    /// natural stop.</summary>
    public int MaxHoldingSections { get; init; } = 3;
}

/// <summary>
/// One non-degenerate horizontal segment of a compiled locomotion route.
/// </summary>
public readonly record struct NPCNavigationRouteSegment(
    Vector3 Start,
    Vector3 End,
    Vector3 Direction,
    float StartDistance,
    float Length);

/// <summary>
/// A signed route corner and the arc-length interval over which it should be anticipated.
/// </summary>
public readonly record struct NPCNavigationRouteCorner(
    float Distance,
    float SignedAngle,
    float AnticipationStartDistance,
    float AnticipationEndDistance);

/// <summary>
/// Immutable, allocation-free-at-tick-time geometry compiled from one coherent route snapshot.
/// </summary>
public sealed class NPCNavigationRoutePlan
{
    private const float MinimumSegmentLength = 0.0001f;
    private readonly NPCNavigationRouteSegment[] _segmentValues;
    private readonly NPCNavigationRouteCorner[] _cornerValues;
    private readonly ReadOnlyCollection<NPCNavigationRouteSegment> _segments;
    private readonly ReadOnlyCollection<NPCNavigationRouteCorner> _corners;

    private NPCNavigationRoutePlan(
        NPCNavigationRouteSegment[] segments,
        NPCNavigationRouteCorner[] corners,
        Vector3 endpoint,
        float totalLength,
        float terminalYaw,
        long destinationRequestGeneration,
        long routeRevision)
    {
        _segmentValues = segments;
        _cornerValues = corners;
        _segments = Array.AsReadOnly(segments);
        _corners = Array.AsReadOnly(corners);
        Endpoint = endpoint;
        TotalLength = totalLength;
        TerminalYaw = terminalYaw;
        DestinationRequestGeneration = destinationRequestGeneration;
        RouteRevision = routeRevision;
    }

    /// <summary>Gets compiled non-degenerate route segments.</summary>
    public IReadOnlyList<NPCNavigationRouteSegment> Segments => _segments;

    /// <summary>Gets signed corners and their anticipation intervals.</summary>
    public IReadOnlyList<NPCNavigationRouteCorner> Corners => _corners;

    /// <summary>Gets the final finite horizontal route point.</summary>
    public Vector3 Endpoint
    {
        get;
    }

    /// <summary>Gets total route arc length in metres.</summary>
    public float TotalLength
    {
        get;
    }

    /// <summary>Gets requested terminal world yaw in radians.</summary>
    public float TerminalYaw
    {
        get;
    }

    /// <summary>Gets the full-speed planned braking distance in metres.</summary>
    public long DestinationRequestGeneration
    {
        get;
    }

    /// <summary>Gets the coherent route revision.</summary>
    public long RouteRevision
    {
        get;
    }

    /// <summary>
    /// Compiles a coherent navigation sample, retaining the segment immediately behind the active point for projection.
    /// </summary>
    public static NPCNavigationRoutePlan Compile(
        NavigationRouteSnapshot snapshot,
        float authoredExitDistance,
        NPCNavigationRouteConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Compile(
            snapshot.PathPoints,
            0,
            snapshot.Destination,
            snapshot.DestinationRequestGeneration,
            snapshot.RouteRevision,
            authoredExitDistance,
            configuration);
    }

    /// <summary>
    /// Compiles explicit world-space points. This overload is the pure unit-test and tooling boundary.
    /// </summary>
    public static NPCNavigationRoutePlan Compile(
        IReadOnlyList<Vector3> pathPoints,
        int firstPointIndex,
        Transform3D destination,
        long destinationRequestGeneration,
        long routeRevision,
        float authoredExitDistance,
        NPCNavigationRouteConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(pathPoints);
        configuration ??= NPCNavigationRouteConfiguration.Default;

        var segments = new List<NPCNavigationRouteSegment>(Math.Max(pathPoints.Count - 1, 0));
        int startIndex = Math.Clamp(firstPointIndex, 0, Math.Max(pathPoints.Count - 1, 0));
        float distance = 0.0f;
        Vector3 endpoint = destination.Origin.IsFinite() ? destination.Origin : Vector3.Zero;

        for (int index = startIndex; index + 1 < pathPoints.Count; index++)
        {
            Vector3 start = Horizontal(pathPoints[index]);
            Vector3 end = Horizontal(pathPoints[index + 1]);
            if (!start.IsFinite() || !end.IsFinite())
            {
                continue;
            }

            Vector3 offset = end - start;
            float length = offset.Length();
            if (!float.IsFinite(length) || length <= MinimumSegmentLength)
            {
                continue;
            }

            segments.Add(new NPCNavigationRouteSegment(start, end, offset / length, distance, length));
            distance += length;
            endpoint = end;
        }

        var corners = new List<NPCNavigationRouteCorner>(Math.Max(segments.Count - 1, 0));
        float anticipationDistance = SafePositive(configuration.CornerAnticipationDistance, 0.8f);
        for (int index = 0; index + 1 < segments.Count; index++)
        {
            NPCNavigationRouteSegment incoming = segments[index];
            NPCNavigationRouteSegment outgoing = segments[index + 1];
            float angle = SignedAngle(incoming.Direction, outgoing.Direction);
            float cornerDistance = incoming.StartDistance + incoming.Length;
            float scale = Mathf.Clamp(Mathf.Abs(angle) / (Mathf.Pi / 2.0f), 0.2f, 1.5f);
            float lead = Math.Min(anticipationDistance * scale, incoming.Length);
            float exit = Math.Min(anticipationDistance * 0.25f * scale, outgoing.Length);
            corners.Add(new NPCNavigationRouteCorner(cornerDistance, angle, cornerDistance - lead, cornerDistance + exit));
        }

        _ = authoredExitDistance;
        float terminalYaw = YawOf(destination.Basis);

        return new NPCNavigationRoutePlan(
            [.. segments],
            [.. corners],
            endpoint,
            distance,
            terminalYaw,
            destinationRequestGeneration,
            routeRevision);
    }

    internal RouteProjection Project(Vector3 position, float previousDistance, float searchBacktrack)
    {
        if (!position.IsFinite() || _segments.Count == 0)
        {
            return new RouteProjection(0.0f, Endpoint, Vector3.Forward, 0.0f);
        }

        float minimumDistance = Math.Max(previousDistance - Math.Max(searchBacktrack, 0.0f), 0.0f);
        float bestDistanceSquared = float.PositiveInfinity;
        RouteProjection best = default;
        for (int index = 0; index < _segmentValues.Length; index++)
        {
            NPCNavigationRouteSegment segment = _segmentValues[index];
            float parameter = Mathf.Clamp((position - segment.Start).Dot(segment.Direction), 0.0f, segment.Length);
            float arcDistance = segment.StartDistance + parameter;
            if (arcDistance + MinimumSegmentLength < minimumDistance)
            {
                continue;
            }

            Vector3 point = segment.Start + (segment.Direction * parameter);
            float squared = position.DistanceSquaredTo(point);
            if (squared < bestDistanceSquared)
            {
                bestDistanceSquared = squared;
                best = new RouteProjection(arcDistance, point, segment.Direction, Mathf.Sqrt(squared));
            }
        }

        return float.IsFinite(bestDistanceSquared)
            ? best
            : new RouteProjection(Mathf.Clamp(previousDistance, 0.0f, TotalLength), Endpoint, LastDirection(), 0.0f);
    }

    internal Vector3 Sample(float distance, out Vector3 direction)
    {
        float clamped = Mathf.Clamp(float.IsFinite(distance) ? distance : 0.0f, 0.0f, TotalLength);
        for (int index = 0; index < _segmentValues.Length; index++)
        {
            NPCNavigationRouteSegment segment = _segmentValues[index];
            if (clamped <= segment.StartDistance + segment.Length)
            {
                direction = segment.Direction;
                return segment.Start + (segment.Direction * (clamped - segment.StartDistance));
            }
        }

        direction = LastDirection();
        return Endpoint;
    }

    internal float DesiredRouteYaw(float distance)
    {
        _ = Sample(distance, out Vector3 direction);
        float yaw = YawFromDirection(direction);
        for (int index = 0; index < _cornerValues.Length; index++)
        {
            NPCNavigationRouteCorner corner = _cornerValues[index];
            if (distance < corner.AnticipationStartDistance || distance > corner.AnticipationEndDistance)
            {
                continue;
            }

            float range = Math.Max(corner.AnticipationEndDistance - corner.AnticipationStartDistance, MinimumSegmentLength);
            float blend = Mathf.SmoothStep(0.0f, 1.0f, (distance - corner.AnticipationStartDistance) / range);
            yaw = WrapAngle(yaw + (corner.SignedAngle * blend));
            break;
        }

        return yaw;
    }

    /// <summary>
    /// Resolves signed endpoint errors in the terminal route frame. Positive longitudinal error is short of the endpoint;
    /// positive cross-route error places the actor to the route's right.
    /// </summary>
    internal static float YawFromDirection(Vector3 direction)
        => direction.IsFinite() && new Vector2(direction.X, direction.Z).LengthSquared() > MinimumSegmentLength * MinimumSegmentLength
            ? Mathf.Atan2(-direction.X, -direction.Z)
            : 0.0f;

    internal static float WrapAngle(float angle) => Mathf.Wrap(angle, -Mathf.Pi, Mathf.Pi);

    private Vector3 LastDirection() => _segments.Count == 0 ? Vector3.Forward : _segments[^1].Direction;

    private static Vector3 Horizontal(Vector3 value) => new(value.X, 0.0f, value.Z);

    private static float SignedAngle(Vector3 from, Vector3 to)
        => Mathf.Atan2((from.Z * to.X) - (from.X * to.Z), Mathf.Clamp(from.Dot(to), -1.0f, 1.0f));

    /// <summary>Yaw of a basis's forward (-Z) about +Y, in the same convention as <see cref="YawFromDirection"/>.</summary>
    internal static float YawOf(Basis basis)
        => basis.IsFinite() ? YawFromDirection(basis.Orthonormalized() * Vector3.Forward) : 0.0f;

    private static float SafePositive(float value, float fallback) => float.IsFinite(value) && value > 0.0f ? value : fallback;
}

internal readonly record struct RouteProjection(
    float Distance,
    Vector3 Point,
    Vector3 Direction,
    float CrossTrackDistance);

/// <summary>The leg decision for one route-following replan event (NAV-001 User Requirement 8): a nonterminal
/// section toward a tracking station, or the single terminal approach that owns the final facing, stop and
/// completion.</summary>
/// <param name="Terminal">Whether the terminal approach owns the remainder.</param>
/// <param name="StationDistance">Route arc length of the section's station.</param>
/// <param name="StationYawRadians">Route heading at the station.</param>
/// <param name="StationOutwardOffset">World XZ offset applied to the station point, wide of an upcoming corner.</param>
/// <param name="SignificantCornersAhead">Significant compiled-route corners ahead of the realised distance.</param>
/// <param name="BindingCorner">The first significant corner ahead, when one binds this leg.</param>
internal readonly record struct NPCNavigationLegDecision(
    bool Terminal,
    float StationDistance,
    float StationYawRadians,
    Vector2 StationOutwardOffset,
    int SignificantCornersAhead,
    NPCNavigationRouteCorner? BindingCorner = null);

/// <summary>
/// Pure sectioning of a compiled route from the realised route distance: nothing here touches the actor, the
/// animation tree or the planner. Every significant corner is turned by a nonterminal section (a native arc), so
/// the terminal approach — the only plan allowed to stop, return to Idle and apply final facing — begins when no
/// significant corner remains and the straight remainder is within one terminal plan's reach (long from a
/// standing start, short from a travelling one). Otherwise the next station sits one section horizon ahead,
/// pulled back before the next significant corner and offset to the turn's outside, so the following section
/// redirects through the corner with its inside on navigable ground.
/// </summary>
internal static class NPCNavigationRouteSections
{
    internal static NPCNavigationLegDecision Decide(
        NPCNavigationRoutePlan route,
        float realisedDistance,
        NPCNavigationRouteConfiguration configuration,
        bool travellingStart)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(configuration);
        float remaining = route.TotalLength - realisedDistance;
        float terminalReach = travellingStart
            ? configuration.TravellingStraightTerminalMetres
            : configuration.StraightTerminalMaxMetres;
        int significant = 0;
        NPCNavigationRouteCorner? binding = null;
        foreach (NPCNavigationRouteCorner corner in route.Corners)
        {
            if (corner.Distance > realisedDistance && MathF.Abs(corner.SignedAngle) >= configuration.SignificantCornerRadians)
            {
                significant++;
                binding ??= corner;
            }
        }

        if ((significant == 0 && remaining <= terminalReach) || route.TotalLength <= 0f)
        {
            return new NPCNavigationLegDecision(true, route.TotalLength, route.TerminalYaw, Vector2.Zero, significant);
        }

        float station = MathF.Min(realisedDistance + configuration.SectionHorizonMetres, route.TotalLength);
        if (binding is { } ahead)
        {
            // Stations stop CornerLead before the corner so the redirect arc starts there; once the actor has
            // REACHED that point, the next station lands on the outgoing leg past the corner, so the section
            // turns through the corner instead of crawling forward under it with a near-straight bearing.
            float before = ahead.Distance - configuration.CornerLeadMetres;
            station = realisedDistance >= before - 0.0001f
                ? MathF.Max(station, ahead.Distance + configuration.CornerExitMetres)
                : MathF.Min(station, before);
        }

        station = Mathf.Clamp(station, realisedDistance + configuration.MinSectionAdvanceMetres, route.TotalLength);

        // The station before a significant corner is offset to the turn's outside: a native arc of the turn's
        // fixed radius cuts inside the corner vertex, and the wide station keeps that inside on navigable ground.
        Vector2 outward = Vector2.Zero;
        if (binding is { } turn && station < turn.Distance)
        {
            _ = route.Sample(station, out Vector3 towards);
            outward = OutsideOffset(towards, turn, configuration);
        }

        return new NPCNavigationLegDecision(false, station, route.DesiredRouteYaw(station), outward, significant, binding);
    }

    /// <summary>
    /// The world XZ offset that places a station wide of a significant turn's inside, along the route heading at
    /// the station: the fixed-radius redirect arcs cut inside the corner vertex, so a station on the turn's
    /// outside keeps that inside sweep on navigable ground. Shared by the before-corner stations and the
    /// corner-anchored section fallback (CORNER-07).
    /// </summary>
    internal static Vector2 OutsideOffset(Vector3 towards, NPCNavigationRouteCorner turn, NPCNavigationRouteConfiguration configuration)
    {
        if (configuration.CornerOutsideOffsetMetres <= 0f)
        {
            return Vector2.Zero;
        }

        var heading = new Vector2(towards.X, towards.Z);
        if (heading.LengthSquared() <= 0.000001f)
        {
            return Vector2.Zero;
        }

        var left = new Vector2(heading.Y, -heading.X);
        return -MathF.Sign(turn.SignedAngle) * configuration.CornerOutsideOffsetMetres * left.Normalized();
    }
}
