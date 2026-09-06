namespace AlleyCat.Mind.AI;

/// <summary>
/// Opaque identity for one speech continuation protected across admission arbitration and fresh invalidation.
/// </summary>
internal readonly record struct SpeechContinuationKey(string VoiceID, string SpeechGroupID, int SegmentIndex);
