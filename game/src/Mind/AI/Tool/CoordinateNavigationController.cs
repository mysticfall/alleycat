using System.Globalization;
using AlleyCat.Character;
using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// World queries the coordinate navigation tools need beyond the <see cref="INavigation" /> facade: the subject's
/// live transform, a read-only route probe on the subject's navigation map, the navigation-map height at a ground
/// spot, and the pace setter. Keeping them behind this port leaves NAV-001 and <see cref="INavigation" />
/// unchanged and lets deterministic tests drive the controller without the Godot runtime.
/// </summary>
internal interface ICoordinateNavigationEnvironment
{
    /// <summary>Gets the subject's live world transform.</summary>
    Transform3D SubjectTransform
    {
        get;
    }

    /// <summary>
    /// Probes the route the subject's navigation would compute between two points, without changing any state.
    /// </summary>
    /// <returns>The route corners, or an empty array when unknown or unreachable.</returns>
    Vector3[] QueryPath(Vector3 from, Vector3 to);

    /// <summary>
    /// Resolves the navigation-map point closest to a ground spot, or null when the map cannot answer.
    /// </summary>
    Vector3? ProjectToNavigationMap(Vector3 point);

    /// <summary>Applies a NAV-001 UR4 normalised speed to the subject's navigation, when it supports one.</summary>
    void ApplyNormalisedSpeed(float normalisedSpeed);
}

/// <summary>
/// Production <see cref="ICoordinateNavigationEnvironment" />: the live transform comes from the subject
/// character, and route probes use the same Godot navigation map, layers and path optimisation as the subject's
/// <see cref="NavigationAgent3D" />-backed navigation, so a probe matches the route the accepted request follows.
/// </summary>
internal sealed class GodotCoordinateNavigationEnvironment(ICharacter subject, INavigation navigation)
    : ICoordinateNavigationEnvironment
{
    /// <inheritdoc />
    public Transform3D SubjectTransform => subject.GlobalTransform;

    /// <inheritdoc />
    public Vector3[] QueryPath(Vector3 from, Vector3 to)
        => TryGetReadyMap(out NavigationAgent3D? agent, out Rid map)
            ? NavigationServer3D.MapGetPath(map, from, to, optimize: true, agent!.NavigationLayers)
            : [];

    /// <inheritdoc />
    public Vector3? ProjectToNavigationMap(Vector3 point)
        => TryGetReadyMap(out _, out Rid map) ? NavigationServer3D.MapGetClosestPoint(map, point) : null;

    /// <inheritdoc />
    public void ApplyNormalisedSpeed(float normalisedSpeed)
    {
        if (navigation is MotionBricksNavigation generated)
        {
            generated.NormalisedSpeed = normalisedSpeed;
        }
    }

    private bool TryGetReadyMap(out NavigationAgent3D? agent, out Rid map)
    {
        agent = navigation as NavigationAgent3D;
        map = agent is not null && GodotObject.IsInstanceValid(agent) && agent.IsInsideTree()
            ? agent.GetNavigationMap()
            : default;
        return map.IsValid && NavigationServer3D.MapGetIterationId(map) != 0;
    }
}

/// <summary>
/// Short textual result of one coordinate navigation tool call (AI-015 TR-10).
/// </summary>
/// <param name="Started">Whether NAV-001 accepted a request; false for refusals and no-ops.</param>
/// <param name="Message">The model-facing result text.</param>
internal readonly record struct CoordinateNavigationResponse(bool Started, string Message);

