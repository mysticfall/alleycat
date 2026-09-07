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
    public float StoppingMargin
    {
        get; init;
    }
    public float EndpointCorrectionDistance { get; init; } = 0.65f;
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
        float brakingDistance,
        bool usesShortEndpointCorrection,
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
        BrakingDistance = brakingDistance;
        UsesShortEndpointCorrection = usesShortEndpointCorrection;
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
    public float BrakingDistance
    {
        get;
    }

    /// <summary>Gets whether this route permits side-step endpoint correction.</summary>
    public bool UsesShortEndpointCorrection
    {
        get;
    }

    /// <summary>Gets the accepted destination generation from the coherent sample.</summary>
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

        float acceleration = SafePositive(configuration.ForwardAcceleration, 1.8f);
        const float forwardSpeed = 0.93f;
        float continuousBrakingDistance = forwardSpeed * forwardSpeed / (2.0f * acceleration);
        float authoredReleaseDistance = SafeNonNegative(authoredExitDistance, 0.0f);
        float brakingDistance = (authoredReleaseDistance > 0.0f
            ? authoredReleaseDistance
            : continuousBrakingDistance)
            + SafeNonNegative(configuration.StoppingMargin, 0.0f);
        float terminalYaw = ExtractYaw(destination.Basis);

        return new NPCNavigationRoutePlan(
            [.. segments],
            [.. corners],
            endpoint,
            distance,
            terminalYaw,
            brakingDistance,
            distance <= SafePositive(configuration.EndpointCorrectionDistance, 0.65f),
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
    internal Vector2 TerminalError(Vector3 position)
    {
        Vector3 direction = LastDirection();
        Vector3 actorOffset = Horizontal(position) - Endpoint;
        float longitudinal = -actorOffset.Dot(direction);
        float crossRoute = (direction.X * actorOffset.Z) - (direction.Z * actorOffset.X);
        return new Vector2(crossRoute, longitudinal);
    }

    /// <summary>Gets the final route-segment direction in world space.</summary>
    internal Vector3 TerminalDirection => LastDirection();

    internal float TargetSpeed(float remainingDistance, float maximumSpeed, float acceleration)
    {
        if (remainingDistance <= 0.0f || maximumSpeed <= 0.0f || acceleration <= 0.0f)
        {
            return 0.0f;
        }

        float continuousBrakingDistance = maximumSpeed * maximumSpeed / (2.0f * acceleration);
        float available = Math.Max(
            remainingDistance - Math.Max(BrakingDistance - continuousBrakingDistance, 0.0f),
            0.0f);
        return Math.Min(maximumSpeed, Mathf.Sqrt(2.0f * acceleration * available));
    }

    internal static float YawFromDirection(Vector3 direction)
        => direction.IsFinite() && new Vector2(direction.X, direction.Z).LengthSquared() > MinimumSegmentLength * MinimumSegmentLength
            ? Mathf.Atan2(-direction.X, -direction.Z)
            : 0.0f;

    internal static float WrapAngle(float angle) => Mathf.Wrap(angle, -Mathf.Pi, Mathf.Pi);

    private Vector3 LastDirection() => _segments.Count == 0 ? Vector3.Forward : _segments[^1].Direction;

    private static Vector3 Horizontal(Vector3 value) => new(value.X, 0.0f, value.Z);

    private static float SignedAngle(Vector3 from, Vector3 to)
        => Mathf.Atan2((from.Z * to.X) - (from.X * to.Z), Mathf.Clamp(from.Dot(to), -1.0f, 1.0f));

    private static float ExtractYaw(Basis basis)
        => basis.IsFinite() ? YawFromDirection(basis.Orthonormalized() * Vector3.Forward) : 0.0f;

    private static float SafePositive(float value, float fallback) => float.IsFinite(value) && value > 0.0f ? value : fallback;

    private static float SafeNonNegative(float value, float fallback) => float.IsFinite(value) && value >= 0.0f ? value : fallback;
}

internal readonly record struct RouteProjection(
    float Distance,
    Vector3 Point,
    Vector3 Direction,
    float CrossTrackDistance);
