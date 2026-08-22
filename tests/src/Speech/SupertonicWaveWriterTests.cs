using System.Buffers.Binary;
using AlleyCat.Speech.Generation.Supertonic;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for Supertonic float32-to-PCM16 WAV encoding.
/// </summary>
public sealed class SupertonicWaveWriterTests
{
    /// <summary>
    /// Written containers must parse back as canonical RIFF/WAVE with 16-bit PCM mono payload at the given rate.
    /// </summary>
    [Fact]
    public void Write_FloatSamples_ProducesCanonicalPcm16MonoWaveContainer()
    {
        float[] samples = [0f, 1f, -1f, 0.5f, -0.5f];

        byte[] waveBytes = SupertonicWaveWriter.Write(samples, sampleRate: 44100);

        Assert.Equal(44 + (samples.Length * 2), waveBytes.Length);
        Assert.True(HasAscii(waveBytes, 0, "RIFF"));
        Assert.True(HasAscii(waveBytes, 8, "WAVE"));
        Assert.True(HasAscii(waveBytes, 12, "fmt "));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(waveBytes.AsSpan(16, 4)));
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(20, 2)));
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(22, 2)));
        Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(waveBytes.AsSpan(24, 4)));
        Assert.Equal(88200, BinaryPrimitives.ReadInt32LittleEndian(waveBytes.AsSpan(28, 4)));
        Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(32, 2)));
        Assert.Equal((short)16, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(34, 2)));
        Assert.True(HasAscii(waveBytes, 36, "data"));
        Assert.Equal(samples.Length * 2, BinaryPrimitives.ReadInt32LittleEndian(waveBytes.AsSpan(40, 4)));
        Assert.Equal(36 + (samples.Length * 2), BinaryPrimitives.ReadInt32LittleEndian(waveBytes.AsSpan(4, 4)));
    }

    /// <summary>
    /// Float samples must convert to scaled little-endian PCM16 values.
    /// </summary>
    [Fact]
    public void Write_FloatSamples_EncodesScaledLittleEndianPcm16Values()
    {
        byte[] waveBytes = SupertonicWaveWriter.Write([0f, 1f, -1f, 0.5f], sampleRate: 44100);

        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(44, 2)));
        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(46, 2)));
        Assert.Equal(-32767, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(48, 2)));
        Assert.Equal(16383, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(50, 2)));
    }

    /// <summary>
    /// Out-of-range floats must clamp into the [-1, 1] range before PCM16 scaling.
    /// </summary>
    [Fact]
    public void Write_OutOfRangeSamples_ClampBeforePcm16Scaling()
    {
        byte[] waveBytes = SupertonicWaveWriter.Write([2f, 1.5f, -1.5f, -3f], sampleRate: 44100);

        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(44, 2)));
        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(46, 2)));
        Assert.Equal(-32767, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(48, 2)));
        Assert.Equal(-32767, BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(50, 2)));
    }

    /// <summary>
    /// Non-default sample rates must be reflected in both the sample-rate and byte-rate header fields.
    /// </summary>
    [Fact]
    public void Write_CustomSampleRate_ReportsRateAndByteRateInHeader()
    {
        byte[] waveBytes = SupertonicWaveWriter.Write([0.25f], sampleRate: 16000);

        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(waveBytes.AsSpan(24, 4)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(waveBytes.AsSpan(28, 4)));
    }

    /// <summary>
    /// Non-positive sample rates must be rejected.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Write_NonPositiveSampleRate_Throws(int sampleRate)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SupertonicWaveWriter.Write([0.5f], sampleRate));

        Assert.Contains("sample rate must be greater than zero", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Empty sample buffers must be rejected so no zero-length data chunks are emitted.
    /// </summary>
    [Fact]
    public void Write_EmptySamples_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SupertonicWaveWriter.Write([], sampleRate: 44100));

        Assert.Contains("at least one audio sample", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Null sample buffers must be rejected.
    /// </summary>
    [Fact]
    public void Write_NullSamples_Throws() => _ = Assert.Throws<ArgumentNullException>(() => SupertonicWaveWriter.Write(null!, sampleRate: 44100));

    private static bool HasAscii(byte[] data, int offset, string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (data[offset + index] != text[index])
            {
                return false;
            }
        }

        return true;
    }
}