/// <summary>
/// Session-scoped driver behind <c>walk_to</c>, <c>turn_to</c> and <c>stop</c> (AI-015 TR-4–TR-16), bound at
/// composition to the subject's <see cref="INavigation" />. Calls never block: each submits at most one NAV-001
/// request and returns its start confirmation or contained refusal. Every accepted request is tracked by its
/// NAV-001 identity until its single terminal record is read from <see cref="INavigationOutcomeSource" />,
/// published as one <see cref="ObservedNavigationOutcome" /> and acknowledged: removal from the tracked set before
/// publishing makes delivery exactly once. Records of requests this controller did not start are left untouched.
/// </summary>
/// <remarks>
/// Every member runs on the Godot main thread: tool invocations through the main-thread dispatcher and
/// <see cref="Poll" /> from the owning mind's frame processing.
/// </remarks>
internal sealed class CoordinateNavigationController(
    INavigation navigation,
    ICoordinateNavigationEnvironment environment,
    Action<ObservedNavigationOutcome> publishOutcome)
{
    /// <summary>
    /// Planar length below which a route segment or straight-line offset is too short to define a travel direction.
    /// </summary>
    internal const float MinimumDirectionSegmentMetres = 0.05f;

    private const string UnchangedSuffix = " Nothing changed; any move in progress continues.";

    private readonly Dictionary<NavigationRequestIdentity, TrackedMove> _tracked = [];
    private readonly List<NavigationTerminalOutcomeRecord> _matched = [];

    /// <summary>Gets the number of started moves still awaiting their outcome.</summary>
    internal int PendingOutcomeCount => _tracked.Count;

    /// <summary>
    /// Starts walking to world (<paramref name="x" />, <paramref name="z" />) (AI-015 TR-4, TR-6, TR-7). With a
    /// heading the request carries that terminal facing; without one it carries the bearing of the planned route's
    /// final segment, so the character ends facing along the route's final stretch. NAV-001 may adjust the facing
    /// with a small turn after the position is reached when the realised approach differs from the plan.
    /// </summary>
    public CoordinateNavigationResponse WalkTo(double x, double z, double? heading, string? pace)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z))
        {
            return Refuse("Walk", "`x` and `z` must be finite numbers of metres");
        }

        double? bearing = null;
        if (heading is { } requested)
        {
            if (!CompassBearing.TryNormalise(requested, out double normalised))
            {
                return Refuse("Walk", "`heading` must be a finite number of degrees");
            }

            bearing = normalised;
        }

        if (!NavigationPace.TryResolve(pace, out string paceName, out float speed))
        {
            return Refuse(
                "Walk",
                $"unknown pace '{pace}'; use {NavigationPace.Stroll}, {NavigationPace.Walk} or {NavigationPace.Hurry}");
        }

        Transform3D subject = environment.SubjectTransform;
        Vector3 start = subject.Origin;
        var spot = new Vector3((float)x, start.Y, (float)z);
        float height = environment.ProjectToNavigationMap(spot) is { } projected && float.IsFinite(projected.Y)
            ? projected.Y
            : start.Y;
        var destination = new Vector3(spot.X, height, spot.Z);
        double facing = bearing ?? DeriveArrivalBearing(
            start,
            destination,
            environment.QueryPath(start, destination),
            CurrentBearing(subject));

        string? failure = Submit(
            new Transform3D(CompassBearing.ToBasis(facing), destination),
            CoordinateMoveKind.Walk,
            speed,
            out bool replaced);
        if (failure is not null)
        {
            return Refuse("Walk", failure);
        }

        string arrivalFacing = bearing is { } face
            ? $", to face {CompassBearing.FormatWholeDegrees(face)}° on arrival"
            : string.Empty;
        return new CoordinateNavigationResponse(
            true,
            $"Started walking to ({FormatCoordinate(x)}, {FormatCoordinate(z)}) at {paceName} pace{arrivalFacing}."
                + ReplacedSuffix(replaced)
                + " You will be told when you arrive or if the walk fails.");
    }

    /// <summary>
    /// Starts a stationary turn (AI-015 TR-8): a destination at the subject's current position with the requested
    /// facing, so NAV-001's finite terminal-facing turn plan turns in place without translation.
    /// </summary>
    public CoordinateNavigationResponse TurnTo(double heading)
    {
        if (!CompassBearing.TryNormalise(heading, out double bearing))
        {
            return Refuse("Turn", "`heading` must be a finite number of degrees");
        }

        Transform3D subject = environment.SubjectTransform;
        string? failure = Submit(
            new Transform3D(CompassBearing.ToBasis(bearing), subject.Origin),
            CoordinateMoveKind.Turn,
            normalisedSpeed: null,
            out bool replaced);
        return failure is not null
            ? Refuse("Turn", failure)
            : new CoordinateNavigationResponse(
                true,
                $"Started turning to face {CompassBearing.FormatWholeDegrees(bearing)}°."
                    + ReplacedSuffix(replaced)
                    + " You will be told when the turn ends.");
    }

    /// <summary>
    /// Clears the current destination (AI-015 TR-9). With no move in progress it succeeds as a no-op and no
    /// outcome follows; a tracked move ends with a <see cref="NavigationOutcomeKind.Stopped" /> outcome.
    /// </summary>
    public CoordinateNavigationResponse Stop()
    {
        NavigationCurrentStatus status = navigation.CurrentStatus;
        if (!status.HasActiveRequest)
        {
            return new CoordinateNavigationResponse(false, "You are not moving, so there was nothing to stop.");
        }

        NavigationRequestIdentity active = status.ActiveRequest!.Value;
        if (_tracked.TryGetValue(active, out TrackedMove move))
        {
            _tracked[active] = move with
            {
                StopRequested = true
            };
        }

        navigation.ClearDestination();
        Poll();
        return new CoordinateNavigationResponse(true, "Stopped: you stand still where you are.");
    }

    /// <summary>
    /// Publishes and acknowledges the terminal records of tracked moves in NAV-001 commit order (AI-015 TR-13–TR-14).
    /// Allocation-free when nothing matched.
    /// </summary>
    public void Poll()
    {
        if (_tracked.Count == 0)
        {
            return;
        }

        IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes = navigation.TerminalOutcomes;
        _matched.Clear();
        for (int index = 0; index < outcomes.Count; index++)
        {
            if (_tracked.ContainsKey(outcomes[index].Request))
            {
                _matched.Add(outcomes[index]);
            }
        }

        foreach (NavigationTerminalOutcomeRecord record in _matched)
        {
            if (!_tracked.Remove(record.Request, out TrackedMove move))
            {
                continue;
            }

            publishOutcome(ObservedNavigationOutcome.FromTerminalRecord(record, move.Kind, move.StopRequested));
            _ = navigation.TryAcknowledgeOutcome(record.Request, record.Sequence);
        }

        _matched.Clear();
    }

    /// <summary>
    /// Releases this controller at session teardown: a move it started that is still active is cancelled, and every
    /// terminal record of its moves is acknowledged without publishing an outcome (the session that would read it has
    /// ended), so NAV-001's bounded outcome store cannot fill with orphaned records across session restarts.
    /// </summary>
    public void Release()
    {
        if (_tracked.Count == 0)
        {
            return;
        }

        if (navigation is GodotObject node && !GodotObject.IsInstanceValid(node))
        {
            _tracked.Clear();
            return;
        }

        if (navigation.CurrentStatus.ActiveRequest is { } active && _tracked.ContainsKey(active))
        {
            navigation.ClearDestination();
        }

        IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes = navigation.TerminalOutcomes;
        foreach (NavigationTerminalOutcomeRecord record in outcomes.Where(record => _tracked.ContainsKey(record.Request)).ToArray())
        {
            _ = navigation.TryAcknowledgeOutcome(record.Request, record.Sequence);
        }

        _tracked.Clear();
    }

    /// <summary>
    /// Derives the terminal facing of a request without a heading (AI-015 TR-7): the bearing of the last route
    /// segment long enough to define a direction, else the straight line from start to destination, else the
    /// current facing. A post-arrival adjustment turn towards this bearing is allowed.
    /// </summary>
    internal static double DeriveArrivalBearing(
        Vector3 start,
        Vector3 destination,
        IReadOnlyList<Vector3> path,
        double currentBearing)
    {
        if (path.Count > 0)
        {
            Vector3 end = path[^1];
            for (int index = path.Count - 2; index >= 0; index--)
            {
                if (TryPlanarBearing(path[index], end, out double segmentBearing))
                {
                    return segmentBearing;
                }
            }
        }

        return TryPlanarBearing(start, destination, out double straightBearing) ? straightBearing : currentBearing;
    }

    private string? Submit(
        Transform3D destination,
        CoordinateMoveKind kind,
        float? normalisedSpeed,
        out bool replaced)
    {
        replaced = navigation.CurrentStatus.HasActiveRequest;
        long sequenceBefore = LatestTerminalSequence();
        NavigationDestinationResult result = navigation.SetDestination(destination);
        if (result != NavigationDestinationResult.Accepted)
        {
            replaced = false;
            return DescribeRefusal(result, kind);
        }

        NavigationRequestIdentity identity = navigation.CurrentStatus.ActiveRequest
            ?? FindRequestCommittedSince(sequenceBefore)
            ?? throw new InvalidOperationException(
                "Navigation accepted a destination but reports neither an active request nor its terminal record.");
        _tracked[identity] = new TrackedMove(kind, StopRequested: false);
        if (normalisedSpeed is { } speed)
        {
            environment.ApplyNormalisedSpeed(speed);
        }

        // A replacement commits the superseded request's terminal record synchronously; deliver it now.
        Poll();
        return null;
    }

    private long LatestTerminalSequence()
    {
        long latest = 0;
        IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes = navigation.TerminalOutcomes;
        for (int index = 0; index < outcomes.Count; index++)
        {
            latest = Math.Max(latest, outcomes[index].Sequence);
        }

        return latest;
    }

    /// <summary>
    /// Finds the request accepted by the last submission when it already reached a terminal inside the same call
    /// (for example an immediate motion-unavailable failure): the newest request among records committed since.
    /// </summary>
    private NavigationRequestIdentity? FindRequestCommittedSince(long sequenceBefore)
    {
        NavigationRequestIdentity? newest = null;
        IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes = navigation.TerminalOutcomes;
        for (int index = 0; index < outcomes.Count; index++)
        {
            NavigationTerminalOutcomeRecord record = outcomes[index];
            if (record.Sequence > sequenceBefore
                && (newest is null || record.Request.RequestID > newest.Value.RequestID))
            {
                newest = record.Request;
            }
        }

        return newest;
    }

    private static string DescribeRefusal(NavigationDestinationResult result, CoordinateMoveKind kind)
        => result switch
        {
            NavigationDestinationResult.Unreachable => kind == CoordinateMoveKind.Walk
                ? "that spot cannot be reached from where you are"
                : "you are not standing on walkable ground",
            NavigationDestinationResult.NotReady => "navigation is not ready yet; try again shortly",
            NavigationDestinationResult.OutcomeBackpressure =>
                "too many earlier movement results are still pending; try again shortly",
            NavigationDestinationResult.Invalid or NavigationDestinationResult.Accepted => "the request was not valid",
            _ => "the request was not valid",
        };

    private static CoordinateNavigationResponse Refuse(string action, string reason)
        => new(false, $"{action} refused: {reason}.{UnchangedSuffix}");

    private static string ReplacedSuffix(bool replaced) => replaced ? " This replaced your earlier move." : string.Empty;

    private static double CurrentBearing(Transform3D subject)
        => CompassBearing.TryFromWorldFacing(-subject.Basis.Z, out double bearing) ? bearing : 0d;

    private static bool TryPlanarBearing(Vector3 from, Vector3 to, out double bearing)
    {
        var offset = new Vector3(to.X - from.X, 0f, to.Z - from.Z);
        if (offset.Length() < MinimumDirectionSegmentMetres)
        {
            bearing = double.NaN;
            return false;
        }

        return CompassBearing.TryFromWorldFacing(offset, out bearing);
    }

    private static string FormatCoordinate(double metres) => metres.ToString("0.##", CultureInfo.InvariantCulture);

    private readonly record struct TrackedMove(CoordinateMoveKind Kind, bool StopRequested);
}
