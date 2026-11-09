namespace AlleyCat.Markings;

/// <summary>
/// A Godot-free layer description for composition: whether the layer is enabled and the
/// stamps it contributes, in authored order.
/// </summary>
/// <param name="Enabled">Whether the layer's stamps are composited.</param>
/// <param name="Stamps">The layer's stamps in authored order.</param>
public readonly record struct MarkingLayerPlan(bool Enabled, MarkingStamp[] Stamps);

/// <summary>
/// Stamps marking sources into channel buffers. Owns the channel packing conventions: albedo
/// blends linear-light RGB behind the marking's coverage, normal blends the tangent XY axes
/// with the whiteout operation while never touching the AO packed into blue, and roughness
/// blends only the green channel. Stamping is bbox-limited to the rotated placement rectangle.
/// </summary>
public static class MarkingCompositor
{
    /// <summary>
    /// Stamps one marking into a channel buffer in authored placement order.
    /// </summary>
    /// <param name="destination">The channel buffer to modify in place.</param>
    /// <param name="stamp">The resolved source, placement, and blend controls.</param>
    /// <param name="channel">The packing convention to apply.</param>
    public static void Stamp(MarkingBuffer destination, in MarkingStamp stamp, MarkingChannel channel)
    {
        MarkingBuffer source = stamp.Source;
        if (source.Width <= 0 || source.Height <= 0)
        {
            return;
        }

        StampBounds bounds = ComputeStampBounds(destination.Width, destination.Height, stamp.Placement);
        if (bounds.IsEmpty)
        {
            return;
        }

        (float centreX, float centreY) = ComputeCentrePixels(destination.Width, destination.Height, stamp.Placement);
        (float extentX, float extentY) = ComputeExtentPixels(destination.Width, destination.Height, stamp.Placement);
        float radians = -stamp.Placement.RotationDegrees * MathF.PI / 180f;
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        float coverageScale = Math.Clamp(stamp.Strength, 0f, 1f);

        for (int y = bounds.MinY; y <= bounds.MaxY; y++)
        {
            for (int x = bounds.MinX; x <= bounds.MaxX; x++)
            {
                (float sourceU, float sourceV) = DestinationToSource(
                    x + 0.5f,
                    y + 0.5f,
                    centreX,
                    centreY,
                    extentX,
                    extentY,
                    cos,
                    sin);
                if (sourceU < 0f || sourceU > 1f || sourceV < 0f || sourceV > 1f)
                {
                    continue;
                }

                (float sr, float sg, float sb, float sa) = SampleBilinear(
                    source, sourceU * (source.Width - 1), sourceV * (source.Height - 1));

                float coverage = sa * coverageScale;
                if (coverage <= 0f)
                {
                    continue;
                }

                StampPixel(destination, x, y, sr, sg, sb, coverage, stamp, channel);
            }
        }
    }

    /// <summary>
    /// Stamps a sequence of layers into a channel buffer in authored order, skipping disabled
    /// layers. Layer order spans channels: the same order composes albedo, normal, and
    /// roughness so later layers read consistently over earlier ones everywhere.
    /// </summary>
    /// <param name="destination">The channel buffer to modify in place.</param>
    /// <param name="layers">The layer plans in overlay order.</param>
    /// <param name="channel">The packing convention to apply.</param>
    public static void StampLayers(
        MarkingBuffer destination,
        IReadOnlyList<MarkingLayerPlan> layers,
        MarkingChannel channel)
    {
        foreach (MarkingLayerPlan layer in layers)
        {
            if (!layer.Enabled)
            {
                continue;
            }

            foreach (MarkingStamp stamp in layer.Stamps)
            {
                Stamp(destination, stamp, channel);
            }
        }
    }

    /// <summary>
    /// Computes the clamped integer bounds of a stamp's rotated rectangle within a destination
    /// buffer. Inclusive on both axes; empty when the rectangle misses the buffer entirely.
    /// </summary>
    public static StampBounds ComputeStampBounds(int width, int height, in MarkingPlacement placement)
    {
        (float centreX, float centreY) = ComputeCentrePixels(width, height, placement);
        (float extentX, float extentY) = ComputeExtentPixels(width, height, placement);
        float radians = placement.RotationDegrees * MathF.PI / 180f;
        float cos = MathF.Abs(MathF.Cos(radians));
        float sin = MathF.Abs(MathF.Sin(radians));
        float halfX = MathF.Abs(extentX) * 0.5f;
        float halfY = MathF.Abs(extentY) * 0.5f;

        // Axis-aligned bounding box of the rotated rectangle: the extent projections.
        float reachX = (halfX * cos) + (halfY * sin);
        float reachY = (halfX * sin) + (halfY * cos);

        // Pixel p is potentially covered when its centre (p + 0.5) lies inside the rectangle,
        // so the first candidate is ceil(min - 0.5) and the last is floor(max - 0.5).
        int minX = (int)MathF.Ceiling(centreX - reachX - 0.5f);
        int minY = (int)MathF.Ceiling(centreY - reachY - 0.5f);
        int maxX = (int)MathF.Floor(centreX + reachX - 0.5f);
        int maxY = (int)MathF.Floor(centreY + reachY - 0.5f);

        return maxX < 0 || maxY < 0 || minX > width - 1 || minY > height - 1
            ? StampBounds.Empty
            : new StampBounds(
            Math.Clamp(minX, 0, width - 1),
            Math.Clamp(minY, 0, height - 1),
            Math.Clamp(maxX, 0, width - 1),
            Math.Clamp(maxY, 0, height - 1));
    }

