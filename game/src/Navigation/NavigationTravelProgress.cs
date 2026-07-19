using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Commits monotonic forward arc distance projected onto valid sampled navigation segments.
/// </summary>
internal sealed class NavigationTravelProgress
{
    private const float PointToleranceSquared = 0.000001f;

    private Vector3 _lastPosition;
    private Vector3[] _route = [];
    private int _routeStartIndex;
    private int _lastPathIndex;
    private float _routeHighWater;
    private bool _hasPosition;
    private bool _requiresReanchor;

    /// <summary>
    /// Gets the finite monotonic path distance committed from valid samples.
    /// </summary>
    public float Distance
    {
        get; private set;
    }

    /// <summary>
    /// Anchors the first valid actor sample to its active navigation path without adding progress.
    /// </summary>
    public void Start(Vector3 position, ReadOnlySpan<Vector3> path, int pathIndex)
    {
        Reset();
        if (!position.IsFinite())
        {
            return;
        }

        _lastPosition = position;
        _hasPosition = true;
        int routeStartIndex = ActiveSegmentStart(path, pathIndex);
        if (TryProject(position, path, routeStartIndex, routeStartIndex, out float arcDistance))
        {
            SetRoute(path, pathIndex, routeStartIndex, arcDistance);
        }
        else
        {
            _requiresReanchor = true;
        }
    }

    /// <summary>
    /// Projects actor advancement onto the active route and commits only new forward arc distance.
    /// </summary>
    public float Sample(Vector3 position, ReadOnlySpan<Vector3> path, int pathIndex)
    {
        if (!position.IsFinite())
        {
            return Distance;
        }

        if (!_hasPosition)
        {
            Start(position, path, pathIndex);
            return Distance;
        }

        bool sameRoute = IsSameRoute(path);
        int routeStartIndex = sameRoute ? _routeStartIndex : ActiveSegmentStart(path, pathIndex);
        int searchStartIndex = sameRoute
            ? Math.Max(routeStartIndex, ActiveSegmentStart(path, Math.Min(_lastPathIndex, pathIndex)))
            : routeStartIndex;
        bool hasPreviousProjection = TryProject(
            _lastPosition,
            path,
            routeStartIndex,
            searchStartIndex,
            out float previousArc);
        bool hasCurrentProjection = TryProject(
            position,
            path,
            routeStartIndex,
            searchStartIndex,
            out float currentArc);
        if (!hasPreviousProjection || !hasCurrentProjection)
        {
            _lastPosition = position;
            _requiresReanchor = true;
            return Distance;
        }

        if (_requiresReanchor)
        {
            _lastPosition = position;
            SetRoute(path, pathIndex, routeStartIndex, currentArc);
            _requiresReanchor = false;
            return Distance;
        }

        float advancement;
        if (sameRoute)
        {
            advancement = Mathf.Max(currentArc - _routeHighWater, 0.0f);
            _routeHighWater = Mathf.Max(_routeHighWater, currentArc);
            _lastPathIndex = Math.Clamp(pathIndex, 0, path.Length);
        }
        else
        {
            advancement = Mathf.Max(currentArc - previousArc, 0.0f);
            SetRoute(path, pathIndex, routeStartIndex, Mathf.Max(previousArc, currentArc));
        }

        _lastPosition = position;
        Commit(advancement);
        return Distance;
    }

    /// <summary>
    /// Clears progress for a new accepted request.
    /// </summary>
    public void Reset()
    {
        _lastPosition = Vector3.Zero;
        _route = [];
        _routeStartIndex = 0;
        _lastPathIndex = 0;
        _routeHighWater = 0.0f;
        _hasPosition = false;
        _requiresReanchor = false;
        Distance = 0.0f;
    }

    private void SetRoute(
        ReadOnlySpan<Vector3> path,
        int pathIndex,
        int routeStartIndex,
        float highWater)
    {
        _route = path.ToArray();
        _routeStartIndex = routeStartIndex;
        _lastPathIndex = Math.Clamp(pathIndex, 0, path.Length);
        _routeHighWater = NonNegativeFinite(highWater);
        _requiresReanchor = false;
    }

    private bool IsSameRoute(ReadOnlySpan<Vector3> path)
    {
        if (_route.Length != path.Length)
        {
            return false;
        }

        for (int index = 0; index < path.Length; index++)
        {
            Vector3 previous = _route[index];
            Vector3 current = path[index];
            if (previous.IsFinite() != current.IsFinite())
            {
                return false;
            }

            if (previous.IsFinite() && previous.DistanceSquaredTo(current) > PointToleranceSquared)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryProject(
        Vector3 position,
        ReadOnlySpan<Vector3> path,
        int routeStartIndex,
        int searchStartIndex,
        out float arcDistance)
    {
        arcDistance = 0.0f;
        if (!position.IsFinite() || path.Length < 2)
        {
            return false;
        }

        routeStartIndex = Math.Clamp(routeStartIndex, 0, path.Length - 2);
        searchStartIndex = Math.Clamp(searchStartIndex, routeStartIndex, path.Length - 2);
        float traversedDistance = 0.0f;
        float closestDistanceSquared = float.MaxValue;
        bool foundSegment = false;

        for (int index = routeStartIndex; index < searchStartIndex; index++)
        {
            if (!TryGetSegmentLength(path[index], path[index + 1], out float segmentLength))
            {
                continue;
            }

            traversedDistance += segmentLength;
            if (!float.IsFinite(traversedDistance))
            {
                return false;
            }
        }

        for (int index = searchStartIndex; index < path.Length - 1; index++)
        {
            Vector3 start = path[index];
            Vector3 end = path[index + 1];
            if (!TryGetSegmentLength(start, end, out float segmentLength))
            {
                continue;
            }

            Vector3 segment = end - start;
            float segmentLengthSquared = segment.LengthSquared();
            float ratio = Mathf.Clamp((position - start).Dot(segment) / segmentLengthSquared, 0.0f, 1.0f);
            Vector3 projected = start + (segment * ratio);
            float distanceSquared = position.DistanceSquaredTo(projected);
            if (float.IsFinite(distanceSquared) && distanceSquared < closestDistanceSquared)
            {
                closestDistanceSquared = distanceSquared;
                arcDistance = traversedDistance + (segmentLength * ratio);
                foundSegment = true;
            }

            traversedDistance += segmentLength;
            if (!float.IsFinite(traversedDistance))
            {
                return false;
            }
        }

        arcDistance = NonNegativeFinite(arcDistance);
        return foundSegment;
    }

    private static bool TryGetSegmentLength(Vector3 start, Vector3 end, out float segmentLength)
    {
        segmentLength = 0.0f;
        if (!start.IsFinite() || !end.IsFinite())
        {
            return false;
        }

        float lengthSquared = (end - start).LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= PointToleranceSquared)
        {
            return false;
        }

        segmentLength = Mathf.Sqrt(lengthSquared);
        return float.IsFinite(segmentLength);
    }

    private void Commit(float advancement)
    {
        float accumulated = Distance + NonNegativeFinite(advancement);
        Distance = float.IsFinite(accumulated) ? accumulated : float.MaxValue;
    }

    private static int ActiveSegmentStart(ReadOnlySpan<Vector3> path, int pathIndex)
        => path.Length < 2 ? 0 : Math.Clamp(pathIndex - 1, 0, path.Length - 2);

    private static float NonNegativeFinite(float value) => float.IsFinite(value) ? Mathf.Max(value, 0.0f) : 0.0f;
}
