using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Locomotion.Rotation;

[GlobalClass]
public abstract partial class RotationCalculatorFactory : NodeFactory<IRotationCalculator>
{
    [Export(PropertyHint.Range, "1,360")] public float MaxTurnRate { get; set; } = 90.0f;

    protected override Eff<IEnv, IRotationCalculator> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from maxTurnRate in AngularVelocity.FromDegrees(MaxTurnRate).ToEff(identity)
        from calculator in CreateService(maxTurnRate, loggerFactory)
        select calculator;

    protected abstract Eff<IEnv, IRotationCalculator> CreateService(
        AngularVelocity maxTurnRate,
        ILoggerFactory loggerFactory
    );
}