namespace AlleyCat.Mind.Observation;

/// <summary>
/// Ingestion-only provenance and automatic-segment identity for one observed speech payload.
/// </summary>
/// <remarks>
/// This data is retained beside, never within, <see cref="ObservedSpeech"/>. It supports Mind's exact-once
/// gate and session continuation routing, and must never become model-facing text.
/// </remarks>
internal sealed class SpeechObservationTransport
{
    internal SpeechObservationTransport(
        string sourceVoiceID,
        string? speechGroupID = null,
        int segmentIndex = 0,
        bool continued = false)
    {
        ArgumentNullException.ThrowIfNull(sourceVoiceID);

        SourceVoiceID = sourceVoiceID;
        if (!string.IsNullOrWhiteSpace(sourceVoiceID)
            && !string.IsNullOrWhiteSpace(speechGroupID)
            && segmentIndex >= 0
            && continued == (segmentIndex > 0))
        {
            SpeechGroupID = speechGroupID;
            SegmentIndex = segmentIndex;
        }
    }

    internal string SourceVoiceID
    {
        get;
    }

    internal string? SpeechGroupID
    {
        get;
    }

    internal int SegmentIndex
    {
        get;
    }

    internal bool HasGroupedSegmentIdentity => SpeechGroupID is not null;

    internal ObservationCommitIdentity? CommitIdentity => HasGroupedSegmentIdentity
        ? new ObservationCommitIdentity(SourceVoiceID, SpeechGroupID!, SegmentIndex)
        : null;
}
