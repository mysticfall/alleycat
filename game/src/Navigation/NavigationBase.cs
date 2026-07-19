using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Godot-backed navigation policy that samples paths without mutating the navigated actor.
/// </summary>
[GlobalClass]
public abstract partial class NavigationBase : NavigationAgent3D, INavigation
{
    private Transform3D _destination = Transform3D.Identity;
    private NavigationMotionIntent _intent = CompletedIntent(Vector3.Zero);
    private Vector3 _initialFacing = Vector3.Forward;
    private float _initialPathLength;
    private readonly NavigationTravelProgress _travelProgress = new();
    private bool _hasInitialSample;
    private Vector3[] _acceptedPath = [];
    private int _acceptedPathIndex;

    /// <inheritdoc/>
    public bool HasDestination
    {
        get; private set;
    }

    /// <inheritdoc/>
    public Transform3D Destination => _destination;

    /// <inheritdoc/>
    public bool IsNavigationRunning => HasDestination && !_intent.IsComplete;

    /// <inheritdoc/>
    bool INavigation.IsNavigationFinished => !HasDestination || _intent.IsComplete;

    /// <inheritdoc/>
    [ExportGroup("Steering Distances")]
    [Export]
    public float DestinationReachedDistance
    {
        get => TargetDesiredDistance;
        set => TargetDesiredDistance = value;
    }

    /// <inheritdoc/>
    [Export]
    public float InitialFacingRampDistance { get; set; } = 1.0f;

    /// <inheritdoc/>
    [Export]
    public float FacingRampDistance { get; set; } = 1.0f;

    /// <inheritdoc/>
    [Export]
    public float ShortMoveDistance { get; set; } = 0.5f;

    /// <inheritdoc/>
    [ExportGroup("Facing")]
    [Export(PropertyHint.Range, "0,180,0.1,or_greater")]
    public float FacingToleranceDegrees { get; set; } = 3.0f;

    /// <inheritdoc/>
    [ExportGroup("Avoidance Facade")]
    [Export]
    public float AvoidanceRadius
    {
        get => Radius;
        set => Radius = value;
    }

    /// <inheritdoc/>
    [Export]
    public float AvoidanceHeight
    {
        get => Height;
        set => Height = value;
    }

    /// <inheritdoc/>
    [Export]
    public float AvoidanceMaxSpeed
    {
        get => MaxSpeed;
        set => MaxSpeed = value;
    }

    /// <inheritdoc/>
    public Vector3[] CurrentPath
    {
        get
        {
            Vector3[] agentPath = GetCurrentNavigationPath();
            return agentPath.Length > 0 ? agentPath : [.. _acceptedPath];
        }
    }

    /// <inheritdoc/>
    public int CurrentPathIndex
        => GetCurrentNavigationPath().Length > 0 ? GetCurrentNavigationPathIndex() : _acceptedPathIndex;

    /// <summary>
    /// Gets whether the navigation map has completed at least one synchronisation.
    /// </summary>
    protected bool IsNavigationMapReady => NavigationServer3D.MapGetIterationId(GetNavigationMap()) != 0;

    /// <summary>
    /// Gets whether the latest poll used the synchronously accepted path while the agent path was unavailable.
    /// </summary>
    internal bool UsedAcceptedPathFallbackForLastPoll
    {
        get; private set;
    }

    /// <summary>
    /// Gets the path index used by the latest coherent poll sample.
    /// </summary>
    internal int LastSampledPathIndex
    {
        get; private set;
    }

    /// <inheritdoc/>
    public NavigationDestinationResult SetDestination(Transform3D destination)
    {
        if (!destination.IsFinite())
        {
            return NavigationDestinationResult.Invalid;
        }

        if (!IsNavigationMapReady)
        {
            return NavigationDestinationResult.NotReady;
        }

        if (!CanReach(destination.Origin, out Vector3[] acceptedPath))
        {
            return NavigationDestinationResult.Unreachable;
        }

        TargetPosition = destination.Origin;
        _destination = destination;
        HasDestination = true;
        _hasInitialSample = false;
        _initialFacing = Vector3.Forward;
        _initialPathLength = 0.0f;
        _travelProgress.Reset();
        UsedAcceptedPathFallbackForLastPoll = false;
        LastSampledPathIndex = 0;
        _acceptedPath = acceptedPath;
        _acceptedPathIndex = 0;
        _intent = PendingIntent(destination.Origin);
        OnDestinationAccepted(destination);
        return NavigationDestinationResult.Accepted;
    }

