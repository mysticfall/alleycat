using Godot;
using LanguageExt;

namespace AlleyCat.Rig.Pose;

public static class KneelingPoseExtensions
{
    public static Eff<bool> IsKneeling(
        this IRigged<HumanBone> subject,
        Length? maxKneeHeight = null
    )
    {
        var rig = subject.Rig;

        var kneeHeightThreshold = maxKneeHeight ?? 15.Centimetres();

        return
            from head in rig
                .GetPose(HumanBone.Head)
                .Map(x => x.Origin)
            from hips in rig
                .GetPose(HumanBone.Hips)
                .Map(x => x.Origin)
            from rightUpperLeg in rig
                .GetPose(HumanBone.RightUpperLeg)
            from leftUpperLeg in rig
                .GetPose(HumanBone.LeftUpperLeg)
            from rightLowerLeg in rig
                .GetPose(HumanBone.RightLowerLeg)
            from leftLowerLeg in rig
                .GetPose(HumanBone.LeftLowerLeg)
            from rightFoot in rig
                .GetPose(HumanBone.RightFoot)
                .Map(x => x.Origin)
            from leftFoot in rig
                .GetPose(HumanBone.LeftFoot)
                .Map(x => x.Origin)
            let up = (head - hips).Normalized()
            select
                IsKneeling(rightUpperLeg, rightLowerLeg, rightFoot, up, kneeHeightThreshold) &&
                IsKneeling(leftUpperLeg, leftLowerLeg, leftFoot, up, kneeHeightThreshold);
    }

    private static bool IsKneeling(
        Transform3D upper,
        Transform3D lower,
        Vector3 foot,
        Vector3 up,
        Length maxKneeHeight
    )
    {
        var knee = lower.Origin;
        var shinLength = (knee - upper.Origin).Length();

        var angle = GetLegAngle(upper, lower);

        var matchesAngle = angle <= 100;
        var matchesHeight = knee.Y <= maxKneeHeight.Metres;
        var matchesDot = up.Dot(Vector3.Up) > 0.9;
        var matchesZ = knee.Z - foot.Z >= shinLength / 3;

        return matchesAngle && matchesHeight && matchesDot && matchesZ;
    }

    private static float GetLegAngle(Transform3D upper, Transform3D lower)
    {
        var upperDir = upper.Basis * Vector3.Down;
        var lowerDir = lower.Basis * Vector3.Up;

        return float.RadiansToDegrees(upperDir.AngleTo(lowerDir));
    }
}