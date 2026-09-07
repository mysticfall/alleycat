using AlleyCat.Common;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Rigging;
using AlleyCat.Testing;
using AlleyCat.XR.HandTracking;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.XR;

/// <summary>
/// Manages XR startup and exposes XR-node abstractions for runtime and tests.
/// </summary>
[GlobalClass]
public partial class XRManager : Node, IServiceRegistrar
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
    /// Emitted when the committed global hand-pose mode changes (XR-002 TR27).
    /// </summary>
    [Signal]
    public delegate void HandTrackingModeChangedEventHandler();

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
    public IXRRuntime Runtime { get; protected internal set; } = null!;

    /// <summary>
    /// Committed global hand-pose mode forwarded from the active runtime; controller before initialisation
    /// (XR-002 TR1, TR27).
    /// </summary>
    public XRHandTrackingMode HandTrackingMode
        => Runtime is { } runtime ? runtime.HandTrackingMode : XRHandTrackingMode.Controller;

    /// <summary>
    /// Optical hand-joint provider forwarded from the active runtime (XR-002 TR28).
    /// </summary>
    public IXRHandJointProvider OpticalHandJoints
        => Runtime is { } runtime
            ? runtime.OpticalHandJoints
            : throw new InvalidOperationException("XR runtime is not initialised.");

    /// <summary>
    /// Per-hand optical grab presentation arbitration state (XR-002 TR30-TR31; INTR-003 TR19): written
    /// authoritatively by the hand grab lifecycle at commit/release and queried by the finger modifier before
    /// each hand's writes. Independent of runtime initialisation so the seam exists as soon as the manager does.
    /// </summary>
    public IOpticalGrabPresentationArbiter OpticalGrabArbiter { get; } = new OpticalGrabPresentationArbiter();

    /// <summary>
    /// Gets the per-side hand-pose source forwarded from the active runtime (XR-002 TR27).
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <returns>The hand-pose source for the requested side.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the XR runtime is not initialised.</exception>
    public IXRHandPoseSource GetHandPoseSource(LimbSide side)
        => Runtime is { } runtime
            ? runtime.GetHandPoseSource(side)
            : throw new InvalidOperationException("XR runtime is not initialised.");

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
        => services.AddSingleton(this);

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
        if (RuntimeContext.ShouldBypassGlobalStartup(GetTree()))
        {
            return;
        }

        PackedScene runtimeScene = RuntimeContext.IsIntegrationTest() ? MockRuntimeScene : OpenXrRuntimeScene;

        Node runtimeNode = runtimeScene.Instantiate()
            ?? throw new InvalidOperationException("XR runtime scene is not configured on XRManager.");

        AddChild(runtimeNode);

        Runtime = runtimeNode as IXRRuntime
                  ?? throw new InvalidOperationException($"XR runtime root '{runtimeNode.GetType().FullName}' must implement IXRRuntime.");

        Runtime.PoseRecentered += EmitPoseRecenteredSignal;
        Runtime.HandTrackingModeChanged += OnRuntimeHandTrackingModeChanged;

        bool initialised = Runtime.Initialise(this.RequireNode<SubViewport>("SubViewport"), MaximumRefreshRate);
        InitialisationAttempted = true;
        InitialisationSucceeded = initialised;

        if (GameLoggerResolver.TryResolve(out ILogger<XRManager>? logger) && logger is not null)
        {
            logger.LogInformation(
                "XR runtime {RuntimeType} initialised with hand-pose mode {HandTrackingMode}.",
                Runtime.GetType().Name,
                Runtime.HandTrackingMode);
        }

        _ = EmitSignal(SignalName.Initialised, initialised);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        _ = _initialisationStates.Remove(GetInstanceId());

        if (Runtime is not null)
        {
            Runtime.PoseRecentered -= EmitPoseRecenteredSignal;
            Runtime.HandTrackingModeChanged -= OnRuntimeHandTrackingModeChanged;
        }
    }

    private void EmitPoseRecenteredSignal() => _ = EmitSignal(SignalName.PoseRecentered);

    private void OnRuntimeHandTrackingModeChanged()
    {
        XRHandTrackingMode mode = Runtime.HandTrackingMode;

        if (GameLoggerResolver.TryResolve(out ILogger<XRManager>? logger) && logger is not null)
        {
            logger.LogInformation("XR hand-pose mode committed to {HandTrackingMode}.", mode);
        }

        _ = EmitSignal(SignalName.HandTrackingModeChanged);
    }
}
