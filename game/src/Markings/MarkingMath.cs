namespace AlleyCat.Markings;

/// <summary>
/// sRGB transfer functions and linear-space blend operations for marking composition. Pure
/// scalar maths with no engine dependency, so the channel conventions are unit-testable
/// without a running Godot runtime.
/// </summary>
public static class MarkingMath
{
    /// <summary>
    /// Decodes a stored sRGB channel value into its linear-light value, using the standard
    /// piecewise sRGB transfer function (the same curve Godot shaders sample sRGB textures with).
    /// </summary>
    /// <param name="value">Channel value in the 0..1 sRGB range; values outside are clamped.</param>
    /// <returns>The decoded linear-light value in 0..1.</returns>
    public static float SrgbToLinear(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>
    /// Encodes a linear-light channel value for storage in an sRGB texture.
    /// </summary>
    /// <param name="value">Linear-light value in 0..1; values outside are clamped.</param>
    /// <returns>The encoded sRGB channel value in 0..1.</returns>
    public static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f
            ? value * 12.92f
            : (1.055f * MathF.Pow(value, 1f / 2.4f)) - 0.055f;
    }

    /// <summary>
    /// Blends a linear base value with a linear ink value using the given mode.
    /// </summary>
    /// <param name="baseValue">Base channel value in linear space.</param>
    /// <param name="inkValue">Ink channel value in linear space, after tinting.</param>
    /// <param name="mode">The blend operation to apply.</param>
    /// <returns>The blended linear value.</returns>
    public static float Blend(float baseValue, float inkValue, MarkingBlendMode mode) => mode switch
    {
        MarkingBlendMode.Multiply => baseValue * inkValue,
        MarkingBlendMode.Overlay => baseValue <= 0.5f
            ? 2f * baseValue * inkValue
            : 1f - (2f * (1f - baseValue) * (1f - inkValue)),
        MarkingBlendMode.SoftLight => inkValue <= 0.5f
            ? baseValue - ((1f - (2f * inkValue)) * baseValue * (1f - baseValue))
            : baseValue + (((2f * inkValue) - 1f) * (PegtopSqrt(baseValue) - baseValue)),
        MarkingBlendMode.Darken => MathF.Min(baseValue, inkValue),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown marking blend mode."),
    };

    // Square root with the degenerate negative-input case clamped, keeping SoftLight stable for
    // out-of-range intermediate values.
    private static float PegtopSqrt(float value) => MathF.Sqrt(Math.Max(value, 0f));
}
