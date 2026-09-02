using AlleyCat.Core.Logging;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Speech-to-text dispatch diagnostics owned by the transcription subsystem (SPCH-003), routed through the shared
/// pipeline diagnostic machinery of CORE-007.
/// </summary>
/// <remarks>
/// The dispatch marker emits at debug level under the dedicated <c>AlleyCat.Pipeline.STT</c> child category, whose
/// configured level is the single universal switch for its console output and notification toast: the shipped debug
/// default keeps the toast visible, while <c>None</c> or any level above <c>Debug</c> suppresses the entry entirely
/// before any provider sees it.
/// </remarks>
internal static class SttDispatchLog
{
    private const string CategoryName = "AlleyCat.Pipeline.STT";

    /// <summary>
    /// Logs the speech-to-text dispatch marker — the only notification-eligible STT diagnostic — at debug level under
    /// the dedicated STT child category. Emitted exactly once per logical transcription request, immediately before
    /// the SDK transcription call, so the retry policy's body replays cannot duplicate it.
    /// </summary>
    public static void Dispatch(string sourceMode, TimeSpan duration, int pcmByteCount)
    {
        ILogger logger = PipelineDebugLog.CreateCategoryLogger(CategoryName);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.Log(
                LogLevel.Debug,
                default,
                new SttDispatchEntry(sourceMode, duration, pcmByteCount),
                exception: null,
                static (state, _) => state.ToNotificationText());
        }
    }
}
