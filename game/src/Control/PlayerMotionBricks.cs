using AlleyCat.Animations;
using AlleyCat.Common;
using AlleyCat.Core.Logging;
using AlleyCat.Models;
using AlleyCat.Motion;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

/// <summary>Current player intent owns generated motion; no destination-navigation policy is involved.</summary>
[GlobalClass]
public partial class PlayerMotionBricks : Node
{
    /// <summary>Shared local FP32 payload; a nonblank MOTIONBRICKS_MODEL overrides it.</summary>
    [Export(PropertyHint.File, "*.onnx")]
    public string ModelPath { get; set; } = "res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx";
    /// <summary>Radial movement and horizontal turning deadzone.</summary>
    [Export(PropertyHint.Range, "0,0.9,0.01")]
    public float Deadzone { get; set; } = 0.15f;
    /// <summary>Virtual walking yaw rate at full right-stick deflection; separate from stationary window angle.</summary>
    [Export(PropertyHint.Range, "1,90,1,suffix:°/s")]
    public float WalkingSteeringDegreesPerSecond { get; set; } = 60;
    /// <summary>Virtual-only backward component / forward requested speed. (0,1]; invalid values use 0.75. Startup-copied.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float BackwardSpeedRatio
    {
        get;
        set => field = WalkingSteeringPrediction.ValidBackwardRatio(value);
    } = 0.75f;

    /// <summary>Current shared source role; source acceptance is not seamless-loop admission.</summary>
    public string ReviewedSourceStatus => ContinuousDriver?.ReviewedRoles is { } roles ? "reviewed-set: " + roles.Role : "loading";

    private CharacterBody3D? _actor;
    private Skeleton3D? _skeleton;
    private AnimationTree? _tree;
    private NativeSomaRig? _rig;
    private SomaMotionData? _data;
    private RealisedPoseHistory? _history;
    private ILogger<PlayerMotionBricks>? _logger;
    private Transform3D[] _observed = [], _pose = [];
    private Vector2 _left, _right, _movement;
    private Basis _movementFrame = Basis.Identity;
    private double _time, _observedTime;
    private ulong _observedPhysicsFrame;
    private long _ticket;

    internal WalkingExperimentDriver? ContinuousDriver
    {
        get; private set;
    }
    internal Transform3D[] SuppliedPose => ContinuousDriver?.SuppliedPose ?? _pose;
    /// <inheritdoc/>
    [Export(PropertyHint.Range, "0.05,1,0.05")]
    public double VirtualSnapshotSeconds { get; set; } = 0.2;
    /// <inheritdoc/>
    [Export(PropertyHint.Range, "0.01,0.5,0.01")]
    public float VirtualChangeMetres { get; set; } = 0.08f;
    /// <inheritdoc/>
    [Export(PropertyHint.Range, "1,45,1")]
    public float VirtualChangeDegrees { get; set; } = 10;
    /// <inheritdoc/>
    [Export(PropertyHint.Range, "15,120,1")]
    public float VirtualSharpDegrees { get; set; } = 45;
    private string _requestReason = "initial";
    private long _inputEpoch;
    private Vector2 _rawLeft, _rawRight;

    // Instance-local diagnostic seam over the per-consumer session, captured before worker creation. The shared
    // native session stays owned by the shared model registry.
    internal Func<SomaInferenceWorker.ISession, SomaInferenceWorker.ISession>? SessionDecorator
    {
        get; set;
    }
    // Native-free component tests bind this before Ready; gameplay leaves it null.
    internal Func<SomaInferenceWorker.ISession>? SessionFactory
    {
        get; set;
    }
    // Registry injection seam for tests and standalone fixtures; gameplay resolves the Game-owned registry.
    internal ISharedModelRegistry? SharedModelRegistry
    {
        get; set;
    }
    internal long LastAppliedRequest
    {
        get; private set;
    }
    internal Transform3D ObservedRoot => _observed.Length > 0 ? _observed[0] : Transform3D.Identity;

    internal Vector2 MovementIntent => _movement;
    internal float TurnIntent
    {
        get; private set;
    }

    /// <summary>Read-only state for representative playtest diagnostics.</summary>
    public string Phase { get; private set; } = "loading";
    /// <summary>Explicit setup/generation failure; no fallback locomotion is substituted.</summary>
    public string Failure { get; private set; } = "";
    /// <summary>Actual skin-ready observations, including pass-through idle.</summary>
    public long SamplesRecorded => _history?.SamplesRecorded ?? 0;
    /// <summary>Number of native requests submitted.</summary>
    public int Requests
    {
        get; private set;
    }
    /// <summary>Discarded superseded native results.</summary>
    public int DiscardedResults => ContinuousDriver?.DiscardedResults ?? 0;
    /// <summary>Diagnostic native response latency; not a responsiveness guarantee.</summary>
    public double LastResponseSeconds
    {
        get; private set;
    }
    /// <summary>Worker completion remains owned and drained by Game.</summary>
    public Task? WorkerCompletion => ContinuousDriver?.Completion;
    /// <summary>Copied submitted windows for focused evidence only.</summary>
    public event Action<long, Transform3D[][], Transform3D[][]>? WindowSubmitted;

