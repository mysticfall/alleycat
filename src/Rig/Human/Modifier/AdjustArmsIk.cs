using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier;

public class AdjustArmsIk : IIkModifier
{
    public IObservable<Duration> OnIkProcess { get; }

    public ILogger Logger { get; }

    public ILoggerFactory? LoggerFactory { get; }

    private readonly Eff<IEnv, Unit> _process;

    private readonly Length _poleLength;

    public AdjustArmsIk(
        ILocatable3d rightHand,
        ILocatable3d leftHand,
        IMovable3d rightShoulder,
        IMovable3d leftShoulder,
        IMovable3d rightElbowPole,
        IMovable3d leftElbowPole,
        IRig<HumanBone> rig,
        IObservable<Duration> onIkProcess,
        Length? poleLength = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        OnIkProcess = onIkProcess;

        Logger = loggerFactory.GetLogger<AdjustArmsIk>();
        LoggerFactory = loggerFactory;

        _poleLength = poleLength ?? 50.Centimetres();

        _process =
            from toSkeleton in rig.GlobalTransform
            let fromSkeleton = toSkeleton.Inverse()
            from neck in rig.GetPose(HumanBone.Neck)
            from hips in rig.GetPose(HumanBone.Hips)
            from rightUpperArm in rig.GetPose(HumanBone.RightUpperArm)
            from leftUpperArm in rig.GetPose(HumanBone.LeftUpperArm)
            let up = (neck.Origin - hips.Origin).Normalized()
            let right = (rightUpperArm.Origin - leftUpperArm.Origin).Normalized()
            let forward = up.Cross(right)
            let basis = new Basis(right, up, forward * -1)
            from neckRest in rig.GetRest(HumanBone.Neck)
            from hipsRest in rig.GetRest(HumanBone.Hips)
            from rightUpperArmRest in rig.GetRest(HumanBone.RightUpperArm)
            from leftUpperArmRest in rig.GetRest(HumanBone.LeftUpperArm)
            let upRest = (neckRest.Origin - hipsRest.Origin).Normalized()
            let rightRest = (rightUpperArmRest.Origin - leftUpperArmRest.Origin).Normalized()
            let forwardRest = upRest.Cross(rightRest)
            let basisRest = new Basis(rightRest, upRest, forwardRest * -1)
            from _1 in SyncArm(
                toSkeleton,
                fromSkeleton,
                basis,
                basisRest,
                HumanBone.RightShoulder,
                HumanBone.RightUpperArm,
                HumanBone.RightLowerArm,
                HumanBone.RightHand,
                rightHand,
                rightShoulder,
                rightElbowPole
            )
            from _2 in SyncArm(
                toSkeleton,
                fromSkeleton,
                basis,
                basisRest,
                HumanBone.LeftShoulder,
                HumanBone.LeftUpperArm,
                HumanBone.LeftLowerArm,
                HumanBone.LeftHand,
                leftHand,
                leftShoulder,
                leftElbowPole
            )
            select unit;

        return;

        Eff<IEnv, Unit> SyncArm(
            Transform3D toSkeleton,
            Transform3D fromSkeleton,
            Basis basis,
            Basis basisRest,
            HumanBone shoulderBone,
            HumanBone upperArmBone,
            HumanBone lowerArmBone,
            HumanBone handBone,
            ILocatable3d handRef,
            IMovable3d shoulderTarget,
            IMovable3d poleTarget
        )
        {
            return
                from shoulderLocalRest in rig.GetLocalRest(shoulderBone)
                from upperArmRest in rig.GetRest(upperArmBone)
                from upperArmLocalRest in rig.GetLocalRest(upperArmBone)
                from lowerArmRest in rig.GetRest(lowerArmBone)
                from handRest in rig.GetRest(handBone)
                from shoulder in rig.GetPose(shoulderBone)
                from upperArm in rig.GetPose(upperArmBone)
                from handTarget in handRef.GlobalTransform.Map(x => fromSkeleton * x)
                let arm = handTarget.Origin - upperArm.Origin
                let armDir = arm.Normalized()
                let armCentre = upperArm.Origin + armDir * arm.Length() / 2
                let handRestFromUpperArmLocal = upperArmRest.Inverse() * handRest.Origin
                let armRestDirFromUpperArmLocal = handRestFromUpperArmLocal.Normalized()
                let handFromUpperArmLocal = upperArm.Basis.Inverse() * handTarget.Origin
                let armDirFromUpperArmLocal = handFromUpperArmLocal.Normalized()
                let armRotFromUpperArmLocal =
                    new Quaternion(armRestDirFromUpperArmLocal, armDirFromUpperArmLocal)
                let backDirFromUpperArmLocal = upperArm.Basis.Inverse() * basis * Vector3.Back
                let poleDirFromUpperArmLocal = armRotFromUpperArmLocal * backDirFromUpperArmLocal
                let poleDirForArmDir = upperArm.Basis * poleDirFromUpperArmLocal
                let handBack = handTarget.Basis * Vector3.Back
                let plane = new Plane(armDir, armCentre)
                let poleDirForHandDir = Optional(plane.IntersectsRay(handTarget.Origin, handBack))
                    .Map(x => (x - armCentre).Normalized())
                    .IfNone(handBack)
                let dot = armDir.Dot(handBack)
                let poleDir = poleDirForHandDir.Lerp(poleDirForArmDir, Math.Abs(dot))
                let pole = toSkeleton * (armCentre + poleDir * (float)_poleLength.Metres)
                from _1 in poleTarget.SetGlobalTransform(new Transform3D(Basis.Identity, pole))
                let fromUpperArmRest = upperArmRest.Inverse()
                let elbowFromUpperArmRest = new Transform3D(basisRest, upperArmRest.Origin).Inverse() * lowerArmRest.Origin
                let armLength = handRest.Origin.DistanceTo(upperArmRest.Origin)
                let elbowDistance =
                    MathF.Sqrt(MathF.Max(MathF.Pow(armLength / 2, 2) - MathF.Pow(arm.Length() / 2, 2), 0))
                let elbow = armCentre + poleDir * elbowDistance
                let elbowFromUpperArm = new Transform3D(basis, upperArm.Origin).Inverse() * elbow
                let upperArmRot = new Quaternion(elbowFromUpperArmRest, elbowFromUpperArm)
                from upperChest in rig.GetPose(HumanBone.UpperChest)
                let shoulderLocalRotAxis =
                    upperChest.Basis.Inverse() *
                    basis.Inverse() *
                    upperArmRot.GetAxis()
                let shoulderLocalPose = new Transform3D(
                    shoulderLocalRest.Basis.Rotated(shoulderLocalRotAxis, upperArmRot.GetAngle() * 0.25f),
                    shoulderLocalRest.Origin)
                let shoulderPose = upperChest * shoulderLocalPose
                from _2 in shoulderTarget.SetGlobalTransform(toSkeleton * shoulderPose)
                select unit;
        }
    }

    public Eff<IEnv, Unit> Process(Duration delta) => _process;
}