using AlleyCat.Common;
using AlleyCat.Testing;
using Godot;

namespace AlleyCat.XR;

/// <summary>
/// Manages XR startup and exposes XR-node abstractions for runtime and tests.
/// </summary>
[GlobalClass]
public partial class XRManager : Node
{
    /// <summary>
    /// Emitted after the manager attempts initialisation.
    /// </summary>
    [Signal]
    public delegate void InitialisedEventHandler(bool succeeded);

    /// <summary>
    /// Emitted when the user recentres pose.
    /// </summary>
    [Signal]
    public delegate void PoseRecenteredEventHandler();

    /// <summary>
    /// Runtime scene used for normal OpenXR execution.
    /// </summary>
    [Export]
    public PackedScene OpenXrRuntimeScene { get; set; } = null!;

    /// <summary>
    /// Runtime scene used during integration tests.
    /// </summary>
    [Export]
    public PackedScene MockRuntimeScene { get; set; } = null!;

    /// <summary>
    /// Represents the maximum allowable refresh rate for XR devices.
    /// </summary>
    [Export]
    public int MaximumRefreshRate { get; set; } = 90;

    /// <summary>
    /// Abstraction for the active XR origin.
    /// </summary>
    public IXROrigin Origin { get; private set; } = null!;

    /// <summary>
    /// Abstraction for the active XR camera.
    /// </summary>
    public IXRCamera Camera { get; private set; } = null!;

    /// <summary>
    /// Abstraction for the active right-hand controller.
    /// </summary>
    public IXRHandController RightHandController { get; private set; } = null!;

    /// <summary>
    /// Abstraction for the active left-hand controller.
    /// </summary>
    public IXRHandController LeftHandController { get; private set; } = null!;

    private IXRRuntime? _runtime;

    /// <inheritdoc />
    public override void _Ready()
    {
        PackedScene runtimeScene = RuntimeContext.IsIntegrationTest() ? MockRuntimeScene : OpenXrRuntimeScene;

        Node runtimeNode = runtimeScene.Instantiate()
            ?? throw new InvalidOperationException("XR runtime scene is not configured on XRManager.");

        AddChild(runtimeNode);

        _runtime = runtimeNode as IXRRuntime
            ?? throw new InvalidOperationException($"XR runtime root '{runtimeNode.GetType().FullName}' must implement IXRRuntime.");

        _runtime.PoseRecentered += EmitPoseRecenteredSignal;

        Origin = _runtime.Origin;
        Camera = _runtime.Camera;
        RightHandController = _runtime.RightHandController;
        LeftHandController = _runtime.LeftHandController;

        bool initialised = _runtime.Initialise(this.RequireNode<SubViewport>("SubViewport"), MaximumRefreshRate);
        _ = EmitSignal(SignalName.Initialised, initialised);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_runtime == null)
        {
            return;
        }

        _runtime.PoseRecentered -= EmitPoseRecenteredSignal;
        _runtime = null;
    }

    private void EmitPoseRecenteredSignal() => _ = EmitSignal(SignalName.PoseRecentered);
}
