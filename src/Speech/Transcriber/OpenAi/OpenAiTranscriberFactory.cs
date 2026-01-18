using AlleyCat.Ai;
using AlleyCat.Ai.OpenAi;
using AlleyCat.Env;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech.Transcriber.OpenAi;

[GlobalClass]
public partial class OpenAiTranscriberFactory : TranscriberFactory
{
    [Export] public string? Prompt { get; set; }

    [Export] public OpenAiAudioClientFactory? AudioClient { get; set; }

    protected override Eff<IEnv, ITranscriber> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from prompt in Optional(Prompt)
            .Traverse(PromptText.Create)
            .As()
            .ToEff(identity)
        from client in AudioClient
            .Require("AudioClient is not set.")
            .Bind(x => x.TypedService)
        select (ITranscriber)new OpenAiTranscriber(client, prompt, loggerFactory);
}