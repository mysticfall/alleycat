namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Bounded allocation-free accumulator that downmixes stereo float frames to mono PCM16.
/// </summary>
internal sealed class PCMAudioAccumulator
{
    private const short OutputChannelCount = 1;
    private const int InputChannelCount = 2;
    private const int BytesPerFrame = OutputChannelCount * sizeof(short);
    private readonly byte[] _buffer;
    private bool _completed;

    public PCMAudioAccumulator(int maximumFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrames);
        _buffer = new byte[checked(maximumFrames * BytesPerFrame)];
    }

    public int FrameCount
    {
        get; private set;
    }

    public int MaximumFrames => _buffer.Length / BytesPerFrame;

    public int RemainingFrames => MaximumFrames - FrameCount;

    public bool AppendStereoFrame(float left, float right)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (FrameCount >= MaximumFrames)
        {
            return false;
        }

        int offset = FrameCount * BytesPerFrame;
        WriteSample(offset, Downmix(left, right));
        FrameCount++;
        return true;
    }

    public int AppendInterleavedStereo(ReadOnlySpan<float> samples)
    {
        if (samples.Length % InputChannelCount != 0)
        {
            throw new ArgumentException("Stereo samples must contain complete left/right pairs.", nameof(samples));
        }

        int framesToAppend = Math.Min(samples.Length / InputChannelCount, RemainingFrames);
        for (int frame = 0; frame < framesToAppend; frame++)
        {
            _ = AppendStereoFrame(
                samples[frame * InputChannelCount],
                samples[(frame * InputChannelCount) + 1]);
        }

        return framesToAppend;
    }

    public RecordedAudioData Complete(int sampleRate)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _completed = true;
        return new RecordedAudioData(
            _buffer,
            FrameCount * BytesPerFrame,
            sampleRate,
            OutputChannelCount,
            takeOwnership: true);
    }

    private static float Downmix(float left, float right)
    {
        float mono = (left + right) * 0.5f;
        return float.IsNaN(mono) ? 0f : Math.Clamp(mono, -1f, 1f);
    }

    private void WriteSample(int offset, float sample)
    {
        short pcm = sample <= -1f ? short.MinValue : (short)MathF.Round(sample * short.MaxValue);
        _buffer[offset] = (byte)pcm;
        _buffer[offset + 1] = (byte)(pcm >> 8);
    }
}
