using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Pure helpers for kneeling transition/binding metrics.
/// </summary>
public static class KneelingPoseMetrics
{
    private const float HeightFloor = 1e-3f;
    private const float RatioFloor = 1e-3f;

    /// <summary>
    /// Computes crouch depth ratio from rest-pose body measure.
    /// </summary>
    /// <remarks>
    /// The ratio is derived from head descent projected on the rest up-axis and normalised by the
    /// intrinsic rest-pose head-height measure. This is body-relative and does not depend on world
    /// origin/elevation.
    /// </remarks>
    public static float ComputeCrouchDepthRatio(
        Transform3D headTargetRestTransform,
        Transform3D headTargetTransform,
        float restHeadHeight)
    {
        Vector3 upAxis = headTargetRestTransform.Basis.Y.Normalized();
        Vector3 delta = headTargetTransform.Origin - headTargetRestTransform.Origin;
        float descentMetres = -delta.Dot(upAxis);
        float safeRestHeight = restHeadHeight > HeightFloor
            ? restHeadHeight
            : 1f;

        float ratio = descentMetres / safeRestHeight;
        return MathF.Max(0f, ratio);
    }

    /// <summary>
    /// Computes crouch depth blend in range <c>[0, 1]</c> from a full-crouch depth ratio baseline.
    /// </summary>
    public static float ComputeCrouchDepthBlend(
        float crouchDepthRatio,
        float fullCrouchDepthRatio)
    {
        float safeFullCrouchRatio = fullCrouchDepthRatio > RatioFloor
            ? fullCrouchDepthRatio
            : RatioFloor;

        float blend = crouchDepthRatio / safeFullCrouchRatio;
        return Mathf.Clamp(blend, 0f, 1f);
    }

    /// <summary>
    /// Computes forward head offset in metres from the calibrated head-target rest transform.
    /// </summary>
    /// <remarks>
    /// Positive values indicate forward motion in Godot's <c>-Z</c> direction.
    /// </remarks>
    public static float ComputeForwardOffsetMetres(
        Transform3D headTargetRestTransform,
        Transform3D headTargetTransform)
    {
        Vector3 forwardAxis = (-headTargetRestTransform.Basis.Z).Normalized();
        Vector3 delta = headTargetTransform.Origin - headTargetRestTransform.Origin;
        return delta.Dot(forwardAxis);
    }

    /// <summary>
    /// Computes rest-pose head-height normalised forward offset.
    /// </summary>
    public static float ComputeForwardOffsetRatio(
        Transform3D headTargetRestTransform,
        Transform3D headTargetTransform,
        float restHeadHeight)
    {
        float safeRestHeight = restHeadHeight > HeightFloor
            ? restHeadHeight
            : 1f;
        return ComputeForwardOffsetMetres(headTargetRestTransform, headTargetTransform) / safeRestHeight;
    }

    /// <summary>
    /// Computes forward offset ratio relative to the fully crouched forward baseline ratio.
    /// </summary>
    public static float ComputeForwardOffsetFromFullCrouchRatio(
        Transform3D headTargetRestTransform,
        Transform3D headTargetTransform,
        float restHeadHeight,
        float fullCrouchForwardOffsetRatio)
        => ComputeForwardOffsetRatio(headTargetRestTransform, headTargetTransform, restHeadHeight)
           - fullCrouchForwardOffsetRatio;

    /// <summary>
    /// Computes clamped kneel seek blend from forward offset relative to full crouch.
    /// </summary>
    public static float ComputeKneelSeekBlend(
        float forwardOffsetFromFullCrouchRatio,
        float maximumKneelForwardRangeRatio)
    {
        const float RangeFloor = 1e-3f;

        float safeRange = maximumKneelForwardRangeRatio > RangeFloor
            ? maximumKneelForwardRangeRatio
            : RangeFloor;

        float ratio = forwardOffsetFromFullCrouchRatio / safeRange;
        return Mathf.Clamp(ratio, 0f, 1f);
    }

}
