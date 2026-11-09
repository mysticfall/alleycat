namespace AlleyCat.Markings;

/// <summary>
/// Blend operations the compositor applies when stamping a marking into the albedo or
/// roughness channels. All operations run on decoded linear values, never on stored sRGB
/// bytes; the identity overlay value is noted per mode.
/// </summary>
public enum MarkingBlendMode
{
    /// <summary>Multiplies the base by the ink; the identity overlay is white.</summary>
    Multiply,

    /// <summary>Contrast-preserving overlay; the identity overlay is mid grey.</summary>
    Overlay,

    /// <summary>Softly lightens or darkens the base; the identity overlay is mid grey.</summary>
    SoftLight,

    /// <summary>Keeps the darker of base and ink; the identity overlay is white.</summary>
    Darken,
}
