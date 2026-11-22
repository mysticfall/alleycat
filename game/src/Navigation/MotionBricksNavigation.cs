using System.Runtime.CompilerServices;
using AlleyCat.Common;
using AlleyCat.Core.Logging;
using AlleyCat.Motion;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Navigation;

/// <summary>Single-NPC generated playback. Navigation owns routes; this node alone translates the collision root.</summary>
[GlobalClass]
public partial class MotionBricksNavigation : NavigationBase
{
    /// <summary>Local FP32 model payload; an explicit MOTIONBRICKS_MODEL override takes precedence.</summary>
    [Export(PropertyHint.File, "*.onnx")]
    public string ModelPath { get; set; } = "res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx";

    // Resolve on the scene thread, before passing immutable native paths to the worker.
    internal string ResolveModelPath(string? environmentOverride) => ProjectSettings.GlobalizePath(
        string.IsNullOrWhiteSpace(environmentOverride) ? ModelPath : environmentOverride);

    // Passive playback association for evidence. Null frames mean authored idle has no predicted contacts.
    internal Action<long, Transform3D[][]?, double>? PlaybackObserved
    {
        get; set;
    }
    // Instance-local test seam, captured before worker startup; never exported or configured by gameplay.
    internal Func<SomaInferenceWorker.ISession, SomaInferenceWorker.ISession>? SessionDecorator
    {
        get; set;
    }
    // Deterministic production-route contract tests only; never exported or set by gameplay.
    internal Func<SomaInferenceWorker.ISession>? SessionFactory
    {
        get; set;
    }
    // Instance-local representative probe only. No exported flag or normal controller policy change.
    internal Func<double, Transform3D[][], Transform3D[][]?>? TargetWindowOverride
    {
        get; set;
    }
    internal double ObservationTime
    {
        get; private set;
    }
    private Character.Character? _actor;
    private Skeleton3D? _skeleton;
    private SomaMotionData? _data;
    private MotionCatalogue? _catalogue;
    private ReviewedRoleSampler? _reviewed;
    private NavigationMotionIntent _intent;
    /// <summary>Forward/lateral component near-zero tolerance; same default as player input.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float RoleComponentDeadzone { get; set; } = .15f;
    /// <summary>Actual selected reviewed role, not an override of path intent.</summary>
    public string ReviewedRole => _reviewed?.Role ?? "";
    private SomaInferenceWorker? _worker;
    private NativeSomaRig? _rig;
    private RealisedPoseHistory? _history;
    private ILogger<MotionBricksNavigation>? _logger;
    private Transform3D[] _pose = [], _observed = [];
    private Transform3D[][]? _segment;
    private double _phaseTime, _requestStarted;
    private Vector3 _lastGround, _idleGround;
    private Basis _idleYaw = Basis.Identity;
    private bool _pending, _needRequest, _arrival, _endpoint, _record;
    private bool _unavailable, _idleTransitionRequested, _idleTransitionActive, _arrivalFinished;
    private long _ticket;
    private readonly StrongBox<string?> _nativePhase = new("not-started");

    /// <summary>Observational native-call phase; no barrier, delay or cancellation is injected.</summary>
    public string NativePhase => Volatile.Read(ref _nativePhase.Value) ?? "not-started";
    /// <summary>Worker lifetime, available to shutdown observers without retaining a scene node.</summary>
    public Task? WorkerCompletion => _worker?.Completion;
    // Captured before worker startup. A diagnostic sink must contain no scene-node references.
    internal Action<SomaInferenceWorker.Result>? ReturnedMotion
    {
        get; set;
    }
    internal ReadOnlySpan<Transform3D> GeneratedInputPose => _pose;
    internal ReadOnlySpan<Transform3D> RealisedFeedback => _observed;
    internal Transform3D RecordingMeasuredTransform => _actor is null ? Transform3D.Identity : MeasuredTransform();
    internal float? RecordingAnatomicalHeading => _data is null || _observed.Length == 0 ? null : AnatomicalHeading(_observed) + Mathf.Pi;
    internal SomaMotionData? GetRecordingPoseLayout() => _data;
    internal bool IsRecordingObservationPending() => _record;
    /// <summary>Diagnostic count of discarded completed native requests.</summary>
    public int DiscardedResults => _worker?.DiscardedResults ?? 0;

