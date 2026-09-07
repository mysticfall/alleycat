using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.IK;

/// <summary>Named physical locations used by opt-in, read-only foot-motion diagnostics.</summary>
public enum FootPosePoint
{
    /// <summary>Foot-bone origin.</summary>
    Ankle,
    /// <summary>Virtual rear sole marker owned by the foot bone.</summary>
    HeelSole,
    /// <summary>Virtual weight-bearing sole marker owned by the foot bone.</summary>
    Midsole,
    /// <summary>Toe-bone origin.</summary>
    Ball,
    /// <summary>Virtual distal marker owned by the toe bone.</summary>
    ToeTip,
}

/// <summary>Five world-independent skeleton-local point transforms for one foot.</summary>
public readonly record struct FootPosePointSet(
    Transform3D Ankle,
    Transform3D HeelSole,
    Transform3D Midsole,
    Transform3D Ball,
    Transform3D ToeTip)
{
    /// <summary>Returns the named point transform.</summary>
    public Transform3D Get(FootPosePoint point) => point switch
    {
        FootPosePoint.Ankle => Ankle,
        FootPosePoint.HeelSole => HeelSole,
        FootPosePoint.Midsole => Midsole,
        FootPosePoint.Ball => Ball,
        FootPosePoint.ToeTip => ToeTip,
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, null),
    };
}

/// <summary>Source-derived physical point definitions for the imported reference foot.</summary>
public readonly record struct FootPosePointDefinition(
    LimbSide Side,
    StringName FootBone,
    StringName ToeBone,
    Vector3 HeelSoleFootLocal,
    Vector3 MidsoleFootLocal,
    Vector3 ToeTipToeLocal,
    string Source)
{
    private const float MinimumFootLengthMetres = 0.01f;
    private const float MaximumFootLengthMetres = 1.0f;

    /// <summary>
    /// Derives marker offsets from the imported foot-to-toe rest transform. The toe-origin height defines the sole
    /// plane, avoiding an assumed world axis; the imported 180-degree rig container is verified separately.
    /// </summary>
    public static bool TryCreate(Skeleton3D skeleton, LimbSide side, out FootPosePointDefinition definition)
    {
        StringName footName = side == LimbSide.Left ? "LeftFoot" : "RightFoot";
        StringName toeName = side == LimbSide.Left ? "LeftToes" : "RightToes";
        int foot = skeleton.FindBone(footName);
        int toe = skeleton.FindBone(toeName);
        if (foot < 0 || toe < 0)
        {
            definition = default;
            return false;
        }

        Transform3D toeInFoot = skeleton.GetBoneGlobalRest(foot).AffineInverse() * skeleton.GetBoneGlobalRest(toe);
        Vector3 longitudinal = new(toeInFoot.Origin.X, 0f, toeInFoot.Origin.Z);
        float length = longitudinal.Length();
        if (!float.IsFinite(length) || length is < MinimumFootLengthMetres or > MaximumFootLengthMetres)
        {
            definition = default;
            return false;
        }

        Vector3 direction = longitudinal / length;
        float soleY = toeInFoot.Origin.Y;
        definition = new(
            side,
            footName,
            toeName,
            new Vector3(-direction.X * length * 0.35f, soleY, -direction.Z * length * 0.35f),
            new Vector3(direction.X * length * 0.50f, soleY, direction.Z * length * 0.50f),
            new Vector3(direction.X * length * 0.45f, 0f, direction.Z * length * 0.45f),
            $"runtime imported rest: {footName}->{toeName}; footLength={length:G9}m; soleY={soleY:G9}m");
        return Finite(definition.HeelSoleFootLocal)
            && Finite(definition.MidsoleFootLocal)
            && Finite(definition.ToeTipToeLocal);
    }

    /// <summary>Evaluates every point from its owning posed bone so rotation contributes to point motion.</summary>
    public FootPosePointSet Evaluate(Transform3D footPose, Transform3D toePose)
        => new(
            footPose,
            footPose * new Transform3D(Basis.Identity, HeelSoleFootLocal),
            footPose * new Transform3D(Basis.Identity, MidsoleFootLocal),
            toePose,
            toePose * new Transform3D(Basis.Identity, ToeTipToeLocal));

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
