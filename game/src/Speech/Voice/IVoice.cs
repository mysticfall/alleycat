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
}
