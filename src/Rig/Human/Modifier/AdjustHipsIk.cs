using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier;

public class AdjustHipsIk(
    ILocatable3d headIkTarget,
    IMovable3d hipsIkTarget,
    IRig<HumanBone> rig,
    IObservable<Duration> onIkProcess,
    ILoggerFactory? loggerFactory = null
) : IIkModifier
{
    public IObservable<Duration> OnIkProcess => onIkProcess;

    public ILogger Logger { get; } = loggerFactory.GetLogger<AdjustHipsIk>();

    public ILoggerFactory? LoggerFactory => loggerFactory;

    public Eff<IEnv, Unit> Process(Duration delta) =>
        from toSkeleton in rig.GlobalTransform
        let fromSkeleton = toSkeleton.Inverse()
        from headTarget in headIkTarget.GlobalTransform
        let targetOrigin = (fromSkeleton * headTarget).Origin
        from headOrigin in rig.GetPose(HumanBone.Head).Map(x => x.Origin)
        let headOffset = targetOrigin - headOrigin
        from hipsPose in rig.GetPose(HumanBone.Hips).Map(x => x.Translated(headOffset))
        let hipsGlobalPose = toSkeleton * hipsPose
        from _ in hipsIkTarget.SetGlobalTransform(hipsGlobalPose)
        select unit;
}