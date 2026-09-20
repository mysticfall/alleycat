using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Vision;

/// <summary>
/// Unit coverage for VISION-001 Eyes target-to-AnimationNodeTimeSeek time conversion.
/// </summary>
public sealed class EyesLookMathTests
{
    /// <summary>
    /// Verifies the reference look animations are treated as normalised one-second timelines.
    /// </summary>
    [Fact]
    public void SeekTimeConstants_DefineNormalisedReferenceAnimationContract()
    {
        Assert.Equal(0f, EyesLookMath.MinimumSeekTimeSeconds);
        Assert.Equal(0.5f, EyesLookMath.NeutralSeekTimeSeconds);
        Assert.Equal(1f, EyesLookMath.MaximumSeekTimeSeconds);
    }

    /// <summary>
    /// Verifies a forward target resolves to the neutral eye animation seek positions.
    /// </summary>
    [Fact]
    public void ResolveLookSeekTimesFromLocalDirection_ForwardTargetReturnsNeutralSeekTimes()
    {
        Vector2 seekTimes = EyesLookMath.ResolveLookSeekTimesFromLocalDirection(
            new Vector3(0f, 0f, -10f),
            Mathf.DegToRad(35f),
            Mathf.DegToRad(25f));

        Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, seekTimes.X, precision: 5);
        Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, seekTimes.Y, precision: 5);
    }

    /// <summary>
    /// Verifies signed local directions map to the normalised 0..1 second reference look animation seek range.
    /// </summary>
    [Theory]
    [InlineData(1f, 0f, -1f, 0f, 0.5f)]
    [InlineData(-1f, 0f, -1f, 1f, 0.5f)]
    [InlineData(0f, 1f, -1f, 0.5f, 0f)]
    [InlineData(0f, -1f, -1f, 0.5f, 1f)]
    public void ResolveLookSeekTimesFromLocalDirection_ClampsSignedDirectionsToNormalisedAnimationRange(
        float x,
        float y,
        float z,
        float expectedHorizontal,
        float expectedVertical)
    {
        Vector2 seekTimes = EyesLookMath.ResolveLookSeekTimesFromLocalDirection(
            new Vector3(x, y, z),
            Mathf.DegToRad(35f),
            Mathf.DegToRad(25f));

        Assert.Equal(expectedHorizontal, seekTimes.X, precision: 5);
        Assert.Equal(expectedVertical, seekTimes.Y, precision: 5);
    }

    /// <summary>
    /// Verifies a forward local direction resolves to zero signed angles and that zero angles build the identity rotation.
    /// </summary>
    [Fact]
    public void ResolveLookAnglesFromLocalDirection_ForwardDirectionReturnsZeroAnglesAndIdentityRotation()
    {
        Vector2 angles = EyesLookMath.ResolveLookAnglesFromLocalDirection(
            new Vector3(0f, 0f, -10f),
            Mathf.DegToRad(35f),
            Mathf.DegToRad(25f));

        Assert.Equal(0f, angles.X, precision: 5);
        Assert.Equal(0f, angles.Y, precision: 5);
        Assert.Equal(Quaternion.Identity, EyesLookMath.BuildGazeRotation(0f, 0f));
    }

    /// <summary>
    /// Verifies signed angle extraction agrees with the reference seek direction contract used by the existing
    /// TimeSeek tests: right and up are positive angles mapping to seek 0, left and down are negative angles
    /// mapping to seek 1.
    /// </summary>
    [Theory]
    [InlineData(1f, 0f, -1f, 1f, 0f, 0f, 0.5f)]
    [InlineData(-1f, 0f, -1f, -1f, 0f, 1f, 0.5f)]
    [InlineData(0f, 1f, -1f, 0f, 1f, 0.5f, 0f)]
    [InlineData(0f, -1f, -1f, 0f, -1f, 0.5f, 1f)]
    public void ResolveLookAnglesFromLocalDirection_SignsMatchReferenceSeekDirectionContract(
        float x,
        float y,
        float z,
        float expectedHorizontalSign,
        float expectedVerticalSign,
        float expectedHorizontalSeek,
        float expectedVerticalSeek)
    {
        const float horizontalLimit = 0.610865f;
        const float verticalLimit = 0.436332f;
        Vector3 direction = new(x, y, z);

        Vector2 angles = EyesLookMath.ResolveLookAnglesFromLocalDirection(direction, horizontalLimit, verticalLimit);
        Vector2 seekTimes = EyesLookMath.ResolveLookSeekTimesFromLocalDirection(direction, horizontalLimit, verticalLimit);

        Assert.Equal(expectedHorizontalSign > 0f, angles.X > 0f);
        Assert.Equal(expectedVerticalSign > 0f, angles.Y > 0f);
        Assert.Equal(expectedHorizontalSeek, seekTimes.X, precision: 5);
        Assert.Equal(expectedVerticalSeek, seekTimes.Y, precision: 5);
    }

    /// <summary>
    /// Verifies each axis clamps independently against its own limit, including targets beyond both limits.
    /// </summary>
    [Theory]
    [InlineData(2f, 3f, -1f, true, true)]
    [InlineData(-2f, -3f, -1f, true, true)]
    [InlineData(0.2f, 3f, -1f, false, true)]
    [InlineData(2f, 0.1f, -1f, true, false)]
    [InlineData(0.2f, 0.1f, -1f, false, false)]
    public void ResolveLookAnglesFromLocalDirection_ClampsEachAxisIndependently(
        float x,
        float y,
        float z,
        bool horizontalClamped,
        bool verticalClamped)
    {
        const float horizontalLimit = 0.610865f;
        const float verticalLimit = 0.436332f;
        Vector3 direction = new(x, y, z);

        // Independent first-principles expectation: raw angles from an unclamped spherical decomposition of the
        // direction, clamped per axis with the shared limits.
        float rawHorizontal = Mathf.Atan2(direction.X, -direction.Z);
        float planarDistance = Mathf.Sqrt((direction.X * direction.X) + (direction.Z * direction.Z));
        float rawVertical = Mathf.Atan2(direction.Y, planarDistance);
        float expectedHorizontal = Mathf.Clamp(rawHorizontal, -horizontalLimit, horizontalLimit);
        float expectedVertical = Mathf.Clamp(rawVertical, -verticalLimit, verticalLimit);

        Vector2 angles = EyesLookMath.ResolveLookAnglesFromLocalDirection(direction, horizontalLimit, verticalLimit);

        Assert.Equal(expectedHorizontal, angles.X, precision: 5);
        Assert.Equal(expectedVertical, angles.Y, precision: 5);
        Assert.Equal(horizontalClamped, Mathf.Abs(angles.X) >= horizontalLimit - 0.00001f);
        Assert.Equal(verticalClamped, Mathf.Abs(angles.Y) >= verticalLimit - 0.00001f);
    }

    /// <summary>
    /// Verifies a target directly behind the origin has defined behaviour matching the existing clamp policy:
    /// the horizontal angle clamps to its limit while the vertical angle stays neutral.
    /// </summary>
    [Fact]
    public void ResolveLookAnglesFromLocalDirection_BehindOriginFollowsExistingClampPolicy()
    {
        const float horizontalLimit = 0.610865f;
        const float verticalLimit = 0.436332f;

        Vector2 angles = EyesLookMath.ResolveLookAnglesFromLocalDirection(
            new Vector3(0f, 0f, 1f),
            horizontalLimit,
            verticalLimit);

        Assert.Equal(horizontalLimit, angles.X, precision: 5);
        Assert.Equal(0f, angles.Y, precision: 5);

        Vector2 seekTimes = EyesLookMath.ResolveLookSeekTimesFromLocalDirection(
            new Vector3(0f, 0f, 1f),
            horizontalLimit,
            verticalLimit);
        Assert.Equal(EyesLookMath.MinimumSeekTimeSeconds, seekTimes.X, precision: 5);
        Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, seekTimes.Y, precision: 5);
    }

    /// <summary>
    /// Verifies the signed-angle and reference-seek conversions are inverses and preserve the unchanged
    /// normalised 0..1 contract at the original limits.
    /// </summary>
    [Theory]
    [InlineData(0f, 0.5f)]
    [InlineData(0.610865f, 0f)]
    [InlineData(-0.610865f, 1f)]
    [InlineData(0.25f, 0.295372f)]
    [InlineData(-0.3054326f, 0.75f)]
    [InlineData(1.2f, 0f)]
    [InlineData(-1.2f, 1f)]
    public void ConvertSignedAngleAndReferenceSeek_RoundTripWithinNormalisedContract(
        float signedAngleRadians,
        float expectedSeekTime)
    {
        const float limit = 0.610865f;

        float seekTime = EyesLookMath.ConvertSignedAngleToReferenceSeek(signedAngleRadians, limit);

        Assert.True(seekTime is >= 0f and <= 1f, "Reference seek conversion must stay within the normalised 0..1 contract.");
        Assert.Equal(Mathf.Clamp(expectedSeekTime, 0f, 1f), seekTime, precision: 5);

        float roundTripAngle = EyesLookMath.ConvertReferenceSeekToSignedAngle(seekTime, limit);
        Assert.Equal(Mathf.Clamp(signedAngleRadians, -limit, limit), roundTripAngle, precision: 5);
    }

    /// <summary>
    /// Verifies clamped reference-seek inputs decode back into angles within the limits.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1f)]
    [InlineData(-0.5f)]
    [InlineData(1.5f)]
    public void ConvertReferenceSeekToSignedAngle_ClampsSeekInputBeforeDecoding(float seekTime)
    {
        const float limit = 0.436332f;

        float angle = EyesLookMath.ConvertReferenceSeekToSignedAngle(seekTime, limit);

        Assert.True(angle is >= (-limit - 0.000001f) and <= (limit + 0.000001f), "Decoded angles must stay within the supplied limit.");
    }

    /// <summary>
    /// Verifies building a rotation from clamped angles and re-extracting the angles from the rotated forward
    /// direction returns the same clamped angles across representative quadrant and limit values.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 1.4f, 1.4f)]
    [InlineData(0.3f, 0f, 1.4f, 1.4f)]
    [InlineData(-0.4f, 0.2f, 1.4f, 1.4f)]
    [InlineData(0.5f, -0.3f, 1.4f, 1.4f)]
    [InlineData(-0.5f, -0.3f, 1.4f, 1.4f)]
    [InlineData(0.610865f, 0.436332f, 1.4f, 1.4f)]
    [InlineData(-0.610865f, 0.436332f, 1.4f, 1.4f)]
    [InlineData(0f, 0.436332f, 1.4f, 1.4f)]
    [InlineData(0.610865f, -0.436332f, 1.4f, 1.4f)]
    [InlineData(0.9f, 0.8f, 1.4f, 1.4f)]
    [InlineData(0.9f, 0.8f, 0.610865f, 0.436332f)]
    [InlineData(-0.9f, -0.8f, 0.610865f, 0.436332f)]
    public void BuildGazeRotation_IsInverseConsistentWithAngleExtraction(
        float horizontalAngleRadians,
        float verticalAngleRadians,
        float extractionHorizontalLimit,
        float extractionVerticalLimit)
    {
        Quaternion gazeRotation = EyesLookMath.BuildGazeRotation(horizontalAngleRadians, verticalAngleRadians);
        Vector3 rotatedForward = gazeRotation * new Vector3(0f, 0f, -1f);

        Vector2 extracted = EyesLookMath.ResolveLookAnglesFromLocalDirection(
            rotatedForward,
            extractionHorizontalLimit,
            extractionVerticalLimit);

        float expectedHorizontal = Mathf.Clamp(horizontalAngleRadians, -extractionHorizontalLimit, extractionHorizontalLimit);
        float expectedVertical = Mathf.Clamp(verticalAngleRadians, -extractionVerticalLimit, extractionVerticalLimit);
        Assert.Equal(expectedHorizontal, extracted.X, precision: 5);
        Assert.Equal(expectedVertical, extracted.Y, precision: 5);
    }

    /// <summary>
    /// Verifies world-to-eye-origin angle resolution is rotation-only, so positive non-uniform scale on the
    /// eye-origin transform cannot distort the gaze angles compared with an identically rotated unscaled reference.
    /// </summary>
    [Fact]
    public void ResolveLookAngles_IgnoresPositiveNonUniformScaleOnEyeOriginTransform()
    {
        var originRotation = Quaternion.FromEuler(new Vector3(0.21f, -0.63f, 0.14f));
        var origin = new Vector3(1.2f, 1.7f, -0.4f);
        var target = new Vector3(-2.4f, 3.1f, -4.8f);
        Transform3D unscaledOrigin = new(new Basis(originRotation), origin);
        Transform3D scaledOrigin = new(
            new Basis(originRotation).ScaledLocal(new Vector3(2.2f, 0.45f, 1.35f)),
            origin);

        Vector2 unscaledAngles = EyesLookMath.ResolveLookAngles(unscaledOrigin, target, 1.4f, 1.4f);
        Vector2 scaledAngles = EyesLookMath.ResolveLookAngles(scaledOrigin, target, 1.4f, 1.4f);

        Assert.Equal(unscaledAngles.X, scaledAngles.X, precision: 5);
        Assert.Equal(unscaledAngles.Y, scaledAngles.Y, precision: 5);
        Assert.NotEqual(0f, unscaledAngles.X, precision: 5);
        Assert.NotEqual(0f, unscaledAngles.Y, precision: 5);
    }
}
