using LanguageExt;

namespace AlleyCat.Rig.Pose;

public static class StandingPoseExtensions
{
    public static Eff<bool> IsStanding(
        this IRigged<HumanBone> subject,
        Length? maxNeckDisplacement = null,
        Length? maxFootDisplacement = null
    )
    {
        var rig = subject.Rig;

        var neckOffsetThreshold = maxNeckDisplacement ?? 50.Centimetres();
        var footOffsetThreshold = maxFootDisplacement ?? 50.Centimetres();

        return
            from neckRest in rig
                .GetRest(HumanBone.Neck)
                .Map(x => x.Origin.Y)
            from neckPose in rig
                .GetPose(HumanBone.Neck)
                .Map(x => x.Origin.Y)
            from footRest in rig
                .GetRest(HumanBone.RightFoot)
                .Map(x => x.Origin.Y)
            from rightFootPose in rig
                .GetPose(HumanBone.RightFoot)
                .Map(x => x.Origin.Y)
            from leftFootPose in rig
                .GetPose(HumanBone.LeftFoot)
                .Map(x => x.Origin.Y)
            let neckOffset = Math.Abs(neckRest - neckPose).Metres()
            let rightFootOffset = Math.Abs(footRest - rightFootPose).Metres()
            let leftFootOffset = Math.Abs(footRest - leftFootPose).Metres()
            select neckOffset <= neckOffsetThreshold &&
                   (rightFootOffset <= footOffsetThreshold ||
                    leftFootOffset <= footOffsetThreshold);
    }
}