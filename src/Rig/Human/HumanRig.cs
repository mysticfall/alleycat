using AlleyCat.Rig.Ik;
using Godot;
using LanguageExt;

namespace AlleyCat.Rig.Human;

public enum HumanBone
{
    Head,
    Neck,
    UpperChest,
    Chest,
    Spine,
    RightShoulder,
    RightUpperArm,
    RightLowerArm,
    RightHand,
    LeftShoulder,
    LeftUpperArm,
    LeftLowerArm,
    LeftHand,
    Hips,
    RightUpperLeg,
    RightLowerLeg,
    RightFoot,
    LeftUpperLeg,
    LeftLowerLeg,
    LeftFoot
}

public readonly record struct HumanRig(
    Skeleton3D Skeleton,
    IObservable<Duration> OnBeforeIk,
    IObservable<Duration> OnAfterIk
) : IIkRig<HumanBone>;