using Godot;
using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI.Provider;

/// <summary>
/// Replaceable backend factory for agent chat clients: it supplies only the chat client — the session owner owns
/// any run-message bootstrap (AI-002 TR-7).
/// </summary>
[GlobalClass]
public abstract partial class ClientProvider : Resource
{
    /// <summary>
    /// Creates a valid chat client for tool-only agent session execution.
    /// </summary>
    /// <returns>A chat client ready for tool-only agent session execution.</returns>
    public abstract IChatClient CreateChatClient();
}
