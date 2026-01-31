using System.Reactive.Linq;
using System.Reactive.Subjects;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Xr;

public readonly record struct XrTrackers(
    XRController3D RightHand,
    XRController3D LeftHand
);

public class XrDevices(
    OpenXRInterface xr,
    XROrigin3D origin,
    XRCamera3D camera,
    XrTrackers trackers,
    ILoggerFactory? loggerFactory = null
) : IRunnable, IDisposable
{
    public OpenXRInterface Interface => xr;

    public XROrigin3D Origin => origin;

    public XRCamera3D Camera => camera;

    public XrTrackers Trackers => trackers;

    public IObservable<Length> OnEyeHeightChange => _eyeHeightSubject.AsObservable();

    private readonly Subject<Length> _eyeHeightSubject = new();

    private readonly ILogger _logger = loggerFactory.GetLogger<XrDevices>();

    public Eff<IEnv, IDisposable> Run()
    {
        var onRecenter = Observable
            .FromEvent(
                add => xr.PoseRecentered += add,
                remove => xr.PoseRecentered -= remove);

        return liftEff(() => onRecenter.Subscribe(_ =>
        {
            var height = (camera.GlobalPosition.Y - origin.GlobalPosition.Y).Metres();

            _eyeHeightSubject.OnNext(height);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Adjusted the eye height to {height} metres.", height);
            }
        }));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _eyeHeightSubject.Dispose();
    }
}