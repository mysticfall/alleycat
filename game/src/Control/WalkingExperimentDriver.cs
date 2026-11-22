using AlleyCat.Motion;
using Godot;

namespace AlleyCat.Control;

// Normal player source-phase and generated-playback owner; visual quality remains independently reviewed.
internal sealed class WalkingExperimentDriver(CharacterBody3D actor, Skeleton3D skeleton, SomaMotionData data,
    string model, Func<SomaInferenceWorker.ISession, SomaInferenceWorker.ISession> decorate)
{
    private readonly CharacterBody3D _actor = actor;
    private readonly SomaMotionData _data = data;
    private readonly Transform3D[] _treeDestination = new Transform3D[data.Names.Length];
    private Transform3D _settlingModelToSource;
    private Vector3 _heldLocalHips;
    private bool _replacingStop;
    private bool _destinationReady;
    private ulong? _waitingForMixer;
    private readonly NativeSomaRig _rig = new(data, skeleton);
    private readonly RealisedPoseHistory _history = new(data.Names.Length);
    private readonly Func<SomaInferenceWorker.ISession, SomaInferenceWorker.ISession> _decorate = decorate;
    private readonly string _model = model;
    internal Func<SomaInferenceWorker.ISession>? SessionFactory
    {
        get; init;
    }
    private readonly Transform3D[] _observed = new Transform3D[data.Names.Length];
    private SomaInferenceWorker? _worker;
    private Transform3D[][]? _segment;
    private Vector2 _movement;
    private float _turn;
    private bool _request;
    internal required ReviewedRoleSampler ReviewedRoles
    {
        get; init;
    }
    internal MetadataStationaryTurn? MetadataTurn
    {
        get; private set;
    }
    internal MetadataStationaryTurn.Snapshot? LastMetadataTurnRequest
    {
        get; private set;
    }
    private long _metadataTurnTicket;
    private float _metadataPendingYaw;
    internal float RoleDeadzone { get; init; } = .15f;
    internal SourcePhaseClock.Selection? ReviewedSelection
    {
        get; private set;
    }
    internal SourcePhaseClock.Selection? LastReviewedRequest
    {
        get; private set;
    }
    internal string ReviewedSourceStatus { get; private set; } = "ready";
    public double PredictionSeconds
    {
        get; private set;
    }
    public Vector3 RequestedVelocity
    {
        get; private set;
    }
    public double SnapshotSeconds { get; init; } = 0.2;
    private bool _inputChangedSinceSubmission;
    internal bool AllowsSnapshotSubmission() => _turn != 0 || _inputChangedSinceSubmission;
    public float TargetChangeMetres { get; init; } = 0.08f;
    public float TargetChangeDegrees { get; init; } = 10;
    public float SharpChangeDegrees { get; init; } = 45;
    public Transform3D? VirtualTarget
    {
        get; private set;
    }
    public Transform3D? LastSubmittedTarget
    {
        get; private set;
    }
    private Vector2 _submittedMovement, _activeMovement;
    private double _nextSnapshot;
    public float WalkingSteeringDegreesPerSecond { get; init; } = 60;
    public float BackwardSpeedRatio { get; init; } = 0.75f;
    public double SteeringYaw
    {
        get; private set;
    }
    private double _predictionYaw;
    private Vector3 _predictionVelocity;
    private double SteeringRate => _movement != Vector2.Zero
        ? -Mathf.DegToRad(WalkingSteeringDegreesPerSecond) * _turn : 0;
    public string RequestReason { get; private set; } = "initial";
    public double ObservedAt
    {
        get; private set;
    }
    public Transform3D[] SuppliedPose { get; } = new Transform3D[data.Names.Length];
    public bool IsPlaying => _segment is not null;
    public int DiscardedResults => _worker?.DiscardedResults ?? 0;
    public event Action<long>? PlaybackStarted;
    public event Action<long>? PlaybackCompleted;
    public event Action? IntentRetired;
    internal Action<SomaInferenceWorker.Result>? ReturnedObserved
    {
        get; init;
    }
    internal double RecordingFrame { get; private set; } = -1;
    // Last immutable submission/adoption, not the latest compatible input magnitude.
    internal float SubmittedTurnIntent
    {
        get; private set;
    }
    internal float PlayedTurnIntent
    {
        get; private set;
    }
    internal bool RecordingAdvanced
    {
        get; private set;
    }
    internal bool RecordingPending
    {
        get; private set;
    }
    internal Transform3D[] RecordingRawPose { get; } = new Transform3D[data.Names.Length];
    private Basis _frame = Basis.Identity;
    private Vector3 _lastGround;
    private double _time, _playTime, _submittedAt;
    private ulong? _settlingObservedFrame;
    private bool _agedSplice, _hasPose, _settling, _endpoint;
    private bool _holdFacingOnRelease;
    private long _ticket;
    // Temporary opt-in headset trace. No callbacks installed during normal play.
    internal Action<long, string>? TraceRetirement
    {
        get; set;
    }
    internal Action<long, string>? TraceWorkerEvent
    {
        get; set;
    }
    public bool Reconcile
    {
        get; init;
    }
    public Action<object>? Sampled
    {
        get; set;
    }
    private const double DecaySeconds = 0.2;
    private readonly Transform3D[] _heldTurnPose = new Transform3D[data.Names.Length];
    private bool _turnSuccessor;
    private double _turnJoinSeconds;
    private readonly Quaternion[] _rotationOffsets = new Quaternion[data.Names.Length];
    private Vector3 _actorObserved, _rootVelocity, _positionOffset, _velocityOffset;
    private double _decayTime = DecaySeconds;
    private bool _haveObservation;
    public string Phase { get; private set; } = "tracked-idle";
    public long LastAppliedRequest
    {
        get; private set;
    }
    public int Requests
    {
        get; private set;
    }
    public double LastResponseSeconds
    {
        get; private set;
    }
    public Task? Completion => _worker?.Completion;
    public event Action<long, Transform3D[][], Transform3D[][]>? WindowSubmitted;
    public event Action<object>? Spliced;

    public void Observe()
    {
        float oldHeight = _observed[0].Origin.Y;
        _rig.Observe(_observed);
        if (_haveObservation && _time > ObservedAt)
        {
            float dt = (float)(_time - ObservedAt);
            _rootVelocity = SomaMotionData.ModelToWorld.Inverse() * ((_actor.GlobalPosition - _actorObserved) / dt);
            _rootVelocity.Y = (_observed[0].Origin.Y - oldHeight) / dt;
        }
        _actorObserved = _actor.GlobalPosition;
        _haveObservation = true;
        _history.Record(_time, _observed);
        if (_settling && _endpoint)
        {
            _settlingObservedFrame ??= Engine.GetPhysicsFrames();
        }
        ObservedAt = _time;
    }

    public bool SetIntent(Vector2 movement, Basis frame, float turn = 0)
    {
        if (movement != _movement || turn != _turn)
        {
            bool roleChanged = ReviewedRoles.SetIntent(ReviewedRoleSampler.Classify(movement, turn, RoleDeadzone), _time, GenerationLookaheadSeconds);
            _inputChangedSinceSubmission = true;
            Vector2 direction = TerminalDirection(movement, turn);
            bool retainTurn = !roleChanged && RetainsStationaryTurn(movement, turn);
            bool retire = !retainTurn && (roleChanged || movement == Vector2.Zero || _movement == Vector2.Zero ||
                DirectionChange(direction, _submittedMovement) >= SharpChangeDegrees ||
                DirectionChange(direction, _activeMovement) >= SharpChangeDegrees);
            if (movement == Vector2.Zero || _movement == Vector2.Zero)
            {
                SteeringYaw = 0;
            }
            if (retire)
            {
                TraceRetirement?.Invoke(_ticket, movement == Vector2.Zero && turn == 0 ? "re-centre" :
                    turn * _turn < 0 && movement == Vector2.Zero && _movement == Vector2.Zero ? "reversal" :
                    roleChanged ? "role-change" : "incompatible-input");
            }
            // A turn's world hips include an actor-relative residual. It is still articulation during settling.
            _holdFacingOnRelease = MetadataTurn is not null && movement == Vector2.Zero && turn == 0;
            _movement = movement;
            _turn = turn;
            _frame = frame;
            {
                string role = ReviewedRoleSampler.Classify(movement, turn, RoleDeadzone);
                if (role is not ("left_turn" or "right_turn"))
                {
                    MetadataTurn?.Retire();
                    MetadataTurn = null;
                }
                else if (MetadataTurn?.Reference.Role != role)
                {
                    MetadataTurn?.Retire();
                    MetadataTurn = new MetadataStationaryTurn(ReviewedRoles.TurnReference(role), _actor.GlobalRotation.Y);
                }
            }
            if (!retire)
            {
                return false;
            }
            RetireIntent();
            return true;
        }
        return false;
    }

    private bool RetainsStationaryTurn(Vector2 movement, float turn) =>
        movement == Vector2.Zero && _movement == Vector2.Zero && !_settling &&
        SameTurnDirection(_turn, turn) &&
        (!RecordingPending || SameTurnDirection(SubmittedTurnIntent, turn)) &&
        (_segment is null || SameTurnDirection(PlayedTurnIntent, turn));

    // Intensity is prospective. Releases and sign changes are different intents, not speed changes.
    private static bool SameTurnDirection(float owned, float requested) => float.IsFinite(owned) && float.IsFinite(requested) &&
        owned != 0 && requested != 0 && (owned > 0) == (requested > 0);

    private void RetireIntent()
    {
        _replacingStop = _hasPose && (_settling || _replacingStop) && (_movement != Vector2.Zero || _turn != 0);
        _destinationReady = false;
        _waitingForMixer = null;
        _settlingObservedFrame = null;
        MetadataTurn?.Retire();
        _worker?.CancelRequest();
        _segment = null;
        RecordingPending = _endpoint = false;
        _settling = _movement == Vector2.Zero && _turn == 0;
        _decayTime = DecaySeconds;
        VirtualTarget = LastSubmittedTarget = null;
        _submittedMovement = _activeMovement = Vector2.Zero;
        _nextSnapshot = _time;
        _request = true;
        RequestReason = _settling ? "release" : "input-change";
        Phase = _settling ? "settling-requested" : "intent-changed";
        IntentRetired?.Invoke();
    }

    private static float DirectionChange(Vector2 a, Vector2 b) => a == Vector2.Zero || b == Vector2.Zero
        ? 0 : Mathf.RadToDeg(Mathf.Abs(a.AngleTo(b)));

    private Vector2 TerminalDirection(Vector2 movement, float turn)
    {
        double yaw = SteeringYaw - (Mathf.DegToRad(WalkingSteeringDegreesPerSecond) * turn * GenerationLookaheadSeconds);
        System.Numerics.Vector2 direction = WalkingSteeringPrediction.Rotate(
            WalkingSteeringPrediction.RequestedTravel(new System.Numerics.Vector2(movement.X, movement.Y), BackwardSpeedRatio), yaw);
        return new Vector2(direction.X, direction.Y);
    }

    private Basis PredictedFrame(double time) => new Basis(Vector3.Up, (float)(_predictionYaw + (SteeringRate * time))) * _frame;

    private Vector3 PredictedOffset(double time)
    {
        System.Numerics.Vector2 offset = WalkingSteeringPrediction.Offset(
            new System.Numerics.Vector2(_rootVelocity.X, _rootVelocity.Z),
            new System.Numerics.Vector2(_predictionVelocity.X, _predictionVelocity.Z), SteeringRate, PredictionSeconds, time);
        return new Vector3(offset.X, 0, offset.Y);
    }

    // One fixed experimental guide horizon: 14 tokens -> 50 valid poses -> 49 intervals at 30 Hz.
    // This is separate from input comparison timing, not a promised native arrival time.
    public const double GenerationLookaheadSeconds = 49.0 / 30;
    public Transform3D? GenerationTarget
    {
        get; private set;
    }
    public Vector3 GuideVelocity
    {
        get; private set;
    }

    private void UpdateVirtualTarget()
    {
        if (_movement == Vector2.Zero || !_haveObservation)
        {
            VirtualTarget = null;
            GenerationTarget = null;
            GuideVelocity = Vector3.Zero;
            PredictionSeconds = 0;
            RequestedVelocity = Vector3.Zero;
            return;
        }
        Transform3D root = ReviewedRoles.GuideRoot;
        float fullSpeed = ReviewedRoles.Role == "backward" ? ReviewedRoles.ForwardReferenceSpeed : ReviewedRoles.ReferenceSpeed;
        // Keep the original magnitude-selected forward stage as the ratio's speed reference.
        // This one bout-local command feeds both horizons, terminal velocity and every guide root.
        System.Numerics.Vector2 travel = WalkingSteeringPrediction.RequestedTravel(
            new System.Numerics.Vector2(_movement.X, _movement.Y), BackwardSpeedRatio);
        Vector3 requested = new(travel.X, 0, travel.Y);
        _predictionYaw = SteeringYaw - (SteeringRate * (_time - ObservedAt));
        _predictionVelocity = SomaMotionData.ModelToWorld.Inverse() *
            (PredictedFrame(0) * requested) * fullSpeed;
        RequestedVelocity = SomaMotionData.ModelToWorld.Inverse() *
            (new Basis(Vector3.Up, (float)SteeringYaw) * _frame * requested) * fullSpeed;
        // Targets only: integrate a linear transition from measured realised velocity to held-input
        // velocity. The deadline is the next comparison; include the age of the actual observation.
        PredictionSeconds = _nextSnapshot - ObservedAt;
        Vector3 offset = PredictedOffset(PredictionSeconds);
        VirtualTarget = new Transform3D(PredictedFrame(PredictionSeconds) * root.Basis,
            Ground(_observed[0].Origin) + offset + new Vector3(0, root.Origin.Y, 0));
        // Both horizons and terminal samples use this same continuing curve and actual initial velocity.
        System.Numerics.Vector2 velocity = WalkingSteeringPrediction.Velocity(
            new System.Numerics.Vector2(_rootVelocity.X, _rootVelocity.Z),
            new System.Numerics.Vector2(_predictionVelocity.X, _predictionVelocity.Z), SteeringRate, PredictionSeconds, GenerationLookaheadSeconds);
        GuideVelocity = new Vector3(velocity.X, 0, velocity.Y);
        Vector3 guideOffset = PredictedOffset(GenerationLookaheadSeconds);
        GenerationTarget = new Transform3D(PredictedFrame(GenerationLookaheadSeconds) * root.Basis,
            Ground(_observed[0].Origin) + guideOffset + new Vector3(0, root.Origin.Y, 0));
    }

    private bool VirtualTargetChanged() => GenerationTarget is { } desired && LastSubmittedTarget is { } submitted &&
        (Ground(desired.Origin - submitted.Origin).Length() >= TargetChangeMetres ||
         Mathf.RadToDeg(desired.Basis.Z.AngleTo(submitted.Basis.Z)) >= TargetChangeDegrees);

    public void Advance(double delta, Vector2 movement, Basis frame, float turn = 0, bool sticksNeutral = true)
    {
        RecordingAdvanced = false;
        if (MetadataTurn is not null)
        {
            if (RecordingPending && _segment is null && !Mathf.IsEqualApprox(_metadataPendingYaw, _actor.GlobalRotation.Y))
            {
                // No other stationary owner may move the actor during native work. Replace from actual context.
                TraceRetirement?.Invoke(_ticket, "root-drift");
                RetireIntent();
                // The displaced plan no longer owns this world anchor; restart the bout from the observed actor.
                MetadataTurn = new MetadataStationaryTurn(MetadataTurn.Reference, _actor.GlobalRotation.Y);
                RequestReason = "turn-context-root-drift";
            }
        }
        _time += delta;
        Vector3 actorBefore = _actor.GlobalPosition;
        _ = SetIntent(movement, frame, turn);
        if (_movement != Vector2.Zero)
        {
            SteeringYaw += SteeringRate * delta;
        }
        bool snapshotDue = _time >= _nextSnapshot;
        if (snapshotDue)
        {
            // A long native hold can outlive compatibility even with unchanged stick values.
            Vector2 direction = TerminalDirection(_movement, _turn);
            if (_movement != Vector2.Zero &&
                (DirectionChange(direction, _submittedMovement) >= SharpChangeDegrees ||
                 DirectionChange(direction, _activeMovement) >= SharpChangeDegrees))
            {
                TraceRetirement?.Invoke(_ticket, "stale-movement-direction");
                RetireIntent();
            }
            // Roll a due deadline BEFORE predicting: an update predicts the following cycle,
            // never a zero/negative horizon. Other frames predict the remaining time to that deadline.
            _nextSnapshot = _time + SnapshotSeconds;
        }
        UpdateVirtualTarget();
        if (_request && _settling && !_destinationReady)
        {
            _waitingForMixer ??= Engine.GetPhysicsFrames();
            if (Engine.GetPhysicsFrames() > _waitingForMixer + 1)
            {
                throw new InvalidOperationException("The player animation tree did not supply a release destination.");
            }
        }
        else if (_request && _history.SamplesRecorded >= 8)
        {
            _request = false;
            Submit(false);
        }
        if (_endpoint && !RecordingPending && (!_settling || (sticksNeutral && !_request && _settlingObservedFrame < Engine.GetPhysicsFrames())))
        {
            _endpoint = false;
            if (_settling)
            {
                Phase = "tracked-idle";
                _hasPose = false;
                _heldLocalHips = Vector3.Zero;
                _settlingObservedFrame = null;
                _rig.Relinquish();
            }
            else
            {
                RequestReason = "segment-end";
                Submit(false, turnSuccessor: MetadataTurn is not null && _metadataTurnTicket == LastAppliedRequest);
            }
        }
        if (snapshotDue)
        {
            // Compare to the last actual submission, never to the preceding sample. Pending useful work
            // finishes; the continuously updated desired target survives until an eligible submission.
            if (!RecordingPending && _segment is not null && AllowsSnapshotSubmission() && VirtualTargetChanged())
            {
                RequestReason = "virtual-target-change";
                Submit(true);
            }
        }
        // Upstream example's ten-frame early transition. Context is the latest actual observation,
        // NOT an old segment's future endpoint. Old playback continues only while native work runs.
        if (_segment is not null && _movement != Vector2.Zero && !RecordingPending && !_settling &&
            _playTime >= ((_segment.Length - 1) / 30.0) - (10.0 / 30))
        {
            RequestReason = "early-transition";
            Submit(true);
        }
        object? splice = null;
        if (RecordingPending && _worker!.TryTake(out SomaInferenceWorker.Result? result))
        {
            if (result!.Request != _ticket)
            {
                throw new InvalidDataException("Superseded continuous result escaped worker ownership.");
            }
            if (result!.Error is not null)
            {
                throw result.Error;
            }
            RecordingPending = false;
            _endpoint = false;
            _replacingStop = false;
            LastResponseSeconds = _time - _submittedAt;
            LastAppliedRequest = result.Request;
            PlayedTurnIntent = SubmittedTurnIntent;
            _activeMovement = _submittedMovement;
            _segment = result.Poses!;
            // Explicit diagnostic choice: sample at context age while old playback advanced.
            // Optional measured splice reconciliation follows; history remains actual, never predicted.
            _playTime = _agedSplice ? _time - _contextAtSubmit : 0;
            var next = new Transform3D[SuppliedPose.Length];
            SomaMotionData.Sample(_segment, _playTime, next);
            _turnJoinSeconds = _turnSuccessor ? Math.Min(DecaySeconds, (_segment.Length - 1) / 60.0) : 0;
            if (_turnJoinSeconds > 0)
            {
                // Context is the realised pose. The downstream modifiers' continuous input,
                // however, is the last supplied pose, not their output from the previous tick.
                SuppliedPose.CopyTo(_heldTurnPose, 0);
            }
            if (Reconcile && _agedSplice)
            {
                var after = new Transform3D[SuppliedPose.Length];
                SomaMotionData.Sample(_segment, _playTime + delta, after);
                Vector3 newVelocity = (after[0].Origin - next[0].Origin) / (float)delta;
                Vector3 anchor = new(_lastGround.X, _observed[0].Origin.Y, _lastGround.Z);
                _positionOffset = anchor + (_rootVelocity * (float)delta) - next[0].Origin;
                _velocityOffset = _rootVelocity - newVelocity;
                for (int j = 0; j < SuppliedPose.Length; j++)
                {
                    Basis desired = _observed[j].Basis;
                    if (j == 0)
                    {
                        Vector3 facing = (SomaMotionData.ModelToWorld * desired).Z;
                        float yaw = Mathf.Atan2(-facing.X, -facing.Z);
                        desired = new Basis(Vector3.Up, _actor.GlobalRotation.Y - yaw) * desired;
                    }
                    _rotationOffsets[j] = (desired * next[j].Basis.Inverse()).Orthonormalized().GetRotationQuaternion();
                }
                _decayTime = 0;
            }
            splice = new
            {
                time = _time,
                ticket = result.Request,
                contextTime = _contextAtSubmit,
                contextAge = _time - _contextAtSubmit,
                ageAligned = _agedSplice,
                sampleTime = _playTime,
                previous = _hasPose ? SuppliedPose.Select(Flatten).ToArray() : [],
                next = next.Select(Flatten).ToArray(),
                commandedRootDelta = Position(_settling ? Vector3.Zero : SomaMotionData.ModelToWorld * (Ground(next[0].Origin) - _lastGround)),
                actorBefore = Position(_actor.GlobalPosition)
                ,
                reconciled = Reconcile && _agedSplice,
                observedVelocity = Position(_rootVelocity),
                positionOffset = Position(_positionOffset),
                velocityOffset = Position(_velocityOffset),
                decaySeconds = DecaySeconds,
                rotationOffsets = Reconcile && _agedSplice ? _rotationOffsets.Select(q => new[] { q.X, q.Y, q.Z, q.W }).ToArray() : []
            };
            _hasPose = true;
            Phase = _settling ? "settling" : _movement != Vector2.Zero ? "moving" : "turning";
            PlaybackStarted?.Invoke(result.Request);
        }
        if (RecordingPending && _time - _submittedAt > 30)
        {
            throw new TimeoutException("Continuous walking exceeded the 30-second native request budget.");
        }
        if (_segment is not null)
        {
            RecordingFrame = Math.Clamp(_playTime * 30, 0, _segment.Length - 1);
            RecordingAdvanced = true;
            SomaMotionData.Sample(_segment, _playTime, SuppliedPose);
            Array.Copy(SuppliedPose, RecordingRawPose, SuppliedPose.Length);
            if (_playTime < _turnJoinSeconds)
            {
                float u = (float)(_playTime / _turnJoinSeconds);
                float weight = u * u * (3 - (2 * u));
                for (int j = 0; j < SuppliedPose.Length; j++)
                {
                    SuppliedPose[j] = SomaMotionData.Blend(_heldTurnPose[j], SuppliedPose[j], weight);
                }
                // Planar travel belongs to the actor, not the visual splice. Keep the
                // sampled X/Z so the first delta starts at the realised submission root.
                Vector3 sampledRoot = RecordingRawPose[0].Origin;
                SuppliedPose[0].Origin = new Vector3(sampledRoot.X, SuppliedPose[0].Origin.Y, sampledRoot.Z);
            }
            if (Reconcile && _decayTime < DecaySeconds)
            {
                float u = (float)(_decayTime / DecaySeconds);
                float weight = (2 * u * u * u) - (3 * u * u) + 1;
                float velocityWeight = (float)DecaySeconds * ((u * u * u) - (2 * u * u) + u);
                SuppliedPose[0].Origin += (_positionOffset * weight) + (_velocityOffset * velocityWeight);
                for (int j = 0; j < SuppliedPose.Length; j++)
                {
                    SuppliedPose[j].Basis = new Basis(Quaternion.Identity.Slerp(_rotationOffsets[j], weight)) * SuppliedPose[j].Basis;
                }
            }
            Vector3 ground = Ground(SuppliedPose[0].Origin);
            if (_settling)
            {
                // The collider receives no generated travel during a stop. Interpret the returned
                // hips in the tree request's frame; external movement carries that local pose.
                _heldLocalHips = _settlingModelToSource * SuppliedPose[0].Origin;
            }
            else
            {
                // An interrupted stop leaves an actor-local reference, not a new travel
                // demand. Keep it fixed through travel; the next stop replaces it absolutely.
                _ = _actor.MoveAndCollide(SomaMotionData.ModelToWorld * (ground - _lastGround));
            }
            _lastGround = ground;
            if (MetadataTurn?.Owner is { } owner && _metadataTurnTicket == LastAppliedRequest)
            {
                // Apply the world pose unchanged below: NativeSomaRig factors this actor frame out of the hips.
                _actor.GlobalBasis = new Basis(Vector3.Up, (float)owner.Playback.AtFrame(RecordingFrame, _segment.Length - 1));
            }
            else if (!_holdFacingOnRelease)
            {
                Vector3 forward = (SomaMotionData.ModelToWorld * SuppliedPose[0].Basis).Z;
                _actor.GlobalBasis = new Basis(Vector3.Up, Mathf.Atan2(-forward.X, -forward.Z));
            }
            if (_playTime >= (_segment.Length - 1) / 30.0)
            {
                _segment = null;
                _endpoint = true;
                if (MetadataTurn is { } metadata && LastMetadataTurnRequest is { } completed && _metadataTurnTicket == LastAppliedRequest)
                {
                    _ = metadata.Complete(completed);
                }
                PlaybackCompleted?.Invoke(LastAppliedRequest);
            }
            _playTime += delta;
        }
        if (_hasPose)
        {
            _rig.Apply(SuppliedPose, _heldLocalHips);
        }
        if (RecordingPending && _segment is null)
        {
            Phase = "inference-hold";
        }
        if (splice is not null)
        {
            Spliced?.Invoke(splice);
        }
        Sampled?.Invoke(new
        {
            time = _time,
            physicsFrame = Engine.GetPhysicsFrames(),
            ticket = LastAppliedRequest,
            state = Phase,
            sampleTime = _playTime - delta,
            contextTime = _contextAtSubmit,
            decayTime = _decayTime,
            decaySeconds = DecaySeconds,
            reconcile = Reconcile,
            reviewedSelection = ReviewedSelection,
            reviewedSourceStatus = ReviewedSourceStatus,
            metadataTurn = MetadataTurn is { } metadataState ? new
            {
                metadataState.Cycle,
                metadataState.EventIndex,
                metadataState.Epoch,
                latestMagnitude = Math.Abs(_turn),
                owner = metadataState.Owner,
                lastSubmitted = LastMetadataTurnRequest,
                ticket = _metadataTurnTicket
            } : null,
            observedAtStart = _observed.Select(Flatten).ToArray(),
            raw = RecordingRawPose.Select(Flatten).ToArray(),
            applied = SuppliedPose.Select(Flatten).ToArray(),
            actorBefore = Position(actorBefore),
            actorAfter = Position(_actor.GlobalPosition),
            positionOffset = Position(_positionOffset),
            velocityOffset = Position(_velocityOffset)
        });
        _decayTime += delta;
    }

    private double _contextAtSubmit;
    private void Submit(bool agedSplice, bool turnSuccessor = false)
    {
        bool reviewedRoleActive = ReviewedRoles.Role.Length > 0 && !_settling;
        if (_worker is null)
        {
            string frames = ProjectSettings.GlobalizePath("res://data/motionbricks/model-frames.json");
            _worker = Game.Instance.MotionWorkers.Create(() =>
            {
                SomaInferenceWorker.ISession session = SessionFactory?.Invoke() ?? new SomaInference(_data, _model, frames);
                try
                {
                    return _decorate(session);
                }
                catch { session.Dispose(); throw; }
            }, ReturnedObserved);
            _worker.TraceWorkerEvent = TraceWorkerEvent;
        }
        Transform3D[][] context = _history.Context();
        bool moving = _movement != Vector2.Zero && !_settling;
        Basis yaw = moving ? PredictedFrame(GenerationLookaheadSeconds) : _actor.GlobalBasis.Orthonormalized();
        Vector3 placement = Ground(context[3][0].Origin);
        if (moving)
        {
            placement = Ground(GenerationTarget!.Value.Origin);
        }
        Transform3D[][] target;
        if (reviewedRoleActive)
        {
            if (MetadataTurn is { } metadata)
            {
                LastMetadataTurnRequest = metadata.Select(Math.Abs(_turn));
                target = ReviewedRoles!.FiniteTurnTargets(LastMetadataTurnRequest.Role, _time, LastMetadataTurnRequest.WindowStart,
                    LastMetadataTurnRequest.LocalPhase, LastMetadataTurnRequest.CycleStart, LastMetadataTurnRequest.Scale, placement);
                ReviewedSourceStatus = "primary lift on allocated cycle; intended phase is not displayed foot phase";
            }
            else
            {
                target = ReviewedRoles!.Targets(_time, GenerationLookaheadSeconds, yaw, placement, moving ? _movement.Length() : 1);
            }
            ReviewedSelection = ReviewedRoles.Selection;
            if (moving)
            {
                // Same held-input curve as the generation guide; retain source height/articulation and local yaw.
                for (int i = 0; i < 4; i++)
                {
                    double future = GenerationLookaheadSeconds + (i / 30.0);
                    Vector3 position = Ground(_observed[0].Origin) + PredictedOffset(future);
                    position.Y = target[i][0].Origin.Y;
                    Basis steering = PredictedFrame(future) * PredictedFrame(GenerationLookaheadSeconds).Inverse();
                    target[i][0] = new Transform3D(steering * target[i][0].Basis, position);
                }
            }
        }
        else
        {
            target = new Transform3D[4][];
            for (int i = 0; i < 4; i++)
            {
                target[i] = (Transform3D[])_treeDestination.Clone();
            }
        }
        if (!agedSplice)
        {
            _lastGround = Ground(context[3][0].Origin);
        }
        _agedSplice = agedSplice;
        _turnSuccessor = turnSuccessor;
        _contextAtSubmit = ObservedAt;
        Transform3D[]? presentationStart = null;
        if (_hasPose && (_settling || _replacingStop))
        {
            presentationStart = (Transform3D[])SuppliedPose.Clone();
            presentationStart[0] = _rig.PresentedRoot();
            if (!_settling)
            {
                // Replacement root deltas start at realised context. The retained local
                // stance already positions the skeleton; do not command it as frame-zero travel.
                presentationStart[0].Origin = _lastGround + (Vector3.Up * presentationStart[0].Origin.Y);
            }
        }
        _ticket = _worker.Submit(context, target, presentationStart);
        if (MetadataTurn is not null)
        {
            _metadataTurnTicket = _ticket;
            _metadataPendingYaw = _actor.GlobalRotation.Y;
        }
        SubmittedTurnIntent = MetadataTurn is not null && LastMetadataTurnRequest is { } turnOwner
            ? Math.Sign(_turn) * (float)turnOwner.Magnitude : moving || _settling ? 0 : _turn;
        LastReviewedRequest = reviewedRoleActive ? ReviewedSelection : null;
        _inputChangedSinceSubmission = false;
        LastSubmittedTarget = target[0][0];
        _submittedMovement = TerminalDirection(_movement, _turn);
        _submittedAt = _time;
        Requests++;
        RecordingPending = true;
        Phase = agedSplice ? "moving-generating" : "inference-hold";
        WindowSubmitted?.Invoke(_ticket, context, target);
    }

    public void Stop() => _worker?.Dispose();
    internal void ReadTreeDestination()
    {
        if (_request && _settling && !_destinationReady)
        {
            _settlingModelToSource = _rig.ReadTreeDestination(_treeDestination);
            _destinationReady = true;
        }
    }
    public void ApplyHeldPose()
    {
        if (_hasPose)
        {
            _rig.Apply(SuppliedPose, _heldLocalHips);
        }
    }
    private static Vector3 Ground(Vector3 v) => new(v.X, 0, v.Z);
    private static float[] Position(Vector3 v) => [v.X, v.Y, v.Z];
    private static float[] Flatten(Transform3D t) => [t.Origin.X, t.Origin.Y, t.Origin.Z,
        t.Basis.X.X, t.Basis.X.Y, t.Basis.X.Z, t.Basis.Y.X, t.Basis.Y.Y, t.Basis.Y.Z, t.Basis.Z.X, t.Basis.Z.Y, t.Basis.Z.Z];
}

