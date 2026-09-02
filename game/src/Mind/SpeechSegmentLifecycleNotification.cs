using AlleyCat.Speech;

namespace AlleyCat.Mind;

/// <summary>Textless lifecycle transition for an external automatic speech segment.</summary>
internal enum SpeechSegmentLifecycleTransition
{
    /// <summary>Speech started with the identified segment.</summary>
    Started,

    /// <summary>Speech resumed with the identified segment.</summary>
    Resumed,

    /// <summary>The identified segment completed without text.</summary>
    Blank,

    /// <summary>The identified segment failed.</summary>
    Failed,

    /// <summary>The identified segment was abandoned.</summary>
    Abandoned,
}

/// <summary>Transient external speech lifecycle notification reserved for agent runtime consumption.</summary>
/// <param name="SourceVoiceID">Raw source voice ID captured at notification time.</param>
/// <param name="Metadata">Stable identity metadata for the automatic segment.</param>
/// <param name="Transition">The textless lifecycle transition.</param>
internal readonly record struct SpeechSegmentLifecycleNotification(
    string SourceVoiceID,
    SpeechSegmentMetadata Metadata,
    SpeechSegmentLifecycleTransition Transition);
