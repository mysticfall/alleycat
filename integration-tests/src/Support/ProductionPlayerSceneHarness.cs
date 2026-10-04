using System.Reflection;
using System.Text;
using AlleyCat.Control;
using AlleyCat.Control.Locomotion;
using AlleyCat.Rigging;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591, IDE0044, IDE0051, CS0067

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Production player-scene locomotion harness: loads the real <c>ally_player.tscn</c> through its real
/// installers with a fixture-<see cref="Game"/> singleton takeover already active, so the installed
/// <see cref="PlayerController"/> binds to fake XR controllers exactly as it would bind in VR. Stick
/// samples reach the locomotion through real XR controller action events — never direct
/// <c>Move</c>/<c>Rotate</c> calls — and every physics tick can be captured as a
/// <see cref="ProductionWiringSample"/> with optional per-tick CSV evidence. Use this harness when the
/// behaviour under test depends on real production animation content (clip lengths, catalogue metadata,
/// tree wiring); prefer <see cref="PlayerLocomotionProductionWiringHarness"/> for fixture-content
/// mechanism checks.
/// </summary>
public sealed class ProductionPlayerSceneHarness : IAsyncDisposable
{
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";
    private const string MovementAction = "primary";
    private const string RotationAction = "primary";

    private static readonly StringName _travelRateParameter = new("parameters/States/PlayerTravel/Rate/scale");
    private static readonly StringName _playbackParameter = new("parameters/States/playback");

    private readonly Game _global;
    private readonly FakeProductionXRManager _xrManager;
    private readonly PlayerController _controller;
    private readonly AnimationTree _tree;
    private readonly AnimationNodeStateMachinePlayback _playback;
    private readonly Node _sceneRoot;
    private bool _disposed;

    private ProductionPlayerSceneHarness(
        Game global,
        FakeProductionXRManager xrManager,
        PlayerController controller,
        CharacterLocomotion locomotion,
        AnimationTree tree,
        AnimationNodeStateMachinePlayback playback,
        Node sceneRoot,
        string evidenceDirectory)
    {
        _global = global;
        _xrManager = xrManager;
        _controller = controller;
        Locomotion = locomotion;
        _tree = tree;
        _playback = playback;
        _sceneRoot = sceneRoot;
        EvidenceDirectory = evidenceDirectory;
    }

    /// <summary>Directory the harness writes per-tick CSV evidence into (created on demand).</summary>
    public string EvidenceDirectory
    {
        get;
    }

    /// <summary>Whether the PlayerController has bound to the fake XR controllers.</summary>
    public bool ControllerBound
    {
        get
        {
            FieldInfo boundField = typeof(PlayerController).GetField("_isBound", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Expected PlayerController _isBound field.");
            return (bool)(boundField.GetValue(_controller) ?? false);
        }
    }

    /// <summary>The locomotion component driving the production player composition.</summary>
    public CharacterLocomotion Locomotion
    {
        get;
    }

    /// <summary>The production player actor body.</summary>
    public CharacterBody3D Actor => Locomotion.TargetCharacterBodyNode!;

    /// <summary>
    /// Assembles the production player composition with the fixture-Game takeover, ready for XR-path
    /// stick input. <paramref name="evidenceSubdirectory"/> is created under
    /// <c>res://temp/&lt;evidenceRoot&gt;/</c> for CSV output.
    /// </summary>
    public static async Task<ProductionPlayerSceneHarness> CreateAsync(
        SceneTree sceneTree,
        string evidenceRoot,
        string evidenceSubdirectory,
        int installFrames = 45)
    {
        string evidenceDirectory = ProjectSettings.GlobalizePath($"res://temp/{evidenceRoot}/{evidenceSubdirectory}");
        _ = Directory.CreateDirectory(evidenceDirectory);

        Game global = new()
        {
            Name = "ProductionPlayerSceneGlobal"
        };
        FakeProductionXRManager xrManager = new()
        {
            Name = "XR"
        };
        global.AddChild(xrManager);
        global._EnterTree();
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, global);
        await WaitForFramesAsync(sceneTree, 4);

        Node playerRoot = LoadPackedScene(PlayerScenePath).Instantiate();
        sceneTree.Root.AddChild(playerRoot);
        await WaitForFramesAsync(sceneTree, installFrames);

        CharacterLocomotion locomotion = playerRoot.FindChildren("*", "", true, false)
            .OfType<CharacterLocomotion>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Expected the installed player scene to expose CharacterLocomotion.");
        PlayerController controller = playerRoot.FindChildren("*", "", true, false)
            .OfType<PlayerController>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Expected the installed player scene to expose PlayerController.");
        AnimationTree tree = playerRoot.FindChildren("*", "", true, false)
            .OfType<AnimationTree>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Expected the installed player scene to expose AnimationTree.");
        AnimationNodeStateMachinePlayback playback = tree.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>()
            ?? throw new InvalidOperationException("Expected the player tree to expose the States playback.");

        return new ProductionPlayerSceneHarness(
            global, xrManager, controller, locomotion, tree, playback, playerRoot, evidenceDirectory);
    }

