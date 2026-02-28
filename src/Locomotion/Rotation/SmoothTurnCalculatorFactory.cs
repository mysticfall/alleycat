using AlleyCat.Common;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Locomotion.Rotation;

[GlobalClass]
public partial class SmoothTurnCalculatorFactory : RotationCalculatorFactory
{
    protected override Eff<IEnv, IRotationCalculator> CreateService(
        AngularVelocity maxTurnRate,
        ILoggerFactory loggerFactory
    ) => SuccessEff<IRotationCalculator>(new SmoothTurnCalculator(maxTurnRate));
}