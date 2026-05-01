using Godot;

namespace AlleyCat.Common;

/// <summary>
/// Directional clamp limits for a 3D offset, normalised by a reference distance.
/// </summary>
[GlobalClass]
public partial class OffsetLimits3D : Resource
{
    private const float NormalisationEpsilon = 1e-4f;

    /// <summary>
    /// Whether the positive-Y offset is clamped.
    /// </summary>
    [Export]
    public bool HasUpLimit { get; set; } = true;

    /// <summary>
    /// Maximum positive-Y offset, normalised by the reference distance.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater")]
    public float Up { get; set; } = 1.0f;

    /// <summary>
    /// Whether the negative-Y offset is clamped.
    /// </summary>
    [Export]
    public bool HasDownLimit { get; set; } = true;

    /// <summary>
    /// Maximum negative-Y offset magnitude, normalised by the reference distance.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater")]
    public float Down { get; set; } = 1.0f;

    /// <summary>
    /// Whether the negative-X offset is clamped.
    /// </summary>
    [Export]
    public bool HasLeftLimit { get; set; } = true;

    /// <summary>
    /// Maximum negative-X offset magnitude, normalised by the reference distance.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater")]
    public float Left { get; set; } = 1.0f;

    /// <summary>
    /// Whether the positive-X offset is clamped.
    /// </summary>
    [Export]
    public bool HasRightLimit { get; set; } = true;

    /// <summary>
    /// Maximum positive-X offset, normalised by the reference distance.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater")]
    public float Right { get; set; } = 1.0f;

    /// <summary>
    /// Whether the negative-Z offset is clamped.
    /// </summary>
    [Export]
    public bool HasForwardLimit { get; set; } = true;

    /// <summary>
    /// Maximum negative-Z offset magnitude, normalised by the reference distance.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater")]
    public float Forward { get; set; } = 1.0f;

    /// <summary>
    /// Whether the positive-Z offset is clamped.
    /// </summary>
    [Export]
    public bool HasBackLimit { get; set; } = true;

    /// <summary>
    /// Maximum positive-Z offset, normalised by the reference distance.
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater")]
    public float Back { get; set; } = 1.0f;

    /// <summary>
    /// Gets the optional positive-Y limit.
    /// </summary>
    public float? UpLimit => HasUpLimit ? Up : null;

    /// <summary>
    /// Gets the optional negative-Y limit.
    /// </summary>
    public float? DownLimit => HasDownLimit ? Down : null;

    /// <summary>
    /// Gets the optional negative-X limit.
    /// </summary>
    public float? LeftLimit => HasLeftLimit ? Left : null;

    /// <summary>
    /// Gets the optional positive-X limit.
    /// </summary>
    public float? RightLimit => HasRightLimit ? Right : null;

    /// <summary>
    /// Gets the optional negative-Z limit.
    /// </summary>
    public float? ForwardLimit => HasForwardLimit ? Forward : null;

    /// <summary>
    /// Gets the optional positive-Z limit.
    /// </summary>
    public float? BackLimit => HasBackLimit ? Back : null;

    /// <summary>
    /// Clamps <paramref name="offset"/> against the configured directional maxima scaled by
    /// <paramref name="normalisationDistance"/>.
    /// </summary>
    public Vector3 ClampOffset(Vector3 offset, float normalisationDistance) =>
        ClampOffset(
            offset,
            normalisationDistance,
            UpLimit,
            DownLimit,
            LeftLimit,
            RightLimit,
            ForwardLimit,
            BackLimit);

    /// <summary>
    /// Clamps <paramref name="offset"/> against explicit directional maxima scaled by
    /// <paramref name="normalisationDistance"/>.
    /// </summary>
    public static Vector3 ClampOffset(
        Vector3 offset,
        float normalisationDistance,
        float? up,
        float? down,
        float? left,
        float? right,
        float? forward,
        float? back)
        => !float.IsFinite(normalisationDistance) || normalisationDistance <= NormalisationEpsilon
            ? offset
            : new Vector3(
                ClampAxis(offset.X, left, right, normalisationDistance),
                ClampAxis(offset.Y, down, up, normalisationDistance),
                ClampAxis(offset.Z, forward, back, normalisationDistance));

    private static float ClampAxis(
        float value,
        float? negativeDirectionLimit,
        float? positiveDirectionLimit,
        float normalisationDistance)
    {
        if (!negativeDirectionLimit.HasValue && !positiveDirectionLimit.HasValue)
        {
            return value;
        }

        float minimum = negativeDirectionLimit.HasValue
            ? -Mathf.Max(negativeDirectionLimit.Value, 0.0f) * normalisationDistance
            : float.NegativeInfinity;
        float maximum = positiveDirectionLimit.HasValue
            ? Mathf.Max(positiveDirectionLimit.Value, 0.0f) * normalisationDistance
            : float.PositiveInfinity;
        return Mathf.Clamp(value, minimum, maximum);
    }
}
