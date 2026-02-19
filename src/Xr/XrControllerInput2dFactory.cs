using AlleyCat.Control;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;
using Side = AlleyCat.Common.Side;

namespace AlleyCat.Xr;

[GlobalClass]
public partial class XrControllerInput2dFactory : Input2dFactory
{
    [Export] public Side Side { get; set; } = Side.Right;

    [Export] public string? EventName { get; set; } = "primary";

    protected override Eff<IEnv, IInput2d> CreateService(
        IObservable<Duration> onProcess,
        ILoggerFactory loggerFactory
    ) =>
        from xr in service<XrDevices>()
        let controller = xr.Trackers[Side].Controller
        from eventName in InputEventName.Create(EventName).ToEff(identity)
        select (IInput2d)new XrControllerInput2d(eventName, controller, onProcess);
}