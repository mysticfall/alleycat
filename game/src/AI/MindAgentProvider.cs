using Godot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AlleyCat.AI;

/// <summary>
/// Replaceable backend factory for Mind Agent Framework execution.
/// </summary>
[GlobalClass]
public abstract partial class MindAgentProvider : Resource
{
    /// <summary>
    /// Creates the agent used by a Mind component.
    /// </summary>
    /// <param name="definition">Agent instructions, metadata, and tools owned by the Mind.</param>
    /// <returns>An agent ready to run Mind turns.</returns>
    public virtual ChatClientAgent CreateAgent(MindAgentDefinition definition)
    {
        IChatClient? chatClient = CreateChatClient();
        return chatClient is null
            ? throw new NotSupportedException($"{GetType().Name} must provide either an AI agent or a chat client.")
            : chatClient.AsAIAgent(
                instructions: definition.Instructions,
                name: definition.Name,
                description: definition.Description,
                tools: definition.Tools);
    }

    /// <summary>
    /// Creates the chat client used by the default Agent Framework adapter.
    /// </summary>
    /// <returns>A chat client, or <see langword="null" /> when <see cref="CreateAgent" /> is overridden directly.</returns>
    protected virtual IChatClient? CreateChatClient() => null;
}

/// <summary>
/// Mind-owned Agent Framework metadata and tools.
/// </summary>
public sealed record MindAgentDefinition(
    string Instructions,
    string Name,
    string Description,
    IList<AITool> Tools);