    /// <inheritdoc/>
    public override void _Ready()
    {
        if (!Engine.IsEditorHint())
        {
            _logger = GameLoggerResolver.ResolveRequired<PlayerMotionBricks>();
            ShowSubmittedTarget |= System.Environment.GetEnvironmentVariable("PLAYER_MOTION_TARGET") == "1";
            _logger.LogInformation("Player motion startup: shared source roles, source-landmark turns, stable-input chaining");
            string? traceLabel = System.Environment.GetEnvironmentVariable("PLAYER_MOTION_TRACE");
            if (!string.IsNullOrWhiteSpace(traceLabel))
            {
                InputTraceLabel = traceLabel;
                RecordInputTrace = true;
            }
            Callable.From(Initialise).CallDeferred();
        }
    }

    private void Initialise()
    {
        try
        {
            Character.Character character = GetParent<Character.Character>();
            _actor = character;
            _skeleton = character.RequireMotionTarget();
            _data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
            _pose = new Transform3D[_data.Names.Length];
            _observed = new Transform3D[_pose.Length];
            _rig = new NativeSomaRig(_data, _skeleton);
            _history = new RealisedPoseHistory(_pose.Length);
            ReviewedRoleSampler reviewedRoles = AnimationCatalogue.RequireFor(character).Locomotion.CreateSampler(_data);
            _ = reviewedRoles.TurnReference("left_turn");
            _ = reviewedRoles.TurnReference("right_turn");
            {
                string? configured = System.Environment.GetEnvironmentVariable("MOTIONBRICKS_MODEL");
                string model = ProjectSettings.GlobalizePath(string.IsNullOrWhiteSpace(configured) ? ModelPath : configured);
                ContinuousDriver = new WalkingExperimentDriver(_actor, _skeleton, _data, model, session =>
                {
                    SomaInferenceWorker.ISession decorated = SessionDecorator?.Invoke(session) ?? session;
                    decorated = _inputTrace?.Wrap(decorated) ?? decorated;
                    return _turnTrace?.Wrap(decorated) ?? decorated;
                })
                {
                    Reconcile = true,
                    SessionFactory = SessionFactory,
                    SharedModelRegistry = SharedModelRegistry,
                    ReturnedObserved = PlayerPoseRecording.ObserveReturns(_poseRecordingLink),
                    BackwardSpeedRatio = BackwardSpeedRatio,
                    SnapshotSeconds = VirtualSnapshotSeconds,
                    TargetChangeMetres = VirtualChangeMetres,
                    TargetChangeDegrees = VirtualChangeDegrees,
                    SharpChangeDegrees = VirtualSharpDegrees,
                    WalkingSteeringDegreesPerSecond = WalkingSteeringDegreesPerSecond
                    ,
                    ReviewedRoles = reviewedRoles,
                    RoleDeadzone = Deadzone
                };
                ContinuousDriver.WindowSubmitted += OnContinuousSubmitted;
                ContinuousDriver.PlaybackStarted += OnContinuousStarted;
                ContinuousDriver.PlaybackCompleted += OnContinuousCompleted;
                ContinuousDriver.IntentRetired += () => SetTargetDiagnostic("");
            }
            ProcessPhysicsPriority = -100;
            _skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Physics;
            _skeleton.SkeletonUpdated += Observe;
            _tree = _actor.RequireNode<AnimationTree>("AnimationTree");
            _tree.MixerApplied += SupplyTreeDestination;
            Phase = "tracked-idle";
            StartTurnTrace();
        }
        catch (Exception error) { Fail(error); }
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        UpdateInputTrace();
        RefreshTargetDiagnostic();
        Advance(delta);
        TraceTurnTick();
    }

