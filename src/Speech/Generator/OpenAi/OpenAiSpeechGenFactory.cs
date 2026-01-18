using AlleyCat.Ai.OpenAi;
using AlleyCat.Env;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;

namespace AlleyCat.Speech.Generator.OpenAi;

[GlobalClass]
public partial class OpenAiSpeechGenFactory : SpeechGeneratorFactory
{
    [Export] public string Voice { get; set; } = GeneratedSpeechVoice.Alloy.ToString();

    [Export] public OpenAiAudioClientFactory? AudioClient { get; set; }

    protected override Eff<IEnv, ISpeechGenerator> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from voice in Voice
            .Require("Voice is not set.")
            .Map(v => new GeneratedSpeechVoice(v))
        from client in AudioClient
            .Require("AudioClient is not set.")
            .Bind(x => x.TypedService)
        select (ISpeechGenerator)new OpenAiSpeechGen(
            voice,
            client,
            loggerFactory
        );
}