using AlleyCat.Env;
using Godot;
using LanguageExt;

namespace AlleyCat.Speech.Transcriber;

public interface ITranscriber
{
    Eff<IEnv, DialogueText> Transcribe(AudioStreamWav audio);
}