    /// <summary>Production XR vector input; right vertical is deliberately ignored.</summary>
    public void SetStick(bool left, Vector2 value, string origin = "controller-primary")
    {
        _inputTrace?.Record("input-process", new
        {
            physicsFrame = Engine.GetPhysicsFrames(),
            origin,
            side = left ? "left" : "right",
            raw = new[] { value.X, value.Y }
        });
        Vector2 rawValue = value;
        if (left)
        {
            _rawLeft = value;
        }
        else
        {
            _rawRight = value;
        }
        if (!value.IsFinite())
        {
            value = Vector2.Zero;
        }
        if (!left)
        {
            value = new Vector2(value.X, 0);
        }
        // Quantise controller noise, not playback. A zero/deadzone release always retires the epoch.
        value = left ? value.LimitLength()
            : new Vector2(Mathf.Snapped(value.X, 0.05f), Mathf.Snapped(value.Y, 0.05f)).LimitLength();
        if (left)
        {
            _left = value;
        }
        else
        {
            _right = value;
        }
        Vector2 movement = _left.Length() > Deadzone ? _left : Vector2.Zero;
        float turn = Mathf.Abs(_right.X) > Deadzone ? _right.X : 0;
        _turnTrace?.Input(left, rawValue, value, origin);
        if (movement == _movement && turn == TurnIntent)
        {
            RecordStick(left, rawValue, value, false, origin);
            return;
        }
        if (movement != Vector2.Zero && movement != _movement && _movement == Vector2.Zero)
        {
            // Snapshot actor yaw for this held intent. Feeding generated hip sway back into the
            // direction each window would turn a held lateral stick into successive quarter turns.
            _movementFrame = _actor?.GlobalBasis.Orthonormalized() ?? Basis.Identity;
        }
        _movement = movement;
        TurnIntent = turn;
        if (ContinuousDriver is { } driver && !driver.SetIntent(movement, _movementFrame, turn))
        {
            RecordStick(left, rawValue, value, true, origin);
            return;
        }
        _inputTrace?.Record("retire", new
        {
            physicsFrame = Engine.GetPhysicsFrames(),
            epoch = _inputEpoch,
            request = _ticket,
            lastApplied = LastAppliedRequest,
            playedFrame = ContinuousDriver?.RecordingFrame,
            cause = "processed-input-change; cancellation supersedes ownership, not native interruption",
            phase = Phase
        });
        _inputEpoch++;
        if (ContinuousDriver is null)
        {
            SetTargetDiagnostic("");
        }
        bool settling = movement == Vector2.Zero && turn == 0;
        _requestReason = settling ? "release" : "input-change";
        Phase = settling ? "settling-requested" : "intent-changed";
        RecordStick(left, rawValue, value, true, origin);
    }

    /// <summary>Pause/disconnection retires both sticks atomically.</summary>
    public void ClearInput()
    {
        _right = Vector2.Zero;
        SetStick(true, Vector2.Zero, "clear-input");
    }

    internal void Advance(double delta)
    {
        if (_rig is null || Game.Instance.MotionWorkers.IsStopping)
        {
            return;
        }
        if (Failure.Length > 0)
        {
            ContinuousDriver?.ApplyHeldPose();
            return;
        }
        try
        {
            _time += delta;
            if (ContinuousDriver is not null)
            {
                ContinuousDriver.Advance(delta, _movement, _movementFrame, TurnIntent,
                    _left.Length() <= Deadzone && _right.Length() <= Deadzone);
                RefreshVirtualMarker();
                Phase = ContinuousDriver.Phase;
                Requests = ContinuousDriver.Requests;
                LastAppliedRequest = ContinuousDriver.LastAppliedRequest;
                LastResponseSeconds = ContinuousDriver.LastResponseSeconds;
                return;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void Observe()
    {
        _rig!.Observe(_observed);
        _history!.Record(_time, _observed);
        _observedTime = _time;
        _observedPhysicsFrame = Engine.GetPhysicsFrames();
        ContinuousDriver?.Observe();
        RecordSkinPose();
        TraceTurnSkin();
    }

    private void SupplyTreeDestination() => ContinuousDriver?.ReadTreeDestination();

    private void Fail(Exception error)
    {
        Failure = error.ToString();
        Phase = "failed";
        SetTargetDiagnostic("");
        ContinuousDriver?.Stop();
        _turnTrace?.Stop();
        _inputTrace?.Record("failure", new
        {
            Failure,
            epoch = _inputEpoch,
            request = _ticket
        });
        _logger!.LogError(error, "Player MotionBricks unavailable; no fallback locomotion applied");
    }

    private static Vector3 Ground(Vector3 value) => new(value.X, 0, value.Z);

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (_tree is not null && IsInstanceValid(_tree))
        {
            _tree.MixerApplied -= SupplyTreeDestination;
        }
        if (_skeleton is not null && IsInstanceValid(_skeleton))
        {
            _skeleton.SkeletonUpdated -= Observe;
        }

        ContinuousDriver?.Stop();
        _inputTrace?.Record("shutdown", new
        {
            epoch = _inputEpoch,
            request = _ticket,
            DiscardedResults
        });
        _inputTrace?.Stop();
        StopPoseTrace(WorkerCompletion, false);
    }
}
