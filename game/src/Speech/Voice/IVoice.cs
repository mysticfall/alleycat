using AlleyCat.Core;
using Godot;

namespace AlleyCat.Speech.Voice;

/// <summary>
/// Component capability for objects that can initiate spoken speech from a world-space origin.
/// </summary>
public interface IVoice : IComponent, IIdentifiable
{
    /// <inheritdoc />
    string IIdentifiable.Type => "voice";

    /// <summary>
    /// Indicates whether this voice's speaking window is currently open.
    /// </summary>
    bool IsSpeaking
    {
        get;
    }

    /// <summary>
    /// Raised when this voice's speaking window opens.
    /// </summary>
    event Action<IVoice>? SpeechStarted;

    /// <summary>
    /// Raised when this voice's speaking window closes.
    /// </summary>
    event Action<IVoice>? SpeechEnded;

    /// <summary>Raised for a textless speech-start lifecycle transition.</summary>
    /// <remarks>
    /// Automatic qualified onsets carry the real speech-group identity, while manual recording presses carry a
    /// fresh opaque synthetic token, so listeners can track the pending segment before any text exists.
    /// </remarks>
    event Action<IVoice, SpeechSegmentMetadata>? SpeechSegmentStarted
    {
        add
        {
        }
        remove
        {
        }
    }

    /// <summary>Raised for a textless automatic speech-resume lifecycle transition.</summary>
    event Action<IVoice, SpeechSegmentMetadata>? SpeechResumed
    {
        add
        {
        }
        remove
        {
        }
    }

    /// <summary>Raised for a textless terminal automatic-segment outcome.</summary>
    event Action<IVoice, SpeechSegmentSettlement>? SpeechSegmentSettled
    {
        add
        {
        }
        remove
        {
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// World-space position where this voice originates.
    /// </summary>
    Vector3 Origin
    {
        get;
    }

    /// <summary>
    /// Starts speech output for the supplied speech text.
    /// </summary>
    /// <param name="speech">Speech text to speak.</param>
    void Speak(string speech);

    /// <summary>
    /// Submits speech without waiting for generation or playback completion.
    /// </summary>
    /// <param name="speech">Speech text to submit.</param>
    /// <param name="cancellationToken">Cancellation observed until submission commits.</param>
    ValueTask SpeakAsync(
        string speech,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits speech as an explicitly cancellable submission that completes at playback hand-off.
    /// </summary>
    /// <param name="speech">Speech text to submit.</param>
    /// <param name="cancellationToken">Caller-supplied cancellation observed through generation, conversion, and
    /// preparation until playback hand-off.</param>
    /// <remarks>
    /// <para>
    /// Cancellation observed before playback hand-off aborts the submission silently: no failure signalling, no
    /// listener notification, and no partial speech output. Playback hand-off is the irreversibility boundary, so
    /// cancellation observed after it neither retracts nor cuts the committed speech.
    /// </para>
    /// <para>
    /// Unlike <see cref="SpeakAsync(string, CancellationToken)" />, which completes when the submission is admitted,
    /// this submission completes only once playback hand-off has occurred.
    /// </para>
    /// </remarks>
    ValueTask SpeakCancellableAsync(
        string speech,
        CancellationToken cancellationToken = default);
}
