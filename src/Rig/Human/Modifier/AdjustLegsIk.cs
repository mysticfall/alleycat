using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier;

public class AdjustLegsIk(
    IMovable3d rightFootTarget,
    IMovable3d leftFootTarget,
    IRig<HumanBone> rig,
    IObservable<Duration> onIkProcess,
    ILoggerFactory? loggerFactory = null
) : IIkModifier
{
    public IObservable<Duration> OnIkProcess => onIkProcess;

    public ILogger Logger { get; } = loggerFactory.GetLogger<AdjustLegsIk>();

    public ILoggerFactory? LoggerFactory => loggerFactory;

    public Eff<IEnv, Unit> Process(Duration delta) =>
        from animRightFoot in rig.GetGlobalPose(HumanBone.RightFoot)
        from animLeftFoot in rig.GetGlobalPose(HumanBone.LeftFoot)
        from _1 in rightFootTarget.SetGlobalTransform(animRightFoot)
        from _2 in leftFootTarget.SetGlobalTransform(animLeftFoot)
        select unit;
}