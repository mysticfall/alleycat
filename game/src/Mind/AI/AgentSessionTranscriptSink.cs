namespace AlleyCat.Mind.AI;

/// <summary>
/// Runner-level recording sink for one agent session's transcripts (AI-011 TR-5): the runner reports each provider
/// request cycle once its outcome resolves, and the sink either records it or does nothing.
/// </summary>
/// <remarks>
/// The sink follows the runner's optional-dependency injection pattern: <see cref="AgentSessionRunner" /> stays
/// character-agnostic and never receives the character, its identity, or the file system. A disabled toggle injects
/// <see cref="NullAgentSessionTranscriptSink" />, and recording never alters runner behaviour (AI-011 TR-7).
/// </remarks>
internal interface IAgentSessionTranscriptSink
{
    /// <summary>Records one fully resolved provider request cycle (AI-011 TR-6).</summary>
    /// <param name="cycle">The cycle's captured state. Implementations must contain every failure and never throw.</param>
    void Record(MindSessionCycleTranscript cycle);
}

/// <summary>
/// Null-object sink (AI-011 TR-5): every call does nothing, performs no file input or output, and leaves runner
/// behaviour unchanged when session transcript logging is disabled.
/// </summary>
internal sealed class NullAgentSessionTranscriptSink : IAgentSessionTranscriptSink
{
    private NullAgentSessionTranscriptSink()
    {
    }

    /// <summary>The shared instance injected whenever session transcript logging is disabled.</summary>
    public static NullAgentSessionTranscriptSink Instance { get; } = new();

    /// <inheritdoc />
    public void Record(MindSessionCycleTranscript cycle)
    {
    }
}

/// <summary>
/// Runner-scoped transcript sink gate (AI-011 TR-6/TR-7): recording is scoped to the runner's lifetime. Once
/// <see cref="AgentSessionRunner.RunAsync" /> has exited, the gate closes in its <c>finally</c>, and any later
/// capture attempt from an abandoned continuation becomes a contained no-op that neither writes nor throws.
/// </summary>
internal sealed class SessionScopedTranscriptSink(IAgentSessionTranscriptSink inner) : IAgentSessionTranscriptSink
{
    private volatile bool _ended;

    /// <summary>Closes the gate; called when the owning runner's session loop has exited.</summary>
    public void End() => _ended = true;

    /// <inheritdoc />
    public void Record(MindSessionCycleTranscript cycle)
    {
        if (_ended)
        {
            return;
        }

        inner.Record(cycle);
    }
}