    /// <summary>Pushes a movement stick sample through the real controller input path.</summary>
    public void PushMovement(Vector2 value)
        => _xrManager.LeftController.TriggerActionVector2InputChanged(MovementAction, value);

    /// <summary>Pushes a rotation stick sample through the real controller input path.</summary>
    public void PushRotation(Vector2 value)
        => _xrManager.RightController.TriggerActionVector2InputChanged(RotationAction, value);

    /// <summary>Releases both sticks through the controller input path.</summary>
    public void ReleaseSticks()
    {
        PushMovement(Vector2.Zero);
        PushRotation(Vector2.Zero);
    }

    /// <summary>Awaits <paramref name="physicsTicks"/> real physics frames, capturing one sample per frame.</summary>
    public async Task<List<ProductionWiringSample>> RunAsync(SceneTree sceneTree, int physicsTicks)
    {
        List<ProductionWiringSample> samples = new(physicsTicks);
        for (int tick = 0; tick < physicsTicks; tick++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            samples.Add(CaptureSample());
        }

        return samples;
    }

    /// <summary>Captures one observation of the current harness state without waiting.</summary>
    public ProductionWiringSample CaptureSample()
    {
        CharacterBody3D actor = Actor;
        return new(
            Engine.GetPhysicsFrames(),
            TimeTicksToSeconds(0),
            actor.GlobalPosition,
            actor.GlobalBasis.GetEuler().Y,
            -actor.GlobalBasis.Z,
            -actor.GlobalBasis.Z,
            -actor.GlobalBasis.Z,
            Locomotion.AuthoredPlanarVelocity,
            Locomotion.AuthoredYawRate,
            new Vector2(actor.Velocity.X, actor.Velocity.Z),
            _playback.GetCurrentNode().ToString(),
            (float)_playback.GetCurrentPlayPosition(),
            _playback.GetFadingFromNode().ToString(),
            _tree.Get(_travelRateParameter).As<float>(),
            0f,
            Vector2.Zero,
            Vector2.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector2.Zero,
            Vector2.Zero,
            Locomotion.LastOrdinaryTravelDemand,
            Locomotion.PlayerState,
            Locomotion.MovementOperationCount,
            Locomotion.LastAuthoredRootWorldDelta,
            Locomotion.LastRequestedWorldDelta,
            Locomotion.LastRealisedWorldDelta);
    }

