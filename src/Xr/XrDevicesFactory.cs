using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Xr;

[GlobalClass]
public partial class XrDevicesFactory : NodeFactory<XrDevices>
{
    [Export] private XROrigin3D? Origin { get; set; }

    [Export] private XRCamera3D? Camera { get; set; }

    [Export] private XRController3D? RightController { get; set; }

    [Export] private XRController3D? LeftController { get; set; }

    [Export] public int MaximumRefreshRate { get; set; } = 90;

    protected override Eff<IEnv, XrDevices> CreateService(ILoggerFactory loggerFactory) =>
        from env in runtime<IEnv>()
        from xr in Optional(XRServer.FindInterface("OpenXR"))
            .Filter(x => x.IsInitialized())
            .ToEff(Error.New("OpenXR not initialised, please check if your headset is connected."))
        from origin in Origin.Require("Origin is not set")
        from camera in Camera.Require("Camera is not set")
        from rightController in RightController.Require("Right controller is not set")
        from leftController in LeftController.Require("Left controller is not set")
        from maxFps in FrameRate.Create(MaximumRefreshRate).ToEff(identity)
        let logger = loggerFactory.CreateLogger<XrDevicesFactory>()
        let openXr = (OpenXRInterface)xr
        from viewport in env.Scene.GetViewport()
        from _ in liftIO(async () =>
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

            viewport.UseXR = true;

            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

            if (RenderingServer.GetRenderingDevice() != null)
            {
                viewport.VrsMode = Viewport.VrsModeEnum.XR;
            }
            else if ((int)ProjectSettings.GetSetting("xr/openxr/foveation_level") == 0)
            {
                logger.LogWarning("OpenXR: Recommend setting Foveation level to High in Project Settings");
            }

            if (!xr.IsInitialized())
            {
                await ToSignal(xr, OpenXRInterface.SignalName.SessionBegun);
            }

            var currentRefreshRate = (int)openXr.DisplayRefreshRate;
            var availableRates = openXr.GetAvailableDisplayRefreshRates();

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Available refresh rates: {rates}", availableRates);
            }

            var newRate = availableRates
                .Select(x => (int)x)
                .OrderDescending()
                .AsIterable()
                .Filter(x => currentRefreshRate <= x && x <= maxFps)
                .Head;

            newRate.Match(
                x =>
                {
                    if (x != currentRefreshRate)
                    {
                        openXr.DisplayRefreshRate = x;
                    }

                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Changed refresh rate to {}.", x);
                    }

                    Engine.PhysicsTicksPerSecond = x;
                },
                () =>
                {
                    if (availableRates.Count == 0)
                    {
                        logger.LogWarning("Target does not support refresh rate extension.");
                    }

                    Engine.PhysicsTicksPerSecond = currentRefreshRate;
                });

            logger.LogInformation("OpenXR session started.");
        })
        let trackers = new XrTrackers(rightController, leftController)
        select new XrDevices(xr, origin, camera, trackers);
}