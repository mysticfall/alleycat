using System.Text.Json;
using Godot;

namespace AlleyCat.Navigation;

public partial class MotionBricksNavigation
{
    internal event Action<JsonElement>? OrdinaryRefusalObserved;
    private static float[] RefusalPoint(Vector3 p) => [p.X, p.Y, p.Z];
    private static float[] RefusalTransform(Transform3D t) => Recording.NavigationRecordingSink.Pack([t]);

    private void PublishOrdinaryRefusal(string reason)
    {
        Action<JsonElement>? observers = OrdinaryRefusalObserved;
        if (observers is null)
        {
            return;
        }
        JsonElement snapshot;
        try
        {
            NavigationRouteSnapshot? route = CurrentRouteSnapshot;
            snapshot = JsonSerializer.SerializeToElement(new
            {
                schema = 3,
                kind = "ordinary-navigation-refusal",
                authority = "Godot NavigationAgent3D",
                reason,
                clock = System.Diagnostics.Stopwatch.GetTimestamp(),
                tick = Engine.GetPhysicsFrames(),
                time = ObservationTime,
                request = CurrentStatus.ActiveRequest,
                revision = CurrentStatus.Revision,
                nativeRevision = _nativeRevision,
                requestedTransform = HasDestination ? RefusalTransform(Destination) : null,
                actorTransform = _actor is null ? null : RefusalTransform(_actor.GlobalTransform),
                anatomicalHeading = RecordingAnatomicalHeading,
                Phase,
                role = RoleDecision,
                selectedRole = ReviewedRole,
                source = _reviewed?.Selection,
                route = route is null ? null : new
                {
                    owned = OwnsRoute(route),
                    generation = route.DestinationRequestGeneration,
                    revision = route.RouteRevision,
                    cursor = route.ActivePathIndex,
                    nextPoint = RefusalPoint(route.NextPathPoint),
                    pointCount = route.PathPoints.Count,
                    truncated = route.PathPoints.Count > DiagnosticPathLimit,
                    points = route.PathPoints.Take(DiagnosticPathLimit).Select(RefusalPoint).ToArray(),
                    route.UsedAcceptedPathFallback,
                    route.WasReplanned
                },
                nativeAgent = new
                {
                    target = RefusalPoint(TargetPosition),
                    cursor = GetCurrentNavigationPathIndex(),
                    reachable = IsTargetReachable(),
                    finished = IsNavigationFinished()
                },
                intentMeaning = "last submitted target placement; not a rejected admission proof",
                intent = SourceRateCurveIntent
            }, _refusalJsonOptions);
        }
        catch (Exception error)
        {
            snapshot = JsonSerializer.SerializeToElement(new
            {
                schema = 3,
                reason,
                unavailable = "refusal-snapshot-failed",
                error = error.GetType().Name
            });
        }
        foreach (Action<JsonElement> observer in observers.GetInvocationList().Cast<Action<JsonElement>>())
        {
            try
            {
                observer(snapshot);
            }
            catch (Exception) { /* Recording cannot alter navigation or another observer. */ }
        }
    }
}
