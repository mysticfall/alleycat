using System.Numerics;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Test-only oracle for the NAV-001 raw-path passage contract. It deliberately does not share any implementation
/// with <c>NavigationTravelProgress</c>, because path-distance progress is not evidence that a waypoint was passed.
/// </summary>
internal sealed class RoutePassageOracle(float planarToleranceMetres = 0.10f)
{
    public const float DefaultPlanarToleranceMetres = 0.10f;

    private readonly float _planarToleranceMetres = planarToleranceMetres;
    private readonly List<RoutePassageSegmentDiagnostic> _segments = [];
    private readonly List<RoutePassageRetirement> _retirements = [];
    private readonly List<RoutePassageReanchor> _reanchors = [];
    private readonly List<RoutePassageReplacementAttempt> _replacementAttempts = [];
    private ActiveRoute? _activeRoute;

    public bool TryActivate(RoutePassageRoute route, Vector3 realisedReanchor)
    {
        if (_activeRoute is not null || !IsValidRoute(route) || !IsFinite(realisedReanchor))
        {
            return false;
        }

        _activeRoute = new ActiveRoute(route);
        _reanchors.Add(new RoutePassageReanchor(
            route.RequestID,
            route.RequestGeneration,
            route.RouteRevision,
            realisedReanchor,
            RoutePassageReanchorReason.Activation));
        return true;
    }

    /// <summary>
    /// Atomically retires the old unpassed remainder and starts a route at the collision-realised actor position.
    /// Invalid candidates leave the current ledger untouched.
    /// </summary>
    public bool TryReplace(RoutePassageRoute replacement, Vector3 realisedReanchor)
    {
        bool accepted = _activeRoute is not null && IsValidRoute(replacement) && IsFinite(realisedReanchor);
        _replacementAttempts.Add(new RoutePassageReplacementAttempt(
            replacement.RequestID,
            replacement.RequestGeneration,
            replacement.RouteRevision,
            realisedReanchor,
            accepted));
        if (!accepted)
        {
            return false;
        }

        ActiveRoute oldRoute = _activeRoute!;
        RoutePassagePoint[] retiredPoints = oldRoute.Route.Points[oldRoute.NextPointIndex..];
        _retirements.Add(new RoutePassageRetirement(
            oldRoute.Route.RequestID,
            oldRoute.Route.RequestGeneration,
            oldRoute.Route.RouteRevision,
            retiredPoints));

        _activeRoute = new ActiveRoute(replacement);
        _reanchors.Add(new RoutePassageReanchor(
            replacement.RequestID,
            replacement.RequestGeneration,
            replacement.RouteRevision,
            realisedReanchor,
            RoutePassageReanchorReason.Replacement));
        return true;
    }

    /// <summary>
    /// Evaluates only the collision-realised swept planar segment. NavigationAgent3D target-index and desired-distance
    /// values are retained as diagnostics but cannot grant ledger credit.
    /// </summary>
    public RoutePassageSegmentDiagnostic ObserveCollisionRealisedSegment(RoutePassageMotion motion)
    {
        ActiveRoute? activeRoute = _activeRoute;
        if (activeRoute is null)
        {
            return RoutePassageSegmentDiagnostic.NoActiveRoute(motion);
        }

        if (!IsValidMotion(motion))
        {
            RoutePassageSegmentDiagnostic invalid = new(
                activeRoute.Route.RequestID,
                activeRoute.Route.RequestGeneration,
                activeRoute.Route.RouteRevision,
                motion,
                IsValidInput: false,
                PointChecks: []);
            _segments.Add(invalid);
            return invalid;
        }

        List<RoutePassagePointCheck> checks = [];
        float previousPassedParameter = 0.0f;
        while (activeRoute.NextPointIndex < activeRoute.Route.Points.Length)
        {
            RoutePassagePoint point = activeRoute.Route.Points[activeRoute.NextPointIndex];
            RoutePassageSweep sweep = EvaluateSweptPlanarPassage(
                motion.CollisionRealisedStart,
                motion.CollisionRealisedEnd,
                point.Position);
            bool passed = sweep.MinimumDistanceMetres <= _planarToleranceMetres
                && sweep.Parameter >= previousPassedParameter;
            checks.Add(new RoutePassagePointCheck(
                point.PointID,
                point.Order,
                sweep.MinimumDistanceMetres,
                sweep.Parameter,
                passed));
            if (!passed)
            {
                break;
            }

            previousPassedParameter = sweep.Parameter;
            activeRoute.NextPointIndex++;
        }

        RoutePassageSegmentDiagnostic diagnostic = new(
            activeRoute.Route.RequestID,
            activeRoute.Route.RequestGeneration,
            activeRoute.Route.RouteRevision,
            motion,
            IsValidInput: true,
            PointChecks: checks);
        _segments.Add(diagnostic);
        return diagnostic;
    }

    public RoutePassageAggregate EvaluateFinalAggregate()
    {
        ActiveRoute? activeRoute = _activeRoute;
        return new RoutePassageAggregate(
            Passed: activeRoute is not null && activeRoute.NextPointIndex == activeRoute.Route.Points.Length,
            RequestID: activeRoute?.Route.RequestID,
            RequestGeneration: activeRoute?.Route.RequestGeneration,
            RouteRevision: activeRoute?.Route.RouteRevision,
            PassedPointCount: activeRoute?.NextPointIndex ?? 0,
            RemainingPoints: activeRoute is null ? [] : activeRoute.Route.Points[activeRoute.NextPointIndex..],
            Segments: _segments,
            Retirements: _retirements,
            Reanchors: _reanchors,
            ReplacementAttempts: _replacementAttempts);
    }

