using AlleyCat.Entity;
using AlleyCat.Env;
using AlleyCat.Template;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;
using Length = LanguageExt.Length;

namespace AlleyCat.Transform;

public class PositionContextProvider(Length? nearThreshold = null) : ITemplateContextProvider
{
    private static readonly Plane Ground = new(Vector3.Up, Vector3.Zero);

    private readonly Length _nearThreshold = nearThreshold ?? 1.Metres();

    public Eff<IEnv, Map<object, object?>> CreateContext(
        ITemplateRenderable subject,
        IEntity observer
    )
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (subject is not ILocatable3d subject3d || observer is not ILocatable3d observer3d)
        {
            return SuccessEff(Map<object, object?>());
        }

        return
            from sTrans in subject3d.GlobalTransform
            from oTrans in observer3d.GlobalTransform
            let distance = sTrans.Origin.DistanceTo(oTrans.Origin)
            let ground = new Plane(Vector3.Up, Vector3.Zero)
            let sBearing = GetBearing(oTrans, sTrans)
            let oBearing = GetBearing(sTrans, oTrans)
            let sSide = GetSide(sBearing)
            let oSide = GetSide(oBearing)
            select Map<object, object>(
                ("position", Map<object, object>(
                    ("distance", distance),
                    ("side", sSide),
                    ("facing", oSide),
                    ("near", distance <= _nearThreshold.Metres)
                ))
            );
    }

    private static float GetBearing(Transform3D from, Transform3D to)
    {
        var pos = from.Inverse() * to.Origin;
        var dir = Ground.Project(pos).Normalized();

        return Vector3.Forward.SignedAngleTo(dir, Vector3.Up);
    }

    private static bool IsFront(float angle) => Math.Abs(angle) <= float.DegreesToRadians(45);

    private static bool IsBack(float angle) => Math.Abs(angle) >= float.DegreesToRadians(135);

    private static string GetSide(float bearing)
    {
        if (IsFront(bearing)) return "front";
        if (IsBack(bearing)) return "back";

        return Math.Sign(bearing) >= 0 ? "left" : "right";
    }
}