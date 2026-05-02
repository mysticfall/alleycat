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
/// Avatar-relative hip-limit axis semantics resolved into skeleton-local directions for the
/// current character rig.
/// </summary>
internal readonly record struct HipLimitSemanticFrame(
    Vector3 UpLocal,
    Vector3 AvatarForwardLocal,
    Vector3 AvatarRightLocal)
{
    /// <summary>
    /// Semantic frame for the current reference character rig.
    /// </summary>
    /// <remarks>
    /// The imported skeleton lives under a container rotated 180 degrees around Y, so avatar-forward
    /// resolves to skeleton-local +Z and avatar-right resolves to skeleton-local -X.
    /// </remarks>
    public static HipLimitSemanticFrame ReferenceRig => new(
        Vector3.Up,
        Vector3.Back,
        Vector3.Left);

    /// <summary>
    /// Resolves authored avatar-relative limits into raw skeleton-local signed-axis clamp slots.
    /// </summary>
    public HipLimitEnvelope? ResolveOffsetEnvelope(OffsetLimits3D? limits)
        => limits is null
            ? null
            : new HipLimitEnvelope(
                limits.UpLimit,
                limits.DownLimit,
                ResolveNegativeXAxisLimit(limits.LeftLimit, limits.RightLimit),
                ResolvePositiveXAxisLimit(limits.LeftLimit, limits.RightLimit),
                ResolveNegativeZAxisLimit(limits.ForwardLimit, limits.BackLimit),
                ResolvePositiveZAxisLimit(limits.ForwardLimit, limits.BackLimit));

    private float? ResolveNegativeXAxisLimit(float? leftLimit, float? rightLimit)
        => MapsAvatarRightToPositiveX()
            ? leftLimit
            : rightLimit;

    private float? ResolvePositiveXAxisLimit(float? leftLimit, float? rightLimit)
        => MapsAvatarRightToPositiveX()
            ? rightLimit
            : leftLimit;

    private float? ResolveNegativeZAxisLimit(float? forwardLimit, float? backLimit)
        => MapsAvatarForwardToPositiveZ()
            ? backLimit
            : forwardLimit;

    private float? ResolvePositiveZAxisLimit(float? forwardLimit, float? backLimit)
        => MapsAvatarForwardToPositiveZ()
            ? forwardLimit
            : backLimit;

    private bool MapsAvatarRightToPositiveX() => AvatarRightLocal.Dot(Vector3.Right) >= 0f;

    private bool MapsAvatarForwardToPositiveZ() => AvatarForwardLocal.Dot(Vector3.Back) >= 0f;
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
    /// Creates an envelope from an authored <see cref="OffsetLimits3D"/> resource after resolving
    /// avatar-relative semantic directions into raw skeleton-local signed-axis slots.
    /// </summary>
    internal static HipLimitEnvelope? FromOffsetLimits(
        OffsetLimits3D? limits,
        HipLimitSemanticFrame semanticFrame)
        => semanticFrame.ResolveOffsetEnvelope(limits);

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
