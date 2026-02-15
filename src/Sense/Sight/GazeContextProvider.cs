using AlleyCat.Entity;
using AlleyCat.Env;
using AlleyCat.Rig;
using AlleyCat.Rig.Human;
using AlleyCat.Template;
using Godot;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using static LanguageExt.Prelude;
using Length = LanguageExt.Length;

namespace AlleyCat.Sense.Sight;

public class GazeContextProvider(
    Length? maxFaceOffset = null
) : ITemplateContextProvider
{
    //TODO Make the rotation configurable.
    private const float MaxForwardRotation = 20.0f;

    private readonly Length _maxHeadOffset = maxFaceOffset ?? 20.Centimetres();

    public Eff<IEnv, Map<object, object?>> CreateContext(
        ITemplateRenderable subject,
        IEntity observer
    ) =>
        from lookingAtTheFace in IsLookingAtTheFace(subject, observer)
        from rotation in GetRotation(subject)
        let rotationalContext = rotation
            .Map(r => Map<object, object?>(
                ("yaw", r.X),
                ("pitch", r.Y),
                (
                    "forward",
                    Math.Abs(r.X) <= MaxForwardRotation && Math.Abs(r.Y) <= MaxForwardRotation
                ),
                (
                    "up",
                    Math.Abs(r.X) <= MaxForwardRotation && r.Y > MaxForwardRotation
                ),
                (
                    "down",
                    Math.Abs(r.X) <= MaxForwardRotation && r.Y < -MaxForwardRotation
                ),
                (
                    "right",
                    r.X > MaxForwardRotation && Math.Abs(r.Y) <= MaxForwardRotation
                ),
                (
                    "left",
                    r.X < -MaxForwardRotation && Math.Abs(r.Y) <= MaxForwardRotation
                ),
                (
                    "up_right",
                    r is { X: > MaxForwardRotation, Y: > MaxForwardRotation }
                ),
                (
                    "up_left",
                    r is { X: < -MaxForwardRotation, Y: > MaxForwardRotation }
                ),
                (
                    "down_right",
                    r is { X: > MaxForwardRotation, Y: < -MaxForwardRotation }
                ),
                (
                    "down_left",
                    r is { X: < -MaxForwardRotation, Y: < -MaxForwardRotation }
                )
            ))
            .ValueUnsafe()
        select Map<object, object?>(
            (
                "gaze",
                Map<object, object?>(
                    ("looking_at_the_face", lookingAtTheFace)
                ) + rotationalContext
            )
        );

    private Eff<bool> IsLookingAtTheFace(ITemplateRenderable subject, IEntity observer)
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (subject is not IWatcher sSeeing || observer is not IWatcher oSeeing)
        {
            return SuccessEff(false);
        }

        var sSight = sSeeing.Sight;
        var oSight = oSeeing.Sight;

        return
            from sEyes in sSight.GlobalTransform
            from oEyes in oSight.GlobalTransform
            let origin = sEyes.Origin
            let sForward = sEyes.Basis * Vector3.Forward
            let oForward = oEyes.Basis * Vector3.Forward
            let toHead = oEyes.Origin - origin
            let closest = origin + sForward.Dot(toHead) * sForward
            let closestDir = (closest - oEyes.Origin).Normalized()
            let oDot = sForward.Dot(oForward)
            let sDot = closestDir.Dot(sForward)
            let distance = closest.DistanceTo(oEyes.Origin).Metres()
            select sDot > 0 && oDot <= 0 && distance <= _maxHeadOffset;
    }

    private static Eff<Option<Vector2>> GetRotation(ITemplateRenderable subject)
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (subject is not (IWatcher seeing and IRigged<HumanBone> rigged))
        {
            return SuccessEff<Option<Vector2>>(None);
        }

        var rig = rigged.Rig;

        return
            from viewPoint in seeing.Sight.GlobalTransform
            from transform in rig.GlobalTransform
            let toLocal = transform.Basis.Inverse()
            from neck in rig
                .GetPose(HumanBone.Head)
                .Map(x => x.Origin)
            from rightShoulder in rig
                .GetPose(HumanBone.RightUpperArm)
                .Map(x => x.Origin)
            from leftShoulder in rig
                .GetPose(HumanBone.LeftUpperArm)
                .Map(x => x.Origin)
            from hips in rig
                .GetPose(HumanBone.Hips)
                .Map(x => x.Origin)
            let up = (neck - hips).Normalized()
            let right = (rightShoulder - leftShoulder).Normalized()
            let forward = up.Cross(right)
            let yaw = -forward.SignedAngleTo(toLocal * (viewPoint.Basis * Vector3.Forward), up)
            let pitch = (float)Math.PI / 2 - Vector3.Up.AngleTo(viewPoint.Basis * Vector3.Forward)
            select Optional(
                new Vector2(
                    float.RadiansToDegrees(yaw),
                    float.RadiansToDegrees(pitch)
                )
            );
    }
}