using System.Reflection;
using AlleyCat.Body.Hands;
using AlleyCat.Control;
using AlleyCat.Core;
using AlleyCat.Interaction;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// Runtime coverage for XR controller grab input routed through the live player-controller path.
/// </summary>
public sealed class PlayerControllerGrabInputIntegrationTests
{
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";

    /// <summary>
    /// Verifies analogue grip input drives the same hand grab/release path as XR button clicks.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerController_GripFloatThreshold_RoutesGrabAndReleaseToMatchingHand()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeGrabInputFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.2f);
            Assert.True(fixture.ControllerIsBound, "Expected PlayerController to bind to the fake XR runtime before grab input is emitted.");
            Assert.Equal(0, fixture.RightHand.GrabCallCount);

            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.7f);
            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.9f);
            Assert.Equal(1, fixture.RightHand.GrabCallCount);
            Assert.Equal(0, fixture.LeftHand.GrabCallCount);

            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.4f);
            Assert.Equal(0, fixture.RightHand.ReleaseCallCount);

            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.1f);
            Assert.Equal(1, fixture.RightHand.ReleaseCallCount);

            fixture.XRManager.LeftController.TriggerActionFloatInputChanged("grip", 0.8f);
            Assert.Equal(1, fixture.LeftHand.GrabCallCount);
        }
        finally
        {
            fixture.Global.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Verifies the installed mirror-room player resolves its template-installed hand holder before live grab input is routed.
    /// </summary>
    [Headless]
    [Fact]
    public async Task MirrorRoomPlayer_GripInput_ResolvesTemplateInstalledHandsForRouting()
    {
        SceneTree sceneTree = GetSceneTree();
        MirrorRoomGrabInputFixture fixture = await CreateMirrorRoomFixtureAsync(sceneTree);

        try
        {
            Assert.Same(fixture.PlayerHands, GetPropertyValue<Node>(fixture.Controller, "HandHolderNode"));

            InvokeGrabButtonPressed(fixture.Controller, "Right", "grip_click");
            await WaitForFramesAsync(sceneTree, 1);

            object? hands = fixture.ControllerHands;
            Assert.NotNull(hands);
            Assert.Same(fixture.PlayerHands, hands);
            Assert.Same(fixture.RightHand, TryGetHandNode(hands, "Right"));
            Assert.Same(fixture.LeftHand, TryGetHandNode(hands, "Left"));

            Assert.Same(fixture.PlayerHands, fixture.ControllerHands);
            object? routedHands = fixture.ControllerHands;
            Assert.NotNull(routedHands);
            Assert.Same(fixture.RightHand, TryGetHandNode(routedHands, "Right"));
        }
        finally
        {
            fixture.Global.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    private static async Task<RuntimeGrabInputFixture> CreateFixtureAsync(SceneTree sceneTree)
    {
        Game global = new()
        {
            Name = "Global",
        };

        FakeXRManager xrManager = new()
        {
            Name = "XR",
        };

        Node player = new()
        {
            Name = "Player",
        };

        FakeHands hands = new()
        {
            Name = "Hands",
        };
        FakeHand rightHand = new(LimbSide.Right)
        {
            Name = "RightHand",
        };
        FakeHand leftHand = new(LimbSide.Left)
        {
            Name = "LeftHand",
        };
        FakeLocomotion locomotion = new()
        {
            Name = "Locomotion",
        };
        PlayerController controller = new()
        {
            Name = "PlayerController",
            LocomotionNode = locomotion,
            HandHolderNode = hands,
        };

        hands.AddChild(rightHand);
        hands.AddChild(leftHand);
        player.AddChild(locomotion);
        player.AddChild(hands);
        player.AddChild(controller);
        global.AddChild(xrManager);
        global.AddChild(player);

        global._EnterTree();
        sceneTree.Root.AddChild(global);
        await WaitForFramesAsync(sceneTree, 10);
        xrManager._Ready();
        hands._Ready();
        controller._Ready();
        xrManager.TriggerInitialised();
        ForceControllerXRInitialised(controller);
        await WaitForFramesAsync(sceneTree, 3);

        return new RuntimeGrabInputFixture(global, xrManager, controller, rightHand, leftHand);
    }

    private static async Task<MirrorRoomGrabInputFixture> CreateMirrorRoomFixtureAsync(SceneTree sceneTree)
    {
        Game global = new()
        {
            Name = "Global",
        };

        FakeXRManager xrManager = new()
        {
            Name = "XR",
        };

        Node mirrorRoom = LoadPackedScene(MirrorRoomScenePath).Instantiate();

        global.AddChild(xrManager);
        global.AddChild(mirrorRoom);
        global._EnterTree();
        sceneTree.Root.AddChild(global);

        Node player = mirrorRoom.GetNode("Actors/Player");
        EnsureCharacterRuntimeInstalled(player);
        await WaitForFramesAsync(sceneTree, 8);

        Node controller = player.GetNode<Node>("PlayerController");
        await WaitForFramesAsync(sceneTree, 2);

        return new MirrorRoomGrabInputFixture(
            global,
            xrManager,
            controller,
            player.GetNode<Node>("Hands"),
            player.GetNode<Node>("Hands/RightHand"),
            player.GetNode<Node>("Hands/LeftHand"));
    }

    private sealed record RuntimeGrabInputFixture(
        Game Global,
        FakeXRManager XRManager,
        PlayerController Controller,
        FakeHand RightHand,
        FakeHand LeftHand)
    {
        private static readonly FieldInfo _isBoundField = typeof(PlayerController)
            .GetField("_isBound", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected PlayerController _isBound field for grab-input diagnostics.");

        public bool ControllerIsBound => (bool)(_isBoundField.GetValue(Controller) ?? false);
    }

    private sealed record MirrorRoomGrabInputFixture(
        Game Global,
        FakeXRManager XRManager,
        Node Controller,
        Node PlayerHands,
        Node RightHand,
        Node LeftHand)
    {
        public object? ControllerHands => GetRuntimeField(Controller, "_hands").GetValue(Controller);
    }

    private static void ForceControllerXRInitialised(object controller)
    {
        MethodInfo onXRInitialised = controller.GetType()
            .GetMethod("OnXRInitialised", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected PlayerController OnXRInitialised method for grab-input tests.");
        _ = onXRInitialised.Invoke(controller, [true]);
    }

    private static FieldInfo GetRuntimeField(object source, string fieldName)
        => source.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected runtime field '{fieldName}' on {source.GetType().FullName}.");

    private static T GetPropertyValue<T>(object source, string propertyName)
        => (T)(source.GetType().GetProperty(propertyName)?.GetValue(source)
            ?? throw new InvalidOperationException($"Expected property '{propertyName}' on {source.GetType().FullName}."));

    private static Node? TryGetHandNode(object hands, string sideName)
    {
        MethodInfo tryGetHand = hands.GetType().GetInterface("AlleyCat.Body.Hands.IHasHands")
            ?.GetMethod("TryGetHand")
            ?? throw new InvalidOperationException($"Expected {hands.GetType().FullName} to expose IHasHands.TryGetHand.");
        Type sideType = tryGetHand.GetParameters()[0].ParameterType;
        object side = Enum.Parse(sideType, sideName);
        object?[] parameters = [side, null];

        bool resolved = (bool)(tryGetHand.Invoke(hands, parameters) ?? false);
        return resolved ? parameters[1] as Node : null;
    }

    private static void InvokeGrabButtonPressed(object controller, string sideName, string actionName)
    {
        MethodInfo handleGrabButtonPressed = controller.GetType()
            .GetMethod("HandleGrabButtonPressed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected PlayerController HandleGrabButtonPressed method for grab-input tests.");
        Type sideType = handleGrabButtonPressed.GetParameters()[0].ParameterType;
        object side = Enum.Parse(sideType, sideName);
        _ = handleGrabButtonPressed.Invoke(controller, [side, actionName]);
    }

    private sealed partial class FakeLocomotion : Node, AlleyCat.Control.Locomotion.ILocomotion
    {
        public void Move(Vector2 input)
            => _ = input;

        public void Rotate(Vector2 input)
            => _ = input;
    }

    private sealed partial class FakeHands : Node, IHasHands
    {
        private IComponent[] _components = [];

        public IReadOnlyList<IComponent> Components => _components;

        public override void _Ready() => _components = [.. GetChildren().OfType<IComponent>()];
    }

    private sealed partial class FakeHand(LimbSide side) : Node, IHand
    {
        public LimbSide Side => side;

        public IGrabbable? CurrentGrabbed => null;

        public int GrabCallCount
        {
            get; private set;
        }

        public int ReleaseCallCount
        {
            get; private set;
        }

        public IGrabbable? Grab()
        {
            GrabCallCount++;
            return null;
        }

        public void Release() => ReleaseCallCount++;
    }

    private sealed partial class FakeXRManager : XRManager
    {
        private static readonly FieldInfo _runtimeBackingField = typeof(XRManager)
            .GetField("<Runtime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected XRManager runtime backing field for grab-input tests.");

        private readonly FakeXRRuntime _runtime = new();

        public FakeXRHandController RightController => _runtime.RightControllerNode;

        public FakeXRHandController LeftController => _runtime.LeftControllerNode;

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

    private sealed class FakeXRRuntime : IXRRuntime
    {
        public FakeXRRuntime()
        {
            OriginNode = new Node3D();
            CameraNode = new Camera3D();
            RightControllerNode = new FakeXRHandController();
            LeftControllerNode = new FakeXRHandController();
        }

        public IXROrigin Origin => new FakeXROrigin(OriginNode);

        public IXRCamera Camera => new FakeXRCamera(CameraNode);

        public IXRHandController RightHandController => RightControllerNode;

        public IXRHandController LeftHandController => LeftControllerNode;

#pragma warning disable CS0067
        public event Action? PoseRecentered;
#pragma warning restore CS0067

        public Node3D OriginNode
        {
            get;
        }

        public Camera3D CameraNode
        {
            get;
        }

        public FakeXRHandController RightControllerNode
        {
            get;
        }

        public FakeXRHandController LeftControllerNode
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

    private sealed partial class FakeXRHandController : Node3D, IXRHandController
    {
#pragma warning disable CS0067
        public event Action<string>? ActionButtonPressed;
        public event Action<string>? ActionButtonReleased;
        public event Action<string, float>? ActionFloatInputChanged;
        public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

        public Node3D ControllerNode => this;

        public Node3D HandPositionNode => this;

        public void TriggerActionFloatInputChanged(string actionName, float value)
            => ActionFloatInputChanged?.Invoke(actionName, value);
    }

    private sealed record FakeXROrigin(Node3D OriginNode) : IXROrigin
    {
        public float WorldScale { get; set; } = 1.0f;
    }

    private sealed record FakeXRCamera(Camera3D CameraNode) : IXRCamera;
}
