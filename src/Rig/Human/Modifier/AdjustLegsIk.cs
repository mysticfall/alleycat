using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier;

public class AdjustLegsIk(
    IMovable3d rightFootTarget,
    IMovable3d leftFootTarget,
    IMovable3d rightKneePole,
    IMovable3d leftKneePole,
    IRig<HumanBone> rig,
    IObservable<Duration> onIkProcess,
    Length? poleLength = null,
    ILoggerFactory? loggerFactory = null
) :IkModifier<HumanBone>(rig, onIkProcess, loggerFactory)
{
    private readonly Length _poleLength = poleLength ?? 50.Centimetres();

    private Eff<Unit> SyncLeg(
        Transform3D toSkeleton,
        HumanBone upperLegBone,
        HumanBone lowerLegBone,
        HumanBone footBone,
        IMovable3d footTarget,
        IMovable3d poleTarget
    ) =>
        from upperLeg in Rig.GetPose(upperLegBone)
        from lowerLeg in Rig.GetPose(lowerLegBone)
        from foot in Rig.GetPose(footBone)
        let poleDir = lowerLeg.Basis * Vector3.Forward
        let poleOrigin = lowerLeg.Origin + poleDir * (float)_poleLength.Metres
        let pole = new Transform3D(Basis.Identity, poleOrigin)
        from _1 in footTarget.SetGlobalTransform(toSkeleton * foot)
        from _2 in poleTarget.SetGlobalTransform(toSkeleton * pole)
        select unit;

    protected override Eff<IEnv, Unit> Process(Duration duration) =>
        from toSkeleton in Rig.GlobalTransform
        from _1 in SyncLeg(
            toSkeleton,
            HumanBone.RightUpperLeg,
            HumanBone.RightLowerLeg,
            HumanBone.RightFoot,
            rightFootTarget,
            rightKneePole
        )
        from _2 in SyncLeg(
            toSkeleton,
            HumanBone.LeftUpperLeg,
            HumanBone.LeftLowerLeg,
            HumanBone.LeftFoot,
            leftFootTarget,
            leftKneePole
        )
        select unit;
}