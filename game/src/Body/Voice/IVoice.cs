using AlleyCat.Core;
using Godot;

namespace AlleyCat.Body.Voice;

/// <summary>
/// Component capability for objects that can initiate spoken speech from a world-space origin.
/// </summary>
public interface IVoice : IComponent
{
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
}
