using AlleyCat.Sense;
using AlleyCat.Speech.Voice;

namespace AlleyCat.Speech;

/// <summary>
/// Receives spoken voice notifications from voice nodes in the scene tree.
/// </summary>
public interface IHearing : ISense
{
    /// <summary>
    /// Global Godot group used to discover voice listener nodes.
    /// </summary>
    const string GroupName = "voice_listeners";

    /// <summary>
    /// Receives speech from a source voice.
    /// </summary>
    /// <param name="speech">Speech that was generated or handed off for playback.</param>
    /// <param name="source">Voice instance that emitted the speech event.</param>
    void ReceiveVoice(string speech, IVoice source);

    /// <summary>Receives speech with optional generic automatic-segment identity.</summary>
    void ReceiveVoice(string speech, IVoice source, SpeechSegmentMetadata? metadata) => ReceiveVoice(speech, source);

    /// <summary>
    /// Receives speech with optional automatic-segment identity and the speaker's explicitly declared
    /// addressees.
    /// </summary>
    /// <param name="speech">Speech that was generated or handed off for playback.</param>
    /// <param name="source">Voice instance that emitted the speech event.</param>
    /// <param name="metadata">Optional automatic speech-group segment identity.</param>
    /// <param name="declaredRecipients">
    /// Canonical character FullIds the speaker explicitly declared as addressees, or null/empty when
    /// unspecified. Recipients never restrict hearing reach: every listener still receives the speech.
    /// </param>
    void ReceiveVoice(
        string speech,
        IVoice source,
        SpeechSegmentMetadata? metadata,
        IReadOnlyList<string>? declaredRecipients)
        => ReceiveVoice(speech, source, metadata);
}
