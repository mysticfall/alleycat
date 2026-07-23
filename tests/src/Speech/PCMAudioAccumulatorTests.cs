using AlleyCat.Speech.Transcription;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for bounded float-to-PCM capture accumulation.
/// </summary>
public sealed class PCMAudioAccumulatorTests
{
    /// <summary>
    /// Stereo frames are averaged, clamped, and encoded as mono signed little-endian PCM16.
    /// </summary>
    [Fact]
    public void AppendInterleavedStereo_AveragesClampsAndConvertsToLittleEndianMonoPCM16()
    {
        PCMAudioAccumulator accumulator = new(maximumFrames: 4);

        int appended = accumulator.AppendInterleavedStereo([-2f, -2f, 2f, 2f, 1f, 0f, -1f, 0f]);
        RecordedAudioData recording = accumulator.Complete(sampleRate: 48000);

        Assert.Equal(4, appended);
        Assert.Equal([0x00, 0x80, 0xff, 0x7f, 0x00, 0x40, 0x00, 0xc0], recording.PCMData.ToArray());
    }

    /// <summary>
    /// Equal and opposite channels cancel without retaining either stereo sample.
    /// </summary>
    [Fact]
    public void AppendStereoFrame_WithOppositePhaseChannels_EncodesSilence()
    {
        PCMAudioAccumulator accumulator = new(maximumFrames: 1);

        Assert.True(accumulator.AppendStereoFrame(1f, -1f));

        Assert.Equal([0x00, 0x00], accumulator.Complete(sampleRate: 48000).PCMData.ToArray());
    }

    /// <summary>
    /// Non-finite downmix results have deterministic saturating or silent output.
    /// </summary>
    [Fact]
    public void AppendInterleavedStereo_WithNonFiniteValues_HandlesThemWithoutOverflow()
    {
        PCMAudioAccumulator accumulator = new(maximumFrames: 4);

        _ = accumulator.AppendInterleavedStereo(
            [float.PositiveInfinity, 1f, float.NegativeInfinity, -1f, float.NaN, 1f, float.PositiveInfinity, float.NegativeInfinity]);

        Assert.Equal(
            [0xff, 0x7f, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00],
            accumulator.Complete(sampleRate: 48000).PCMData.ToArray());
    }

    /// <summary>
    /// Samples beyond the configured frame bound are rejected without buffer growth.
    /// </summary>
    [Fact]
    public void AppendInterleavedStereo_MoreThanMaximumDuration_TruncatesWithoutGrowing()
    {
        PCMAudioAccumulator accumulator = new(maximumFrames: 2);

        int appended = accumulator.AppendInterleavedStereo([0f, 0f, 0.25f, -0.25f, 1f, 1f]);

        Assert.Equal(2, appended);
        Assert.Equal(2, accumulator.FrameCount);
        Assert.Equal(0, accumulator.RemainingFrames);
        Assert.False(accumulator.AppendStereoFrame(1f, 1f));
    }

    /// <summary>
    /// Independent accumulators prevent samples leaking between recording sessions.
    /// </summary>
    [Fact]
    public void Complete_UsingIndependentAccumulators_ProducesIndependentRecordings()
    {
        PCMAudioAccumulator firstAccumulator = new(maximumFrames: 2);
        _ = firstAccumulator.AppendStereoFrame(1f, 1f);
        RecordedAudioData first = firstAccumulator.Complete(sampleRate: 48000);

        PCMAudioAccumulator secondAccumulator = new(maximumFrames: 2);
        _ = secondAccumulator.AppendStereoFrame(-1f, -1f);
        RecordedAudioData second = secondAccumulator.Complete(sampleRate: 48000);

        Assert.Equal([0xff, 0x7f], first.PCMData.ToArray());
        Assert.Equal([0x00, 0x80], second.PCMData.ToArray());
        Assert.Equal(1, first.ChannelCount);
        Assert.Equal(1, second.ChannelCount);
    }

    /// <summary>
    /// Public recordings own a copy so caller buffers cannot mutate backend input.
    /// </summary>
    [Fact]
    public void RecordedAudioData_AfterCallerMutatesSource_RemainsImmutable()
    {
        byte[] source = [0x34, 0x12];
        RecordedAudioData recording = new(source, sampleRate: 16000, channelCount: 1);

        source[0] = 0xff;

        Assert.Equal([0x34, 0x12], recording.PCMData.ToArray());
    }

    /// <summary>
    /// Public PCM payloads must contain complete PCM16 frames for every channel.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(6, 2)]
    public void RecordedAudioData_WithIncompletePCM16Frame_Throws(int byteCount, short channelCount)
    {
        byte[] source = new byte[byteCount];

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new RecordedAudioData(source, sampleRate: 16000, channelCount));

        Assert.Contains("complete frames", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Completion retains the exact frame count and sample rate while using two bytes per mono frame.
    /// </summary>
    [Fact]
    public void Complete_AtMaximumBound_ReturnsExactMonoMetadataAndStorage()
    {
        PCMAudioAccumulator accumulator = new(maximumFrames: 2);
        _ = accumulator.AppendInterleavedStereo([0.25f, 0.25f, -0.25f, -0.25f]);

        RecordedAudioData recording = accumulator.Complete(sampleRate: 44100);

        Assert.Equal(2, recording.FrameCount);
        Assert.Equal(4, recording.PCMData.Length);
        Assert.Equal(44100, recording.SampleRate);
        Assert.Equal(1, recording.ChannelCount);
    }
}