    /// <summary>Normalised target speed intent, not time-warped playback.</summary>
    public float NormalisedSpeed
    {
        get;
        set => field = float.IsFinite(value) && value > 0 && value <= 1 ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    } = 1;
    /// <summary>Visible state for the existing playtest and evidence observer.</summary>
    public string Phase { get; private set; } = "loading";
    /// <summary>Last truthful terminal failure, if any.</summary>
    public string Failure { get; private set; } = "";
    /// <summary>Skin-ready sample count, never predicted-history count.</summary>
    public long SamplesRecorded => _history?.SamplesRecorded ?? 0;
    /// <summary>Number of submitted windows in this node lifetime.</summary>
    public int Requests
    {
        get; private set;
    }
    /// <summary>Read-only diagnostic event; payload arrays are owned by the event recipient.</summary>
    public event Action<long, Transform3D[][], Transform3D[][]>? WindowSubmitted;
    /// <summary>Emitted only at the actual skin-ready observation boundary.</summary>
    public event Action? PoseObserved;
    /// <summary>Latest observed canonical world root, not a prediction.</summary>
    public Transform3D ObservedRoot => _observed.Length == 0 ? Transform3D.Identity : _observed[0];

    /// <summary>Godot-callable selection adapter preserving the INavigation facade and ordered via points.</summary>
    public int SelectDestination(Transform3D destination, Vector3[] vias) => (int)SetDestination(destination, vias);

    /// <inheritdoc/>
    public override void _Ready()
    {
        if (Engine.IsEditorHint())
        {
            return;
        }
        _logger = GameLoggerResolver.ResolveRequired<MotionBricksNavigation>();
        Callable.From(Initialise).CallDeferred();
    }

