using System.Reflection;
using AlleyCat.Control;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Logging;
using AlleyCat.XR;
using AlleyCat.XR.Mock;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Tests.LocomotionVisual;

/// <summary>
/// CTRL-001 TR13 narrow-visual-acceptance driver: installs the production mock XR runtime behind a
/// real <see cref="XRManager" /> so the photobooth player's <see cref="PlayerController" /> binds to the
/// genuine controller-event input path, then records trajectory-paired runtime observations
/// (actor transform, response model, evaluated graph state, foot-bone world positions) every physics
/// tick while the GDScript runner captures screenshots.
/// </summary>
/// <remarks>
/// This driver only routes input and observes; it never moves the actor directly, never calls
/// <c>Move</c>/<c>Rotate</c> on the locomotion component, and never alters locomotion behaviour. The
/// production player scene under test is composed entirely by the production installer chain.
/// </remarks>
[GlobalClass]
public sealed partial class LocomotionVisualDriver : Node
{
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";
    private const string MovementActionName = "primary";
    private const string RotationActionName = "primary";

    private static readonly StringName _travelRateParameter = new("parameters/States/PlayerTravel/Rate/scale");
    private static readonly StringName _domainParameter = new("parameters/States/PlayerTravel/Domain/blend_amount");
    private static readonly StringName _directionalLeftParameter = new("parameters/States/PlayerTravel/DirectionalL/blend_position");
    private static readonly StringName _directionalRightParameter = new("parameters/States/PlayerTravel/DirectionalR/blend_position");
    private static readonly string[] _exitNodePrefixes = ["PlayerF2I", "PlayerB2I", "PlayerL2I", "PlayerR2I"];

    private Game? _global;
    private BoothXRManager? _xrManager;
    private MockXRHandControllerNode? _leftController;
    private MockXRHandControllerNode? _rightController;
    private PlayerController? _controller;
    private CharacterBody3D? _player;
    private Node3D? _rig;
    private Skeleton3D? _skeleton;
    private AnimationTree? _animationTree;
    private CharacterLocomotion? _locomotion;
    private AnimationNodeStateMachinePlayback? _playback;
    private int _leftFootBoneIndex = -1;
    private int _rightFootBoneIndex = -1;
    private Vector3 _spawnPosition;
    private ulong _startPhysicsFrame;
    private bool _collecting;
    private readonly List<Godot.Collections.Dictionary> _samples = [];

    private ILogger<LocomotionVisualDriver>? Logger
    {
        get;
        set;
    }

