using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Pure helpers for all-fours transition and state metrics.
/// </summary>
public static class AllFoursPoseMetrics
{
    private const float HeightFloor = 1e-3f;

    /// <summary>
    /// Computes the current head position in skeleton-local space.
    /// </summary>
    public static Vector3 ComputeCurrentHeadLocalPosition(
        Transform3D skeletonGlobalTransform,
        Transform3D headTargetTransform)
        => (skeletonGlobalTransform.AffineInverse() * headTargetTransform).Origin;

    /// <summary>
    /// Computes the current head position in skeleton-local space, normalised by rest head height.
    /// </summary>
    public static Vector3 ComputeNormalizedCurrentHeadLocalPosition(
        Transform3D skeletonGlobalTransform,
        Transform3D headTargetTransform,
        float restHeadHeight)
    {
        float safeRestHeight = restHeadHeight > HeightFloor
            ? restHeadHeight
            : 1f;

        return ComputeCurrentHeadLocalPosition(skeletonGlobalTransform, headTargetTransform) / safeRestHeight;
    }

    /// <summary>
    /// Computes the avatar-forward head offset from the skeleton-local origin, normalised by rest head height.
    /// </summary>
    public static float ComputeNormalizedForwardOffsetFromSkeletonOrigin(
        Transform3D skeletonGlobalTransform,
        Transform3D headTargetTransform,
        float restHeadHeight)
    {
        Vector3 normalizedHeadLocalPosition = ComputeNormalizedCurrentHeadLocalPosition(
            skeletonGlobalTransform,
            headTargetTransform,
            restHeadHeight);

        return normalizedHeadLocalPosition.Dot(HipLimitSemanticFrame.ReferenceRig.AvatarForwardLocal);
    }

    /// <summary>
    /// Computes the head vertical offset from the skeleton-local origin, normalised by rest head height.
    /// </summary>
    public static float ComputeNormalizedVerticalOffsetFromSkeletonOrigin(
        Transform3D skeletonGlobalTransform,
        Transform3D headTargetTransform,
        float restHeadHeight)
    {
        Vector3 normalizedHeadLocalPosition = ComputeNormalizedCurrentHeadLocalPosition(
            skeletonGlobalTransform,
            headTargetTransform,
            restHeadHeight);

        return normalizedHeadLocalPosition.Dot(HipLimitSemanticFrame.ReferenceRig.UpLocal);
    }
}