    /// <summary>
    /// Maps a destination pixel position to normalised source coordinates in 0..1, or outside
    /// that range when the position falls outside the rotated stamp rectangle.
    /// </summary>
    public static (float U, float V) DestinationToSource(
        float pixelX,
        float pixelY,
        float centreX,
        float centreY,
        float extentX,
        float extentY,
        float inverseCos,
        float inverseSin)
    {
        float dx = pixelX - centreX;
        float dy = pixelY - centreY;
        float ex = (dx * inverseCos) - (dy * inverseSin);
        float ey = (dx * inverseSin) + (dy * inverseCos);
        return (0.5f + (ex / extentX), 0.5f + (ey / extentY));
    }

    /// <summary>
    /// Samples the buffer bilinearly at continuous pixel coordinates, clamped to the edges.
    /// </summary>
    public static (float R, float G, float B, float A) SampleBilinear(MarkingBuffer buffer, float x, float y)
    {
        x = Math.Clamp(x, 0f, buffer.Width - 1);
        y = Math.Clamp(y, 0f, buffer.Height - 1);
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int x1 = Math.Min(x0 + 1, buffer.Width - 1);
        int y1 = Math.Min(y0 + 1, buffer.Height - 1);
        float fx = x - x0;
        float fy = y - y0;

        int row0 = y0 * buffer.Width;
        int row1 = y1 * buffer.Width;
        int i00 = (row0 + x0) * 4;
        int i10 = (row0 + x1) * 4;
        int i01 = (row1 + x0) * 4;
        int i11 = (row1 + x1) * 4;

        float w00 = (1f - fx) * (1f - fy);
        float w10 = fx * (1f - fy);
        float w01 = (1f - fx) * fy;
        float w11 = fx * fy;
        float[] data = buffer.Data;

        return (
            (data[i00] * w00) + (data[i10] * w10) + (data[i01] * w01) + (data[i11] * w11),
            (data[i00 + 1] * w00) + (data[i10 + 1] * w10) + (data[i01 + 1] * w01) + (data[i11 + 1] * w11),
            (data[i00 + 2] * w00) + (data[i10 + 2] * w10) + (data[i01 + 2] * w01) + (data[i11 + 2] * w11),
            (data[i00 + 3] * w00) + (data[i10 + 3] * w10) + (data[i01 + 3] * w01) + (data[i11 + 3] * w11));
    }

    private static void StampPixel(
        MarkingBuffer destination,
        int x,
        int y,
        float sourceR,
        float sourceG,
        float sourceB,
        float coverage,
        in MarkingStamp stamp,
        MarkingChannel channel)
    {
        int index = ((y * destination.Width) + x) * 4;
        float[] data = destination.Data;

        switch (channel)
        {
            case MarkingChannel.Albedo:
                {
                    // The ink colour is tinted in linear space, blended in linear space, then laid
                    // over the base behind the stamp's coverage. Alpha carries no meaning for
                    // albedo and is preserved.
                    for (int component = 0; component < 3; component++)
                    {
                        float ink = component switch
                        {
                            0 => sourceR * stamp.TintR,
                            1 => sourceG * stamp.TintG,
                            _ => sourceB * stamp.TintB,
                        };
                        float baseValue = data[index + component];
                        float blended = Math.Clamp(MarkingMath.Blend(baseValue, Math.Clamp(ink, 0f, 1f), stamp.Blend), 0f, 1f);
                        data[index + component] = baseValue + ((blended - baseValue) * coverage);
                    }

                    break;
                }

            case MarkingChannel.Normal:
                {
                    // Whiteout on the tangent axes: the ink's deviation from flat is added to the
                    // base tilt, and coverage mixes the tilt toward that deviation-scaled result.
                    // Blue carries packed ambient occlusion and is never written.
                    float inkX = Math.Clamp(sourceR, 0f, 1f);
                    float inkY = Math.Clamp(sourceG, 0f, 1f);
                    float baseX = data[index];
                    float baseY = data[index + 1];
                    float blendedX = baseX + inkX - 0.5f;
                    float blendedY = baseY + inkY - 0.5f;
                    data[index] = Math.Clamp(baseX + ((blendedX - baseX) * coverage), 0f, 1f);
                    data[index + 1] = Math.Clamp(baseY + ((blendedY - baseY) * coverage), 0f, 1f);
                    break;
                }

            case MarkingChannel.Roughness:
                {
                    // Roughness is a scalar packed into green; the blend mode applies to that
                    // scalar and every other component is preserved.
                    float ink = Math.Clamp(sourceG, 0f, 1f);
                    float baseValue = data[index + 1];
                    float blended = Math.Clamp(MarkingMath.Blend(baseValue, ink, stamp.Blend), 0f, 1f);
                    data[index + 1] = baseValue + ((blended - baseValue) * coverage);
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown marking channel.");
        }
    }

    private static (float X, float Y) ComputeCentrePixels(int width, int height, in MarkingPlacement placement)
        => (placement.PositionU * width, placement.PositionV * height);

    private static (float X, float Y) ComputeExtentPixels(int width, int height, in MarkingPlacement placement)
        => (placement.ScaleU * width, placement.ScaleV * height);

    /// <summary>
    /// Inclusive integer pixel bounds of a stamp within a destination buffer.
    /// </summary>
    /// <param name="MinX">First covered column.</param>
    /// <param name="MinY">First covered row.</param>
    /// <param name="MaxX">Last covered column.</param>
    /// <param name="MaxY">Last covered row.</param>
    public readonly record struct StampBounds(int MinX, int MinY, int MaxX, int MaxY)
    {
        /// <summary>
        /// Gets the empty bounds, used when the stamp rectangle misses the buffer entirely.
        /// </summary>
        public static StampBounds Empty => new(0, 0, -1, -1);

        /// <summary>
        /// Gets a value indicating whether the bounds cover at least one pixel.
        /// </summary>
        public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    }
}
