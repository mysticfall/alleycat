using AlleyCat.Common;
using AlleyCat.Control;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Locomotion;

[GlobalClass]
public partial class LocomotionControlFactory : ControlFactory
{
    [Export] public LocomotionFactory? Locomotion { get; set; }

    [Export] public Input2dFactory? MovementInput { get; set; }

    [Export] public Input2dFactory? RotationInput { get; set; }

    protected override Eff<IEnv, IControl> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from locomotion in Locomotion
            .Require("Locomotion is not set.")
            .Bind(x => x.TypedService)
        from movement in MovementInput
            .Require("Movement input is not set.")
            .Bind(x => x.TypedService)
        from rotation in RotationInput
            .Require("Rotation input is not set.")
            .Bind(x => x.TypedService)
        select (IControl)new LocomotionControl(locomotion, movement, rotation);
}