using Godot;
using Microsoft.Extensions.AI;

namespace AlleyCat.AI.Provider;

/// <summary>
/// Replaceable backend factory for agent chat clients.
/// </summary>
[GlobalClass]
public abstract partial class ClientProvider : Resource
{
    /// <summary>
    /// Creates a valid chat client for Agent Framework turn execution.
    /// </summary>
    /// <returns>A chat client ready for Agent Framework turn execution.</returns>
    public abstract IChatClient CreateChatClient();
}
