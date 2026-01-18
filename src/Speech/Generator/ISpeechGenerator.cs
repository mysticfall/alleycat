using AlleyCat.Ai;
using AlleyCat.Env;
using LanguageExt;

namespace AlleyCat.Speech.Generator;

public readonly struct SpeechAudio
{
    public required byte[] Data { get; init; }
}

public interface ISpeechGenerator
{
    Eff<IEnv, SpeechAudio> Generate(
        DialogueText speech,
        Option<PromptText> instruction = default
    );
}