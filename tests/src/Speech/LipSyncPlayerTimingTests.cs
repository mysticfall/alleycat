using AlleyCat.Speech.LipSync;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Tests audio-clock progression rules used by lip-sync playback.
/// </summary>
public sealed class LipSyncPlayerTimingTests
{
    /// <summary>
    /// Ensures an audio mixer boundary cannot make pose playback revisit an earlier frame.
    /// </summary>
    [Fact]
    public void AdvancePlaybackClock_WhenMixerObservationRegresses_HoldsTheMostRecentPlaybackTime()
    {
        const double previousPlaybackTimeSeconds = 0.1d;
        const double postMixPlaybackTimeSeconds = 0.05d;

        double playbackTimeSeconds = LipSyncPlayer.AdvancePlaybackClock(
            previousPlaybackTimeSeconds,
            postMixPlaybackTimeSeconds);

        Assert.Equal(previousPlaybackTimeSeconds, playbackTimeSeconds);
    }

    /// <summary>
    /// Ensures normal forward audio-clock observations continue advancing pose playback.
    /// </summary>
    [Fact]
    public void AdvancePlaybackClock_WhenMixerObservationAdvances_UsesTheNewPlaybackTime()
    {
        const double previousPlaybackTimeSeconds = 0.1d;
        const double observedPlaybackTimeSeconds = 0.12d;

        double playbackTimeSeconds = LipSyncPlayer.AdvancePlaybackClock(
            previousPlaybackTimeSeconds,
            observedPlaybackTimeSeconds);

        Assert.Equal(observedPlaybackTimeSeconds, playbackTimeSeconds);
    }
}
