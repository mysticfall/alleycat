using System.Buffers.Binary;

namespace AlleyCat.Speech.LipSync;

/// <summary>
/// Resamples mono 16-bit PCM audio between sample rates.
/// </summary>
internal static class Pcm16Resampler
{
    /// <summary>
    /// Resamples mono PCM16 audio data using linear interpolation, rounding interpolated samples away from zero and
    /// clamping them to the 16-bit range.
    /// </summary>
    /// <param name="pcmData">Mono 16-bit PCM audio bytes to resample.</param>
    /// <param name="sourceSampleRate">Sample rate of <paramref name="pcmData" />.</param>
    /// <param name="targetSampleRate">Sample rate to resample to.</param>
    /// <returns>Resampled mono PCM16 audio bytes.</returns>
    internal static byte[] ResampleMonoPcm16(byte[] pcmData, int sourceSampleRate, int targetSampleRate)
    {
        if (sourceSampleRate <= 0)
        {
            throw new InvalidOperationException(
                $"Audio resampling failed: source sample rate must be greater than zero. Got {sourceSampleRate}.");
        }

        if (targetSampleRate <= 0)
        {
            throw new InvalidOperationException(
                $"Audio resampling failed: target sample rate must be greater than zero. Got {targetSampleRate}.");
        }

        int bytesPerSample = sizeof(short);
        if (pcmData.Length < bytesPerSample)
        {
            throw new InvalidOperationException("Audio resampling failed: PCM data contained no audio frames.");
        }

        int sourceSampleCount = pcmData.Length / bytesPerSample;
        int targetSampleCount = Math.Max(
            1,
            (int)Math.Round(sourceSampleCount * (double)targetSampleRate / sourceSampleRate, MidpointRounding.AwayFromZero));
        byte[] output = new byte[targetSampleCount * bytesPerSample];

        for (int targetSampleIndex = 0; targetSampleIndex < targetSampleCount; targetSampleIndex++)
        {
            double sourcePosition = targetSampleIndex * (double)sourceSampleRate / targetSampleRate;
            int sourceSampleIndex = Math.Min((int)Math.Floor(sourcePosition), sourceSampleCount - 1);
            int nextSourceSampleIndex = Math.Min(sourceSampleIndex + 1, sourceSampleCount - 1);
            double sampleFraction = sourcePosition - sourceSampleIndex;

            short sourceSample = BinaryPrimitives.ReadInt16LittleEndian(
                pcmData.AsSpan(sourceSampleIndex * bytesPerSample, bytesPerSample));
            short nextSourceSample = BinaryPrimitives.ReadInt16LittleEndian(
                pcmData.AsSpan(nextSourceSampleIndex * bytesPerSample, bytesPerSample));
            int interpolatedSample = (int)Math.Round(
                sourceSample + ((nextSourceSample - sourceSample) * sampleFraction),
                MidpointRounding.AwayFromZero);
            short clampedSample = (short)Math.Clamp(interpolatedSample, short.MinValue, short.MaxValue);

            BinaryPrimitives.WriteInt16LittleEndian(
                output.AsSpan(targetSampleIndex * bytesPerSample, bytesPerSample),
                clampedSample);
        }

        return output;
    }
}
