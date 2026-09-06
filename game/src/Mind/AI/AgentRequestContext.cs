using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Supplies the immutable, request-specific prefix for one logical provider request.
/// </summary>
/// <remarks>
/// This is deliberately a runtime-only boundary. Implementations may compose game state, but the session runner
/// sees only messages and an opaque confirmation token.
/// </remarks>
internal interface IAgentRequestContextSource
{
    /// <summary>Materialises one logical request's prefix exactly once.</summary>
    ValueTask<AgentRequestContext> MaterialiseAsync(CancellationToken cancellationToken);

    /// <summary>Confirms a context whose locally valid provider response was accepted.</summary>
    void Confirm(object? confirmation);

    /// <summary>Releases a context which was not accepted, so a later logical request can rematerialise it.</summary>
    void Discard(object? confirmation);
}

/// <summary>Immutable materialisation of a logical request's canonical prefix and opaque confirmation token.</summary>
internal sealed class AgentRequestContext
{
    public AgentRequestContext(IReadOnlyList<ChatMessage> prefixMessages, object? confirmation)
    {
        ArgumentNullException.ThrowIfNull(prefixMessages);
        if (prefixMessages.Any(static message => message is null))
        {
            throw new ArgumentException("Request-context prefix messages cannot contain null entries.", nameof(prefixMessages));
        }

        // The list and message content ordering belong to this logical request. The runner may replay this exact
        // materialisation for a transport retry, but neither it nor a source can mutate its message collection.
        PrefixMessages = new ReadOnlyCollection<ChatMessage>(
            [.. prefixMessages.Select(static message => new ChatMessage(message.Role, [.. message.Contents]))]);
        Confirmation = confirmation;
    }

    public IReadOnlyList<ChatMessage> PrefixMessages
    {
        get;
    }

    public object? Confirmation
    {
        get;
    }
}

/// <summary>Default context for runner-only tests and callers with no dynamic prefix.</summary>
internal sealed class EmptyAgentRequestContextSource : IAgentRequestContextSource
{
    public static EmptyAgentRequestContextSource Instance { get; } = new();

    public ValueTask<AgentRequestContext> MaterialiseAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(new AgentRequestContext([], confirmation: null));

    public void Confirm(object? confirmation)
    {
    }

    public void Discard(object? confirmation)
    {
    }
}
