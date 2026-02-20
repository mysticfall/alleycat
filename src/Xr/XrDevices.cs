using System.Reactive.Linq;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using Side = AlleyCat.Common.Side;

namespace AlleyCat.Xr;

public readonly record struct XrHandTracker(
    XRController3D Controller,
    Node3D Placeholder
);

public readonly record struct XrTrackers(
    XrHandTracker RightHand,
    XrHandTracker LeftHand
)
{
    public XrHandTracker this[Side side] => side == Side.Right ? RightHand : LeftHand;
};

public class XrDevices : IRunnable
{
    public OpenXRInterface Interface { get; }

    public XROrigin3D Origin { get; }

    public XRCamera3D Camera { get; }

    public XrTrackers Trackers { get; }

    public IO<Length> EyeHeight { get; }

    public IO<Length> BaseEyeHeight => _baseEyeHeight.ValueIO;

    public Eff<IEnv, IDisposable> Run { get; }

    private readonly Atom<Length> _baseEyeHeight = Atom(1.2.Metres());

    public XrDevices(
        OpenXRInterface xr,
        XROrigin3D origin,
        XRCamera3D camera,
        XrTrackers trackers,
        ILoggerFactory? loggerFactory = null
    )
    {
        Interface = xr;
        Origin = origin;
        Camera = camera;
        Trackers = trackers;
        EyeHeight = IO.lift(() =>
            (camera.GlobalPosition.Y - origin.GlobalPosition.Y).Metres()
        );

        var logger = loggerFactory.GetLogger<XrDevices>();

        var onRecenter = Observable
            .FromEvent(
                add => xr.PoseRecentered += add,
                remove => xr.PoseRecentered -= remove);

        Run = IO.lift(() => onRecenter.Subscribe(_ =>
        {
            var reset =
                from height in EyeHeight
                from _1 in _baseEyeHeight.SwapIO(_ => height)
                from _2 in IO.lift(() =>
                {
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            "Adjusted the eye height to {height} metres.",
                            height
                        );
                    }
                })
                select unit;

            reset.Run();
        }));
    }
}