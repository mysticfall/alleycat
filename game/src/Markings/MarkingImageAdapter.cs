using Godot;

namespace AlleyCat.Markings;

/// <summary>
/// Converts between Godot images and compositor buffers. The only layer that touches sRGB
/// storage: albedo images are decoded to linear light on the way in and re-encoded on the way
/// out, while normal and roughness images pass through unchanged because their channels hold
/// linear data.
/// </summary>
internal static class MarkingImageAdapter
{
    /// <summary>
    /// Converts an image into a compositor buffer, converting to RGBA8 first (decompressing
    /// VRAM-compressed imports such as DXT1 diffuse maps) and decoding the RGB components
    /// from sRGB for albedo images.
    /// </summary>
    public static MarkingBuffer ToBuffer(Image image, MarkingChannel channel)
    {
        Image rgba = image;
        if (image.IsCompressed() || image.GetFormat() != Image.Format.Rgba8 || image.HasMipmaps())
        {
            rgba = (Image)image.Duplicate();
            if (rgba.IsCompressed() && rgba.Decompress() != Error.Ok)
            {
                throw new InvalidOperationException(
                    $"Failed to decompress the {image.GetFormat()} marking base image for baking.");
            }

            if (rgba.GetFormat() != Image.Format.Rgba8)
            {
                rgba.Convert(Image.Format.Rgba8);
            }

            if (rgba.HasMipmaps())
            {
                rgba.ClearMipmaps();
            }
        }

        int width = rgba.GetWidth();
        int height = rgba.GetHeight();
        byte[] bytes = rgba.GetData();
        float[] data = new float[width * height * 4];
        bool decodeSrgb = channel == MarkingChannel.Albedo;

        for (int index = 0; index < data.Length; index += 4)
        {
            for (int component = 0; component < 4; component++)
            {
                float value = bytes[index + component] / 255f;
                data[index + component] = decodeSrgb && component < 3
                    ? MarkingMath.SrgbToLinear(value)
                    : value;
            }
        }

        return new MarkingBuffer(width, height, data);
    }

    /// <summary>
    /// Converts a compositor buffer into an RGBA8 image with mipmaps, encoding the RGB
    /// components to sRGB for albedo buffers.
    /// </summary>
    public static Image ToImage(MarkingBuffer buffer, MarkingChannel channel)
    {
        int width = buffer.Width;
        int height = buffer.Height;
        byte[] bytes = new byte[buffer.Data.Length];
        bool encodeSrgb = channel == MarkingChannel.Albedo;

        for (int index = 0; index < buffer.Data.Length; index += 4)
        {
            for (int component = 0; component < 4; component++)
            {
                float value = buffer.Data[index + component];
                if (encodeSrgb && component < 3)
                {
                    value = MarkingMath.LinearToSrgb(value);
                }

                bytes[index + component] = (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
            }
        }

        var image = Image.CreateFromData(width, height, useMipmaps: false, Image.Format.Rgba8, bytes);
        _ = image.GenerateMipmaps();
        return image;
    }

    /// <summary>
    /// Creates the compositor stamp for a marking on one channel, or returns null when the
    /// marking supplies no art for that channel.
    /// </summary>
    public static MarkingStamp? TryCreateStamp(SurfaceMarking marking, MarkingChannel channel, MarkingBuffer source)
    {
        if (source.Width <= 0 || source.Height <= 0)
        {
            return null;
        }

        MarkingPlacement placement = new(
            marking.Position.X,
            marking.Position.Y,
            marking.RotationDegrees,
            marking.Scale.X,
            marking.Scale.Y);

        return new MarkingStamp(
            source,
            placement,
            marking.Tint.R,
            marking.Tint.G,
            marking.Tint.B,
            marking.Strength,
            channel == MarkingChannel.Roughness ? marking.RoughnessBlend : marking.AlbedoBlend);
    }

    /// <summary>
    /// Loads the source buffer for a marking's channel art, or null when the marking supplies
    /// no art for the channel.
    /// </summary>
    public static MarkingBuffer? TryLoadSource(SurfaceMarking marking, MarkingChannel channel)
        => channel switch
        {
            MarkingChannel.Albedo => marking.AlbedoTexture is null ? null : ToBuffer(marking.AlbedoTexture.GetImage(), channel),
            MarkingChannel.Normal => marking.NormalTexture is null ? null : ToBuffer(marking.NormalTexture.GetImage(), channel),
            MarkingChannel.Roughness => marking.RoughnessTexture is null
                ? null
                : ToBuffer(marking.RoughnessTexture.GetImage(), channel),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown marking channel."),
        };
}
