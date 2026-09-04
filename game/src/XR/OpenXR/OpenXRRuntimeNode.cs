using AlleyCat.Common;
using AlleyCat.Core.Logging;
using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Microsoft.Extensions.Logging;
using Array = Godot.Collections.Array;

namespace AlleyCat.XR.OpenXR;

/// <summary>
/// OpenXR runtime implementation bound to the OpenXR runtime scene root.
/// </summary>
[GlobalClass]
public partial class OpenXRRuntimeNode : XROrigin3D, IXRRuntime, IXROrigin
{
    private int _maximumRefreshRate;

    private OpenXRInterface? _xr;

    private bool _xrSignalsConnected;

    private IOpenXRHandTracking? _handTracking;

    private bool _handTrackingEvaluationLogged;

    /// <inheritdoc />
    public IXROrigin Origin => this;

    /// <inheritdoc />
    public IXRCamera Camera { get; private set; } = null!;

    /// <inheritdoc />
    public IXRHandController RightHandController { get; private set; } = null!;

    /// <inheritdoc />
    public IXRHandController LeftHandController { get; private set; } = null!;

    /// <inheritdoc />
    public event Action? PoseRecentered;

    /// <inheritdoc />
    public event Action? HandTrackingModeChanged;

    /// <inheritdoc />
    /// <remarks>
    /// Controller before initialisation; after initialisation the mode is arbitrated between controller and optical
    /// sources by the hand-tracking adapter when the runtime supports articulated hand tracking (XR-002 TR1, TR26).
    /// </remarks>
    public XRHandTrackingMode HandTrackingMode => _handTracking?.HandTrackingMode ?? XRHandTrackingMode.Controller;

    /// <inheritdoc />
    public IXRHandJointProvider OpticalHandJoints
        => _handTracking?.OpticalHandJoints ?? XREmptyHandJointProvider.Instance;

    /// <inheritdoc />
    public Node3D OriginNode => this;

    /// <summary>
    /// Gets the per-side hand-pose source selected by the committed global hand-pose mode (XR-002 TR27).
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <returns>The hand-pose source for the requested side.</returns>
    /// <exception cref="InvalidOperationException">Thrown before the runtime has been initialised.</exception>
    public IXRHandPoseSource GetHandPoseSource(LimbSide side)
        => _handTracking?.GetHandPoseSource(side)
           ?? throw new InvalidOperationException("OpenXR hand-pose sources are unavailable before Initialise.");

    /// <inheritdoc />
    public bool Initialise(SubViewport viewport, int maximumRefreshRate)
    {
        _maximumRefreshRate = maximumRefreshRate;

        OpenXRCameraNode cameraNode = this.RequireNode<OpenXRCameraNode>("MainCamera");
        OpenXRHandControllerNode rightControllerNode = this.RequireNode<OpenXRHandControllerNode>("RightController");
        OpenXRHandControllerNode leftControllerNode = this.RequireNode<OpenXRHandControllerNode>("LeftController");
        OpenXRCompositionLayerEquirect compositionLayer = this.RequireNode<OpenXRCompositionLayerEquirect>("XRCompositionLayer");
        XRNode3D rightOpticalHand = this.RequireNode<XRNode3D>("RightOpticalHand");
        XRNode3D leftOpticalHand = this.RequireNode<XRNode3D>("LeftOpticalHand");
        Node3D rightOpticalAnchor = rightOpticalHand.RequireNode<Node3D>("WristAnchor/OpticalHandAnchor");
        Node3D leftOpticalAnchor = leftOpticalHand.RequireNode<Node3D>("WristAnchor/OpticalHandAnchor");

        compositionLayer.LayerViewport = viewport;

        Camera = cameraNode;
        RightHandController = rightControllerNode;
        LeftHandController = leftControllerNode;

        _xr = XRServer.FindInterface("OpenXR") as OpenXRInterface;

        if (_xr == null || !_xr.IsInitialized())
        {
            GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogError(
                "OpenXR not initialised, please check if your headset is connected.");
            return false;
        }

        GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogInformation("Initialising OpenXR.");

        _handTracking = CreateHandTracking(
            rightOpticalHand,
            leftOpticalHand,
            rightOpticalAnchor,
            leftOpticalAnchor,
            rightControllerNode,
            leftControllerNode);
        _handTracking.ModeChanged += OnHandTrackingModeChanged;

        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

        Viewport rootViewport = GetTree().GetRoot().GetViewport();
        rootViewport.UseXR = true;

        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

        if (RenderingServer.GetRenderingDevice() != null)
        {
            rootViewport.VrsMode = Viewport.VrsModeEnum.XR;
        }
        else if ((int)ProjectSettings.GetSetting("xr/openxr/foveation_level") == 0)
        {
            GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogWarning(
                "OpenXR: Recommend setting Foveation level to High in Project Settings.");
        }

        _xr.SessionBegun += OnOpenXRSessionBegun;
        _xr.PoseRecentered += OnRuntimePoseRecentered;
        _xrSignalsConnected = true;

        return true;
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        _ = delta;

        // Physics tick: the earliest consistent point before IK consumers sample the hand-pose sources (see
        // OpenXROpticalHandTracking remarks). Both sides are evaluated in one atomic step (XR-002 TR3).
        if (_handTracking is null)
        {
            return;
        }

        if (!_handTrackingEvaluationLogged)
        {
            GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogInformation(
                "OpenXR hand-tracking coordinator received its first physics evaluation tick with process mode {ProcessMode}.",
                ProcessMode);
            _handTrackingEvaluationLogged = true;
        }

        _handTracking.EvaluateTick();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_handTracking is not null)
        {
            _handTracking.ModeChanged -= OnHandTrackingModeChanged;

            if (_handTracking is OpenXROpticalHandTracking opticalHandTracking)
            {
                opticalHandTracking.Shutdown();
            }
        }

