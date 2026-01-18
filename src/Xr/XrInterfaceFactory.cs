using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Xr;

[GlobalClass]
public partial class XrInterfaceFactory : ResourceFactory<OpenXRInterface>
{
    protected override Eff<IEnv, OpenXRInterface> CreateService(ILoggerFactory loggerFactory) =>
        from env in runtime<IEnv>()
        from xr in Optional(XRServer.FindInterface("OpenXR"))
            .Filter(x => x.IsInitialized())
            .ToEff(Error.New("OpenXR not initialised, please check if your headset is connected."))
        let logger = loggerFactory.CreateLogger<XrInterfaceFactory>()
        let openXr = (OpenXRInterface)xr
        from viewport in env.Scene.GetViewport()
        from _ in liftEff(() =>
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

            viewport.UseXR = true;

            openXr.SessionBegun += () =>
            {
                var refreshRate = (int)openXr.DisplayRefreshRate;

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Changed physics refresh rate: {}", refreshRate);
                }

                Engine.PhysicsTicksPerSecond = refreshRate;
            };

            logger.LogInformation("OpenXR session started.");
        })
        select openXr;
}