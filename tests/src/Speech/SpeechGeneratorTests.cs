using System.Buffers.Binary;
using System.Text;
using AlleyCat.Speech.Generation;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for speech-generator WAV normalisation behaviour.
/// </summary>
public sealed class SpeechGeneratorTests
{
    /// <summary>
    /// Leaving the target sample rate disabled must preserve current generator output unchanged.
    /// </summary>
    [Fact]
    public void NormaliseGeneratedAudio_WhenTargetSampleRateDisabled_ReturnsOriginalBytes()
    {
        byte[] waveBytes = CreateWaveFileBytes([0x10, 0x00, 0x20, 0x00], sampleRate: 8000, channelCount: 1, bitsPerSample: 16);

        byte[] normalised = SpeechGenerator.NormaliseGeneratedAudio(waveBytes, targetSampleRate: 0);

        Assert.Same(waveBytes, normalised);
    }

    /// <summary>
    /// Supported PCM WAV audio must be resampled to the configured target rate before downstream consumers receive it.
    /// </summary>
    [Fact]
    public void NormaliseGeneratedAudio_WithDifferentWaveSampleRate_ResamplesToTargetRate()
    {
        byte[] waveBytes = CreateWaveFileBytes([0x00, 0x00, 0x10, 0x00, 0x20, 0x00, 0x30, 0x00], sampleRate: 8000, channelCount: 1, bitsPerSample: 16);

        byte[] normalised = SpeechGenerator.NormaliseGeneratedAudio(waveBytes, targetSampleRate: 16000);
        WaveData wave = ParseWaveFile(normalised);

        Assert.Equal(16000, wave.SampleRate);
        Assert.Equal((short)1, wave.ChannelCount);
        Assert.Equal((short)16, wave.BitsPerSample);
        Assert.Equal(16, wave.PcmData.Length);
        Assert.NotEqual(waveBytes, normalised);
    }

    private static WaveData ParseWaveFile(byte[] waveBytes)
    {
        Assert.True(HasAscii(waveBytes, 0, "RIFF"));
        Assert.True(HasAscii(waveBytes, 8, "WAVE"));

        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(waveBytes.AsSpan(24, 4));
        short channelCount = BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(22, 2));
        short bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(waveBytes.AsSpan(34, 2));
        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(waveBytes.AsSpan(40, 4));
        byte[] pcmData = waveBytes.AsSpan(44, dataLength).ToArray();

        return new WaveData(sampleRate, channelCount, bitsPerSample, pcmData);
    }

    private static bool HasAscii(IReadOnlyList<byte> data, int offset, string text)
    {
        if (offset < 0 || (offset + text.Length) > data.Count)
        {
            return false;
        }

        for (int index = 0; index < text.Length; index++)
        {
            if (data[offset + index] != text[index])
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] CreateWaveFileBytes(byte[] data, int sampleRate, short channelCount, short bitsPerSample)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        short blockAlign = (short)(channelCount * bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + data.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        return stream.ToArray();
    }

    private sealed record WaveData(int SampleRate, short ChannelCount, short BitsPerSample, byte[] PcmData);
}
