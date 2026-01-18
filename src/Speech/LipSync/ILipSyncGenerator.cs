using AlleyCat.Animation.BlendShape;
using AlleyCat.Env;
using AlleyCat.Speech.Generator;
using LanguageExt;

namespace AlleyCat.Speech.LipSync;

public interface ILipSyncGenerator
{
    Eff<IEnv, BlendShapeAnim> Generate(SpeechAudio audio);
}