        if (_xr is null || !_xrSignalsConnected)
        {
            return;
        }

        _xr.SessionBegun -= OnOpenXRSessionBegun;
        _xr.PoseRecentered -= OnRuntimePoseRecentered;
        _xrSignalsConnected = false;
    }

    private IOpenXRHandTracking CreateHandTracking(
        XRNode3D rightOpticalHand,
        XRNode3D leftOpticalHand,
        Node3D rightOpticalAnchor,
        Node3D leftOpticalAnchor,
        OpenXRHandControllerNode rightControllerNode,
        OpenXRHandControllerNode leftControllerNode)
    {
        ILogger<OpenXRRuntimeNode> logger = GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>();

        // Capability gate: absence of articulated hand-tracking support leaves everything in controller mode with
        // no errors (XR-002 TR26). The optical hand nodes stay wired in the scene for when support appears.
        if (_xr is null || !_xr.IsHandTrackingSupported())
        {
            logger.LogInformation(
                "OpenXR: Articulated hand tracking is not supported by this runtime; controller hand-pose mode is kept.");

            return new OpenXRControllerHandTracking(new XRControllerHandTracking(rightControllerNode, leftControllerNode));
        }

        logger.LogInformation("OpenXR: Articulated hand tracking is supported; optical hand-pose mode is enabled.");

        return new OpenXROpticalHandTracking(
            this,
            rightOpticalHand,
            leftOpticalHand,
            rightOpticalAnchor,
            leftOpticalAnchor,
            rightControllerNode,
            leftControllerNode);
    }

    private void OnHandTrackingModeChanged() => HandTrackingModeChanged?.Invoke();

    private void OnRuntimePoseRecentered() => PoseRecentered?.Invoke();

    private void OnOpenXRSessionBegun()
    {
        if (_xr == null)
        {
            return;
        }

        float currentRefreshRate = _xr.DisplayRefreshRate;

        if (currentRefreshRate > 0.0F)
        {
            GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogInformation(
                "OpenXR: Refresh rate reported as {RefreshRate}.",
                currentRefreshRate);
        }
        else
        {
            GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogInformation("OpenXR: No refresh rate given by XR runtime.");
        }

        float newRate = currentRefreshRate;

        Array availableRates = _xr.GetAvailableDisplayRefreshRates();

        switch (availableRates.Count)
        {
            case 0:
                GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogInformation(
                    "OpenXR: Target does not support refresh rate extension.");
                break;
            case 1:
                newRate = (float)availableRates[0];
                break;
            default:
                GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogInformation(
                    "OpenXR: Available refresh rates: {AvailableRates}.",
                    availableRates);

                foreach (Variant rate in availableRates)
                {
                    float value = (float)rate;

                    if (value > newRate && value <= _maximumRefreshRate)
                    {
                        newRate = value;
                    }
                }

                break;
        }

        if (Math.Abs(currentRefreshRate - newRate) > Mathf.Epsilon)
        {
            GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogInformation(
                "OpenXR: Setting refresh rate to {RefreshRate}.",
                newRate);
            _xr.DisplayRefreshRate = newRate;
            currentRefreshRate = newRate;
        }

        Engine.PhysicsTicksPerSecond = (int)currentRefreshRate;

        GameLoggerResolver.ResolveRequired<OpenXRRuntimeNode>().LogInformation("OpenXR session started.");
    }
}
