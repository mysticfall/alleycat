using Godot;

namespace AlleyCat.Navigation;

public partial class MotionBricksNavigation
{
    private FiniteTerminalTurnPlan? _finiteTurn;
    private long _finiteTicket, _finiteRouteRevision, _finiteRevision;
    private bool _finiteAwaitingObservation;
    private object? _finiteRequest;

    internal object? TerminalTurnSnapshot
    {
        get => _finiteTurn is { } plan ? new
        {
            revision = _finiteRevision,
            request = _finiteRequest,
            routeRevision = _finiteRouteRevision,
            plan.Role,
            plan.StartingWorldYaw,
            plan.SignedAngle,
            plan.SourceSignedSpan,
            plan.SourceSHA256,
            plan.Allocations,
            plan.Cues,
            plan.Served,
            plan.FinishedCues,
            currentCue = plan.FinishedCues ? null : plan.Current,
            finishWorldYaw = plan.StartingWorldYaw + plan.SignedAngle,
            ticket = _finiteTicket,
            awaitingPostModifierEndpoint = _finiteAwaitingObservation,
            disposition = "active"
        } : field;
        private set;
    }

    private void InvalidateFiniteTurn()
    {
        if (_finiteTurn is { } plan)
        {
            TerminalTurnSnapshot = new
            {
                revision = _finiteRevision,
                request = _finiteRequest,
                plan.Role,
                plan.StartingWorldYaw,
                plan.SignedAngle,
                plan.SourceSignedSpan,
                plan.SourceSHA256,
                plan.Allocations,
                plan.Served,
                plan.Cues,
                ticket = _finiteTicket,
                disposition = plan.FinishedCues && _arrivalFinished ? "completed" : "interrupted"
            };
        }
        _finiteTurn = null;
        _finiteTicket = 0;
        _finiteAwaitingObservation = false;
    }

    private bool SubmitFiniteTurn(Transform3D[][] context)
    {
        if (_finiteTurn is not { } plan)
        {
            return false;
        }
        if (CurrentRouteSnapshot?.RouteRevision != _finiteRouteRevision)
        {
            throw new NavigationRefusalException("Finite terminal turn lost its native route ownership.");
        }
        RefreshRoleDecision(context); // Fresh measured diagnostics; the finite owner keeps its admitted role/cue plan.
        if (plan.FinishedCues)
        {
            // The reached-position contract admitted this finite sequence; no source-translation-radius re-gate.
            _ = SubmitPositioningIdle(context);
        }
        else
        {
            FiniteTerminalTurnPlan.Cue cue = plan.Current;
            Transform3D[][] target = _reviewed!.FiniteTurnTargets(plan.Role, ObservationTime, cue.VirtualFrame, cue.LocalPhase,
                cue.CycleStart - Math.PI, cue.Scale, Ground(context[3][0].Origin));
            _arrival = false;
            SubmitPlacedPositioning(context, target, _reviewed.ReferenceSpeed, _reviewed.TurnReferenceAngularVelocity(plan.SignedAngle > 0));
        }
        return true;
    }

    private void ObserveFiniteTurnEndpoint()
    {
        if (!_finiteAwaitingObservation || _finiteTurn is not { FinishedCues: false } plan || _activeNativeTicket != _finiteTicket)
        {
            return;
        }
        // Called only after _rig.Observe and _history.Record at SkeletonUpdated, never from a predicted endpoint.
        plan.Service();
        _finiteAwaitingObservation = false;
    }
}
