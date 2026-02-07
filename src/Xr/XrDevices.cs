using System.Reactive.Linq;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Xr;

public readonly record struct XrHandTracker(
    XRController3D Controller,
    Node3D Placeholder
);

public readonly record struct XrTrackers(
    XrHandTracker RightHand,
    XrHandTracker LeftHand
);

public class XrDevices(
    OpenXRInterface xr,
    XROrigin3D origin,
    XRCamera3D camera,
    XrTrackers trackers,
    ILoggerFactory? loggerFactory = null
) : IRunnable
{
    public OpenXRInterface Interface => xr;

    public XROrigin3D Origin => origin;

    public XRCamera3D Camera => camera;

    public XrTrackers Trackers => trackers;

    public IO<Length> EyeHeight => IO.lift(() =>
        (camera.GlobalPosition.Y - origin.GlobalPosition.Y).Metres()
    );

    public IO<Length> BaseEyeHeight => _baseEyeHeight.ValueIO;

    private readonly Atom<Length> _baseEyeHeight = Atom(1.2.Metres());

    private readonly ILogger _logger = loggerFactory.GetLogger<XrDevices>();

    public Eff<IEnv, IDisposable> Run()
    {
        var onRecenter = Observable
            .FromEvent(
                add => xr.PoseRecentered += add,
                remove => xr.PoseRecentered -= remove);

        return liftEff(() => onRecenter.Subscribe(_ =>
        {
            var reset =
                from height in EyeHeight
                from _1 in _baseEyeHeight.SwapIO(_ => height)
                from _2 in IO.lift(() =>
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Adjusted the eye height to {height} metres.", height);
                    }
                })
                select unit;

            reset.Run();
        }));
    }
}