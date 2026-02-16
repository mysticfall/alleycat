using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier;

public class AdjustLegsIk : IIkModifier
{
    public IObservable<Duration> OnIkProcess { get; }

    public ILogger Logger { get; }

    public ILoggerFactory? LoggerFactory { get; }

    private readonly Eff<Unit> _process;

    private readonly Length _poleLength;

    public AdjustLegsIk(
        IMovable3d rightFootTarget,
        IMovable3d leftFootTarget,
        IMovable3d rightKneePole,
        IMovable3d leftKneePole,
        IRig<HumanBone> rig,
        IObservable<Duration> onIkProcess,
        Length? poleLength = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        OnIkProcess = onIkProcess;

        Logger = loggerFactory.GetLogger<AdjustLegsIk>();
        LoggerFactory = loggerFactory;

        _poleLength = poleLength ?? 1.Metres();

        _process =
            from toSkeleton in rig.GlobalTransform
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

        return;

        Eff<Unit> SyncLeg(
            Transform3D toSkeleton,
            HumanBone upperLegBone,
            HumanBone lowerLegBone,
            HumanBone footBone,
            IMovable3d footTarget,
            IMovable3d poleTarget
        ) =>
            from upperLeg in rig.GetPose(upperLegBone)
            from lowerLeg in rig.GetPose(lowerLegBone)
            from foot in rig.GetPose(footBone)
            let poleDir = lowerLeg.Basis * Vector3.Forward
            let poleOrigin = lowerLeg.Origin + poleDir * (float)_poleLength.Metres
            let pole = new Transform3D(Basis.Identity, poleOrigin)
            from _1 in footTarget.SetGlobalTransform(toSkeleton * foot)
            from _2 in poleTarget.SetGlobalTransform(toSkeleton * pole)
            select unit;
    }

    public Eff<IEnv, Unit> Process(Duration delta) => _process;
}