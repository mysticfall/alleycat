using Godot;
using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI.Provider;

/// <summary>
/// Replaceable backend factory for agent chat clients.
/// </summary>
[GlobalClass]
public abstract partial class ClientProvider : Resource
{
    /// <summary>
    /// Creates any provider-required input messages for a new agent run.
    /// </summary>
    /// <returns>Input messages to send alongside the agent instructions.</returns>
    public virtual IReadOnlyList<ChatMessage> CreateRunMessages() => [];

    /// <summary>
    /// Creates a valid chat client for tool-only turn execution.
    /// </summary>
    /// <returns>A chat client ready for tool-only turn execution.</returns>
    public abstract IChatClient CreateChatClient();
}
