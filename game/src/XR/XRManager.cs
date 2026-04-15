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
    private static readonly Dictionary<ulong, (bool Attempted, bool Succeeded)> _initialisationStates = [];

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
    /// Active XR runtime instance.
    /// </summary>
    public IXRRuntime Runtime { get; private set; } = null!;

    /// <summary>
    /// Whether XR initialisation has been attempted.
    /// </summary>
    public bool InitialisationAttempted
    {
        get => _initialisationStates.TryGetValue(GetInstanceId(), out (bool Attempted, bool Succeeded) state)
            && state.Attempted;
        protected set
        {
            ulong instanceId = GetInstanceId();
            bool succeeded = _initialisationStates.TryGetValue(instanceId, out (bool Attempted, bool Succeeded) state)
                && state.Succeeded;
            _initialisationStates[instanceId] = (value, succeeded);
        }
    }

    /// <summary>
    /// Whether the latest XR initialisation attempt succeeded.
    /// </summary>
    public bool InitialisationSucceeded
    {
        get => _initialisationStates.TryGetValue(GetInstanceId(), out (bool Attempted, bool Succeeded) state)
            && state.Succeeded;
        protected set
        {
            ulong instanceId = GetInstanceId();
            bool attempted = _initialisationStates.TryGetValue(instanceId, out (bool Attempted, bool Succeeded) state)
                && state.Attempted;
            _initialisationStates[instanceId] = (attempted, value);
        }
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        PackedScene runtimeScene = RuntimeContext.IsIntegrationTest() ? MockRuntimeScene : OpenXrRuntimeScene;

        Node runtimeNode = runtimeScene.Instantiate()
            ?? throw new InvalidOperationException("XR runtime scene is not configured on XRManager.");

        AddChild(runtimeNode);

        Runtime = runtimeNode as IXRRuntime
                  ?? throw new InvalidOperationException($"XR runtime root '{runtimeNode.GetType().FullName}' must implement IXRRuntime.");

        Runtime.PoseRecentered += EmitPoseRecenteredSignal;

        bool initialised = Runtime.Initialise(this.RequireNode<SubViewport>("SubViewport"), MaximumRefreshRate);
        InitialisationAttempted = true;
        InitialisationSucceeded = initialised;
        _ = EmitSignal(SignalName.Initialised, initialised);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        _ = _initialisationStates.Remove(GetInstanceId());

        Runtime.PoseRecentered -= EmitPoseRecenteredSignal;
    }

    private void EmitPoseRecenteredSignal() => _ = EmitSignal(SignalName.PoseRecentered);
}
