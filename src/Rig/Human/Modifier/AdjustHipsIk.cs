using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
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
        from headOrigin in Rig.GetPose(HumanBone.Head).Map(x => x.Origin)
        from hips in Rig.GetPose(HumanBone.Hips)
        let targetOrigin = (fromSkeleton * headTarget).Origin
        let animHeadDir = (headOrigin - hips.Origin).Normalized()
        let physicalHeadDir = (targetOrigin - hips.Origin).Normalized()
        let minDot = 0.5f
        let dot = Math.Abs(animHeadDir.Dot(physicalHeadDir))
        let influence = Math.Max(dot - minDot, 0) / minDot
        let headOffset = targetOrigin - headOrigin
        let modifier = hips.Basis * new Vector3(-0.4f, 1f, -0.4f)
        let hipsOffset = headOffset  * modifier * influence
        from hipsPose in Rig.GetPose(HumanBone.Hips).Map(x => x.Translated(hipsOffset))
        let hipsGlobalPose = toSkeleton * hipsPose
        from _ in hipsTarget.SetGlobalTransform(hipsGlobalPose)
        select unit;
}