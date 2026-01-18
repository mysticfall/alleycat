using AlleyCat.Control;
using AlleyCat.Env;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Xr;

[GlobalClass]
public partial class XrControllerTriggerFactory : TriggerFactory
{
    [Export] public XRController3D? Controller { get; set; }

    [Export] public string? EventName { get; set; } = "trigger_click";

    protected override Eff<IEnv, ITrigger> CreateService(ILoggerFactory loggerFactory) =>
        from controller in Controller.Require("Controller is not set.")
        from action in InputEventName.Create(EventName).ToEff(identity)
        select (ITrigger)new XrControllerTrigger(action, controller, loggerFactory);
}