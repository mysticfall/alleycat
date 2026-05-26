namespace AlleyCat.Body.Voice;

/// <summary>
/// Receives spoken voice notifications from voice nodes in the scene tree.
/// </summary>
public interface IVoiceListener
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
}
