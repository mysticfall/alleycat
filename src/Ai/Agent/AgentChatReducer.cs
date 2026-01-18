using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace AlleyCat.Ai.Agent;

[Experimental("MEAI001")]
public class AgentChatReducer : IChatReducer
{
    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken
    )
    {
        throw new NotImplementedException();
    }
}