internal sealed class WalkingGuideSession(SomaInferenceWorker.ISession inner,
    Action<Transform3D[][]> record, bool lineTargets = false, Action<object>? recordLine = null) : SomaInferenceWorker.ISession
{
    private readonly WalkingGuidePlacement _placement = new(lineTargets, recordLine);
    public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target) => inner.Generate(context, Adjust(context, target));
    public Transform3D[][] GeneratePresented(Transform3D[][] context, Transform3D[][] target, Transform3D[] start)
        => inner.GeneratePresented(context, Adjust(context, target), start);
    private Transform3D[][] Adjust(Transform3D[][] context, Transform3D[][] target)
    {
        Transform3D[][] adjusted = _placement.Adjust(context, target);
        record(adjusted);
        return adjusted;
    }
    public void Dispose() => inner.Dispose();
}

internal sealed class WalkingGuidePlacement(bool lineTargets, Action<object>? recordLine = null)
{
    private Vector3? _lineOrigin;
    private Vector3 _lineDirection;
    public Vector3? Origin => _lineOrigin;
    public Vector3 Direction => _lineDirection;
    public void Reset() => _lineOrigin = null;
    public Transform3D[][] Adjust(Transform3D[][] context, Transform3D[][] target, float horizon = 0.9f)
    {
        Transform3D[][] adjusted = [.. target.Select(p => (Transform3D[])p.Clone())];
        Vector3 displacement = target[0][0].Origin - context[3][0].Origin;
        displacement.Y = 0;
        if (displacement.LengthSquared() > 1e-8f)
        {
            if (_lineOrigin is null)
            {
                _lineOrigin = new Vector3(context[3][0].Origin.X, 0, context[3][0].Origin.Z);
                _lineDirection = displacement.Normalized();
            }
            Vector3 velocity = (target[3][0].Origin - target[0][0].Origin) * 10;
            velocity.Y = 0;
            float magnitude = displacement.Length() / horizon;
            Vector3 desiredVelocity = displacement.Normalized() * velocity.Length() * magnitude;
            for (int i = 0; i < 4; i++)
            {
                Vector3 position = target[0][0].Origin + (desiredVelocity * (i / 30f));
                position.Y = target[i][0].Origin.Y;
                adjusted[i][0].Origin = position;
            }
            float[][] before = [.. adjusted.Select(p => new[] { p[0].Origin.X, p[0].Origin.Y, p[0].Origin.Z })];
            if (lineTargets)
            {
                for (int i = 0; i < 4; i++)
                {
                    Vector3 position = _lineOrigin.Value + (_lineDirection * (adjusted[i][0].Origin - _lineOrigin.Value).Dot(_lineDirection));
                    position.Y = adjusted[i][0].Origin.Y;
                    adjusted[i][0].Origin = position;
                }
            }
            recordLine?.Invoke(new
            {
                lineTargets,
                origin = new[] { _lineOrigin.Value.X, _lineOrigin.Value.Y, _lineOrigin.Value.Z },
                direction = new[] { _lineDirection.X, _lineDirection.Y, _lineDirection.Z },
                contextRoot = new[] { context[3][0].Origin.X, context[3][0].Origin.Y, context[3][0].Origin.Z },
                before,
                after = adjusted.Select(p => new[] { p[0].Origin.X, p[0].Origin.Y, p[0].Origin.Z }).ToArray()
            });
        }
        return adjusted;
    }
}
