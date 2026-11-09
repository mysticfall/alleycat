using AlleyCat.Markings;
using Xunit;

namespace AlleyCat.Tests.Markings;

/// <summary>
/// Godot-free coverage of the marking compositor maths: sRGB transfer, the blend-mode family,
/// the channel packing conventions (normal whiteout with packed-AO preservation, roughness on
/// green), placement transforms with bilinear sampling, bbox limiting, and layer ordering.
/// </summary>
public sealed class MarkingCompositorTests
{
    private const float Tolerance = 1e-4f;

    // A solid opaque source of the given colour.
    private static MarkingBuffer SolidSource(int width, int height, float r, float g, float b, float a = 1f)
        => new(width, height, r, g, b, a);

    private static MarkingPlacement CentrePlacement(float scaleU, float scaleV, float rotationDegrees = 0f)
        => new(0.5f, 0.5f, rotationDegrees, scaleU, scaleV);

    private static MarkingStamp Stamp(
        MarkingBuffer source,
        float scaleU,
        float scaleV,
        float rotationDegrees = 0f,
        float tintR = 1f,
        float tintG = 1f,
        float tintB = 1f,
        float strength = 1f,
        MarkingBlendMode blend = MarkingBlendMode.Multiply,
        float positionU = 0.5f,
        float positionV = 0.5f)
        => new(
            source,
            new MarkingPlacement(positionU, positionV, rotationDegrees, scaleU, scaleV),
            tintR,
            tintG,
            tintB,
            strength,
            blend);

    /// <summary>
    /// sRGB decode/encode transfer-function facts.
    /// </summary>
    public sealed class SrgbTransferTests
    {
        /// <summary>
        /// Decoding matches the standard sRGB EOTF at known points including both knees.
        /// </summary>
        /// <summary>
        /// Encoding inverts decoding across the full range.
        /// </summary>
        /// <summary>
        /// Out-of-range inputs are clamped, never extrapolated.
        /// </summary>
        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(1f, 1f)]
        [InlineData(0.5f, 0.21404114f)]
        [InlineData(0.04045f, 0.0031308f)]
        [InlineData(0.25f, 0.05087609f)]
        public void SrgbToLinear_MatchesStandardCurve(float srgb, float expectedLinear)
        {
            float actual = MarkingMath.SrgbToLinear(srgb);

            AssertApproximate(expectedLinear, actual);
        }

        /// <inheritdoc/>
        [Fact]
        public void LinearToSrgb_InvertsDecode()
        {
            foreach (float value in new[] { 0f, 0.02f, 0.1f, 0.21404114f, 0.5f, 0.75f, 0.98f, 1f })
            {
                AssertApproximate(value, MarkingMath.LinearToSrgb(MarkingMath.SrgbToLinear(value)));
            }
        }