    /// <summary>
    /// Creates the wiring-global <see cref="Game" /> with a real <see cref="XRManager" /> that installs the
    /// production mock XR runtime, and claims the game singleton exactly like the integration-test wiring
    /// harness. Must run before the photobooth (and its player) enters the tree.
    /// </summary>
    public bool Activate()
    {
        Logger = GameLoggerResolver.TryResolve(out ILogger<LocomotionVisualDriver>? logger)
            ? logger
            : null;

        if (_xrManager is not null)
        {
            Logger?.LogWarning("Locomotion visual driver activated more than once.");
            return true;
        }

        var manager = new BoothXRManager
        {
            Name = "XR",
            MockRuntimeScene = GD.Load<PackedScene>(MockRuntimeScenePath),
        };
        Game global = new()
        {
            Name = "LocomotionVisualGlobal"
        };
        global.AddChild(manager);
        _global = global;
        _xrManager = manager;

        // Mirror the production-wiring harness: claim the singleton before tree attachment so the
        // player's PlayerController._Ready resolves this game's XRManager service.
        global._EnterTree();
        if (Engine.GetMainLoop() is not SceneTree sceneTree)
        {
            Logger?.LogError("Locomotion visual driver requires a SceneTree main loop.");
            return false;
        }

        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, global);
        Logger?.LogInformation("Locomotion visual driver installed the wiring game and mock XR runtime.");
        return true;
    }

    /// <summary>
    /// Resolves the production player composition installed under <paramref name="playerRoot" /> and caches
    /// every node the sampler observes. Returns false when the installer chain has not produced the
    /// expected player wiring.
    /// </summary>
    public bool ConfigurePlayer(Node playerRoot)
    {
        _player = playerRoot as CharacterBody3D;
        _controller = playerRoot.GetNodeOrNull<PlayerController>("PlayerController");
        _locomotion = playerRoot.GetNodeOrNull<CharacterLocomotion>("Locomotion");
        _animationTree = playerRoot.GetNodeOrNull<AnimationTree>("AnimationTree");
        _rig = playerRoot.GetNodeOrNull<Node3D>("Female");
        _skeleton = playerRoot.GetNodeOrNull<Skeleton3D>("Female/GeneralSkeleton");

        if (_player is null || _controller is null || _locomotion is null || _animationTree is null
            || _rig is null || _skeleton is null)
        {
            Logger?.LogError(
                "Locomotion visual driver could not resolve the production player wiring under '{Path}' "
                + "(player={Player}, controller={Controller}, locomotion={Locomotion}, tree={Tree}, "
                + "rig={Rig}, skeleton={Skeleton}).",
                playerRoot.GetPath(),
                _player is not null,
                _controller is not null,
                _locomotion is not null,
                _animationTree is not null,
                _rig is not null,
                _skeleton is not null);
            return false;
        }

        _leftFootBoneIndex = _skeleton.FindBone("LeftFoot");
        _rightFootBoneIndex = _skeleton.FindBone("RightFoot");
        if (_leftFootBoneIndex < 0 || _rightFootBoneIndex < 0)
        {
            Logger?.LogError("Locomotion visual driver could not resolve foot bones on '{Path}'.",
                _skeleton.GetPath());
            return false;
        }

        _playback = _animationTree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
        if (_playback is null)
        {
            Logger?.LogError("Locomotion visual driver could not resolve the player States playback object.");
            return false;
        }

        _leftController = _xrManager?.Runtime.LeftHandController as MockXRHandControllerNode;
        _rightController = _xrManager?.Runtime.RightHandController as MockXRHandControllerNode;
        if (_leftController is null || _rightController is null)
        {
            Logger?.LogError("Locomotion visual driver could not resolve the mock XR hand controllers.");
            return false;
        }

        _spawnPosition = _player.GlobalPosition;
        _startPhysicsFrame = Engine.GetPhysicsFrames();
        SetPhysicsProcess(true);
        Logger?.LogInformation(
            "Locomotion visual driver observing player '{Path}' (spawn {Spawn}, rig yaw basis forward {RigForward}).",
            playerRoot.GetPath(),
            _spawnPosition,
            -_rig.GlobalBasis.Z);
        return true;
    }

    /// <summary>Whether the observed PlayerController is bound to the mock XR controllers.</summary>
    public bool IsControllerBound()
    {
        FieldInfo boundField = typeof(PlayerController).GetField("_isBound", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected PlayerController _isBound field.");
        return _controller is not null && (bool)(boundField.GetValue(_controller) ?? false);
    }

    /// <summary>
    /// Re-invokes the controller's private XR-initialised handler once the installer chain has produced the
    /// locomotion node, mirroring the integration-test wiring harness binding fallback.
    /// </summary>
    public void EnsureControllerBound()
    {
        if (IsControllerBound() || _controller is null)
        {
            return;
        }

        MethodInfo onXRInitialised = typeof(PlayerController).GetMethod(
                "OnXRInitialised",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected PlayerController OnXRInitialised method.");
        _ = onXRInitialised.Invoke(_controller, [true]);
        Logger?.LogInformation("Forced PlayerController XR binding fallback; bound={Bound}.", IsControllerBound());
    }

    /// <summary>Pushes a movement stick sample through the real XR controller input path.</summary>
    public void PushMovement(Vector2 value)
        => _leftController?.TriggerActionVector2InputChanged(MovementActionName, value);

    /// <summary>Pushes a rotation stick sample through the real XR controller input path.</summary>
    public void PushRotation(Vector2 value)
        => _rightController?.TriggerActionVector2InputChanged(RotationActionName, value);

    /// <summary>Releases both sticks through the controller input path.</summary>
    public void ReleaseSticks()
    {
        PushMovement(Vector2.Zero);
        PushRotation(Vector2.Zero);
    }

    /// <summary>Whether the latest evaluated authored-root interval is settled.</summary>
    public bool IsResponseSettled() => _locomotion?.AuthoredRootSettled ?? true;

    /// <summary>Starts gapless per-physics-tick trajectory collection.</summary>
    public void StartCollecting()
    {
        _samples.Clear();
        _collecting = true;
    }

    /// <summary>Number of samples collected since <see cref="StartCollecting" />.</summary>
    public int GetCollectedCount() => _samples.Count;

    /// <summary>Stops collection and returns every recorded sample dictionaries.</summary>
    public Godot.Collections.Array StopCollecting()
    {
        _collecting = false;
        var exported = new Godot.Collections.Array();
        foreach (Godot.Collections.Dictionary sample in _samples)
        {
            exported.Add(sample);
        }

        return exported;
    }

    /// <summary>Captures one observation of the current runtime state without waiting.</summary>
    public Godot.Collections.Dictionary CaptureSample() => BuildSample();

    /// <summary>
    /// Re-seats the actor at the recorded spawn position with identity heading and zero velocity between
    /// bouts. Observation-only harness bookkeeping: each bout starts from rest with a recorded heading.
    /// </summary>
    public void ResetActor()
    {
        if (_player is null)
        {
            return;
        }

        _player.Velocity = Vector3.Zero;
        _player.GlobalBasis = Basis.Identity;
        _player.GlobalPosition = _spawnPosition;
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        _ = delta;
        if (!_collecting)
        {
            return;
        }

        _samples.Add(BuildSample());
    }

    private Godot.Collections.Dictionary BuildSample()
    {
        StringName node = _playback!.GetCurrentNode();
        string graphNode = node.ToString();
        float exitRate = 0f;
        foreach (string prefix in _exitNodePrefixes)
        {
            if (graphNode.StartsWith(prefix, StringComparison.Ordinal))
            {
                exitRate = _animationTree!.Get($"parameters/States/{graphNode}/Rate/scale").As<float>();
                break;
            }
        }

        return new Godot.Collections.Dictionary
        {
            ["tick"] = (float)Engine.GetPhysicsFrames(),
            ["t"] = (Engine.GetPhysicsFrames() - _startPhysicsFrame) / (float)Engine.PhysicsTicksPerSecond,
            ["pos"] = Vec3Array(_player!.GlobalPosition),
            ["yaw"] = _player.GlobalBasis.GetEuler().Y,
            ["fwd"] = Vec3Array(-_player.GlobalBasis.Z),
            ["rig_fwd"] = Vec3Array(-_rig!.GlobalBasis.Z),
            ["authored_v"] = Vec2Array(_locomotion!.AuthoredPlanarVelocity),
            ["authored_yaw"] = _locomotion.AuthoredYawRate,
            ["settled"] = _locomotion.AuthoredRootSettled,
            ["speed"] = new Vector2(_player.Velocity.X, _player.Velocity.Z).Length(),
            ["vel"] = Vec2Array(new Vector2(_player.Velocity.X, _player.Velocity.Z)),
            ["state"] = _locomotion.PlayerState.ToString(),
            ["lane"] = _locomotion.PhaseLane.ToString(),
            ["node"] = graphNode,
            ["play_t"] = (float)_playback.GetCurrentPlayPosition(),
            ["fading"] = _playback.GetFadingFromNode().ToString(),
            ["rate"] = _animationTree!.Get(_travelRateParameter).As<float>(),
            ["domain"] = _animationTree.Get(_domainParameter).As<float>(),
            ["dir_l"] = Vec2Array(_animationTree.Get(_directionalLeftParameter).As<Vector2>()),
            ["dir_r"] = Vec2Array(_animationTree.Get(_directionalRightParameter).As<Vector2>()),
            ["exit_rate"] = exitRate,
            ["foot_l"] = Vec3Array(BoneWorldPosition(_leftFootBoneIndex)),
            ["foot_r"] = Vec3Array(BoneWorldPosition(_rightFootBoneIndex)),
        };
    }

    private Vector3 BoneWorldPosition(int boneIndex)
        => _skeleton!.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private static Godot.Collections.Array Vec3Array(Vector3 value)
        => [value.X, value.Y, value.Z];

    private static Godot.Collections.Array Vec2Array(Vector2 value)
        => [value.X, value.Y];

    /// <summary>
    /// A real <see cref="XRManager" /> that installs the production mock runtime scene directly. The normal
    /// <c>_Ready</c> startup is bypassed for test-context runs, so this subclass performs the same runtime
    /// installation and initialisation signalling the production manager would.
    /// </summary>
    private sealed partial class BoothXRManager : XRManager
    {
        public override void _Ready()
        {
            var subViewport = new SubViewport
            {
                Name = "SubViewport",
                Disable3D = true,
                TransparentBg = true,
                Size = new Vector2I(64, 64),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            };
            AddChild(subViewport);

            Node runtimeNode = MockRuntimeScene.Instantiate()
                ?? throw new InvalidOperationException("Could not instantiate the mock XR runtime scene.");
            AddChild(runtimeNode);
            IXRRuntime runtime = runtimeNode as IXRRuntime
                ?? throw new InvalidOperationException(
                    $"Mock XR runtime root '{runtimeNode.GetType().FullName}' must implement IXRRuntime.");
            _ = runtime.Initialise(subViewport, MaximumRefreshRate);

            Runtime = runtime;
            InitialisationAttempted = true;
            InitialisationSucceeded = true;
            _ = EmitSignal(XRManager.SignalName.Initialised, true);
        }
    }
}