    /// <summary>Writes one per-tick CSV trace (CTRL-001 TR10/TAC-002 evidence) for a release scenario.</summary>
    public void WriteReleaseTraceCsv(
        string scenario,
        ProductionWiringSample atRelease,
        ulong releaseFrame,
        IReadOnlyList<ProductionWiringSample> samples,
        string note = "")
    {
        StringBuilder csv = new();
        _ = csv.AppendLine(
            "tick_since_release,physics_frame,state,graph_node,graph_play_pos_s,fading_from,travel_rate,"
            + "speed_mps,vel_x,vel_z,actor_x,actor_z,actor_yaw,resp_speed,resp_x,resp_z,root_dx,root_dz,"
            + "req_dx,req_dz,real_dx,real_dz,disp_cum_mm,note");

        Vector3 origin = atRelease.ActorPosition;
        foreach (ProductionWiringSample sample in samples)
        {
            long tick = (long)(sample.PhysicsFrame - releaseFrame);
            float cumulative = new Vector2(sample.ActorPosition.X - origin.X, sample.ActorPosition.Z - origin.Z).Length();
            _ = csv.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{tick},{sample.PhysicsFrame},{sample.PlayerState},{sample.GraphNode},"
                + $"{sample.GraphPlayPositionSeconds:0.000000},{sample.FadingFromNode},{sample.TravelPlaybackRate:0.000000},"
                + $"{sample.ActorPlanarVelocity.Length():0.000000},{sample.ActorPlanarVelocity.X:0.000000},"
                + $"{sample.ActorPlanarVelocity.Y:0.000000},{sample.ActorPosition.X:0.000000},{sample.ActorPosition.Z:0.000000},"
                + $"{sample.ActorYawRadians:0.000000},{sample.ResponseVelocity.Length():0.000000},"
                + $"{sample.ResponseVelocity.X:0.000000},{sample.ResponseVelocity.Y:0.000000},"
                + $"{sample.AuthoredRootDelta.X:0.000000},{sample.AuthoredRootDelta.Z:0.000000},"
                + $"{sample.RequestedMovement.X:0.000000},{sample.RequestedMovement.Z:0.000000},"
                + $"{sample.RealisedMovement.X:0.000000},{sample.RealisedMovement.Z:0.000000},"
                + $"{cumulative * 1000f:0.00},{note}"));
        }

        File.WriteAllText(Path.Combine(EvidenceDirectory, $"ticks-{scenario}.csv"), csv.ToString());
    }

    private static float TimeTicksToSeconds(ulong ticks)
        => (float)((double)ticks / Math.Max(1, Engine.PhysicsTicksPerSecond));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sceneRoot.QueueFree();
        if (GodotObject.IsInstanceValid(_global))
        {
            _global.QueueFree();
        }

        var sceneTree = Engine.GetMainLoop() as SceneTree;
        if (sceneTree is not null)
        {
            await WaitForFramesAsync(sceneTree, 3);
        }
    }

    private sealed partial class FakeProductionXRManager : XRManager
    {
        private static readonly FieldInfo _runtimeBackingField = typeof(XRManager)
            .GetField("<Runtime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected XRManager runtime backing field.");

        private readonly FakeProductionXRRuntime _runtime = new();

        public FakeProductionXRHandController RightController => _runtime.RightControllerNode;

        public FakeProductionXRHandController LeftController => _runtime.LeftControllerNode;

        public override void _Ready()
        {
            _runtimeBackingField.SetValue(this, _runtime);
            InitialisationAttempted = true;
            InitialisationSucceeded = true;
            _ = EmitSignal(SignalName.Initialised, true);
        }

        public void TriggerInitialised()
            => _ = EmitSignal(SignalName.Initialised, true);
    }

    private sealed class FakeProductionXRRuntime : IXRRuntime
    {
        private readonly XRControllerHandTracking _handTracking;

        public FakeProductionXRRuntime()
        {
            OriginNode = new Node3D();
            CameraNode = new Camera3D();
            RightControllerNode = new FakeProductionXRHandController();
            LeftControllerNode = new FakeProductionXRHandController();
            _handTracking = new XRControllerHandTracking(RightControllerNode, LeftControllerNode);
        }

        public IXROrigin Origin => new FakeProductionXROrigin(OriginNode);

        public IXRCamera Camera => new FakeProductionXRCamera(CameraNode);

        public IXRHandController RightHandController => RightControllerNode;

        public IXRHandController LeftHandController => LeftControllerNode;

        public XRHandTrackingMode HandTrackingMode => XRHandTrackingMode.Controller;

        public IXRHandJointProvider OpticalHandJoints => XREmptyHandJointProvider.Instance;

        public IXRHandPoseSource GetHandPoseSource(LimbSide side) => _handTracking.GetHandPoseSource(side);

        public event Action? PoseRecentered;

        public event Action? HandTrackingModeChanged;

        public Node3D OriginNode
        {
            get;
        }

        public Camera3D CameraNode
        {
            get;
        }

        public FakeProductionXRHandController RightControllerNode
        {
            get;
        }

        public FakeProductionXRHandController LeftControllerNode
        {
            get;
        }

        public bool Initialise(SubViewport viewport, int maximumRefreshRate)
        {
            _ = viewport;
            _ = maximumRefreshRate;
            return true;
        }
    }

    private sealed partial class FakeProductionXRHandController : Node3D, IXRHandController
    {
        public event Action<string>? ActionButtonPressed;

        public event Action<string>? ActionButtonReleased;

        public event Action<string, float>? ActionFloatInputChanged;

        public event Action<string, Vector2>? ActionVector2InputChanged;

        public Node3D ControllerNode => this;

        public Node3D HandPositionNode => this;

        public void TriggerActionVector2InputChanged(string actionName, Vector2 value)
            => ActionVector2InputChanged?.Invoke(actionName, value);
    }

    private sealed record FakeProductionXROrigin(Node3D OriginNode) : IXROrigin
    {
        public float WorldScale { get; set; } = 1.0f;
    }

    private sealed record FakeProductionXRCamera(Camera3D CameraNode) : IXRCamera;
}
