using System.Buffers.Binary;
using AlleyCat.Speech.LipSync;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the mono PCM16 resampler used by lip-sync inference normalisation.
/// </summary>
public sealed class Pcm16ResamplerTests
{
    /// <summary>
    /// Resampling between equal rates must reproduce the input samples unchanged.
    /// </summary>
    [Fact]
    public void ResampleMonoPcm16_WithEqualRates_ReturnsOriginalSamplesUnchanged()
    {
        byte[] pcmData = CreatePcm16Bytes([0x1000, -0x2000, 0x7FFF, short.MinValue]);

        byte[] resampled = Pcm16Resampler.ResampleMonoPcm16(pcmData, 16000, 16000);

        Assert.Equal(pcmData, resampled);
    }

    /// <summary>
    /// Upsampling must produce the rounded target sample count as reported by the output byte length.
    /// </summary>
    [Fact]
    public void ResampleMonoPcm16_WhenUpsampling_ProducesRoundedTargetSampleCount()
    {
        // Five samples doubled: round(5 * 16000 / 8000) = 10 samples.
        byte[] doubled = Pcm16Resampler.ResampleMonoPcm16(CreatePcm16Bytes(new short[5]), 8000, 16000);
        Assert.Equal(20, doubled.Length);

        // Three samples to 44100 Hz: round(3 * 44100 / 16000) = round(8.26875) = 8 samples.
        byte[] fractional = Pcm16Resampler.ResampleMonoPcm16(CreatePcm16Bytes(new short[3]), 16000, 44100);
        Assert.Equal(16, fractional.Length);
    }

    /// <summary>
    /// Downsampling must produce the rounded target sample count as reported by the output byte length.
    /// </summary>
    [Fact]
    public void ResampleMonoPcm16_WhenDownsampling_ProducesRoundedTargetSampleCount()
    {
        // Seven samples from 44100 Hz: round(7 * 16000 / 44100) = round(2.539...) = 3 samples.
        byte[] fractional = Pcm16Resampler.ResampleMonoPcm16(CreatePcm16Bytes(new short[7]), 44100, 16000);
        Assert.Equal(6, fractional.Length);

        // Nine samples from 48000 Hz: round(9 * 16000 / 48000) = 3 samples exactly.
        byte[] exact = Pcm16Resampler.ResampleMonoPcm16(CreatePcm16Bytes(new short[9]), 48000, 16000);
        Assert.Equal(6, exact.Length);
    }

    /// <summary>
    /// A single sample whose rounded target count would be zero must still produce at least one output sample.
    /// </summary>
    [Fact]
    public void ResampleMonoPcm16_WhenRoundedCountWouldBeZero_ProducesAtLeastOneSample()
    {
        // One sample from 44100 Hz: round(1 * 16000 / 44100) = round(0.362...) = 0, clamped to a minimum of 1.
        byte[] resampled = Pcm16Resampler.ResampleMonoPcm16(CreatePcm16Bytes([0x1234]), 44100, 16000);

        short[] samples = ReadPcm16Samples(resampled);
        short[] expectedSample = [0x1234];
        Assert.Equal(expectedSample, samples);
    }

    /// <summary>
    /// Upsampling must linearly interpolate between adjacent samples, repeating the final sample at the edge.
    /// </summary>
    [Fact]
    public void ResampleMonoPcm16_WhenUpsampling_LinearlyInterpolatesBetweenAdjacentSamples()
    {
        byte[] pcmData = CreatePcm16Bytes([0, 16, 32, 48]);

        byte[] resampled = Pcm16Resampler.ResampleMonoPcm16(pcmData, 8000, 16000);

        // Each source position j * 0.5 interpolates halfway between neighbours; the trailing edge clamps to the last sample.
        short[] expectedSamples = [0, 8, 16, 24, 32, 40, 48, 48];
        Assert.Equal(expectedSamples, ReadPcm16Samples(resampled));
    }

    /// <summary>
    /// Interpolated midpoints landing exactly on .5 must round away from zero in both amplitude directions.
    /// </summary>
    [Fact]
    public void ResampleMonoPcm16_WhenInterpolationLandsOnHalfSample_RoundsAwayFromZero()
    {
        byte[] positive = Pcm16Resampler.ResampleMonoPcm16(CreatePcm16Bytes([2, 3]), 8000, 16000);
        short[] expectedPositiveSamples = [2, 3, 3, 3];
        Assert.Equal(expectedPositiveSamples, ReadPcm16Samples(positive));

        byte[] negative = Pcm16Resampler.ResampleMonoPcm16(CreatePcm16Bytes([-2, -3]), 8000, 16000);
        short[] expectedNegativeSamples = [-2, -3, -3, -3];
        Assert.Equal(expectedNegativeSamples, ReadPcm16Samples(negative));
    }

    /// <summary>
    /// Interpolating between the extreme 16-bit amplitudes must stay within the short range without overflow.
    /// </summary>
    [Fact]
    public void ResampleMonoPcm16_WithExtremeSampleValues_StaysWithinShortRange()
    {
        byte[] pcmData = CreatePcm16Bytes([short.MinValue, short.MaxValue]);

        byte[] resampled = Pcm16Resampler.ResampleMonoPcm16(pcmData, 8000, 16000);

        // The midpoint of the extremes is -0.5, which rounds away from zero to -1; no sample may wrap around.
        short[] expectedSamples = [short.MinValue, -1, short.MaxValue, short.MaxValue];
        Assert.Equal(expectedSamples, ReadPcm16Samples(resampled));
    }

    /// <summary>
    /// Non-positive sample rates must fail fast with a descriptive error.
    /// </summary>
    [Fact]
    public void ResampleMonoPcm16_WithNonPositiveSampleRate_ThrowsInvalidOperationException()
    {
        byte[] pcmData = CreatePcm16Bytes([0, 16]);

        InvalidOperationException sourceException = Assert.Throws<InvalidOperationException>(
            () => Pcm16Resampler.ResampleMonoPcm16(pcmData, sourceSampleRate: 0, targetSampleRate: 16000));
        Assert.Contains("source sample rate", sourceException.Message, StringComparison.Ordinal);

        InvalidOperationException targetException = Assert.Throws<InvalidOperationException>(
            () => Pcm16Resampler.ResampleMonoPcm16(pcmData, sourceSampleRate: 8000, targetSampleRate: -1));
        Assert.Contains("target sample rate", targetException.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// PCM data shorter than one 16-bit frame must fail fast with a descriptive error.
    /// </summary>
    [Fact]
    public void ResampleMonoPcm16_WithDataShorterThanOneFrame_ThrowsInvalidOperationException()
    {
        InvalidOperationException emptyException = Assert.Throws<InvalidOperationException>(
            () => Pcm16Resampler.ResampleMonoPcm16([], 8000, 16000));
        Assert.Contains("no audio frames", emptyException.Message, StringComparison.Ordinal);

        _ = Assert.Throws<InvalidOperationException>(
            () => Pcm16Resampler.ResampleMonoPcm16([0x01], 8000, 16000));
    }

    private static byte[] CreatePcm16Bytes(short[] samples)
    {
        byte[] data = new byte[samples.Length * sizeof(short)];
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(sampleIndex * sizeof(short)),
                samples[sampleIndex]);
        }

        return data;
    }

    private static short[] ReadPcm16Samples(byte[] data)
    {
        short[] samples = new short[data.Length / sizeof(short)];
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            samples[sampleIndex] = BinaryPrimitives.ReadInt16LittleEndian(
                data.AsSpan(sampleIndex * sizeof(short)));
        }

        return samples;
    }
}
