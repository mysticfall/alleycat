using AlleyCat.Entity;
using AlleyCat.Env;
using AlleyCat.Speech.Voice;
using LanguageExt;

namespace AlleyCat.Speech;

public interface ISpeaker : IEntity
{
    IVoice Voice { get; }
}

public static class SpeakerExtensions
{
    extension(ISpeaker speaker)
    {
        public IO<bool> IsSpeaking => speaker.Voice.IsSpeaking;

        public Eff<IEnv, Unit> Speak(DialogueText speech) => speaker.Voice.Speak(speech);
    }
}