using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// Converts world-space eye targets into reference eye animation seek times.
/// </summary>
public static class EyesLookMath
{
    /// <summary>
    /// Minimum seek time for the normalised reference eye look animations.
    /// </summary>
    public const float MinimumSeekTimeSeconds = 0f;

    /// <summary>
    /// Neutral seek time for the normalised reference eye look animations.
    /// </summary>
    public const float NeutralSeekTimeSeconds = 0.5f;

    /// <summary>
    /// Maximum seek time for the normalised reference eye look animations.
    /// </summary>
    public const float MaximumSeekTimeSeconds = 1f;

    /// <summary>
    /// Resolves horizontal and vertical animation seek times from a target position.
    /// </summary>
    public static Vector2 ResolveLookSeekTimes(
        Transform3D eyeOriginGlobalTransform,
        Vector3 targetGlobalPosition,
        float maxHorizontalAngleRadians,
        float maxVerticalAngleRadians)
    {
        Vector3 localTarget = eyeOriginGlobalTransform.AffineInverse() * targetGlobalPosition;
        return ResolveLookSeekTimesFromLocalDirection(localTarget, maxHorizontalAngleRadians, maxVerticalAngleRadians);
    }

    /// <summary>
    /// Resolves horizontal and vertical animation seek times from an eye-origin local direction.
    /// </summary>
    public static Vector2 ResolveLookSeekTimesFromLocalDirection(
        Vector3 localDirection,
        float maxHorizontalAngleRadians,
        float maxVerticalAngleRadians)
    {
        if (localDirection.LengthSquared() <= Mathf.Epsilon)
        {
            return new Vector2(NeutralSeekTimeSeconds, NeutralSeekTimeSeconds);
        }

        float horizontalLimit = Mathf.Max(Mathf.Epsilon, maxHorizontalAngleRadians);
        float verticalLimit = Mathf.Max(Mathf.Epsilon, maxVerticalAngleRadians);
        float horizontalAngle = Mathf.Clamp(Mathf.Atan2(localDirection.X, -localDirection.Z), -horizontalLimit, horizontalLimit);
        float planarDistance = Mathf.Sqrt((localDirection.X * localDirection.X) + (localDirection.Z * localDirection.Z));
        float verticalAngle = Mathf.Clamp(Mathf.Atan2(localDirection.Y, planarDistance), -verticalLimit, verticalLimit);

        return new Vector2(
            RemapSignedAngleToReferenceSeek(horizontalAngle, horizontalLimit),
            RemapSignedAngleToReferenceSeek(verticalAngle, verticalLimit));
    }

    /// <summary>
    /// Resolves horizontal and vertical animation seek times from a target position.
    /// </summary>
    [Obsolete("Use ResolveLookSeekTimes because BODY-004 controls AnimationNodeTimeSeek seek times.")]
    public static Vector2 ResolveLookWeights(
        Transform3D eyeOriginGlobalTransform,
        Vector3 targetGlobalPosition,
        float maxHorizontalAngleRadians,
        float maxVerticalAngleRadians)
        => ResolveLookSeekTimes(
            eyeOriginGlobalTransform,
            targetGlobalPosition,
            maxHorizontalAngleRadians,
            maxVerticalAngleRadians);

    /// <summary>
    /// Resolves horizontal and vertical animation seek times from an eye-origin local direction.
    /// </summary>
    [Obsolete("Use ResolveLookSeekTimesFromLocalDirection because BODY-004 controls AnimationNodeTimeSeek seek times.")]
    public static Vector2 ResolveLookWeightsFromLocalDirection(
        Vector3 localDirection,
        float maxHorizontalAngleRadians,
        float maxVerticalAngleRadians)
        => ResolveLookSeekTimesFromLocalDirection(localDirection, maxHorizontalAngleRadians, maxVerticalAngleRadians);

    private static float RemapSignedAngleToReferenceSeek(float angle, float limit)
        => Mathf.Clamp(
            NeutralSeekTimeSeconds - (angle / (2f * limit)),
            MinimumSeekTimeSeconds,
            MaximumSeekTimeSeconds);
}
