using AlleyCat.Common;
using AlleyCat.Core.Logging;
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
    public Node3D OriginNode => this;

    /// <inheritdoc />
    public bool Initialise(SubViewport viewport, int maximumRefreshRate)
    {
        _maximumRefreshRate = maximumRefreshRate;

        OpenXRCameraNode cameraNode = this.RequireNode<OpenXRCameraNode>("MainCamera");
        OpenXRHandControllerNode rightControllerNode = this.RequireNode<OpenXRHandControllerNode>("RightController");
        OpenXRHandControllerNode leftControllerNode = this.RequireNode<OpenXRHandControllerNode>("LeftController");
        OpenXRCompositionLayerEquirect compositionLayer = this.RequireNode<OpenXRCompositionLayerEquirect>("XRCompositionLayer");

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
    public override void _ExitTree()
    {
        if (_xr is null || !_xrSignalsConnected)
        {
            return;
        }

        _xr.SessionBegun -= OnOpenXRSessionBegun;
        _xr.PoseRecentered -= OnRuntimePoseRecentered;
        _xrSignalsConnected = false;
    }

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
