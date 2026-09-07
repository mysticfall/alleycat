using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Godot-backed navigation policy that samples paths without mutating the navigated actor.
/// </summary>
[GlobalClass]
public abstract partial class NavigationBase : NavigationAgent3D, INavigation
{
    private const int TerminalOutcomeCapacity = 32;

    private Transform3D _destination = Transform3D.Identity;
    private NavigationMotionIntent _intent = CompletedIntent(Vector3.Zero);
    private Vector3 _initialFacing = Vector3.Forward;
    private float _initialPathLength;
    private readonly NavigationTravelProgress _travelProgress = new();
    private bool _hasInitialSample;
    private Vector3[] _acceptedPath = [];
    private int _acceptedPathIndex;
    private Vector3 _pathTraversalStart;
    private long _destinationRequestGeneration;
    private long _routeRevision;
    private Transform3D _lastActorTransform = Transform3D.Identity;
    private readonly NavigationRequestLifecycle _requestLifecycle = new(TerminalOutcomeCapacity);

    /// <inheritdoc/>
    [Export]
    public bool IsMovementAuthorityActive { get; private set; } = true;

    /// <inheritdoc/>
    public void ActivateMovementAuthority()
    {
        IsMovementAuthorityActive = true;
        _requestLifecycle.Enable();
    }

    /// <inheritdoc/>
    public void DeactivateMovementAuthority()
    {
        IsMovementAuthorityActive = false;
        _ = _requestLifecycle.Disable(
            CreateLifecycleTerminalContext(_lastActorTransform, NavigationTerminalReason.Cancellation),
            Engine.GetPhysicsFrames());
        ClearDestinationState();
    }

    /// <inheritdoc/>
    public bool HasDestination
    {
        get; private set;
    }

    /// <inheritdoc/>
    public Transform3D Destination => _destination;

    /// <inheritdoc/>
    public NavigationCurrentStatus CurrentStatus => _requestLifecycle.CurrentStatus;

    /// <inheritdoc/>
    public IReadOnlyList<NavigationTerminalOutcomeRecord> TerminalOutcomes => _requestLifecycle.TerminalOutcomes;

    /// <inheritdoc/>
    public bool TryAcknowledgeOutcome(NavigationRequestIdentity request, long terminalSequence)
        => _requestLifecycle.TryAcknowledgeOutcome(request, terminalSequence);

    /// <inheritdoc/>
    public bool IsNavigationRunning => HasDestination && (!_intent.IsComplete || !CanReportNavigationComplete());

    /// <inheritdoc/>
    bool INavigation.IsNavigationFinished => !HasDestination || (_intent.IsComplete && CanReportNavigationComplete());

    /// <summary>
    /// Allows a movement consumer to retain public motion authority after the facade reaches its broad arrival policy.
    /// </summary>
    protected virtual bool CanReportNavigationComplete() => true;

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

    /// <summary>
    /// Gets whether the latest call to <see cref="Poll" /> produced a valid sample rather than returning cached intent.
    /// </summary>
    public bool LastPollProducedValidSample
    {
        get; private set;
    }

    /// <summary>
    /// Gets the immutable route sample produced by the latest valid poll, or <see langword="null" /> before one exists.
    /// </summary>
    protected NavigationRouteSnapshot? CurrentRouteSnapshot
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

        Vector3 navigationStartPosition = GetNavigationStartPosition();
        if (!CanReach(navigationStartPosition, destination.Origin, out Vector3[] acceptedPath))
        {
            return NavigationDestinationResult.Unreachable;
        }

        Transform3D realisedTransform = ResolveLifecycleTransform(navigationStartPosition);
        NavigationTerminalContext? replacementContext = HasDestination
            ? CreateLifecycleTerminalContext(realisedTransform, NavigationTerminalReason.Replacement)
            : null;
        long nextRouteRevision = _routeRevision + 1;
        NavigationDestinationAttempt attempt = _requestLifecycle.RequestDestination(
            ToOutcomeTransform(destination),
            NavigationRequestValidation.Accepted,
            nextRouteRevision,
            clockGeneration: 0,
            Engine.GetPhysicsFrames(),
            replacementContext,
            Engine.GetPhysicsFrames());
        if (!attempt.IsAccepted)
        {
            return attempt.Result;
        }

