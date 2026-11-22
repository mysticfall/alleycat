using System.Diagnostics;
using AlleyCat.Motion;
using Godot;

namespace AlleyCat.Navigation;

public partial class MotionBricksNavigation
{
    private const double NativeEarlyReserve = 10.0 / 30;
    private sealed record NativePacket(long Ticket, long Revision, double ContextAt, bool Arrival, bool Idle,
        bool Overlap, Vector3 Start, Vector3 CorridorEnd, bool Curve,
        bool RouteGuided = false, long RouteRevision = 0, RoleSubmission? Role = null);
    private NativePacket? _nativePending;
    private RoleSubmission? _shortPositioningRole;
    private NavigationPathQuery? _shortPositioningQuery;
    private long _nativeRevision, _activeNativeTicket;
    private double _observedAt;
    private bool _earlyNativeSubmission;
    private Vector3 _submissionCorridorEnd;

    internal sealed record NativeHandoffObservation(long PreviousRequest, long Request, long Revision,
        double GameTime, double ContextAt, double Age, double OldRemaining, long PublishedAt, long ConsumedAt,
        bool Accepted, string Reason, bool Arrival, bool Curve, Transform3D[] Previous, Transform3D[] Outgoing, Transform3D[] Incoming);
    internal Action<NativeHandoffObservation>? NativeHandoffObserved
    {
        get; set;
    }

    private void ResetNativeHandoff()
    {
        InvalidateFiniteTurn();
        _nativeDeparturePending = true;
        _nativeRevision++;
        _nativePending = null;
        _shortPositioningRole = null;
        _shortPositioningQuery = null;
        _activeNativeTicket = 0;
        _earlyNativeSubmission = false;
        ResetRoleDecision();
    }

