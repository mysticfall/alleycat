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
public partial class XrControllerTriggerFactory : TriggerFactory
{
    [Export] public Side Side { get; set; } = Side.Right;

    [Export] public string? EventName { get; set; } = "trigger_click";

    protected override Eff<IEnv, ITrigger> CreateService(ILoggerFactory loggerFactory) =>
        from xr in service<XrDevices>()
        let controller = xr.Trackers[Side].Controller
        from eventName in InputEventName.Create(EventName).ToEff(identity)
        select (ITrigger)new XrControllerTrigger(eventName, controller, loggerFactory);
}