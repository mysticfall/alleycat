using AlleyCat.Common;
using Godot;
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
            GD.PushError("OpenXR not initialised, please check if your headset is connected.");
            return false;
        }

        GD.Print("Initialising OpenXR.");

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
            GD.PushWarning("OpenXR: Recommend setting Foveation level to High in Project Settings");
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

        GD.Print(currentRefreshRate > 0.0F
            ? $"OpenXR: Refresh rate reported as {currentRefreshRate}"
            : "OpenXR: No refresh rate given by XR runtime");

        float newRate = currentRefreshRate;

        Array availableRates = _xr.GetAvailableDisplayRefreshRates();

        switch (availableRates.Count)
        {
            case 0:
                GD.Print("OpenXR: Target does not support refresh rate extension");
                break;
            case 1:
                newRate = (float)availableRates[0];
                break;
            default:
                GD.Print("OpenXR: Available refresh rates: ", availableRates);

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
            GD.Print($"OpenXR: Setting refresh rate to {newRate}");
            _xr.DisplayRefreshRate = newRate;
            currentRefreshRate = newRate;
        }

        Engine.PhysicsTicksPerSecond = (int)currentRefreshRate;

        GD.Print("OpenXR session started.");
    }
}
