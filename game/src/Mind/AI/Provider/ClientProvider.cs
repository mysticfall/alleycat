using Godot;
using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI.Provider;

/// <summary>
/// Replaceable backend factory for agent chat clients: it supplies only the chat client — the session owner owns
/// any run-message bootstrap (AI-002 TR-2).
/// </summary>
[GlobalClass]
public abstract partial class ClientProvider : Resource
{
    /// <summary>
    /// The effective chat client kind of the client this provider constructs (AI-002 TR-36), or null when the
    /// provider declares no endpoint kind — the OpenAI Responses default applies then (AI-013 TR-10).
    /// </summary>
    /// <remarks>
    /// The value is resolved at client construction and stays in force for that client's lifetime. Session
    /// composition reads it exactly once after <see cref="CreateChatClient" /> to select the AI-013 visual-context
    /// transport path (AI-013 TR-10); it never changes which adapter construction itself selected.
    /// </remarks>
    public virtual OpenAIChatClientKind? EffectiveChatClientKind => null;

    /// <summary>
    /// Creates a valid chat client for tool-only agent session execution.
    /// </summary>
    /// <returns>A chat client ready for tool-only agent session execution.</returns>
    public abstract IChatClient CreateChatClient();
}
