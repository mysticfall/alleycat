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
    private NavigationPathQuery? _routeQuery;
    private int _acceptedPathIndex;
    private int _nativeViaIndex;
    private bool _nativeLegSampled;
    private Vector3[] _viaPoints = [];
    private Vector3[] _nativeViaEnds = [];
    private bool[] _viaPointsPassed = [];
    private bool[] _viaPointsMissed = [];
    private float[] _viaRouteDistances = [];
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
            CreateLifecycleTerminalContext(CurrentRealisedTransform, NavigationTerminalReason.Cancellation),
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
            if (_viaPoints.Length > 0)
            {
                return [.. _acceptedPath];
            }
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
    /// The via-point pass radius (NAV-001 TR18, a tuning value): a via counts as passed when the actor comes
    /// within this planar radius of it. Default 0.5 m — roughly 3.3× the worst realised corner-tracking error
    /// measured on the authored navigation scene (0.152 m at the second cylinder corner,
    /// game/temp/nav001-corner-composed-containment/run-01/candidate/corner/), comfortably above straight-leg
    /// walk tracking noise, while a factor of four below the Waypoint Route scenario's 2 m legs, so a
    /// wrong-side or short-of-the-via pass still misses.
    /// </summary>
    [ExportGroup("Via-Points")]
    [Export(PropertyHint.Range, "0.05,2,0.01,or_greater")]
    public float ViaPointPassRadiusMetres
    {
        get;
        set;
    } = 0.5f;

    /// <inheritdoc/>
    public IReadOnlyList<Vector3> ViaPoints => _viaPoints;

    /// <inheritdoc/>
    public IReadOnlyList<bool> ViaPointsPassed => _viaPointsPassed;

    /// <summary>
    /// Diagnostics (TR17-style, report-only): which vias were passed by without coming within the pass radius
    /// — a missed via is never backtracked or stopped for, only recorded.
    /// </summary>
    public IReadOnlyList<bool> ViaPointsMissed => _viaPointsMissed;

    /// <summary>The count of vias passed within the radius so far this request (value-only diagnostics).</summary>
    public int ViaPointsPassedCount
    {
        get
        {
            int count = 0;
            foreach (bool passed in _viaPointsPassed)
            {
                count += passed ? 1 : 0;
            }

            return count;
        }
    }

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

    /// <summary>Current native agent result after its path update, not an independent geometric judgement.</summary>
    protected bool NativeTargetUnreachable
    {
        get; private set;
    }

    /// <summary>Only the final native leg may supply requested-destination arrival animation.</summary>
    protected bool NativeDestinationLeg => _nativeViaIndex >= _viaPoints.Length;

    /// <summary>
    /// Gets the immutable route sample produced by the latest valid poll, or <see langword="null" /> before one exists.
    /// </summary>
    protected NavigationRouteSnapshot? CurrentRouteSnapshot
    {
        get; private set;
    }

    /// <inheritdoc/>
    public NavigationDestinationResult SetDestination(Transform3D destination, IReadOnlyList<Vector3>? viaPoints)
    {
        if (!destination.IsFinite() || viaPoints?.Any(via => !via.IsFinite()) == true)
        {
            return NavigationDestinationResult.Invalid;
        }

        if (!IsNavigationMapReady)
        {
            return NavigationDestinationResult.NotReady;
        }

        Vector3 navigationStartPosition = GetNavigationStartPosition();
        Vector3[] acceptedPath;
        Vector3[] nativeViaEnds;
        float[] viaRouteDistances;
        if (viaPoints is { Count: > 0 } points)
        {
            // TR18: the route is the ordered chain start → via 1 → … → via N → destination, each segment
            // validated exactly like a plain destination's path. Any unreachable segment refuses the whole
            // request and leaves existing state unchanged.
            var segments = new Vector3[points.Count + 1];
            bool reachable = true;
            Vector3 cursor = navigationStartPosition;
            for (int index = 0; reachable && index < segments.Length; index++)
            {
                Vector3 target = index < points.Count ? points[index] : destination.Origin;
                Vector3[] segment = NavigationServer3D.MapGetPath(
                    GetNavigationMap(), cursor, target, optimize: true, NavigationLayers);
                if (segment.Length == 0)
                {
                    reachable = false;
                    break;
                }

                segments[index] = cursor = segment[^1];
            }

            if (!reachable)
            {
                return NavigationDestinationResult.Unreachable;
            }

            (acceptedPath, viaRouteDistances) = NavigationViaChain.Build(
                navigationStartPosition,
                points,
                destination.Origin,
                (from, to) => NavigationServer3D.MapGetPath(GetNavigationMap(), from, to, optimize: true, NavigationLayers));
            nativeViaEnds = segments[..points.Count];
        }
        else
        {
            if (!CanReach(navigationStartPosition, destination.Origin, out acceptedPath))
            {
                return NavigationDestinationResult.Unreachable;
            }

            viaRouteDistances = [];
            nativeViaEnds = [];
        }

        NavigationDestinationResult accepted = AcceptDestination(destination, navigationStartPosition, acceptedPath);
        if (accepted != NavigationDestinationResult.Accepted)
        {
            return accepted;
        }

        _viaPoints = [.. viaPoints ?? []];
        _nativeViaEnds = nativeViaEnds;
        _viaPointsPassed = new bool[_viaPoints.Length];
        _viaPointsMissed = new bool[_viaPoints.Length];
        _viaRouteDistances = viaRouteDistances;
        _nativeViaIndex = 0;
        _nativeLegSampled = false;
        NativeTargetUnreachable = false;
        if (_viaPoints.Length > 0)
        {
            TargetPosition = _viaPoints[0];
        }
        return NavigationDestinationResult.Accepted;
    }

    /// <inheritdoc/>
    public NavigationDestinationResult SetDestination(Transform3D destination)
        => SetDestination(destination, viaPoints: null);

    private NavigationDestinationResult AcceptDestination(
        Transform3D destination,
        Vector3 navigationStartPosition,
        Vector3[] acceptedPath)
    {
        Transform3D realisedTransform = ResolveLifecycleTransform(navigationStartPosition);
        NavigationTerminalContext? replacementContext = HasDestination
            ? CreateLifecycleTerminalContext(realisedTransform, NavigationTerminalReason.Replacement)
            : null;
        long nextRouteRevision = _routeRevision + 1;
        NavigationDestinationAttempt attempt = _requestLifecycle.RequestDestination(
            ToOutcomeTransform(destination),
            NavigationRequestValidation.Accepted,
            nextRouteRevision,
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
        _routeQuery = new(destination, _destinationRequestGeneration, acceptedPath);
        _acceptedPathIndex = 0;
        _intent = PendingIntent(destination.Origin);
        OnDestinationAccepted(destination);
        return NavigationDestinationResult.Accepted;
    }

    /// <inheritdoc/>
    public void ClearDestination()
    {
        _ = _requestLifecycle.TryCancel(
            NavigationCancellationSource.ExplicitClear,
            CreateLifecycleTerminalContext(CurrentRealisedTransform, NavigationTerminalReason.Cancellation),
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
        _routeQuery = null;
        _acceptedPathIndex = 0;
        _nativeViaIndex = 0;
        _nativeLegSampled = false;
        NativeTargetUnreachable = false;
        _viaPoints = [];
        _nativeViaEnds = [];
        _viaPointsPassed = [];
        _viaPointsMissed = [];
        _viaRouteDistances = [];
        CurrentRouteSnapshot = null;
        _intent = CompletedIntent(_destination.Origin);
        LastPollProducedValidSample = false;
        OnDestinationCleared();
    }

    /// <summary>
    /// Records realised via proximity. Native leg completion above owns progression and any missed-via report.
    /// </summary>
    private void TrackViaPoints(Vector3 actorPosition)
    {
        float radius = Mathf.Max(ViaPointPassRadiusMetres, 0.0f);
        for (int index = 0; index < _viaPoints.Length; index++)
        {
            if (_viaPointsPassed[index])
            {
                continue;
            }

            if (PlanarDistanceTo(actorPosition, _viaPoints[index]) <= radius)
            {
                _viaPointsPassed[index] = true;
                continue;
            }

        }
    }

    private int ChainIndexOf(Vector3 via)
    {
        for (int index = 0; index < _acceptedPath.Length; index++)
        {
            if (_acceptedPath[index] == via)
            {
                return index;
            }
        }

        return -1;
    }

    private static float PlanarDistanceTo(Vector3 from, Vector3 to)
        => MathF.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Z - from.Z) * (to.Z - from.Z)));

    /// <summary>
    /// The route distances of the still-unvisited vias, in order (value-only): the caller's station clamp
    /// keeps the next section from leapfrogging an unvisited via. Passed and missed vias no longer bind.
    /// </summary>
    internal IReadOnlyList<float> UnvisitedViaRouteDistances
    {
        get
        {
            List<float> distances = [];
            for (int index = 0; index < _viaRouteDistances.Length; index++)
            {
                if (!_viaPointsPassed[index] && !_viaPointsMissed[index])
                {
                    distances.Add(_viaRouteDistances[index]);
                }
            }

            return distances;
        }
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

        // Vias are sequential native targets, not a second waypoint-progress controller. Native completion
        // releases each leg without requesting an idle/arrival animation at an intermediate via.
        if (_nativeLegSampled && _nativeViaIndex < _viaPoints.Length && IsTargetReached())
        {
            _viaPointsPassed[_nativeViaIndex] = PlanarDistanceTo(actorTransform.Origin, _viaPoints[_nativeViaIndex]) <= ViaPointPassRadiusMetres;
            _viaPointsMissed[_nativeViaIndex] = !_viaPointsPassed[_nativeViaIndex];
            _nativeViaIndex++;
            TargetPosition = _nativeViaIndex < _viaPoints.Length ? _viaPoints[_nativeViaIndex] : _destination.Origin;
            _nativeLegSampled = false;
        }
        // Update once per active physics poll; a completed native path is read without re-advancing it.
        bool advanceNative = !_nativeLegSampled || !IsNavigationFinished();
        Vector3 nextPathPosition = advanceNative ? GetNextPathPosition() : Vector3.Zero;
        Vector3[] path = GetCurrentNavigationPath();
        int pathIndex = GetCurrentNavigationPathIndex();
        if (!advanceNative && path.Length > 0)
        {
            nextPathPosition = path[Math.Clamp(pathIndex, 0, path.Length - 1)];
        }
        NativeTargetUnreachable = path.Length > 0 && !IsTargetReachable();
        AdjustPathSample(ref nextPathPosition, ref path, ref pathIndex);
        UsedAcceptedPathFallbackForLastPoll = false;
        bool viaChainedRoute = _viaPoints.Length > 0;
        if (path.Length > 0)
        {
            _nativeLegSampled = true;
            // Keep the remaining ordered native-query legs available to distance-ahead animation sampling.
            // Only this active leg's NavigationAgent3D cursor advances; no game-owned nearest/plane cursor.
            if (_nativeViaIndex < _viaPoints.Length)
            {
                int junction = ChainIndexOf(_nativeViaEnds[_nativeViaIndex]);
                if (junction >= 0)
                {
                    path = [.. path, .. _acceptedPath.Skip(junction + 1)];
                }
            }
            if (!HasSamePath(_acceptedPath, path))
            {
                _acceptedPath = path;
                _routeQuery = new(_destination, _destinationRequestGeneration, path);
            }
            _acceptedPathIndex = pathIndex;
        }
        else if (_acceptedPath.Length > 0)
        {
            // A native synchronous acceptance query may precede agent synchronisation. Hold its last native
            // cursor until the agent supplies progress; do not invent progress while its path is unavailable.
            UsedAcceptedPathFallbackForLastPoll = true;
            path = _acceptedPath;
            pathIndex = Math.Clamp(_acceptedPathIndex, 0, path.Length - 1);
            nextPathPosition = path[pathIndex];
        }

        if (viaChainedRoute)
        {
            TrackViaPoints(actorTransform.Origin);
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
            geometryChanged,
            _routeQuery);
        _intent = sampledIntent;
        LastPollProducedValidSample = true;
        return _intent;
    }

    internal bool OwnsRoute(NavigationRouteSnapshot route) => HasDestination && ReferenceEquals(CurrentRouteSnapshot, route) &&
        ReferenceEquals(_routeQuery, route.Query) && _routeQuery is { } query &&
        query.Generation == _destinationRequestGeneration && route.DestinationRequestGeneration == query.Generation &&
        route.Destination == _destination && query.Destination == _destination && route.RouteRevision == _routeRevision &&
        HasSamePath(query.Points, route.PathPoints.ToArray());

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

    /// <summary>Records sequence completion and measured errors without asserting destination convergence.</summary>
    protected bool TryCommitArrivalSequenceOutcome(NavigationTerminalContext context, ulong terminalPhysicsTick)
        => TryCommitAndClear(() => _requestLifecycle.TryFinishArrivalSequence(context, terminalPhysicsTick));

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
    {
        Transform3D realised = CurrentRealisedTransform;
        return realised.IsFinite() && (realised != Transform3D.Identity || navigationStartPosition == Vector3.Zero)
            ? realised
            : new Transform3D(Basis.Identity, navigationStartPosition);
    }

    /// <summary>
    /// Realised actor transform recorded as lifecycle terminal evidence when a request is cancelled, replaced or
    /// deactivated between polls: by default the transform measured at the latest poll. A consumer that realises
    /// motion after its poll overrides this with its current measurement, so the record matches the displayed pose.
    /// </summary>
    protected virtual Transform3D CurrentRealisedTransform => _lastActorTransform;

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
        return path.Length != 0;
    }

    private static NavigationMotionIntent PendingIntent(Vector3 destination)
        => new(destination, Vector3.Zero, Vector3.Forward, 0.0f, 0.0f, 0.0f, false, false, false, false);

    private static NavigationMotionIntent CompletedIntent(Vector3 position)
        => new(position, Vector3.Zero, Vector3.Forward, 0.0f, 0.0f, 0.0f, true, true, true, false);
}
