using AlleyCat.Common;
using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// State-resolved per-tick hip-limit reference and clamp envelope.
/// </summary>
public sealed record HipLimitFrame
{
    /// <summary>
    /// Gets the absolute hip reference position in skeleton-local space.
    /// </summary>
    public Vector3 ReferenceHipLocalPosition { get; init; } = Vector3.Zero;

    /// <summary>
    /// Gets optional absolute directional clamp bounds in skeleton-local space.
    /// When present, these take precedence over <see cref="OffsetEnvelope"/> because individual
    /// directional sides may anchor to different authored reference frames.
    /// </summary>
    public HipLimitBounds? AbsoluteBounds
    {
        get; init;
    }

    /// <summary>
    /// Gets the optional directional clamp envelope applied relative to
    /// <see cref="ReferenceHipLocalPosition"/>.
    /// </summary>
    public HipLimitEnvelope? OffsetEnvelope
    {
        get; init;
    }
}

/// <summary>
/// Absolute skeleton-local directional hip bounds resolved for a single tick.
/// </summary>
public readonly record struct HipLimitBounds(
    float? Up,
    float? Down,
    float? Left,
    float? Right,
    float? Forward,
    float? Back)
{
    /// <summary>
    /// Clamps <paramref name="position"/> against these absolute bounds.
    /// </summary>
    public Vector3 ClampPosition(Vector3 position) => new(
        ClampAxis(position.X, Left, Right),
        ClampAxis(position.Y, Down, Up),
        ClampAxis(position.Z, Forward, Back));

    private static float ClampAxis(float value, float? minimum, float? maximum)
        => !minimum.HasValue && !maximum.HasValue
            ? value
            : Mathf.Clamp(
                value,
                minimum ?? float.NegativeInfinity,
                maximum ?? float.PositiveInfinity);
}

/// <summary>
/// Directional hip-offset clamp envelope resolved for a single tick.
/// </summary>
public readonly record struct HipLimitEnvelope(
    float? Up,
    float? Down,
    float? Left,
    float? Right,
    float? Forward,
    float? Back)
{
    /// <summary>
    /// Creates an envelope from an authored <see cref="OffsetLimits3D"/> resource.
    /// </summary>
    public static HipLimitEnvelope? FromOffsetLimits(OffsetLimits3D? limits) =>
        limits is null
            ? null
            : new HipLimitEnvelope(
                limits.UpLimit,
                limits.DownLimit,
                limits.LeftLimit,
                limits.RightLimit,
                limits.ForwardLimit,
                limits.BackLimit);

    /// <summary>
    /// Interpolates between two envelopes component-wise without synthesising new directional bounds.
    /// </summary>
    public static HipLimitEnvelope Lerp(HipLimitEnvelope from, HipLimitEnvelope to, float weight)
    {
        float t = Mathf.Clamp(weight, 0f, 1f);
        return new HipLimitEnvelope(
            LerpLimit(from.Up, to.Up, t),
            LerpLimit(from.Down, to.Down, t),
            LerpLimit(from.Left, to.Left, t),
            LerpLimit(from.Right, to.Right, t),
            LerpLimit(from.Forward, to.Forward, t),
            LerpLimit(from.Back, to.Back, t));
    }

    /// <summary>
    /// Clamps <paramref name="offset"/> against this envelope.
    /// </summary>
    public Vector3 ClampOffset(Vector3 offset, float normalisationDistance) =>
        OffsetLimits3D.ClampOffset(offset, normalisationDistance, Up, Down, Left, Right, Forward, Back);

    private static float? LerpLimit(float? from, float? to, float weight)
        => (from, to) switch
        {
            (null, null) => null,
            ({ }, null) => weight >= 1f ? null : from,
            (null, { }) => weight <= 0f ? null : to,
            ({ } fromValue, { } toValue) => Mathf.Lerp(fromValue, toValue, weight),
        };
}
