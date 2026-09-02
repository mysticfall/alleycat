using System.Globalization;

using AlleyCat.Core.Logging;

namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Speech-to-text dispatch marker, eligible for notification display alongside debug logging under the dedicated
/// STT pipeline child category.
/// </summary>
/// <remarks>
/// The entry carries only non-sensitive operational metadata: the manual or automatic source mode, the audio
/// duration, and the PCM byte count. It must never carry transcript text, audio content, prompt or hotword hints,
/// credentials, endpoints, or request bodies.
/// </remarks>
public sealed record SttDispatchEntry(string SourceMode, TimeSpan Duration, int PcmByteCount) : IUINotificationEntry
{
    /// <inheritdoc />
    public string ToNotificationText()
    {
        string seconds = Duration.TotalSeconds.ToString("0.0#", CultureInfo.InvariantCulture);
        return $"Dispatching {SourceMode} audio to STT ({seconds} seconds, {PcmByteCount} PCM bytes)";
    }
}