    private void StepNativeHandoff(double delta)
    {
        if (_finiteAwaitingObservation)
        {
            _rig!.Apply(_pose);
            _record = true;
            return;
        }
        if (_reviewed is null)
        {
            throw new InvalidOperationException("Native handoff probe requires reviewed NAV roles.");
        }

        ObservationTime += delta;
        _phaseTime += delta;
        _intent = HasDestination ? Poll(MeasuredTransform()) : default;
        if (HasDestination && NativeTargetUnreachable)
        {
            throw new NavigationRefusalException("Godot navigation agent reports target unreachable.");
        }

        if (_endpoint)
        {
            _endpoint = false;
            if (_idleTransitionActive)
            {
                _idleTransitionActive = false;
                EnterIdle();
            }
            else if (_arrival)
            {
                _arrivalFinished = true;
                _ = TryCommitArrivalSequenceOutcome(CreateLifecycleTerminalContext(MeasuredTransform(),
                    NavigationTerminalReason.ArrivalSequenceCompleted), Engine.GetPhysicsFrames());
            }
            else if (!_pending)
            {
                _needRequest = true;
            }
        }
        if (_idleTransitionRequested && _history!.SamplesRecorded > 0)
        {
            SubmitIdleTransition();
        }

        if (_needRequest && HasDestination && _history!.SamplesRecorded >= 8 && _intent.HasValidSample)
        {
            SubmitWindow();
        }

        if (_segment is not null && !_pending && !_arrival && !_idleTransitionActive && HasDestination &&
            _finiteTurn is null && !RetainShortPositioningTranslation() &&
            _phaseTime >= ((_segment.Length - 1) / 30.0) - NativeEarlyReserve &&
            _phaseTime < (_segment.Length - 1) / 30.0)
        {
            bool activeArrival = _arrival;
            _earlyNativeSubmission = true;
            try
            {
                SubmitWindow();
            }
            finally { _arrival = activeArrival; _earlyNativeSubmission = false; }
        }
        if (_pending && _worker!.TryTake(out SomaInferenceWorker.Result? result))
        {
            long consumedAt = Stopwatch.GetTimestamp();
            NativePacket packet = _nativePending ?? throw new InvalidDataException("Missing native handoff packet.");
            if (result!.Request != packet.Ticket || packet.Revision != _nativeRevision)
            {
                throw new InvalidDataException("Invalidated native handoff packet escaped ownership.");
            }

            if (result.Error is not null)
            {
                throw new InvalidOperationException("Native handoff generation failed.", result.Error);
            }

            // Reconsider against current displayed context before installing work, not a forecast or worker pose.
            // Sampling/decision allocation stays at submission/consumption boundaries, never on every VR tick.
            bool finitePacket = _finiteTurn is not null && packet.Ticket == _finiteTicket;
            if (finitePacket && !packet.Arrival && _reviewed.Role != _finiteTurn!.Role)
            {
                throw new NavigationRefusalException("Finite terminal turn source role changed before consumption.");
            }
            if (!packet.Arrival && !packet.Idle && !finitePacket)
            {
                RefreshRoleDecision(_history!.Context());
            }

            if ((packet.RouteGuided && CurrentRouteSnapshot?.RouteRevision != packet.RouteRevision) ||
                (!finitePacket && packet.Role is { } role && !RoleStillCurrent(role)))
            {
                if (finitePacket)
                {
                    throw new NavigationRefusalException("Finite terminal turn route changed before consumption.");
                }
                // Ordinary Godot rerouting supersedes placement, not inference availability. Never install stale geometry.
                _pending = false;
                _nativePending = null;
                _needRequest = true;
            }
            else
            {
                Transform3D[][] incoming = result.Poses!;
                double age = packet.Overlap ? ObservationTime - packet.ContextAt : 0;
                double remaining = _segment is null ? 0 : ((_segment.Length - 1) / 30.0) - _phaseTime;
                var next = new Transform3D[_pose.Length];
                // Refuse out-of-range ages, never clamp them to a native endpoint as successful continuation.
                bool inRange = age >= 0 && age < (incoming.Length - 1) / 30.0;
                if (inRange)
                {
                    SomaMotionData.Sample(incoming, age, next);
                }

                var oldNow = new Transform3D[_pose.Length];
                if (_segment is not null)
                {
                    SomaMotionData.Sample(_segment, _phaseTime, oldNow);
                }
                else
                {
                    Array.Copy(_pose, oldNow, _pose.Length);
                }

                string reason = !inRange ? "native-age-out-of-range" : packet.Overlap && remaining <= 0 ? "native-overrun-safe-hold" : "eligible";
                bool finiteCandidate = packet.Start.IsFinite() && packet.CorridorEnd.IsFinite() && _actor!.GlobalPosition.IsFinite() &&
                    _lastGround.IsFinite() && next.All(p => p.Origin.IsFinite() && p.Basis.IsFinite());
                if (reason == "eligible" && !finiteCandidate)
                {
                    reason = "native-nonfinite-output";
                }
                NativeHandoffObserved?.Invoke(new NativeHandoffObservation(_activeNativeTicket, packet.Ticket, packet.Revision,
                    ObservationTime, packet.ContextAt, age, remaining, result.PublishedAt, consumedAt,
                    reason == "eligible", reason, packet.Arrival, packet.Curve, (Transform3D[])_pose.Clone(), oldNow, next));
                _pending = false;
                _nativePending = null;
                if (reason != "eligible")
                {
                    ResetFailureDiagnosticSubmission();
                    _failureDiagnosticCategory = reason;
                    _failureDiagnosticCorridor = packet.CorridorEnd;
                    if (reason == "native-overrun-safe-hold" && finiteCandidate)
                    {
                        // A valid result missed this request's overlap; it does not prove the provider unavailable.
                        // Reject it without replay, then use the existing finite failure/idle and replacement lifecycle.
                        LogFailureDiagnostic();
                        Fail(reason);
                        _rig!.Apply(_pose);
                        _record = true;
                    }
                    else
                    {
                        InferenceUnavailable(reason);
                    }
                    return;
                }
                _segment = incoming;
                _activeNativeTicket = packet.Ticket;
                RememberShortPositioningTranslation(packet);
                _arrival = packet.Arrival;
                _idleTransitionActive = packet.Idle;
                _phaseTime = age;
                Phase = packet.Idle ? "failure-to-idle" : packet.Arrival ? "arrival" : Requests == 1 ? "departure" : "sustained-walk";
            }
        }
        if (_pending && ObservationTime - _requestStarted > 30)
        {
            InferenceUnavailable("Native overlap exceeded the existing request budget.");
            return;
        }
        if (_segment is not null)
        {
            SomaMotionData.Sample(_segment, _phaseTime, _pose);
            if (_finiteTurn is not null && _activeNativeTicket == _finiteTicket)
            {
                // NAV plan headings use atan2(forward.X,forward.Z); Godot actor yaw faces -Z at zero.
                double heading = _finitePlayback.AtFrame(_phaseTime * 30, _segment.Length - 1);
                _actor!.GlobalBasis = new Basis(Vector3.Up, (float)(heading - Math.PI));
            }
            PlaybackObserved?.Invoke(_activeNativeTicket, _segment, Math.Clamp(_phaseTime * 30, 0, _segment.Length - 1));
            if (_phaseTime >= (_segment.Length - 1) / 30.0)
            {
                _segment = null;
                _endpoint = true;
                _finiteAwaitingObservation = _finiteTurn is { FinishedCues: false } && !_arrival && _activeNativeTicket == _finiteTicket;
            }
        }
        else if (!HasDestination && !_pending && !_idleTransitionActive && !_idleTransitionRequested)
        {
            SampleIdle();
        }

        Vector3 ground = Ground(_pose[0].Origin);
        KinematicCollision3D? collision = _actor!.MoveAndCollide(SomaMotionData.ModelToWorld * (ground - _lastGround));
        Vector3 movement = ground - _lastGround;
        _lastGround = ground;
        if (collision is not null && movement.LengthSquared() > .000001f && !_idleTransitionActive)
        {
            Fail("Generated root encountered a static obstruction.");
        }

        _rig!.Apply(_pose);
        _record = true;
    }

    private void RememberShortPositioningTranslation(NativePacket packet)
    {
        _shortPositioningRole = null;
        _shortPositioningQuery = null;
        if (!packet.Arrival && !packet.Idle && packet.Role is { Decision.Positioning: true } role &&
            role.SelectedRole.Length > 0 && !role.SelectedRole.EndsWith("_turn", StringComparison.Ordinal) &&
            Math.Max(role.Decision.Inputs.RemainingJourney, role.Decision.Inputs.DestinationDistance) <= Math.Max(0, ShortMoveDistance) &&
            CurrentRouteSnapshot is { } route && OwnsRoute(route))
        {
            _shortPositioningRole = role;
            _shortPositioningQuery = route.Query;
        }
    }

    private bool RetainShortPositioningTranslation() => _shortPositioningRole is { } role &&
        _shortPositioningQuery is { } query && _intent.HasValidSample && !_intent.PositionReached &&
        CurrentRouteSnapshot is { } route && ReferenceEquals(route.Query, query) &&
        route.RouteRevision == role.RouteRevision && RoleStillCurrent(role);
}
