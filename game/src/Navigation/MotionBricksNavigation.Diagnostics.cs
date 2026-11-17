using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Navigation;

public partial class MotionBricksNavigation
{
    private const int DiagnosticPathLimit = 64;
    private static readonly EventId _refusalDiagnosticEvent = new(7101, "NavigationRefusalDiagnostic");
    private static readonly JsonSerializerOptions _refusalJsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private string? _failureDiagnosticCategory;
    private Vector3? _failureDiagnosticCorridor;

    private void ResetFailureDiagnosticSubmission()
    {
        _failureDiagnosticCategory = null;
        _failureDiagnosticCorridor = null;
    }

    private void LogFailureDiagnostic()
    {
        try
        {
            if (_logger?.IsEnabled(LogLevel.Error) != true)
            {
                return;
            }
            NavigationRouteSnapshot? route = CurrentRouteSnapshot;
            var payload = new
            {
                schema = 2,
                authority = "Godot NavigationAgent3D",
                category = _failureDiagnosticCategory ?? "motion-unavailable",
                request = CurrentStatus.ActiveRequest,
                revision = CurrentStatus.Revision,
                nativeRevision = _nativeRevision,
                actorStart = _actor is null ? null : RefusalPoint(_actor.GlobalPosition),
                requestedTarget = HasDestination ? RefusalPoint(Destination.Origin) : null,
                viaCount = ViaPoints.Count,
                nextNativePoint = _failureDiagnosticCorridor is Vector3 p ? RefusalPoint(p) : null,
                route = route is null ? null : new
                {
                    owned = OwnsRoute(route),
                    revision = route.RouteRevision,
                    generation = route.DestinationRequestGeneration,
                    cursor = route.ActivePathIndex,
                    nextPoint = RefusalPoint(route.NextPathPoint),
                    route.UsedAcceptedPathFallback,
                    route.WasReplanned,
                    pointCount = route.PathPoints.Count,
                    truncated = route.PathPoints.Count > DiagnosticPathLimit,
                    points = route.PathPoints.Take(DiagnosticPathLimit).Select(RefusalPoint).ToArray()
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
            };
            _logger.LogError(_refusalDiagnosticEvent, "Navigation refusal diagnostic: {Diagnostic}", JsonSerializer.Serialize(payload, _refusalJsonOptions));
        }
        catch (Exception) { /* Diagnostics cannot replace the original failure or safe hold. */ }
    }
}
