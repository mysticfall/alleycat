using AlleyCat.Testing;
using Godot;
using Array = Godot.Collections.Array;

namespace AlleyCat.XR;

/// <summary>
/// Represents a Node that manages XR device configurations and setup in a Godot project
/// utilising the OpenXR interface.
/// </summary>
[GlobalClass]
public partial class XRDevices : Node
{
    /// <summary>
    /// Represents the maximum allowable refresh rate for XR devices.
    /// </summary>
    [Export]
    public int MaximumRefreshRate
    {
        get;
        set;
    } = 90;

    private OpenXRInterface? _xr;

    /// <inheritdoc />
    public override void _Ready()
    {
        if (RuntimeContext.IsIntegrationTest())
        {
            return;
        }

        _xr = XRServer.FindInterface("OpenXR") as OpenXRInterface;

        if (_xr == null || !_xr.IsInitialized())
        {
            GD.PushError("OpenXR not initialised, please check if your headset is connected.");
            return;
        }

        GD.Print("Initialising OpenXR.");

        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

        Viewport viewport = GetTree().GetRoot().GetViewport();
        viewport.UseXR = true;

        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

        if (RenderingServer.GetRenderingDevice() != null)
        {
            viewport.VrsMode = Viewport.VrsModeEnum.XR;
        }
        else if ((int)ProjectSettings.GetSetting("xr/openxr/foveation_level") == 0)
        {
            GD.PushWarning("OpenXR: Recommend setting Foveation level to High in Project Settings");
        }

        _xr.SessionBegun += OnOpenXRSessionBegun;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_xr is not null)
        {
            _xr.SessionBegun -= OnOpenXRSessionBegun;
        }
    }

    /// <summary>
    /// Handle OpenXR session ready
    /// </summary>
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

        Array? availableRates = _xr.GetAvailableDisplayRefreshRates();

        switch (availableRates.Count)
        {
            case 0:
                GD.Print("OpenXR: Target does not support refresh rate extension");
                break;
            case 1:
                newRate = (float)availableRates[0];
                break;
            default:
                {
                    GD.Print("OpenXR: Available refresh rates: ", availableRates);

                    foreach (Variant rate in availableRates)
                    {
                        float value = (float)rate;

                        if (value > newRate && value <= MaximumRefreshRate)
                        {
                            newRate = value;
                        }
                    }

                    break;
                }
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
