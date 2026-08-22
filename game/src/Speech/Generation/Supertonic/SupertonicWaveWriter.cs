// Adapted from the Supertonic C# reference implementation (csharp/Helper.cs, WriteWavFile),
// Copyright (c) 2025 Supertone Inc. Licensed under the MIT License.
// https://github.com/supertone-inc/supertonic
using System.Text;

namespace AlleyCat.Speech.Generation.Supertonic;

/// <summary>
/// Encodes float32 audio samples into 16-bit PCM mono RIFF/WAVE byte containers.
/// </summary>
public static class SupertonicWaveWriter
{
    private const short PcmFormatCode = 1;
    private const short ChannelCount = 1;
    private const short BitsPerSample = 16;
    private const int FmtChunkSize = 16;
    private const int WaveHeaderSizeBytes = 36;

    /// <summary>
    /// Encodes mono samples into a RIFF/WAVE container with 16-bit PCM data.
    /// </summary>
    /// <param name="samples">Float samples in the range [-1, 1]; out-of-range values are clamped.</param>
    /// <param name="sampleRate">Sample rate of the supplied samples, in hertz.</param>
    /// <returns>The complete WAV file bytes, ready for downstream normalisation or playback.</returns>
    public static byte[] Write(IReadOnlyList<float> samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
        {
            throw new InvalidOperationException($"Supertonic WAV sample rate must be greater than zero. Got {sampleRate}.");
        }

        if (samples.Count == 0)
        {
            throw new InvalidOperationException("Supertonic WAV encoding requires at least one audio sample.");
        }

        short blockAlign = ChannelCount * (BitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;
        int dataSizeBytes = samples.Count * (BitsPerSample / 8);

        using MemoryStream stream = new(WaveHeaderSizeBytes + 8 + dataSizeBytes);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8);
        writer.Write(WaveHeaderSizeBytes + dataSizeBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(FmtChunkSize);
        writer.Write(PcmFormatCode);
        writer.Write(ChannelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(BitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataSizeBytes);

        foreach (float sample in samples)
        {
            writer.Write(FloatToPcm16(sample));
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static short FloatToPcm16(float sample)
    {
        float clamped = Math.Clamp(sample, -1f, 1f);
        return (short)(clamped * 32767f);
    }
}
