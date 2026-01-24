using AlleyCat.Control;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;

namespace AlleyCat.Xr;

[GlobalClass]
public partial class XrControllerTriggerFactory : TriggerFactory
{
    [Export] public XrControllerSide Side { get; set; } = XrControllerSide.Right;

    [Export] public string? EventName { get; set; } = "trigger_click";

    protected override Eff<IEnv, ITrigger> CreateService(ILoggerFactory loggerFactory) =>
        from xr in service<XrDevices>()
        let trackers = xr.Trackers
        let controller = Side == XrControllerSide.Right ? trackers.RightHand : trackers.LeftHand
        from action in InputEventName.Create(EventName).ToEff(identity)
        select (ITrigger)new XrControllerTrigger(action, controller, loggerFactory);
}