    /// <inheritdoc/>
    public void ClearDestination()
    {
        HasDestination = false;
        _hasInitialSample = false;
        _travelProgress.Reset();
        UsedAcceptedPathFallbackForLastPoll = false;
        LastSampledPathIndex = 0;
        _acceptedPath = [];
        _acceptedPathIndex = 0;
        _intent = CompletedIntent(_destination.Origin);
    }

    /// <inheritdoc/>
    public NavigationMotionIntent Poll(Transform3D actorTransform)
    {
        if (!HasDestination || !IsNavigationMapReady)
        {
            return _intent;
        }

        // This is intentionally the sole advancing path call in a poll.
        Vector3 nextPathPosition = GetNextPathPosition();
        Vector3[] path = GetCurrentNavigationPath();
        int pathIndex = GetCurrentNavigationPathIndex();
        AdjustPathSample(ref nextPathPosition, ref path, ref pathIndex);
        UsedAcceptedPathFallbackForLastPoll = false;
        if (path.Length == 0 && _acceptedPath.Length > 0)
        {
            UsedAcceptedPathFallbackForLastPoll = true;
            path = _acceptedPath;
            _acceptedPathIndex = AdvanceAcceptedPathIndex(actorTransform.Origin);
            pathIndex = _acceptedPathIndex;
            nextPathPosition = path[pathIndex];
        }
        LastSampledPathIndex = pathIndex;
        float destinationTolerance = Mathf.Max(DestinationReachedDistance, 0.0f);
        bool atDestination = actorTransform.Origin.IsFinite()
            && actorTransform.Origin.DistanceSquaredTo(_destination.Origin) <= destinationTolerance * destinationTolerance;
        if (!nextPathPosition.IsFinite() || (path.Length == 0 && !atDestination))
        {
            return _intent;
        }

        Vector3 sampleFacing = ExtractHorizontalFacing(actorTransform.Basis, _initialFacing);

        NavigationMotionIntent sampledIntent = NavigationSteering.Calculate(
            actorTransform,
            _destination,
            nextPathPosition,
            path,
            pathIndex,
            _hasInitialSample ? _initialFacing : sampleFacing,
            _hasInitialSample ? _initialPathLength : float.MaxValue,
            _hasInitialSample ? _travelProgress.Sample(actorTransform.Origin, path, pathIndex) : 0.0f,
            InitialFacingRampDistance,
            FacingRampDistance,
            ShortMoveDistance,
            DestinationReachedDistance,
            PathDesiredDistance,
            Mathf.DegToRad(Mathf.Max(FacingToleranceDegrees, 0.0f)));

        if (!_hasInitialSample
            && actorTransform.IsFinite()
            && nextPathPosition.IsFinite()
            && float.IsFinite(sampledIntent.RemainingPathDistance))
        {
            _initialFacing = sampleFacing;
            _initialPathLength = sampledIntent.RemainingPathDistance;
            _travelProgress.Start(actorTransform.Origin, path, pathIndex);
            _hasInitialSample = true;
            sampledIntent = NavigationSteering.Calculate(
                actorTransform,
                _destination,
                nextPathPosition,
                path,
                pathIndex,
                _initialFacing,
                _initialPathLength,
                _travelProgress.Distance,
                InitialFacingRampDistance,
                FacingRampDistance,
                ShortMoveDistance,
                DestinationReachedDistance,
                PathDesiredDistance,
                Mathf.DegToRad(Mathf.Max(FacingToleranceDegrees, 0.0f)));
        }

        _intent = sampledIntent;
        return _intent;
    }