        /// <summary>
        /// Multiply darkens by the ink factor; white is the identity ink.
        /// </summary>
        /// <summary>
        /// Overlay uses the 2ab form below half and the screen form above; mid grey is identity.
        /// </summary>
        /// <summary>
        /// SoftLight follows the W3C pegtop curve; mid grey is identity.
        /// </summary>
        /// <summary>
        /// Darken keeps the minimum; white is the identity ink.
        /// </summary>
        /// <summary>
        /// Out-of-range inputs are clamped, never extrapolated.
        /// </summary>
        [Fact]
        public void Transfer_Functions_ClampOutOfRangeValues()
        {
            Assert.Equal(0f, MarkingMath.SrgbToLinear(-0.5f));
            AssertApproximate(1f, MarkingMath.SrgbToLinear(2f));
            Assert.Equal(0f, MarkingMath.LinearToSrgb(-0.5f));
            AssertApproximate(1f, MarkingMath.LinearToSrgb(2f));
        }
    }

    /// <summary>
    /// The linear-space blend-mode family.
    /// </summary>
    public sealed class BlendTests
    {
        /// <inheritdoc/>
        [Theory]
        [InlineData(0.8f, 0.5f, 0.4f)]
        [InlineData(0.8f, 1f, 0.8f)]
        [InlineData(0f, 0.5f, 0f)]
        public void Multiply_BlendsBaseWithInk(float baseValue, float ink, float expected)
            => AssertApproximate(expected, MarkingMath.Blend(baseValue, ink, MarkingBlendMode.Multiply));

        /// <inheritdoc/>
        [Theory]
        [InlineData(0.25f, 0.25f, 0.125f)]
        [InlineData(0.75f, 0.75f, 0.875f)]
        [InlineData(0.2f, 0.5f, 0.2f)]
        [InlineData(0.8f, 0.5f, 0.8f)]
        public void Overlay_BlendsByBaseLuminance(float baseValue, float ink, float expected)
            => AssertApproximate(expected, MarkingMath.Blend(baseValue, ink, MarkingBlendMode.Overlay));

        /// <inheritdoc/>
        [Theory]
        [InlineData(0.25f, 0f, 0.0625f)]
        [InlineData(0.25f, 1f, 0.5f)]
        [InlineData(0.6f, 0.5f, 0.6f)]
        [InlineData(0.04f, 1f, 0.2f)]
        public void SoftLight_UsesPegtopCurve(float baseValue, float ink, float expected)
            => AssertApproximate(expected, MarkingMath.Blend(baseValue, ink, MarkingBlendMode.SoftLight));

        /// <summary>
        /// A tilted ink on a flat base whiteouts the tangent axes on the covered pixels only.
        /// </summary>
        /// <summary>
        /// The packed AO in blue is preserved bit-exactly under the stamp, whatever the ink blue holds.
        /// </summary>
        /// <summary>
        /// Half strength scales the tilt deviation halfway.
        /// </summary>
        /// <summary>
        /// Half source alpha mixes halfway between base and whiteout.
        /// </summary>
        /// <summary>
        /// Roughness stamps blend the green scalar and preserve every other component.
        /// </summary>
        /// <summary>
        /// Darken-mode roughness ink lowers roughness only where the ink is rougher.
        /// </summary>
        /// <summary>
        /// Multiply with a channel tint darkens exactly the tinted channels; alpha is preserved.
        /// </summary>
        /// <summary>
        /// Half coverage blends halfway to the black multiply ink.
        /// </summary>
        /// <summary>
        /// Zero strength leaves the buffer untouched.
        /// </summary>
        /// <summary>
        /// A centred rectangle covers exactly its bounded pixel set.
        /// </summary>
        /// <summary>
        /// A stamp that misses the buffer is a bounded no-op with empty bounds.
        /// </summary>
        /// <summary>
        /// A ninety-degree rotation swaps the rectangle axes and maps the source halves accordingly.
        /// </summary>
        /// <summary>
        /// Continuous sampling bilinearly averages source texels at the stamp centre.
        /// </summary>
        /// <summary>
        /// A stamp hanging off an edge covers only the visible part without wrap-around.
        /// </summary>
        /// <summary>
        /// Layers composite in authored order, which changes the result for order-sensitive blends.
        /// </summary>
        /// <summary>
        /// Markings inside one layer composite in authored order.
        /// </summary>
        /// <summary>
        /// Disabled layers are skipped entirely.
        /// </summary>
        /// <summary>
        /// The same layer plan list drives every channel, so layer order spans channels.
        /// </summary>
        [Theory]
        [InlineData(0.3f, 0.8f, 0.3f)]
        [InlineData(0.9f, 0.2f, 0.2f)]
        [InlineData(0.4f, 1f, 0.4f)]
        public void Darken_KeepsDarkerValue(float baseValue, float ink, float expected)
            => AssertApproximate(expected, MarkingMath.Blend(baseValue, ink, MarkingBlendMode.Darken));
    }

    /// <summary>
    /// Normal-channel packing: whiteout on the tangent axes with packed-AO preservation.
    /// </summary>
    public sealed class NormalChannelTests
    {
        /// <inheritdoc/>
        [Fact]
        public void Stamp_FlatBaseWithTiltedInk_WhiteoutsTangentAxesOnly()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.5f, 0.8f, 1f);
            MarkingBuffer source = SolidSource(2, 2, 0.8f, 0.3f, 0.9f, 1f);

            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.5f, 0.5f),
                MarkingChannel.Normal);

            // Whiteout: base.xy + ink.xy - 0.5 on the covered centre pixels.
            AssertApproximate(0.8f, destination.GetR(1, 1));
            AssertApproximate(0.3f, destination.GetG(1, 1));
            AssertApproximate(0.8f, destination.GetR(2, 2));
            AssertApproximate(0.3f, destination.GetG(2, 2));
            // Outside the stamp rectangle the base stays flat.
            AssertApproximate(0.5f, destination.GetR(0, 0));
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_PackedAoInBlue_IsPreservedExactlyUnderTheStamp()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.5f, 0.8f, 1f);
            // The ink's blue channel must be irrelevant to the result's blue channel.
            MarkingBuffer source = SolidSource(2, 2, 0.8f, 0.3f, 0.1f, 1f);

            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.5f, 0.5f),
                MarkingChannel.Normal);

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    Assert.Equal(0.8f, destination.GetB(x, y));
                }
            }
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_HalfStrength_ScalesTiltDeviation()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.5f, 0.8f, 1f);
            MarkingBuffer source = SolidSource(2, 2, 0.9f, 0.5f, 1f, 1f);

            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.5f, 0.5f, strength: 0.5f),
                MarkingChannel.Normal);

            // Coverage 0.5 toward whiteout mixes the tilt halfway: 0.5 + 0.5 * (0.9 - 0.5).
            AssertApproximate(0.7f, destination.GetR(1, 1));
            AssertApproximate(0.5f, destination.GetG(1, 1));
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_HalfAlphaCoverage_MixesHalfway()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.5f, 0.8f, 1f);
            MarkingBuffer source = SolidSource(2, 2, 0.9f, 0.5f, 1f, 0.5f);

            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.5f, 0.5f),
                MarkingChannel.Normal);

            AssertApproximate(0.7f, destination.GetR(1, 1));
        }
    }

    /// <summary>
    /// Roughness-channel packing: blends the green scalar only.
    /// </summary>
    public sealed class RoughnessChannelTests
    {
        /// <inheritdoc/>
        [Fact]
        public void Stamp_MultipliesGreenOnly()
        {
            MarkingBuffer destination = new(4, 4, 0.1f, 0.5f, 0.9f, 0.25f);
            MarkingBuffer source = SolidSource(2, 2, 1f, 0.2f, 1f, 1f);

            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.5f, 0.5f),
                MarkingChannel.Roughness);

            AssertApproximate(0.1f, destination.GetG(1, 1));
            // Every other component is preserved exactly.
            Assert.Equal(0.1f, destination.GetR(1, 1));
            Assert.Equal(0.9f, destination.GetB(1, 1));
            Assert.Equal(0.25f, destination.GetA(1, 1));
            // Untouched pixel keeps its roughness.
            Assert.Equal(0.5f, destination.GetG(0, 0));
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_DarkenMode_CanLowerRoughnessOnly()
        {
            MarkingBuffer destination = new(4, 4, 0f, 0.5f, 0f, 1f);
            MarkingBuffer inkAbove = SolidSource(2, 2, 1f, 0.9f, 1f, 1f);
            MarkingBuffer inkBelow = SolidSource(2, 2, 1f, 0.1f, 1f, 1f);

            MarkingCompositor.Stamp(destination, Stamp(inkAbove, 0.5f, 0.5f, blend: MarkingBlendMode.Darken), MarkingChannel.Roughness);
            Assert.Equal(0.5f, destination.GetG(1, 1));

            MarkingCompositor.Stamp(destination, Stamp(inkBelow, 0.5f, 0.5f, blend: MarkingBlendMode.Darken, positionV: 0.25f), MarkingChannel.Roughness);
            AssertApproximate(0.1f, destination.GetG(1, 1));
        }
    }

    /// <summary>
    /// Albedo-channel packing: linear-space tinting, blending, and coverage.
    /// </summary>
    public sealed class AlbedoChannelTests
    {
        /// <inheritdoc/>
        [Fact]
        public void Stamp_MultiplyWithTint_DarkensLinearRgbBehindCoverage()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.5f, 0.5f, 1f);
            MarkingBuffer source = SolidSource(2, 2, 1f, 1f, 1f, 1f);

            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.5f, 0.5f, tintR: 1f, tintG: 0f, tintB: 0f),
                MarkingChannel.Albedo);

            AssertApproximate(0.5f, destination.GetR(1, 1));
            AssertApproximate(0f, destination.GetG(1, 1));
            AssertApproximate(0f, destination.GetB(1, 1));
            Assert.Equal(1f, destination.GetA(1, 1));
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_HalfCoverage_BlendsHalfway()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.5f, 0.5f, 1f);
            MarkingBuffer source = SolidSource(2, 2, 1f, 1f, 1f, 0.5f);

            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.5f, 0.5f, tintR: 0f, tintG: 0f, tintB: 0f),
                MarkingChannel.Albedo);

            AssertApproximate(0.25f, destination.GetR(1, 1));
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_ZeroStrength_IsACompleteNoOp()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.25f, 0.75f, 1f);
            MarkingBuffer expected = destination.Duplicate();

            MarkingCompositor.Stamp(
                destination,
                Stamp(SolidSource(2, 2, 1f, 0f, 0f, 1f), 1f, 1f, strength: 0f),
                MarkingChannel.Albedo);

            Assert.Equal(expected.Data, destination.Data);
        }
    }

    /// <summary>
    /// Placement transforms: rectangle coverage, rotation, edges, and bilinear sampling.
    /// </summary>
    public sealed class PlacementTests
    {
        /// <inheritdoc/>
        [Fact]
        public void Stamp_CentredRectangle_CoversExactlyTheBoundedPixels()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.5f, 0.5f, 1f);
            MarkingBuffer source = SolidSource(2, 2, 1f, 1f, 1f, 1f);

            MarkingCompositor.StampBounds bounds = MarkingCompositor.ComputeStampBounds(
                4, 4, CentrePlacement(0.5f, 0.5f));
            Assert.Equal(new MarkingCompositor.StampBounds(1, 1, 2, 2), bounds);

            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.5f, 0.5f, tintR: 0f, tintG: 0f, tintB: 0f),
                MarkingChannel.Albedo);

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    bool covered = x is 1 or 2 && y is 1 or 2;
                    Assert.Equal(covered ? 0f : 0.5f, destination.GetR(x, y));
                }
            }
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_OffBuffer_IsABoundedNoOp()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.5f, 0.5f, 1f);
            MarkingBuffer expected = destination.Duplicate();

            MarkingCompositor.StampBounds bounds = MarkingCompositor.ComputeStampBounds(
                4, 4, new MarkingPlacement(2f, 2f, 0f, 0.2f, 0.2f));
            Assert.True(bounds.IsEmpty);

            MarkingCompositor.Stamp(
                destination,
                Stamp(SolidSource(2, 2, 0f, 0f, 0f, 1f), 0.2f, 0.2f, positionU: 2f, positionV: 2f),
                MarkingChannel.Albedo);

            Assert.Equal(expected.Data, destination.Data);
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_RotatedNinetyDegrees_SwapsTheRectangleAxes()
        {
            // Source: left half red, right half blue.
            MarkingBuffer source = new(2, 1, 1f, 0f, 0f, 1f);
            source.Data[4] = 0f;
            source.Data[6] = 1f;

            MarkingBuffer destination = new(8, 8, 0.5f, 0.5f, 0.5f, 1f);
            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.5f, 0.25f, rotationDegrees: 90f),
                MarkingChannel.Albedo);

            // Rotation by +90° maps the source's left (red) half to the top of the vertical
            // strip and the right (blue) half to the bottom. Bilinear sampling smears the
            // source halves by 12.5% at these pixel depths, so the expected values carry that
            // smear: top keeps most of red, bottom most of blue, and the horizontal band the
            // unrotated stamp would cover is untouched.
            AssertApproximate(0.5f * 0.875f, destination.GetR(3, 2));
            AssertApproximate(0f, destination.GetG(3, 2));
            AssertApproximate(0.5f * 0.125f, destination.GetR(4, 5));
            AssertApproximate(0.5f * 0.875f, destination.GetB(4, 5));
            Assert.Equal(0.5f, destination.GetR(2, 3));
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_ContinuousSampling_BlendsBilinearAcrossSourceTexels()
        {
            // One opaque texel in the corner of the source; every bilinear sample around the
            // stamp centre averages it with its transparent neighbours.
            MarkingBuffer source = new(2, 2, 1f, 1f, 1f, 0f);
            source.Data[3] = 1f;

            MarkingBuffer destination = new(5, 5, 0.5f, 0.5f, 0.5f, 1f);
            MarkingCompositor.Stamp(
                destination,
                Stamp(source, 0.4f, 0.4f, tintR: 0f, tintG: 0f, tintB: 0f),
                MarkingChannel.Albedo);

            // The centre pixel samples source (0.5, 0.5): the four texels average to 0.25 alpha,
            // so half-dark ink covers a quarter of the blend.
            AssertApproximate(0.5f * 0.75f, destination.GetR(2, 2));
        }

        /// <inheritdoc/>
        [Fact]
        public void Stamp_PartiallyOffEdge_ClampsToTheVisiblePart()
        {
            MarkingBuffer destination = new(4, 4, 0.5f, 0.5f, 0.5f, 1f);

            MarkingCompositor.Stamp(
                destination,
                Stamp(SolidSource(2, 2, 1f, 1f, 1f, 1f), 1f, 1f, tintR: 0f, tintG: 0f, tintB: 0f, positionU: 1f, positionV: 0.5f),
                MarkingChannel.Albedo);

            // The stamp's centre sits on the right edge: columns 2 and 3 are covered, the
            // columns that fell off the left edge are not, and no wrap-around occurs.
            for (int y = 0; y < 4; y++)
            {
                Assert.Equal(0.5f, destination.GetR(0, y));
                Assert.Equal(0.5f, destination.GetR(1, y));
                Assert.Equal(0f, destination.GetR(2, y));
                Assert.Equal(0f, destination.GetR(3, y));
            }
        }
    }

    /// <summary>
    /// Layer and marking ordering, disabled layers, and shared channel order.
    /// </summary>
    public sealed class LayerOrderingTests
    {
        private static MarkingBuffer CreateBase()
            => new(4, 4, 0.6f, 0.6f, 0.6f, 1f);

        private static MarkingStamp MultiplyHalfStamp()
            => Stamp(SolidSource(2, 2, 1f, 1f, 1f, 1f), 0.5f, 0.5f, tintR: 0.5f, tintG: 0.5f, tintB: 0.5f);

        private static MarkingStamp OverlayZeroStamp()
            => Stamp(SolidSource(2, 2, 1f, 1f, 1f, 1f), 0.5f, 0.5f, tintR: 0f, tintG: 0f, tintB: 0f, blend: MarkingBlendMode.Overlay);

        /// <inheritdoc/>
        [Fact]
        public void StampLayers_CompositesLayersInAuthoredOrder()
        {
            // Multiply-then-overlay: 0.6 -> 0.3 -> clamp(1 - 2 * 0.7 * 1) = 0.
            MarkingBuffer forward = CreateBase();
            MarkingCompositor.StampLayers(
                forward,
                [
                    new MarkingLayerPlan(true, [MultiplyHalfStamp()]),
                    new MarkingLayerPlan(true, [OverlayZeroStamp()]),
                ],
                MarkingChannel.Albedo);
            Assert.Equal(0f, forward.GetR(1, 1));

            // Overlay-then-multiply: 0.6 -> 1 - 2 * 0.4 * 1 = 0.2 -> 0.1.
            MarkingBuffer reverse = CreateBase();
            MarkingCompositor.StampLayers(
                reverse,
                [
                    new MarkingLayerPlan(true, [OverlayZeroStamp()]),
                    new MarkingLayerPlan(true, [MultiplyHalfStamp()]),
                ],
                MarkingChannel.Albedo);
            AssertApproximate(0.1f, reverse.GetR(1, 1));
        }

        /// <inheritdoc/>
        [Fact]
        public void StampLayers_CompositesMarkingsWithinALayerInOrder()
        {
            MarkingBuffer destination = CreateBase();
            MarkingCompositor.StampLayers(
                destination,
                [new MarkingLayerPlan(true, [MultiplyHalfStamp(), OverlayZeroStamp()])],
                MarkingChannel.Albedo);

            Assert.Equal(0f, destination.GetR(1, 1));
        }

        /// <inheritdoc/>
        [Fact]
        public void StampLayers_SkipsDisabledLayers()
        {
            MarkingBuffer destination = CreateBase();
            MarkingCompositor.StampLayers(
                destination,
                [
                    new MarkingLayerPlan(false, [OverlayZeroStamp()]),
                    new MarkingLayerPlan(true, [MultiplyHalfStamp()]),
                ],
                MarkingChannel.Albedo);

            AssertApproximate(0.3f, destination.GetR(1, 1));
        }

        /// <inheritdoc/>
        [Fact]
        public void StampLayers_DifferentChannelsShareLayerOrder()
        {
            // The same layer list drives every channel: the enabled multiply layer acts on
            // roughness green, the disabled layer never runs.
            MarkingBuffer roughness = new(4, 4, 0f, 0.8f, 0f, 1f);
            MarkingCompositor.StampLayers(
                roughness,
                [
                    new MarkingLayerPlan(false, [Stamp(SolidSource(2, 2, 1f, 0f, 1f, 1f), 0.5f, 0.5f)]),
                    new MarkingLayerPlan(true, [Stamp(SolidSource(2, 2, 1f, 0.25f, 1f, 1f), 0.5f, 0.5f)]),
                ],
                MarkingChannel.Roughness);

            AssertApproximate(0.2f, roughness.GetG(1, 1));
        }
    }

    private static void AssertApproximate(float expected, float actual)
        => Assert.True(
            MathF.Abs(expected - actual) < Tolerance,
            $"Expected approximately {expected} but found {actual} (tolerance {Tolerance}).");
}
