using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier;

public readonly record struct ArmsIkRest(
    Transform3D LocalShoulder,
    Transform3D UpperArm,
    Transform3D LowerArm,
    Transform3D Hand
);

public readonly record struct ArmsIkContext(
    Transform3D ToSkeleton,
    Transform3D FromSkeleton,
    Basis RestBasis,
    Map<Side, ArmsIkRest> Rests,
    Length ArmLength
) : IIkContext;

public class AdjustArmsIk(
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
) : IkModifier<HumanBone, ArmsIkContext>(rig, onIkProcess, loggerFactory)
{
    private readonly Length _poleLength = poleLength ?? 50.Centimetres();

    private static Basis CalculateBasis(
        Transform3D neck,
        Transform3D rightUpperArm,
        Transform3D leftUpperArm,
        Transform3D hips
    )
    {
        var up = (neck.Origin - hips.Origin).Normalized();
        var right = (rightUpperArm.Origin - leftUpperArm.Origin).Normalized();
        var forward = up.Cross(right);

        return new Basis(right, up, forward * -1);
    }

    protected override Eff<IEnv, ArmsIkContext> CreateContext(
        Transform3D toSkeleton,
        Transform3D fromSkeleton
    ) =>
        from neck in Rig.GetRest(HumanBone.Neck)
        from hips in Rig.GetRest(HumanBone.Hips)
        from rightShoulderLocal in Rig.GetLocalRest(HumanBone.RightShoulder)
        from leftShoulderLocal in Rig.GetLocalRest(HumanBone.LeftShoulder)
        from rightUpperArm in Rig.GetRest(HumanBone.RightUpperArm)
        from leftUpperArm in Rig.GetRest(HumanBone.LeftUpperArm)
        from rightLowerArm in Rig.GetRest(HumanBone.RightLowerArm)
        from leftLowerArm in Rig.GetRest(HumanBone.LeftLowerArm)
        from rightHand in Rig.GetRest(HumanBone.RightHand)
        from leftHand in Rig.GetRest(HumanBone.LeftHand)
        let rests = Map(
            (
                Side.Right,
                new ArmsIkRest(
                    rightShoulderLocal,
                    rightUpperArm,
                    rightLowerArm,
                    rightHand
                )
            ),
            (
                Side.Left,
                new ArmsIkRest(
                    leftShoulderLocal,
                    leftUpperArm,
                    leftLowerArm,
                    leftHand
                )
            )
        )
        let basis = CalculateBasis(neck, rightUpperArm, leftUpperArm, hips)
        let armLength = rightHand.Origin.DistanceTo(rightUpperArm.Origin)
        select new ArmsIkContext(toSkeleton, fromSkeleton, basis, rests, armLength.Metres());

    private Eff<IEnv, Unit> SyncArm(
        Side side,
        ArmsIkContext context,
        Basis basis,
        Transform3D upperChest,
        HumanBone shoulderBone,
        HumanBone upperArmBone,
        ILocatable3d handRef,
        IMovable3d shoulderTarget,
        IMovable3d poleTarget
    )
    {
        var rests = context.Rests[side];

        return
            from shoulder in Rig.GetPose(shoulderBone)
            from upperArm in Rig.GetPose(upperArmBone)
            from handTarget in handRef.GlobalTransform.Map(x => context.FromSkeleton * x)
            let arm = handTarget.Origin - upperArm.Origin
            let armDir = arm.Normalized()
            let armCentre = upperArm.Origin + armDir * arm.Length() / 2
            let handRestFromUpperArmLocal = rests.UpperArm.Inverse() * rests.Hand.Origin
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
            let pole = context.ToSkeleton * (armCentre + poleDir * (float)_poleLength.Metres)
            from _1 in poleTarget.SetGlobalTransform(new Transform3D(Basis.Identity, pole))
            let fromUpperArmRest = rests.UpperArm.Inverse()
            let elbowFromUpperArmRest =
                new Transform3D(context.RestBasis, rests.UpperArm.Origin).Inverse() * rests.LowerArm.Origin
            let elbowDistance = MathF.Sqrt(
                MathF.Max(
                    MathF.Pow((float)context.ArmLength.Metres / 2, 2) - MathF.Pow(arm.Length() / 2, 2),
                    0
                )
            )
            let elbow = armCentre + poleDir * elbowDistance
            let elbowFromUpperArm = new Transform3D(basis, upperArm.Origin).Inverse() * elbow
            let upperArmRot = new Quaternion(elbowFromUpperArmRest, elbowFromUpperArm)
            let shoulderLocalRotAxis =
                upperChest.Basis.Inverse() *
                basis.Inverse() *
                upperArmRot.GetAxis()
            let shoulderLocalPose = new Transform3D(
                rests.LocalShoulder.Basis.Rotated(shoulderLocalRotAxis, upperArmRot.GetAngle() * 0.25f),
                rests.LocalShoulder.Origin)
            let shoulderPose = upperChest * shoulderLocalPose
            from _2 in shoulderTarget.SetGlobalTransform(context.ToSkeleton * shoulderPose)
            select unit;
    }

    protected override Eff<IEnv, Unit> Process(ArmsIkContext context, Duration duration) =>
        from neck in Rig.GetPose(HumanBone.Neck)
        from upperChest in Rig.GetPose(HumanBone.UpperChest)
        from rightUpperArm in Rig.GetPose(HumanBone.RightUpperArm)
        from leftUpperArm in Rig.GetPose(HumanBone.LeftUpperArm)
        from hips in Rig.GetPose(HumanBone.Hips)
        let basis = CalculateBasis(neck, rightUpperArm, leftUpperArm, hips)
        from _1 in SyncArm(
            Side.Right,
            context,
            basis,
            upperChest,
            HumanBone.RightShoulder,
            HumanBone.RightUpperArm,
            rightHand,
            rightShoulder,
            rightElbowPole
        )
        from _2 in SyncArm(
            Side.Left,
            context,
            basis,
            upperChest,
            HumanBone.LeftShoulder,
            HumanBone.LeftUpperArm,
            leftHand,
            leftShoulder,
            leftElbowPole
        )
        select unit;
}