        TargetPosition = destination.Origin;
        _destinationRequestGeneration++;
        _routeRevision = nextRouteRevision;
        CurrentRouteSnapshot = null;
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
        _pathTraversalStart = navigationStartPosition;
        _intent = PendingIntent(destination.Origin);
        OnDestinationAccepted(destination);
        return NavigationDestinationResult.Accepted;
    }

    /// <inheritdoc/>
    public void ClearDestination()
    {
        _ = _requestLifecycle.TryCancel(
            NavigationCancellationSource.ExplicitClear,
            CreateLifecycleTerminalContext(_lastActorTransform, NavigationTerminalReason.Cancellation),
            Engine.GetPhysicsFrames());
        ClearDestinationState();
    }

    private void ClearDestinationState()
    {
        HasDestination = false;
        _hasInitialSample = false;
        _travelProgress.Reset();
        UsedAcceptedPathFallbackForLastPoll = false;
        LastSampledPathIndex = 0;
        _acceptedPath = [];
        _acceptedPathIndex = 0;
        CurrentRouteSnapshot = null;
        _intent = CompletedIntent(_destination.Origin);
        LastPollProducedValidSample = false;
        OnDestinationCleared();
    }

    /// <inheritdoc/>
    public NavigationMotionIntent Poll(Transform3D actorTransform)
    {
        if (actorTransform.IsFinite())
        {
            _lastActorTransform = actorTransform;
        }

        LastPollProducedValidSample = false;
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
            _acceptedPathIndex = AdvancePathIndex(
                actorTransform.Origin,
                path,
                _acceptedPathIndex,
                _pathTraversalStart,
                Mathf.Max(PathDesiredDistance, 0.0f));
            pathIndex = _acceptedPathIndex;
            nextPathPosition = path[pathIndex];
        }
        else if (path.Length > 0)
        {
            pathIndex = AdvancePathIndex(
                actorTransform.Origin,
                path,
                pathIndex,
                _pathTraversalStart,
                Mathf.Max(PathDesiredDistance, 0.0f));
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

        bool geometryChanged = CurrentRouteSnapshot is not null
            && CurrentRouteSnapshot.DestinationRequestGeneration == _destinationRequestGeneration
            && !HasSamePath(CurrentRouteSnapshot.PathPoints, path);
        if (geometryChanged)
        {
            _routeRevision++;
            _ = _requestLifecycle.AdvanceRevision();
        }

        CurrentRouteSnapshot = new NavigationRouteSnapshot(
            path,
            pathIndex,
            nextPathPosition,
            _destination,
            _destinationRequestGeneration,
            _routeRevision,
            UsedAcceptedPathFallbackForLastPoll,
            geometryChanged);
        _intent = sampledIntent;
        LastPollProducedValidSample = true;
        return _intent;
    }

    /// <summary>
    /// Allows concrete implementations to react to accepted destination intent without moving an actor.
    /// </summary>
    protected virtual void OnDestinationAccepted(Transform3D destination)
    {
    }

    /// <summary>
    /// Allows concrete implementations to react immediately after destination state is cleared.
    /// </summary>
    protected virtual void OnDestinationCleared()
    {
    }

    /// <summary>Commits measured successful terminal evidence through the shared exact-once lifecycle.</summary>
    protected bool TryCommitCompletedOutcome(NavigationTerminalContext context, ulong terminalPhysicsTick)
        => TryCommitAndClear(() => _requestLifecycle.TryComplete(context, terminalPhysicsTick));

    /// <summary>Commits measured failed terminal evidence through the shared exact-once lifecycle.</summary>
    protected bool TryCommitFailedOutcome(NavigationTerminalContext context, ulong terminalPhysicsTick)
        => TryCommitAndClear(() => _requestLifecycle.TryFail(context, terminalPhysicsTick));

    /// <summary>Commits a known obstruction through the shared exact-once lifecycle.</summary>
    protected bool TryCommitBlockedOutcome(NavigationTerminalContext context, ulong terminalPhysicsTick)
        => TryCommitAndClear(() => _requestLifecycle.TryBlock(context, terminalPhysicsTick));

    /// <summary>Commits a caller-owned cancellation source before clearing active navigation state.</summary>
    protected bool TryCancelActiveOutcome(
        NavigationCancellationSource source,
        NavigationTerminalContext context,
        ulong terminalPhysicsTick)
        => TryCommitAndClear(() => _requestLifecycle.TryCancel(source, context, terminalPhysicsTick));

    /// <summary>Updates the retained clock generation without allocating a lifecycle record.</summary>
    protected void SetActiveRequestClockGeneration(long clockGeneration)
        => _requestLifecycle.SetClockGeneration(clockGeneration);

    /// <summary>Marks active work temporarily neutralised without committing a terminal result.</summary>
    protected void EnterActiveRequestNeutralisation() => _requestLifecycle.EnterTransientNeutralisation();

    /// <summary>Marks active work waiting without committing a terminal result.</summary>
    protected void EnterActiveRequestWaiting() => _requestLifecycle.EnterTransientWaiting();

    /// <summary>Returns retained active work to route following.</summary>
    protected void ResumeActiveRequestFollowing() => _requestLifecycle.ResumeFollowing();

    /// <summary>Commits reset cancellation before clearing route state and advances the non-reused identity generation.</summary>
    protected bool ResetNavigationRequestLifecycle(NavigationTerminalContext context, ulong terminalPhysicsTick)
    {
        bool committed = _requestLifecycle.Reset(context, terminalPhysicsTick);
        ClearDestinationState();
        return committed;
    }

    /// <summary>
    /// Cancels active work and transfers every retained outcome before ending storage owned by a removed node.
    /// </summary>
    protected void EndNavigationOwnerLifecycle(
        INavigationTerminalOutcomeSink survivingSink,
        NavigationTerminalContext context,
        ulong terminalPhysicsTick)
    {
        _requestLifecycle.RemoveOwner(survivingSink, context, terminalPhysicsTick);
        ClearDestinationState();
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

    private bool TryCommitAndClear(Func<bool> commit)
    {
        bool committed = commit();
        if (committed)
        {
            ClearDestinationState();
        }

        return committed;
    }

    private Transform3D ResolveLifecycleTransform(Vector3 navigationStartPosition)
        => _lastActorTransform.IsFinite()
            && (_lastActorTransform != Transform3D.Identity || navigationStartPosition == Vector3.Zero)
                ? _lastActorTransform
                : new Transform3D(Basis.Identity, navigationStartPosition);

    /// <summary>Creates measured lifecycle evidence for cancellation and replacement transitions.</summary>
    protected virtual NavigationTerminalContext CreateLifecycleTerminalContext(
        Transform3D finalTransform,
        NavigationTerminalReason reason)
    {
        NavigationOutcomeTransform final = ToOutcomeTransform(finalTransform);
        NavigationOutcomeTransform requested = ToOutcomeTransform(_destination);
        float planarError = new Vector2(final.X - requested.X, final.Z - requested.Z).Length();
        float yawError = MathF.Abs(Mathf.Wrap(final.YawRadians - requested.YawRadians, -Mathf.Pi, Mathf.Pi));
        var error = new NavigationPlanarYawError(planarError, yawError);
        var tolerances = new NavigationTerminalTolerances(
            Mathf.Max(DestinationReachedDistance, 0.0f),
            Mathf.DegToRad(Mathf.Max(FacingToleranceDegrees, 0.0f)));
        return new NavigationTerminalContext(final, error, tolerances, reason);
    }

    private static NavigationOutcomeTransform ToOutcomeTransform(Transform3D transform)
    {
        Vector3 facing = ExtractHorizontalFacing(transform.Basis, Vector3.Forward);
        float yaw = MathF.Atan2(-facing.X, -facing.Z);
        return new NavigationOutcomeTransform(transform.Origin.X, transform.Origin.Y, transform.Origin.Z, yaw);
    }

    private static Vector3 ExtractHorizontalFacing(Basis basis, Vector3 fallback)
    {
        Vector3 facing = basis.IsFinite() ? new Vector3(-basis.Z.X, 0.0f, -basis.Z.Z) : fallback;
        return facing.IsFinite() && facing.LengthSquared() > 0.000001f ? facing.Normalized() : fallback;
    }

    private static bool HasSamePath(IReadOnlyList<Vector3> previous, ReadOnlySpan<Vector3> current)
    {
        if (previous.Count != current.Length)
        {
            return false;
        }

        for (int index = 0; index < current.Length; index++)
        {
            if (previous[index] != current[index])
            {
                return false;
            }
        }

        return true;
    }

    private bool CanReach(Vector3 start, Vector3 destination, out Vector3[] path)
    {
        path = NavigationServer3D.MapGetPath(
            GetNavigationMap(),
            start,
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

    private static int AdvancePathIndex(
        Vector3 actorPosition,
        IReadOnlyList<Vector3> path,
        int currentIndex,
        Vector3 pathTraversalStart,
        float desiredDistance)
    {
        int index = Math.Clamp(currentIndex, 0, path.Count - 1);
        float desiredDistanceSquared = desiredDistance * desiredDistance;
        while (index < path.Count - 1
            && HasReachedOrPassedPathPoint(
                actorPosition,
                path,
                index,
                pathTraversalStart,
                desiredDistanceSquared))
        {
            index++;
        }

        return index;
    }

    private static bool HasReachedOrPassedPathPoint(
        Vector3 actorPosition,
        IReadOnlyList<Vector3> path,
        int index,
        Vector3 pathTraversalStart,
        float desiredDistanceSquared)
    {
        Vector3 point = path[index];
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
            if (!pathTraversalStart.IsFinite())
            {
                return false;
            }

            if (pathTraversalStart.DistanceSquaredTo(point) <= desiredDistanceSquared)
            {
                return true;
            }

            Vector3 initialSegment = point - pathTraversalStart;
            return initialSegment.LengthSquared() > 0.000001f
                && (actorPosition - point).Dot(initialSegment) >= 0.0f;
        }

        if (!path[index - 1].IsFinite())
        {
            return false;
        }

        Vector3 incomingSegment = point - path[index - 1];
        return incomingSegment.LengthSquared() > 0.000001f
            && (actorPosition - point).Dot(incomingSegment) >= 0.0f;
    }

    private static NavigationMotionIntent PendingIntent(Vector3 destination)
        => new(destination, Vector3.Zero, Vector3.Forward, 0.0f, 0.0f, 0.0f, false, false, false, false);

    private static NavigationMotionIntent CompletedIntent(Vector3 position)
        => new(position, Vector3.Zero, Vector3.Forward, 0.0f, 0.0f, 0.0f, true, true, true, false);
}
