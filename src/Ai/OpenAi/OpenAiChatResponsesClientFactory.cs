using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;

namespace AlleyCat.Ai.OpenAi;

[GlobalClass]
public partial class OpenAiResponsesClientFactory : ResourceFactory<ResponsesClient>, IOpenAiClientFactory
{
    [Export] public string? ConfigSection { get; set; }

    protected override Eff<IEnv, ResponsesClient> CreateService(ILoggerFactory loggerFactory) =>
        from cm in ((IOpenAiClientFactory)this).CreateClient(loggerFactory)
        let client = cm.Client
        let model = cm.Model
        select client.GetResponsesClient(model);
}
