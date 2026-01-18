using AlleyCat.Sense.Hearing;
using AlleyCat.Speech.Voice;

namespace AlleyCat.Speech;

public readonly record struct Speech(
    DialogueText Dialogue,
    VoiceId Voice,
    DateTime Timestamp
) : ISound;