    /// <summary>
    /// Allows concrete implementations to react to accepted destination intent without moving an actor.
    /// </summary>
    protected virtual void OnDestinationAccepted(Transform3D destination)
    {
    }

    /// <summary>
    /// Allows specialised path providers to replace the sampled route after the sole advancing agent call.
    /// </summary>
    protected virtual void AdjustPathSample(ref Vector3 nextPathPosition, ref Vector3[] path, ref int pathIndex)
    {
    }

    /// <summary>
    /// Gets the authoritative world-space start position used for synchronous destination validation.
    /// </summary>
    protected virtual Vector3 GetNavigationStartPosition()
    {
        Node? ancestor = GetParent();
        while (ancestor is not null and not Node3D)
        {
            ancestor = ancestor.GetParent();
        }

        if (ancestor is not Node3D node)
        {
            return Vector3.Zero;
        }

        Transform3D worldTransform = node.Transform;
        Node3D? parent = node.GetParentOrNull<Node3D>();
        while (parent is not null && !node.TopLevel)
        {
            worldTransform = parent.Transform * worldTransform;
            node = parent;
            parent = parent.GetParentOrNull<Node3D>();
        }

        return worldTransform.Origin.IsFinite() ? worldTransform.Origin : Vector3.Zero;
    }

    private static Vector3 ExtractHorizontalFacing(Basis basis, Vector3 fallback)
    {
        Vector3 facing = basis.IsFinite() ? new Vector3(-basis.Z.X, 0.0f, -basis.Z.Z) : fallback;
        return facing.IsFinite() && facing.LengthSquared() > 0.000001f ? facing.Normalized() : fallback;
    }

    private bool CanReach(Vector3 destination, out Vector3[] path)
    {
        path = NavigationServer3D.MapGetPath(
            GetNavigationMap(),
            GetNavigationStartPosition(),
            destination,
            optimize: true,
            NavigationLayers);
        if (path.Length == 0)
        {
            return false;
        }

        float tolerance = Mathf.Max(DestinationReachedDistance, 0.0f);
        return path[^1].DistanceSquaredTo(destination) <= tolerance * tolerance;
    }

    private int AdvanceAcceptedPathIndex(Vector3 actorPosition)
    {
        int index = Math.Clamp(_acceptedPathIndex, 0, _acceptedPath.Length - 1);
        float desiredDistance = Mathf.Max(PathDesiredDistance, 0.0f);
        float desiredDistanceSquared = desiredDistance * desiredDistance;
        while (index < _acceptedPath.Length - 1
            && HasReachedOrPassedAcceptedPathPoint(actorPosition, index, desiredDistanceSquared))
        {
            index++;
        }

        return index;
    }

    private bool HasReachedOrPassedAcceptedPathPoint(
        Vector3 actorPosition,
        int index,
        float desiredDistanceSquared)
    {
        Vector3 point = _acceptedPath[index];
        if (!actorPosition.IsFinite() || !point.IsFinite())
        {
            return false;
        }

        if (actorPosition.DistanceSquaredTo(point) <= desiredDistanceSquared)
        {
            return true;
        }

        if (index == 0)
        {
            Vector3 outgoingSegment = _acceptedPath[1] - point;
            return _acceptedPath[1].IsFinite()
                && outgoingSegment.LengthSquared() > 0.000001f
                && (actorPosition - point).Dot(outgoingSegment) >= 0.0f;
        }

        if (!_acceptedPath[index - 1].IsFinite())
        {
            return false;
        }

        Vector3 incomingSegment = point - _acceptedPath[index - 1];
        return incomingSegment.LengthSquared() > 0.000001f
            && (actorPosition - point).Dot(incomingSegment) >= 0.0f;
    }

    private static NavigationMotionIntent PendingIntent(Vector3 destination)
        => new(destination, Vector3.Zero, Vector3.Forward, 0.0f, 0.0f, 0.0f, false, false, false, false);

    private static NavigationMotionIntent CompletedIntent(Vector3 position)
        => new(position, Vector3.Zero, Vector3.Forward, 0.0f, 0.0f, 0.0f, true, true, true, false);
}
