using System.Reflection;
using AlleyCat.Control;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591, IDE0044, IDE0051

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// One recorded physics-tick observation of the production-wiring locomotion harness: the realised actor
/// transform and velocity, the response-model state, the evaluated animation graph state and playback
/// parameters, representative foot world positions, and the last controller samples pushed through the
/// <see cref="PlayerController" /> input path.
/// </summary>
public sealed record ProductionWiringSample(
    ulong PhysicsFrame,
    float TimeSeconds,
    Vector3 ActorPosition,
    float ActorYawRadians,
    Vector3 ActorForwardWorld,
    Vector3 ViewpointForwardWorld,
    Vector3 RigForwardWorld,
    Vector2 ResponseVelocity,
    float ResponseYawRate,
    Vector2 ActorPlanarVelocity,
    string GraphNode,
    float GraphPlayPositionSeconds,
    string FadingFromNode,
    float TravelPlaybackRate,
    float DomainBlend,
    Vector2 DirectionalBlendLeft,
    Vector2 DirectionalBlendRight,
    Vector3 LeftFootWorld,
    Vector3 RightFootWorld,
    Vector2 MovementSample,
    Vector2 RotationSample,
    Vector2 OrdinaryTravelDemand,
    PlayerLocomotionState PlayerState,
    ulong MovementOperationCount,
    Vector3 AuthoredRootDelta,
    Vector3 RequestedMovement,
    Vector3 RealisedMovement);

/// <summary>
/// Fixture-owned player composition with production-equivalent wiring (CTRL-001 reproduction harness): a
/// <see cref="CharacterLocomotion" /> driven through the real <see cref="PlayerController" /> XR input path
/// (fake XR runtime events, never direct <c>Move</c>/<c>Rotate</c> calls), a rig container whose basis can
/// mirror the production 180-degree visual-rig rotation, and a viewpoint camera parented to the actor.
/// All animation and contact content remains fixture-owned (ANIM-002 boundary: no production rigs as
/// oracles). Physics and animation processing run automatically through awaited real physics frames — the
/// harness never manually pumps <c>_PhysicsProcess</c>.
/// </summary>
public sealed class PlayerLocomotionProductionWiringHarness : IAsyncDisposable
{
    private const string MovementAction = "primary";
    private const string RotationAction = "primary";
    private static readonly StringName _travelRateParameter = new("parameters/States/PlayerTravel/Rate/scale");
    private static readonly StringName _domainParameter = new("parameters/States/PlayerTravel/Domain/blend_amount");
    private static readonly StringName _directionalLeftParameter = new("parameters/States/PlayerTravel/DirectionalL/blend_position");
    private static readonly StringName _directionalRightParameter = new("parameters/States/PlayerTravel/DirectionalR/blend_position");

    private readonly SyntheticLocomotionRuntimeFixture _content;
    private readonly Game _global;
    private readonly FakeProductionXRManager _xrManager;
    private readonly PlayerController _controller;
    private readonly Node3D _rig;
    private readonly Skeleton3D _skeleton;
    private readonly AnimationTree _tree;
    private readonly Camera3D _viewpoint;
    private readonly AnimationNodeStateMachinePlayback _playback;
    private readonly int _leftFootBoneIndex;
    private readonly int _rightFootBoneIndex;
    private readonly ulong _startPhysicsFrame;
    private bool _disposed;

    private PlayerLocomotionProductionWiringHarness(
        SyntheticLocomotionRuntimeFixture content,
        Game global,
        FakeProductionXRManager xrManager,
        PlayerController controller,
        CharacterBody3D actor,
        Node3D rig,
        Skeleton3D skeleton,
        AnimationTree tree,
        CharacterLocomotion locomotion,
        Camera3D viewpoint)
    {
        _content = content;
        _global = global;
        _xrManager = xrManager;
        _controller = controller;
        Actor = actor;
        _rig = rig;
        _skeleton = skeleton;
        _tree = tree;
        Locomotion = locomotion;
        _viewpoint = viewpoint;
        _playback = tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>()
            ?? throw new InvalidOperationException("Expected the player tree to expose the States playback.");
        _leftFootBoneIndex = RequireBone("LeftFoot");
        _rightFootBoneIndex = RequireBone("RightFoot");
        _startPhysicsFrame = Engine.GetPhysicsFrames();
    }

    /// <summary>Latest movement sample pushed through the controller input path.</summary>
    public Vector2 MovementSample
    {
        get;
        private set;
    }

