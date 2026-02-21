using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Locomotion.Rotation;
using AlleyCat.Locomotion.Velocity;
using AlleyCat.Service;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Locomotion;

[GlobalClass]
public abstract partial class LocomotionFactory : NodeFactory<ILocomotion>
{
    [Export] public VelocityCalculatorFactory? VelocityCalculator { get; set; }

    [Export] public RotationCalculatorFactory? RotationCalculator { get; set; }

    protected override Eff<IEnv, ILocomotion> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from velocityCalculator in
            this.GetService<IVelocityCalculator>() |
            VelocityCalculator
                .Require("Velocity calculator is not set.")
                .Bind(x => x.TypedService)
        from rotationCalculator in
            this.GetService<IRotationCalculator>() |
            RotationCalculator
                .Require("Rotation calculator is not set.")
                .Bind(x => x.TypedService)
        from locomotion in CreateService(velocityCalculator, rotationCalculator, loggerFactory)
        select locomotion;

    protected abstract Eff<IEnv, ILocomotion> CreateService(
        IVelocityCalculator velocityCalculator,
        IRotationCalculator rotationCalculator,
        ILoggerFactory loggerFactory
    );
}