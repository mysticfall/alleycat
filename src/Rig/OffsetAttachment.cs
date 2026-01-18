using Godot;
using LanguageExt;

namespace AlleyCat.Rig;

public readonly record struct OffsetAttachment<TBone>(
    TBone Bone,
    Transform3D NodeToBone,
    Transform3D BoneToNode
) where TBone : struct, Enum
{
    public Eff<Transform3D> GetGlobalTransform(IRig<TBone> rig)
    {
        var toNode = BoneToNode;

        return from pose in rig.GetGlobalPose(Bone)
            select pose * toNode;
    }

    public Eff<Transform3D> GetTransform(IRig<TBone> rig)
    {
        var toNode = BoneToNode;

        return from pose in rig.GetPose(Bone)
            select pose * toNode;
    }

    public Transform3D GetBoneGlobalPose(Transform3D node) => node * NodeToBone;

    public static OffsetAttachment<TBone> FromNodeChild(TBone bone, Node3D nodeChild)
    {
        var transform = nodeChild.Transform;

        return new OffsetAttachment<TBone>
        {
            Bone = bone,
            NodeToBone = transform.Inverse(),
            BoneToNode = transform
        };
    }

    public static OffsetAttachment<TBone> FromBoneChild(TBone bone, Node3D boneChild)
    {
        var transform = boneChild.Transform;

        return new OffsetAttachment<TBone>
        {
            Bone = bone,
            NodeToBone = transform,
            BoneToNode = transform.Inverse()
        };
    }
}