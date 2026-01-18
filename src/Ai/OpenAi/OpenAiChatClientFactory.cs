using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Ai.OpenAi;

[GlobalClass]
public partial class OpenAiChatClientFactory : ChatClientFactory, IOpenAiClientFactory
{
    [Export] public string? ConfigSection { get; set; }

    protected override Eff<IEnv, IChatClient> CreateService(ILoggerFactory loggerFactory) =>
        from cm in ((IOpenAiClientFactory)this).CreateClient(loggerFactory)
        let client = cm.Client
        let model = cm.Model
        select client.GetChatClient(model).AsIChatClient();
}