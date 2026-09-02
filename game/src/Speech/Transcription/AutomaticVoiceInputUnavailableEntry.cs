using AlleyCat.Core.Logging;

namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Structured warning state for automatic voice input that became unavailable — through an initialisation failure or
/// a mid-session inference failure — routed to the notification UI through the logging pipeline (CORE-007). The full
/// log output carries the diagnostic detail; the notification text stays concise.
/// </summary>
/// <param name="InputMode">Configured voice input mode at the time of the failure.</param>
/// <param name="ModelPath">Fixed Silero model path the detector tried to load.</param>
/// <param name="Exception">Failure that made automatic input unavailable, at initialisation or mid-session inference.</param>
/// <param name="ManualInputAvailable">Indicates whether manual push-to-talk remains usable in this mode.</param>
public sealed record AutomaticVoiceInputUnavailableEntry(
    VoiceInputMode InputMode,
    string ModelPath,
    Exception Exception,
    bool ManualInputAvailable) : IUINotificationEntry
{
    /// <inheritdoc />
    public string ToNotificationText()
        => ManualInputAvailable
            ? "Automatic voice detection is unavailable. Use push-to-talk."
            : "Automatic voice detection is unavailable.";

    /// <summary>
    /// Determines whether the unavailability must emit exactly one terminal <c>TranscriptionFailed</c>:
    /// <c>AutomaticOnly</c> has no alternative input path, and an open automatic utterance already opened the
    /// player's public speaking window through <c>RecordingStarted</c>, so it needs exactly one terminal failure
    /// in every input mode.
    /// </summary>
    /// <param name="manualInputAvailable">Indicates whether manual push-to-talk remains usable in this mode.</param>
    /// <param name="hasOpenAutomaticUtterance">
    /// Indicates whether an automatic utterance was open when the automatic input failed.
    /// </param>
    /// <returns>True when exactly one failure signal must be emitted for the unavailability.</returns>
    public static bool RequiresFailureSignal(bool manualInputAvailable, bool hasOpenAutomaticUtterance)
        => !manualInputAvailable || hasOpenAutomaticUtterance;

    /// <summary>Renders the full-log diagnostic text carried by this entry.</summary>
    public string ToDetailedLogText()
        => $"Automatic voice detection is unavailable and has been disabled. "
            + $"Input mode: {InputMode}. Silero model path: {ModelPath}. "
            + $"Failure: {Exception.GetType().Name}: {Exception.Message} "
            + $"Manual input {(ManualInputAvailable ? "remains available through push-to-talk" : "is not available in AutomaticOnly mode")}.";

    /// <inheritdoc />
    public override string ToString() => ToDetailedLogText();
}
