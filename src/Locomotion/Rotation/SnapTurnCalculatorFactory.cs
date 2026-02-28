using AlleyCat.Common;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Locomotion.Rotation;

[GlobalClass]
public partial class SnapTurnCalculatorFactory : RotationCalculatorFactory
{
    [Export(PropertyHint.Range, "0,1")] public float CoolTime { get; set; } = 0.2f;

    protected override Eff<IEnv, IRotationCalculator> CreateService(
        AngularVelocity maxTurnRate,
        ILoggerFactory loggerFactory
    ) => SuccessEff<IRotationCalculator>(
        new SnapTurnCalculator(maxTurnRate, CoolTime.Seconds())
    );
}