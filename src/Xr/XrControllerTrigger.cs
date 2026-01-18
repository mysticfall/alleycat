using System.Reactive.Linq;
using AlleyCat.Control;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Xr;

public class XrControllerTrigger : ITrigger
{
    public IObservable<Unit> OnPress { get; }

    public IObservable<Unit> OnRelease { get; }

    public XrControllerTrigger(
        InputEventName eventName,
        XRController3D controller,
        ILoggerFactory? loggerFactory = null
    )
    {
        var logger = loggerFactory.GetLogger<XrControllerTrigger>();

        OnPress = Observable.FromEvent<XRController3D.ButtonPressedEventHandler, string>(
                handler => new XRController3D.ButtonPressedEventHandler(handler),
                add => controller.ButtonPressed += add,
                remove => controller.ButtonPressed -= remove
            ).Where(x => x == eventName.Value)
            .Do(_ =>
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Button pressed: {event}", eventName);
                }
            })
            .Select(_ => unit);

        OnRelease = Observable.FromEvent<XRController3D.ButtonReleasedEventHandler, string>(
                handler => new XRController3D.ButtonReleasedEventHandler(handler),
                add => controller.ButtonReleased += add,
                remove => controller.ButtonReleased -= remove
            ).Where(x => x == eventName.Value)
            .Do(_ =>
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Button released: {event}", eventName);
                }
            })
            .Select(_ => unit);
    }
}