    private void Initialise()
    {
        try
        {
            _actor = GetParent<Character.Character>();
            _skeleton = _actor.RequireNode<Skeleton3D>(_actor.MotionModel + "/SOMA/Native/GeneralSkeleton");
            _data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
            _catalogue = new MotionCatalogue(ProjectSettings.GlobalizePath("res://data/motionbricks/catalogue.json"), _data);
            _reviewed = new ReviewedRoleSampler(ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine/set.json"), _data);
            _ = _reviewed.TurnReference("left_turn");
            _ = _reviewed.TurnReference("right_turn");
            _pose = new Transform3D[_data.Names.Length];
            _observed = new Transform3D[_pose.Length];
            _rig = new NativeSomaRig(_data, _skeleton);
            _history = new RealisedPoseHistory(_pose.Length);
            string model = ResolveModelPath(System.Environment.GetEnvironmentVariable("MOTIONBRICKS_MODEL"));
            string frames = ProjectSettings.GlobalizePath("res://data/motionbricks/model-frames.json");
            SomaMotionData data = _data;
            StrongBox<string?> nativePhase = _nativePhase;
            ILogger<MotionBricksNavigation> logger = _logger!;
            Func<SomaInferenceWorker.ISession, SomaInferenceWorker.ISession>? decorate = SessionDecorator;
            Func<SomaInferenceWorker.ISession>? factory = SessionFactory;
            _worker = Game.Instance.MotionWorkers.Create(() =>
            {
                if (factory is not null)
                {
                    return factory();
                }
                var session = new SomaInference(data, model, frames, phase =>
                {
                    Volatile.Write(ref nativePhase.Value, phase);
                    logger.LogInformation("Motion native {Phase}, clock {Clock}, thread {Thread}",
                        phase, System.Diagnostics.Stopwatch.GetTimestamp(), System.Environment.CurrentManagedThreadId);
                });
                try
                {
                    return decorate is null ? session : decorate(session);
                }
                catch { session.Dispose(); throw; }
            }, ReturnedMotion);
            _skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Physics;
            _skeleton.SkeletonUpdated += Observe;
            _idleGround = SomaMotionData.ModelToWorld.Inverse() * _actor.GlobalPosition;
            _idleYaw = _actor.GlobalBasis.Orthonormalized();
            _lastGround = _idleGround;
            Phase = "initial-idle";
            SampleIdle();
            DestinationReachedDistance = 0.1f;
            PathDesiredDistance = 0.15f;
            _logger!.LogInformation("MotionBricks normal lifecycle installed on {Actor}", _actor.Name);
        }
        catch (Exception error)
        {
            Failure = error.ToString();
            _unavailable = true;
            Phase = "failed";
            _logger!.LogError(error, "MotionBricks initialisation failed");
        }
    }

    /// <inheritdoc/>
    protected override bool CanReportNavigationComplete() => !HasDestination;

    /// <inheritdoc/>
    protected override void OnDestinationAccepted(Transform3D destination)
    {
        ResetFailureDiagnosticSubmission();
        SourceRateCurveIntent = null;
        ResetNativeHandoff();
        if (_unavailable)
        {
            _ = TryCommitFailedOutcome(CreateLifecycleTerminalContext(MeasuredTransform(),
                NavigationTerminalReason.MotionUnavailable), Engine.GetPhysicsFrames());
            return;
        }
        _worker?.CancelRequest();
        _ = (_reviewed?.SetIntent("", ObservationTime));
        _pending = false;
        _segment = null;
        _endpoint = false;
        _needRequest = true;
        _arrival = false;
        _arrivalFinished = false;
        _idleTransitionRequested = false;
        _idleTransitionActive = false;
        Failure = "";
    }

    /// <inheritdoc/>
    protected override void OnDestinationCleared()
    {
        SourceRateCurveIntent = null;
        ResetNativeHandoff();
        _ = (_reviewed?.SetIntent("", ObservationTime));
        _worker?.CancelRequest();
        _pending = false;
        _needRequest = false;
        _segment = null;
        _endpoint = false;
        if (_unavailable)
        {
            _idleTransitionRequested = false;
            _idleTransitionActive = false;
            Phase = "failed";
        }
        else if (_history?.SamplesRecorded > 0)
        {
            if (_arrivalFinished)
            {
                EnterIdle();
            }
            else
            {
                _idleTransitionRequested = true;
            }
        }
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        if (Game.Instance.MotionWorkers.IsStopping)
        {
            return;
        }
        if (_rig is null || _worker is null)
        {
            return;
        }
        if (Phase == "failed")
        {
            // Skeleton modifiers reset their output each tick. Retain the input pose, not their transient output.
            _rig.Apply(_pose);
            return;
        }
        try
        {
            Step(delta);
        }
        catch (NavigationRefusalException error)
        {
            PublishOrdinaryRefusal(error.Message);
            LogFailureDiagnostic();
            Fail(error.Message);
            // Refusal skips the normal base write; keep ownership through the post-modifier observation.
            _rig.Apply(_pose);
            _record = true;
        }
        catch (Exception error)
        {
            _logger!.LogError(error, "Generated navigation failed in {Phase}", Phase);
            InferenceUnavailable(error.ToString());
        }
    }

    private void Step(double delta) => StepNativeHandoff(delta);

    private void SubmitWindow()
    {
        ResetFailureDiagnosticSubmission();
        SourceRateCurveIntent = null;
        Transform3D[][] context = _history!.Context();
        if (SubmitFiniteTurn(context))
        {
            return;
        }
        if (TargetWindowOverride is not null)
        {
            _arrival = false;
            Transform3D[][]? overrideTarget = TargetWindowOverride(ObservationTime, context);
            if (overrideTarget is not null)
            {
                SubmitTarget(context, overrideTarget);
            }
            return;
        }
        NavigationRouteSnapshot route = CurrentRouteSnapshot!;
        Vector3 start = _actor!.GlobalPosition;
        Vector3 point = route.NextPathPoint;
        RefreshRoleDecision(context);

        _submissionCorridorEnd = point;
        SubmitNativePathTargets(context, start, point);
    }

    private void SubmitTarget(Transform3D[][] context, Transform3D[][] target)
    {
        if (!_earlyNativeSubmission)
        {
            _lastGround = Ground(context[3][0].Origin);
        }
        _ticket = _worker!.Submit(context, target);
        if (_finiteTurn is not null)
        {
            _finiteTicket = _ticket;
        }
        SnapshotSubmittedRole();
        {
            _nativePending = new NativePacket(_ticket, _nativeRevision, _observedAt, _arrival, false,
                _earlyNativeSubmission, _actor!.GlobalPosition, _submissionCorridorEnd,
                true,
                true,
                CurrentRouteSnapshot?.RouteRevision ?? 0, SubmittedRole);
        }
        Requests++;
        _needRequest = false;
        _pending = true;
        _requestStarted = ObservationTime;
        Phase = _earlyNativeSubmission ? "moving-generating" : "inference-hold";
        WindowSubmitted?.Invoke(_ticket, context, target);
        _logger!.LogInformation("Motion window {Request}: target {Target}, arrival {Arrival}", Requests, target[0][0].Origin, _arrival);
    }

    private void Observe()
    {
        if (!_record)
        {
            return;
        }
        _record = false;
        _rig!.Observe(_observed);
        _history!.Record(ObservationTime, _observed);
        _observedAt = ObservationTime;
        ObserveFiniteTurnEndpoint();
        PoseObserved?.Invoke();
    }

    private Transform3D MeasuredTransform()
    {
        // Service is recorded only after the played endpoint is observed. It preserves facing ownership
        // between tickets of this plan, without acquiring it while the first ticket is still pending.
        if (_finiteTurn is { } plan && (plan.Served > 0 || (_activeNativeTicket != 0 && _activeNativeTicket == _finiteTicket)))
        {
            return _actor!.GlobalTransform;
        }
        Basis world = SomaMotionData.ModelToWorld * (_observed.Length > 0 ? _observed[0].Basis : Basis.Identity);
        Vector3 forward = world.Z;
        float yaw = Mathf.Atan2(-forward.X, -forward.Z);
        return new Transform3D(new Basis(Vector3.Up, yaw), _actor!.GlobalPosition);
    }

    private void EnterIdle()
    {
        _idleGround = Ground(_observed[0].Origin);
        _idleYaw = MeasuredTransform().Basis;
        _lastGround = _idleGround;
        _phaseTime = 0;
        Phase = "settled-idle";
    }

    private void SampleIdle()
    {
        PlaybackObserved?.Invoke(0, null, 0);
        SomaMotionData.Sample(_data!.Idle, _phaseTime % ((_data.Idle.Length - 1) / 30.0), _pose);
        _pose[0] = new Transform3D(_idleYaw * _pose[0].Basis,
            _idleGround + (_idleYaw * (_pose[0].Origin - Ground(_data.Idle[0][0].Origin))));
    }

    private void Fail(string message)
    {
        Failure = message;
        _logger!.LogWarning("Navigation refused further movement: {Reason}", message);
        _ = TryCommitFailedOutcome(CreateLifecycleTerminalContext(MeasuredTransform(), NavigationTerminalReason.ConvergenceFailed), Engine.GetPhysicsFrames());
    }

    private void InferenceUnavailable(string message)
    {
        LogFailureDiagnostic();
        _ = (_reviewed?.SetIntent("", ObservationTime));
        _unavailable = true;
        Failure = string.IsNullOrEmpty(Failure) ? message : Failure + "\n" + message;
        _logger!.LogError("Motion unavailable; retaining last safe pose: {Reason}", message);
        _worker?.CancelRequest();
        _segment = null;
        _pending = _needRequest = _endpoint = _idleTransitionRequested = _idleTransitionActive = false;
        _ = TryCommitFailedOutcome(CreateLifecycleTerminalContext(MeasuredTransform(), NavigationTerminalReason.MotionUnavailable), Engine.GetPhysicsFrames());
        Phase = "failed";
        _rig?.Apply(_pose);
    }

    private void SubmitIdleTransition()
    {
        Transform3D[][] context = _history!.Context();
        MotionCatalogue.Stage stage = _catalogue!.Select(0);
        Transform3D[][] clip = MotionCatalogue.Clip(_data!, stage.Action);
        Basis yaw = MeasuredTransform().Basis;
        Vector3 placement = Ground(context[3][0].Origin);
        Vector3 initial = Ground(clip[stage.Entry][0].Origin);
        var target = new Transform3D[4][];
        for (int f = 0; f < 4; f++)
        {
            target[f] = (Transform3D[])clip[stage.Entry + f].Clone();
            target[f][0] = new Transform3D(yaw * target[f][0].Basis,
                placement + (yaw * (target[f][0].Origin - initial)));
        }
        _lastGround = Ground(context[3][0].Origin);
        _ticket = _worker!.Submit(context, target);
        {
            _nativePending = new NativePacket(_ticket, _nativeRevision, _observedAt, false, true, false,
                _actor!.GlobalPosition, _actor.GlobalPosition, false);
        }
        Requests++;
        _idleTransitionRequested = false;
        _idleTransitionActive = _pending = true;
        _endpoint = false;
        _requestStarted = ObservationTime;
        Phase = "inference-hold";
        WindowSubmitted?.Invoke(_ticket, context, target);
    }

    private static Vector3 Ground(Vector3 p) => new(p.X, 0, p.Z);

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (_skeleton is not null && IsInstanceValid(_skeleton))
        {
            _skeleton.SkeletonUpdated -= Observe;
        }
        if (_worker is not null)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            _logger!.LogInformation("Scene exit during native phase {Phase}, clock {Clock}", NativePhase, start);
            _worker.Dispose();
            _logger!.LogInformation("Scene exit signalled worker in {Milliseconds} ms",
                System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            ILogger<MotionBricksNavigation> logger = _logger!;
            _ = _worker.Completion.ContinueWith(task => logger.LogError(task.Exception, "Motion worker shutdown failed"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
