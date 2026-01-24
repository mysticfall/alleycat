using System.Reactive.Linq;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Xr;

public class WorldScaleAdjuster(
    Skeleton3D skeleton,
    Marker3D viewpoint,
    XRCamera3D camera,
    XROrigin3D origin,
    OpenXRInterface xr,
    ILoggerFactory? loggerFactory = null
) : IRunnable
{
    private readonly ILogger _logger = loggerFactory.GetLogger<WorldScaleAdjuster>();

    public Eff<IEnv, IDisposable> Run()
    {
        var onRecenter = Observable
            .FromEvent(
                add => xr.PoseRecentered += add,
                remove => xr.PoseRecentered -= remove);

        return liftEff(() => onRecenter.Subscribe(_ =>
        {
            var physicalHeight = camera.GlobalPosition.Y - origin.GlobalPosition.Y;
            var virtualHeight = viewpoint.GlobalPosition.Y - skeleton.GlobalPosition.Y;

            var scale = virtualHeight * origin.WorldScale / physicalHeight;

            origin.WorldScale = scale;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Physical height: {height:F3}", physicalHeight);
                _logger.LogDebug("Virtual height: {height:F3}", virtualHeight);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Changed world scale to {scale:F3}.", scale);
            }
        }));
    }
}