    private bool IsValidRoute(RoutePassageRoute route)
    {
        if (!IsValidTolerance()
            || string.IsNullOrWhiteSpace(route.RequestID)
            || route.RequestGeneration < 0
            || route.RouteRevision < 0
            || route.Points is null
            || route.Points.Length == 0)
        {
            return false;
        }

        HashSet<string> pointIDs = new(StringComparer.Ordinal);
        for (int i = 0; i < route.Points.Length; i++)
        {
            RoutePassagePoint point = route.Points[i];
            if (point.Order != i
                || string.IsNullOrWhiteSpace(point.PointID)
                || !pointIDs.Add(point.PointID)
                || !IsFinite(point.Position))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidMotion(RoutePassageMotion motion)
        => IsFinite(motion.RequestedStart)
            && IsFinite(motion.RequestedEnd)
            && IsFinite(motion.CollisionRealisedStart)
            && IsFinite(motion.CollisionRealisedEnd)
            && float.IsFinite(motion.ReportedPathDesiredDistance);

    private bool IsValidTolerance() => float.IsFinite(_planarToleranceMetres) && _planarToleranceMetres >= 0.0f;

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static RoutePassageSweep EvaluateSweptPlanarPassage(Vector3 segmentStart, Vector3 segmentEnd, Vector3 point)
    {
        Vector2 start = new(segmentStart.X, segmentStart.Z);
        Vector2 end = new(segmentEnd.X, segmentEnd.Z);
        Vector2 target = new(point.X, point.Z);
        Vector2 segment = end - start;
        float segmentLengthSquared = segment.LengthSquared();
        if (segmentLengthSquared <= 0.0f)
        {
            return new RoutePassageSweep(Vector2.Distance(start, target), 0.0f);
        }

        float projection = Math.Clamp(Vector2.Dot(target - start, segment) / segmentLengthSquared, 0.0f, 1.0f);
        return new RoutePassageSweep(Vector2.Distance(start + (segment * projection), target), projection);
    }

    private readonly record struct RoutePassageSweep(float MinimumDistanceMetres, float Parameter);

    private sealed class ActiveRoute(RoutePassageRoute route)
    {
        public RoutePassageRoute Route { get; } = route;

        public int NextPointIndex
        {
            get;
            set;
        }
    }
}

internal sealed record RoutePassageRoute(
    string RequestID,
    long RequestGeneration,
    long RouteRevision,
    RoutePassagePoint[] Points);

internal sealed record RoutePassagePoint(string PointID, int Order, Vector3 Position);

/// <summary>
/// A single movement request paired with the segment that collision resolution actually realised. The requested
/// segment exists solely to make a collision-induced miss observable; passage always uses the realised segment.
/// </summary>
internal sealed record RoutePassageMotion(
    Vector3 RequestedStart,
    Vector3 RequestedEnd,
    Vector3 CollisionRealisedStart,
    Vector3 CollisionRealisedEnd,
    int ReportedNavigationAgentPathIndex,
    float ReportedPathDesiredDistance);

internal sealed record RoutePassagePointCheck(
    string PointID,
    int Order,
    float SweptMinimumDistanceMetres,
    float SweptPlanarParameter,
    bool Passed);

internal sealed record RoutePassageSegmentDiagnostic(
    string? RequestID,
    long? RequestGeneration,
    long? RouteRevision,
    RoutePassageMotion Motion,
    bool IsValidInput,
    IReadOnlyList<RoutePassagePointCheck> PointChecks)
{
    public static RoutePassageSegmentDiagnostic NoActiveRoute(RoutePassageMotion motion)
        => new(null, null, null, motion, IsValidInput: false, PointChecks: []);
}

internal sealed record RoutePassageRetirement(
    string RequestID,
    long RequestGeneration,
    long RouteRevision,
    IReadOnlyList<RoutePassagePoint> UnpassedPoints);

internal enum RoutePassageReanchorReason
{
    Activation,
    Replacement,
}

internal sealed record RoutePassageReanchor(
    string RequestID,
    long RequestGeneration,
    long RouteRevision,
    Vector3 CollisionRealisedPosition,
    RoutePassageReanchorReason Reason);

internal sealed record RoutePassageReplacementAttempt(
    string RequestID,
    long RequestGeneration,
    long RouteRevision,
    Vector3 CollisionRealisedReanchor,
    bool Accepted);

internal sealed record RoutePassageAggregate(
    bool Passed,
    string? RequestID,
    long? RequestGeneration,
    long? RouteRevision,
    int PassedPointCount,
    IReadOnlyList<RoutePassagePoint> RemainingPoints,
    IReadOnlyList<RoutePassageSegmentDiagnostic> Segments,
    IReadOnlyList<RoutePassageRetirement> Retirements,
    IReadOnlyList<RoutePassageReanchor> Reanchors,
    IReadOnlyList<RoutePassageReplacementAttempt> ReplacementAttempts);
