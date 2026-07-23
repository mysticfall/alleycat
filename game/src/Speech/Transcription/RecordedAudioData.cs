namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Immutable managed PCM audio passed from Godot capture to transcription backends.
/// </summary>
public sealed class RecordedAudioData
{
    private readonly byte[] _pcmData;
    private readonly int _pcmDataLength;

    /// <summary>
    /// Creates a managed recording and takes an immutable copy of its complete PCM16 frames.
    /// </summary>
    public RecordedAudioData(ReadOnlySpan<byte> pcmData, int sampleRate, short channelCount)
        : this(CopyCompleteFrames(pcmData, channelCount), pcmData.Length, sampleRate, channelCount, takeOwnership: true)
    {
    }

    internal RecordedAudioData(
        byte[] pcmData,
        int pcmDataLength,
        int sampleRate,
        short channelCount,
        bool takeOwnership)
    {
        ArgumentNullException.ThrowIfNull(pcmData);
        ArgumentOutOfRangeException.ThrowIfNegative(pcmDataLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pcmDataLength, pcmData.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        int bytesPerFrame = checked(channelCount * sizeof(short));
        if (pcmDataLength % bytesPerFrame != 0)
        {
            throw new ArgumentException(
                "PCM16 data must contain complete frames for the specified channel count.",
                nameof(pcmDataLength));
        }

        _pcmData = takeOwnership ? pcmData : [.. pcmData.AsSpan(0, pcmDataLength)];
        _pcmDataLength = pcmDataLength;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }

    /// <summary>
    /// Signed little-endian 16-bit PCM samples.
    /// </summary>
    public ReadOnlyMemory<byte> PCMData => _pcmData.AsMemory(0, _pcmDataLength);

    /// <summary>
    /// Number of audio frames per second.
    /// </summary>
    public int SampleRate
    {
        get;
    }

    /// <summary>
    /// Number of interleaved channels in each frame.
    /// </summary>
    public short ChannelCount
    {
        get;
    }

    /// <summary>
    /// Number of captured audio frames, each containing one sample per channel.
    /// </summary>
    public int FrameCount => _pcmDataLength / (ChannelCount * sizeof(short));

    private static byte[] CopyCompleteFrames(ReadOnlySpan<byte> pcmData, short channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        int bytesPerFrame = checked(channelCount * sizeof(short));
        return pcmData.Length % bytesPerFrame == 0
            ? pcmData.ToArray()
            : throw new ArgumentException(
                "PCM16 data must contain complete frames for the specified channel count.",
                nameof(pcmData));
    }
}
