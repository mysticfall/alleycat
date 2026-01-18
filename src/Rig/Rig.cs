using AlleyCat.Transform;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Rig;

public interface IRig : IObject3d
{
    Skeleton3D Skeleton { get; }

    IO<Transform3D> IObject3d.GlobalTransform => IO.lift(() => Skeleton.GlobalTransform);
}

public interface IRig<in TBone> : IRig where TBone : struct, Enum;

public static class RigExtensions
{
    extension<TBone>(IRig<TBone> rig) where TBone : struct, Enum
    {
        public Eff<int> GetBoneIndex(TBone bone)
        {
            var name = bone.ToString();
            var index = rig.Skeleton.FindBone(name);

            if (index == -1)
            {
                return FailEff<int>(
                    Error.New($"No such bone found in skeleton '{rig.Skeleton.Name}': {bone}")
                );
            }

            return SuccessEff(index);
        }

        public Eff<Transform3D> GetGlobalPose(TBone bone) =>
            from globalTransform in rig.GlobalTransform
            from index in rig.GetBoneIndex(bone)
            from pose in liftEff(() => globalTransform * rig.Skeleton.GetBoneGlobalPose(index))
            select pose;

        public Eff<Transform3D> GetPose(TBone bone) =>
            from index in rig.GetBoneIndex(bone)
            from pose in liftEff(() => rig.Skeleton.GetBoneGlobalPose(index))
            select pose;

        public Eff<Transform3D> GetLocalPose(TBone bone) =>
            from index in rig.GetBoneIndex(bone)
            from pose in liftEff(() => rig.Skeleton.GetBonePose(index))
            select pose;

        public Eff<Quaternion> GetPoseRotation(TBone bone) =>
            from index in rig.GetBoneIndex(bone)
            from rotation in liftEff(() => rig.Skeleton.GetBonePoseRotation(index))
            select rotation;

        public Eff<Transform3D> GetGlobalRest(TBone bone) =>
            from globalTransform in rig.GlobalTransform
            from index in rig.GetBoneIndex(bone)
            from pose in liftEff(() => globalTransform * rig.Skeleton.GetBoneGlobalRest(index))
            select pose;

        public Eff<Transform3D> GetRest(TBone bone) =>
            from globalTransform in rig.GlobalTransform
            from index in rig.GetBoneIndex(bone)
            from pose in liftEff(() => rig.Skeleton.GetBoneGlobalRest(index))
            select pose;

        public Eff<Transform3D> GetLocalRest(TBone bone) =>
            from index in rig.GetBoneIndex(bone)
            from pose in liftEff(() => rig.Skeleton.GetBoneRest(index))
            select pose;

        public Eff<Unit> SetPose(TBone bone, Transform3D pose) =>
            from index in rig.GetBoneIndex(bone)
            from _ in liftEff(() => { rig.Skeleton.SetBoneGlobalPose(index, pose); })
            select unit;

        public Eff<Unit> SetLocalPose(TBone bone, Transform3D pose) =>
            from index in rig.GetBoneIndex(bone)
            from _ in liftEff(() => { rig.Skeleton.SetBonePose(index, pose); })
            select unit;
    }
}

public interface IRigged
{
    IRig Rig { get; }
}

public interface IRigged<in TBone> : IRigged
    where TBone : struct, Enum
{
    new IRig<TBone> Rig { get; }
}