    /// <summary>Latest rotation sample pushed through the controller input path.</summary>
    public Vector2 RotationSample
    {
        get;
        private set;
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

    /// <summary>World position of the actor at harness creation (the spawn origin for world markers).</summary>
    public Vector3 SpawnPosition => Actor.GlobalPosition;

    /// <summary>The rig container's world basis — the production RootMotionReference analogue.</summary>
    public Basis RigGlobalBasis => _rig.GlobalBasis;

    /// <summary>The actor body realised by the harness.</summary>
    public CharacterBody3D Actor
    {
        get;
    }

    /// <summary>
    /// Fixture-declared rate-1 unit speed of the sustained forward gait: authored root displacement per cycle
    /// over the declared cycle duration. This is fixture content, not a production tuning value.
    /// </summary>
    public float DeclaredTravelUnitSpeed
    {
        get
        {
            SyntheticNavigationClipDeclaration forward = _content.Declaration.NavigationManifest["wf"];
            return forward.SkeletonLocalRootResponse.Length() / forward.DurationSeconds;
        }
    }

    /// <summary>The locomotion component under reproduction.</summary>
    public CharacterLocomotion Locomotion
    {
        get;
    }

    /// <summary>Places an independent world marker node at <paramref name="worldPosition" />.</summary>
    public Marker3D PlaceWorldMarker(string name, Vector3 worldPosition)
    {
        var marker = new Marker3D { Name = name, Transform = new Transform3D(Basis.Identity, worldPosition) };
        _global.AddChild(marker);
        return marker;
    }

    /// <summary>
    /// Assembles the production-equivalent composition. <paramref name="rigYawRadians" /> rotates the rig
    /// container (and with it the RootMotionReference basis and the authored animation frame) exactly like
    /// the production visual-rig container; <paramref name="actorHeadingRadians" /> pre-rotates the actor;
    /// <paramref name="parentYawRadians" /> rotates an intermediate actor parent so world-frame derivation is
    /// verified under non-identity parent transforms.
    /// </summary>
    public static async Task<PlayerLocomotionProductionWiringHarness> CreateAsync(
        SceneTree sceneTree,
        string fixtureName,
        float rigYawRadians,
        float actorHeadingRadians = 0f,
        int setupFrames = 8,
        float parentYawRadians = 0f)
    {
        var content = SyntheticLocomotionRuntimeFixture.Create(fixtureName);

        // Not named "Global": Game.SetInstance's integration-test escape hatch lets a differently named
        // fixture Game take over the singleton from the still-live autoload Global without modifying the
        // production test runner's isolated-singleton list.
        Game global = new()
        {
            Name = "WiringGlobal"
        };
        var xrManager = new FakeProductionXRManager { Name = "XR" };
        var actor = new CharacterBody3D
        {
            Name = "Player",
            Transform = new Transform3D(new Basis(Vector3.Up, actorHeadingRadians), Vector3.Zero),
        };
        var rig = new Node3D
        {
            Name = "Rig",
            Transform = new Transform3D(new Basis(Vector3.Up, rigYawRadians), Vector3.Zero),
        };
        Skeleton3D skeleton = SyntheticLocomotionRuntimeFixture.BuildSkeleton();
        skeleton.Name = "SyntheticSkeleton";
        rig.AddChild(skeleton);

        var player = new AnimationPlayer { Name = "AnimationPlayer", RootNode = new NodePath("../Rig") };
        EnsureOk(player.AddAnimationLibrary("locomotion", content.LocomotionLibrary), "locomotion library");
        EnsureOk(player.AddAnimationLibrary("idle", content.IdleLibrary), "idle library");

        // The fixture's player playback tree already mirrors the production player topology (nested States
        // machine, authored entries, rate-wrapped Anim→Rate exits, and the blended sustained-travel tree).
        AnimationTree tree = content.CreatePlayerPlaybackTree();
        tree.RootNode = new NodePath("../Rig");

        var locomotion = new CharacterLocomotion
        {
            Name = "Locomotion",
            TargetCharacterBodyNode = actor,
            AnimationTree = tree,
            RootMotionReference = rig,
            CataloguePath = content.NavigationCataloguePath,
        };
        var hands = new FakeProductionHands { Name = "Hands" };
        var controller = new PlayerController
        {
            Name = "PlayerController",
            LocomotionNode = locomotion,
            HandHolderNode = hands,
        };
        var viewpoint = new Camera3D
        {
            Name = "Viewpoint",
            Transform = new Transform3D(Basis.Identity, new Vector3(0f, 1.6f, 0f)),
        };

        actor.AddChild(rig);
        actor.AddChild(player);
        actor.AddChild(tree);
        actor.AddChild(locomotion);
        actor.AddChild(hands);
        actor.AddChild(controller);
        actor.AddChild(viewpoint);
        var actorParent = new Node3D
        {
            Name = "ActorParent",
            Transform = new Transform3D(new Basis(Vector3.Up, parentYawRadians), Vector3.Zero),
        };
        actorParent.AddChild(actor);
        global.AddChild(xrManager);
        global.AddChild(actorParent);

        global._EnterTree();
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, global);
        await WaitForFramesAsync(sceneTree, setupFrames);
        if (!actor.IsInsideTree())
        {
            content.Dispose();
            throw new InvalidOperationException("Expected the production-wiring composition to attach to the scene tree.");
        }

        // Bind the PlayerController to the fake XR runtime exactly like the grab-input wiring tests.
        xrManager._Ready();
        hands._Ready();
        controller._Ready();
        xrManager.TriggerInitialised();
        ForceControllerXRInitialised(controller);
        await WaitForFramesAsync(sceneTree, 3);

        return new PlayerLocomotionProductionWiringHarness(
            content, global, xrManager, controller, actor, rig, skeleton, tree, locomotion, viewpoint);
    }

