using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Supplies the immutable, request-specific context of one logical provider request.
/// </summary>
/// <remarks>
/// This is deliberately a runtime-only boundary. Implementations may compose game state, but the session runner
/// sees only messages and an opaque confirmation token.
/// </remarks>
internal interface IAgentRequestContextSource
{
    /// <summary>Materialises one logical request's context exactly once.</summary>
    ValueTask<AgentRequestContext> MaterialiseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Marks the materialisation's retained transcript messages as appended to the session's retained transcript
    /// (AI-002 TR-18/TR-19): delivery marking is irrevocable and happens exactly when the runner appends the
    /// synthetic exchange, never for a materialisation that is superseded before its request is built.
    /// </summary>
    void Retain(object? confirmation);

    /// <summary>Confirms a context whose locally valid provider response was accepted.</summary>
    void Confirm(object? confirmation);

    /// <summary>Releases a context which was not accepted, so a later logical request can rematerialise it.</summary>
    void Discard(object? confirmation);
}

/// <summary>
/// Immutable materialisation of one logical request's transcript deliveries, trailing scene-status message, and
/// opaque confirmation token (AI-002 TR-2/TR-4).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TranscriptMessages" /> carries the pending synthetic scene-update exchange (AI-002 TR-18): the runner
/// appends it to the session's retained transcript before building the frozen request, so the exchange stays in
/// chronological position for every later request. <see cref="SuffixMessages" /> renders last, after every retained
/// exchange — the fresh current-scene message.
/// </para>
/// <para>
/// The list and message content ordering belong to this logical request. The runner may replay this exact
/// materialisation for a transport retry, but neither it nor a source can mutate its message collections.
/// </para>
/// </remarks>
internal sealed class AgentRequestContext
{
    public AgentRequestContext(
        IReadOnlyList<ChatMessage> transcriptMessages,
        IReadOnlyList<ChatMessage> suffixMessages,
        object? confirmation)
    {
        ArgumentNullException.ThrowIfNull(transcriptMessages);
        ArgumentNullException.ThrowIfNull(suffixMessages);
        if (transcriptMessages.Any(static message => message is null))
        {
            throw new ArgumentException(
                "Request-context transcript messages cannot contain null entries.",
                nameof(transcriptMessages));
        }

        if (suffixMessages.Any(static message => message is null))
        {
            throw new ArgumentException("Request-context suffix messages cannot contain null entries.", nameof(suffixMessages));
        }

        TranscriptMessages = new ReadOnlyCollection<ChatMessage>(
            [.. transcriptMessages.Select(static message => new ChatMessage(message.Role, [.. message.Contents]))]);
        SuffixMessages = new ReadOnlyCollection<ChatMessage>(
            [.. suffixMessages.Select(static message => new ChatMessage(message.Role, [.. message.Contents]))]);
        Confirmation = confirmation;
    }

    /// <summary>Synthetic delivery exchange messages appended to the retained transcript before the frozen request.</summary>
    public IReadOnlyList<ChatMessage> TranscriptMessages
    {
        get;
    }

    /// <summary>Fresh messages rendered after the whole retained transcript — the current-scene message last.</summary>
    public IReadOnlyList<ChatMessage> SuffixMessages
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
        => ValueTask.FromResult(new AgentRequestContext([], [], confirmation: null));

    public void Retain(object? confirmation)
    {
    }

    public void Confirm(object? confirmation)
    {
    }

    public void Discard(object? confirmation)
    {
    }
}
