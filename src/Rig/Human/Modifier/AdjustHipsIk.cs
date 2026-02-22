using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier;

public class AdjustHipsIk(
    ILocatable3d headTarget,
    IMovable3d hipsTarget,
    IRig<HumanBone> rig,
    IObservable<Duration> onIkProcess,
    ILoggerFactory? loggerFactory = null
) : IkModifier<HumanBone>(rig, onIkProcess, loggerFactory)
{
    protected override Eff<IEnv, Unit> Process(Duration delta) =>
        from toSkeleton in Rig.GlobalTransform
        let fromSkeleton = toSkeleton.Inverse()
        from headTarget in headTarget.GlobalTransform
        let targetOrigin = (fromSkeleton * headTarget).Origin
        from headOrigin in Rig.GetPose(HumanBone.Head).Map(x => x.Origin)
        let headOffset = targetOrigin - headOrigin
        from hipsPose in Rig.GetPose(HumanBone.Hips).Map(x => x.Translated(headOffset))
        let hipsGlobalPose = toSkeleton * hipsPose
        from _ in hipsTarget.SetGlobalTransform(hipsGlobalPose)
        select unit;
}