    /// <summary>Pushes a movement stick sample through the real controller input path.</summary>
    public void PushMovement(Vector2 value)
    {
        MovementSample = value;
        _xrManager.LeftController.TriggerActionVector2InputChanged(MovementAction, value);
    }

    /// <summary>Pushes a rotation stick sample through the real controller input path.</summary>
    public void PushRotation(Vector2 value)
    {
        RotationSample = value;
        _xrManager.RightController.TriggerActionVector2InputChanged(RotationAction, value);
    }

    /// <summary>Releases both sticks through the controller input path.</summary>
    public void ReleaseSticks()
    {
        PushMovement(Vector2.Zero);
        PushRotation(Vector2.Zero);
    }

    /// <summary>
    /// Awaits <paramref name="physicsTicks" /> real physics frames, capturing one sample per frame. Physics
    /// and animation processing are fully automatic; the harness only observes.
    /// </summary>
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
        StringName node = _playback.GetCurrentNode();
        string graphNode = node.ToString();

        return new ProductionWiringSample(
            Engine.GetPhysicsFrames(),
            (Engine.GetPhysicsFrames() - _startPhysicsFrame) / (float)Engine.PhysicsTicksPerSecond,
            Actor.GlobalPosition,
            Actor.GlobalBasis.GetEuler().Y,
            -Actor.GlobalBasis.Z,
            -_viewpoint.GlobalBasis.Z,
            -_rig.GlobalBasis.Z,
            Locomotion.AuthoredPlanarVelocity,
            Locomotion.AuthoredYawRate,
            new Vector2(Actor.Velocity.X, Actor.Velocity.Z),
            graphNode,
            (float)_playback.GetCurrentPlayPosition(),
            _playback.GetFadingFromNode().ToString(),
            _tree.Get(_travelRateParameter).As<float>(),
            _tree.Get(_domainParameter).As<float>(),
            _tree.Get(_directionalLeftParameter).As<Vector2>(),
            _tree.Get(_directionalRightParameter).As<Vector2>(),
            BoneWorldPosition(_leftFootBoneIndex),
            BoneWorldPosition(_rightFootBoneIndex),
            MovementSample,
            RotationSample,
            Locomotion.LastOrdinaryTravelDemand,
            Locomotion.PlayerState,
            Locomotion.MovementOperationCount,
            Locomotion.LastAuthoredRootWorldDelta,
            Locomotion.LastRequestedWorldDelta,
            Locomotion.LastRealisedWorldDelta);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (GodotObject.IsInstanceValid(_global))
        {
            _global.QueueFree();
            var sceneTree = Engine.GetMainLoop() as SceneTree;
            if (sceneTree is not null)
            {
                await WaitForFramesAsync(sceneTree, 2);
            }
        }

        _content.Dispose();
    }

    private Vector3 BoneWorldPosition(int boneIndex)
        => _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private int RequireBone(string boneName)
    {
        int index = _skeleton.FindBone(boneName);
        return index >= 0
            ? index
            : throw new InvalidOperationException($"Expected the harness skeleton to expose bone '{boneName}'.");
    }

    private static void ForceControllerXRInitialised(PlayerController controller)
    {
        MethodInfo onXRInitialised = typeof(PlayerController).GetMethod(
                "OnXRInitialised",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected PlayerController OnXRInitialised method.");
        _ = onXRInitialised.Invoke(controller, [true]);
    }

    private static void EnsureOk(Error result, string resource)
    {
        if (result != Error.Ok)
        {
            throw new InvalidOperationException($"Could not add synthetic {resource} to the harness player: {result}.");
        }
    }

    private sealed partial class FakeProductionHands : Node, IHasHands
    {
        private IComponent[] _components = [];

        public IReadOnlyList<IComponent> Components => _components;

        public override void _Ready() => _components = [.. GetChildren().OfType<IComponent>()];
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

#pragma warning disable CS0067
        public event Action? PoseRecentered;

        public event Action? HandTrackingModeChanged;
#pragma warning restore CS0067

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
#pragma warning disable CS0067
        public event Action<string>? ActionButtonPressed;

        public event Action<string>? ActionButtonReleased;

        public event Action<string, float>? ActionFloatInputChanged;
#pragma warning restore CS0067

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
