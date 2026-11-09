namespace AlleyCat.Markings;

/// <summary>
/// The material channel a stamp targets. The channel owns the packing conventions: albedo
/// blends its RGB in linear space, normal blends only the tangent XY axes while preserving the
/// ambient-occlusion value packed into blue, and roughness blends only the green channel.
/// </summary>
public enum MarkingChannel
{
    /// <summary>Base colour. RGB are stored sRGB and blended in linear space; alpha is untouched.</summary>
    Albedo,

    /// <summary>Tangent-space normal. RG carry the tilt (whiteout blend); B carries packed AO and is never written.</summary>
    Normal,

    /// <summary>Scalar roughness packed into the green channel (the skin shader reads roughness from <c>.g</c>).</summary>
    Roughness,
}

/// <summary>
/// A rectangular marking placement authored in the base texture's UV space.
/// </summary>
/// <param name="PositionU">U coordinate of the stamp centre.</param>
/// <param name="PositionV">V coordinate of the stamp centre.</param>
/// <param name="RotationDegrees">Rotation of the stamp rectangle in degrees; positive values rotate
/// clockwise as seen in the texture image (whose row axis points down).</param>
/// <param name="ScaleU">Full horizontal extent of the stamp rectangle in UV units.</param>
/// <param name="ScaleV">Full vertical extent of the stamp rectangle in UV units.</param>
public readonly record struct MarkingPlacement(
    float PositionU,
    float PositionV,
    float RotationDegrees,
    float ScaleU,
    float ScaleV);

/// <summary>
/// One resolved stamp: the marking's source pixels plus the placement and blend controls that
/// the compositor applies to a single channel.
/// </summary>
/// <param name="Source">Overlay source pixels; RGB carry ink colour (albedo) or packed channel data,
/// A carries coverage.</param>
/// <param name="Placement">The stamp rectangle and orientation.</param>
/// <param name="TintR">Red component of the albedo tint in linear space.</param>
/// <param name="TintG">Green component of the albedo tint in linear space.</param>
/// <param name="TintB">Blue component of the albedo tint in linear space.</param>
/// <param name="Strength">Coverage multiplier applied to the source alpha on every channel, 0..1.</param>
/// <param name="Blend">Blend operation for the albedo and roughness channels; normals always whiteout.</param>
public readonly record struct MarkingStamp(
    MarkingBuffer Source,
    MarkingPlacement Placement,
    float TintR = 1f,
    float TintG = 1f,
    float TintB = 1f,
    float Strength = 1f,
    MarkingBlendMode Blend = MarkingBlendMode.Multiply);

/// <summary>
/// A mutable RGBA float image the compositor stamps into. Components are stored interleaved in
/// reading order (row-major, RGBA) with linear-light semantics for albedo RGB; adapters own the
/// conversion to and from stored sRGB bytes.
/// </summary>
public sealed class MarkingBuffer
{
    /// <summary>Gets the buffer width in pixels.</summary>
    public int Width
    {
        get;
    }

    /// <summary>Gets the buffer height in pixels.</summary>
    public int Height
    {
        get;
    }

    /// <summary>
    /// Gets the interleaved RGBA component data, four floats per pixel in row-major order.
    /// </summary>
    public float[] Data
    {
        get;
    }

    /// <summary>
    /// Creates a buffer of the given size filled with a solid colour.
    /// </summary>
    public MarkingBuffer(int width, int height, float r = 0f, float g = 0f, float b = 0f, float a = 1f)
    {
        Width = width;
        Height = height;
        Data = new float[width * height * 4];
        for (int index = 0; index < Data.Length; index += 4)
        {
            Data[index] = r;
            Data[index + 1] = g;
            Data[index + 2] = b;
            Data[index + 3] = a;
        }
    }

    /// <summary>
    /// Creates a buffer wrapping existing interleaved RGBA component data.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the data length does not match the size.</exception>
    public MarkingBuffer(int width, int height, float[] data)
    {
        if (data.Length != width * height * 4)
        {
            throw new ArgumentException(
                $"Expected {width * height * 4} floats for a {width}x{height} RGBA buffer, but found {data.Length}.");
        }

        Width = width;
        Height = height;
        Data = data;
    }

    /// <summary>
    /// Returns a deep copy of the buffer with its own component storage.
    /// </summary>
    public MarkingBuffer Duplicate() => new(Width, Height, [.. Data]);

    /// <summary>
    /// Reads the red component of a pixel. Components are indexed directly for tests and the
    /// compositor; per-pixel accessors are deliberately minimal.
    /// </summary>
    public float GetR(int x, int y) => Data[((y * Width) + x) * 4];

    /// <summary>Reads the green component of a pixel.</summary>
    public float GetG(int x, int y) => Data[(((y * Width) + x) * 4) + 1];

    /// <summary>Reads the blue component of a pixel.</summary>
    public float GetB(int x, int y) => Data[(((y * Width) + x) * 4) + 2];

    /// <summary>Reads the alpha component of a pixel.</summary>
    public float GetA(int x, int y) => Data[(((y * Width) + x) * 4) + 3];
}
