using AlleyCat.Env;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Ai.Agent;

[GlobalClass]
public partial class ChatAgentFactory : AgentFactory
{
    [Export] public ChatClientFactory? ChatClient { get; set; }

    protected override Eff<IEnv, AIAgent> CreateService(ILoggerFactory loggerFactory) =>
        from client in ChatClient
            .Require("Chat client is not set.")
            .Bind(x => x.TypedService)
        select (AIAgent)client.CreateAIAgent(loggerFactory: loggerFactory);
}