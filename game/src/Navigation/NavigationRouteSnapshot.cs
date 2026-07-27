using System.Collections.ObjectModel;
using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Immutable route data captured from one coherent navigation poll.
/// </summary>
public sealed class NavigationRouteSnapshot
{
    private readonly ReadOnlyCollection<Vector3> _pathPoints;

    internal NavigationRouteSnapshot(
        ReadOnlySpan<Vector3> pathPoints,
        int activePathIndex,
        Vector3 nextPathPoint,
        Transform3D destination,
        long destinationRequestGeneration,
        long routeRevision,
        bool usedAcceptedPathFallback,
        bool wasReplanned)
    {
        _pathPoints = Array.AsReadOnly(pathPoints.ToArray());
        ActivePathIndex = activePathIndex;
        NextPathPoint = nextPathPoint;
        Destination = destination;
        DestinationRequestGeneration = destinationRequestGeneration;
        RouteRevision = routeRevision;
        UsedAcceptedPathFallback = usedAcceptedPathFallback;
        WasReplanned = wasReplanned;
    }

    /// <summary>
    /// Gets the copied world-space route geometry used by the poll.
    /// </summary>
    public IReadOnlyList<Vector3> PathPoints => _pathPoints;

    /// <summary>
    /// Gets the active route-point index used by the poll.
    /// </summary>
    public int ActivePathIndex
    {
        get;
    }

    /// <summary>
    /// Gets the next world-space route point used by the poll.
    /// </summary>
    public Vector3 NextPathPoint
    {
        get;
    }

    /// <summary>
    /// Gets the complete accepted destination transform for this request generation.
    /// </summary>
    public Transform3D Destination
    {
        get;
    }

    /// <summary>
    /// Gets the monotonically increasing accepted-destination generation.
    /// </summary>
    public long DestinationRequestGeneration
    {
        get;
    }

    /// <summary>
    /// Gets the monotonically increasing route revision.
    /// </summary>
    public long RouteRevision
    {
        get;
    }

    /// <summary>
    /// Gets whether this poll used the synchronously accepted path because the agent path was unavailable.
    /// </summary>
    public bool UsedAcceptedPathFallback
    {
        get;
    }

    /// <summary>
    /// Gets whether this poll observed changed route geometry within the active destination generation.
    /// </summary>
    public bool WasReplanned
    {
        get;
    }
}
