using Godot;

namespace AlleyCat.Vision;

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
    /// Resolves the signed, clamped horizontal and vertical gaze angles in radians for a target position,
    /// converting the world direction into the eye-origin frame using rotation only. Unlike the affine
    /// inverse used for blendshape seek times, a non-uniformly scaled eye origin cannot distort the angles.
    /// </summary>
    public static Vector2 ResolveLookAngles(
        Transform3D eyeOriginGlobalTransform,
        Vector3 targetGlobalPosition,
        float maxHorizontalAngleRadians,
        float maxVerticalAngleRadians)
    {
        Vector3 worldDirection = targetGlobalPosition - eyeOriginGlobalTransform.Origin;
        Quaternion inverseRotation = eyeOriginGlobalTransform.Basis.GetRotationQuaternion().Inverse();
        return ResolveLookAnglesFromLocalDirection(
            inverseRotation * worldDirection,
            maxHorizontalAngleRadians,
            maxVerticalAngleRadians);
    }

    /// <summary>
    /// Resolves horizontal and vertical animation seek times from an eye-origin local direction.
    /// </summary>
    public static Vector2 ResolveLookSeekTimesFromLocalDirection(
        Vector3 localDirection,
        float maxHorizontalAngleRadians,
        float maxVerticalAngleRadians)
    {
        Vector2 lookAngles = ResolveLookAnglesFromLocalDirection(
            localDirection,
            maxHorizontalAngleRadians,
            maxVerticalAngleRadians);

        return new Vector2(
            ConvertSignedAngleToReferenceSeek(lookAngles.X, maxHorizontalAngleRadians),
            ConvertSignedAngleToReferenceSeek(lookAngles.Y, maxVerticalAngleRadians));
    }

    /// <summary>
    /// Resolves the signed, clamped horizontal and vertical gaze angles in radians from an eye-origin
    /// local direction. Horizontal angles are positive towards +X (right of -Z forward) and vertical
    /// angles are positive towards +Y (up).
    /// </summary>
    public static Vector2 ResolveLookAnglesFromLocalDirection(
        Vector3 localDirection,
        float maxHorizontalAngleRadians,
        float maxVerticalAngleRadians)
    {
        if (localDirection.LengthSquared() <= Mathf.Epsilon)
        {
            return Vector2.Zero;
        }

        float horizontalLimit = Mathf.Max(Mathf.Epsilon, maxHorizontalAngleRadians);
        float verticalLimit = Mathf.Max(Mathf.Epsilon, maxVerticalAngleRadians);
        float horizontalAngle = Mathf.Clamp(Mathf.Atan2(localDirection.X, -localDirection.Z), -horizontalLimit, horizontalLimit);
        float planarDistance = Mathf.Sqrt((localDirection.X * localDirection.X) + (localDirection.Z * localDirection.Z));
        float verticalAngle = Mathf.Clamp(Mathf.Atan2(localDirection.Y, planarDistance), -verticalLimit, verticalLimit);

        return new Vector2(horizontalAngle, verticalAngle);
    }

    /// <summary>
    /// Builds the gaze rotation in the eye-origin frame for the supplied signed gaze angles, using
    /// -Z forward and +Y up. The rotation composes pitch about +X with yaw, matching the angle
    /// extraction in <see cref="ResolveLookAnglesFromLocalDirection"/>.
    /// </summary>
    public static Quaternion BuildGazeRotation(float horizontalAngleRadians, float verticalAngleRadians)
    {
        // Positive horizontal angles look towards +X, which is a negative rotation about +Y for -Z forward.
        Quaternion yawRotation = new(Vector3.Up, -horizontalAngleRadians);
        Quaternion pitchRotation = new(Vector3.Right, verticalAngleRadians);
        return yawRotation * pitchRotation;
    }

    /// <summary>
    /// Converts a signed clamped gaze angle into the normalised reference look animation seek range.
    /// </summary>
    public static float ConvertSignedAngleToReferenceSeek(float signedAngleRadians, float maxAngleRadians)
        => RemapSignedAngleToReferenceSeek(signedAngleRadians, maxAngleRadians);

    /// <summary>
    /// Converts a reference look animation seek time back into the signed clamped gaze angle it encodes.
    /// </summary>
    public static float ConvertReferenceSeekToSignedAngle(float seekTime, float maxAngleRadians)
        => (NeutralSeekTimeSeconds - Mathf.Clamp(seekTime, MinimumSeekTimeSeconds, MaximumSeekTimeSeconds))
            * 2f
            * Mathf.Max(Mathf.Epsilon, maxAngleRadians);

    /// <summary>
    /// Resolves horizontal and vertical animation seek times from a target position.
    /// </summary>
    [Obsolete("Use ResolveLookSeekTimes because VISION-001 controls AnimationNodeTimeSeek seek times.")]
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
    [Obsolete("Use ResolveLookSeekTimesFromLocalDirection because VISION-001 controls AnimationNodeTimeSeek seek times.")]
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
