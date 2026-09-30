using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI;

/// <summary>Outcome of one provider request cycle as resolved by the runner (AI-011 TR-6).</summary>
internal enum MindTranscriptCycleOutcome
{
    /// <summary>The validated response was accepted and its tool batch fully settled.</summary>
    Accepted,

    /// <summary>The request was superseded by a fresh-turn invalidation before a usable response settled.</summary>
    DiscardedBeforeResponse,

    /// <summary>A completed response was discarded: a fresh invalidation won while it was validated, or a
    /// non-cooperative provider returned it after its generation was invalidated.</summary>
    DiscardedStaleResponse,

    /// <summary>An invalid response shape was discarded and recovery backoff completed; a fresh request follows.</summary>
    InvalidResponseRecoveryScheduled,

    /// <summary>Invalid-response recovery was superseded by a fresh-turn invalidation without consuming the budget.</summary>
    InvalidResponseRecoverySuperseded,

    /// <summary>The invalid-response recovery budget was exhausted, ending the session through contained failure.</summary>
    RecoveryExhausted,

    /// <summary>The cycle failed through a provider or transport failure, ending the session through contained failure.</summary>
    Failed,

    /// <summary>The cycle was interrupted by node-lifetime cancellation.</summary>
    Interrupted,
}

/// <summary>Exchange-disposal decision of a settled tool batch (AI-002 TR-17; AI-011 TR-6).</summary>
internal enum MindTranscriptExchangeDisposal
{
    /// <summary>The batch contained a non-opted call, so the disposal decision did not apply.</summary>
    NotApplicable,

    /// <summary>Every call targeted a disposal-opted tool and the completed exchange was removed.</summary>
    Disposed,

    /// <summary>The batch was disposal-eligible but the exchange was retained conservatively.</summary>
    Retained,
}

/// <summary>Kind of an anomalous event annotated on a cycle transcript (AI-011 TR-6).</summary>
internal enum MindTranscriptAnnotationKind
{
    /// <summary>A transient transport failure followed by a retry within the same cycle.</summary>
    TransportRetry,

    /// <summary>An invalid response shape and its recovery events, including budget state.</summary>
    InvalidResponseRecovery,

    /// <summary>A fresh-turn invalidation that discarded or skipped work of this cycle.</summary>
    FreshTurnInvalidation,
}

/// <summary>One annotated anomaly of a recorded cycle.</summary>
internal sealed record MindTranscriptAnnotation(MindTranscriptAnnotationKind Kind, string Message);

/// <summary>Immutable snapshot of the session-fixed request options sent with every cycle (AI-011 TR-6).</summary>
internal sealed record MindTranscriptRequestOptions(
    string? ModelId,
    string ToolMode,
    bool AllowMultipleToolCalls,
    IReadOnlyList<string> ToolNames);

/// <summary>Terminal state of one recorded tool invocation (AI-011 TR-6).</summary>
internal enum MindTranscriptToolInvocationStatus
{
    /// <summary>The call was recorded at invocation start and has not settled yet.</summary>
    InProgress,

    /// <summary>The call was invoked and settled with a delivered result.</summary>
    Completed,

    /// <summary>The call was invoked and interrupted by node-lifetime cancellation before a result existed.</summary>
    Interrupted,

    /// <summary>The call was never invoked because its batch was invalidated; the canonical result was delivered.</summary>
    SkippedByInvalidation,
}

/// <summary>
/// One tool invocation of a recorded cycle, captured at invocation start and updated with its terminal status and
/// delivered result (AI-011 TR-6).
/// </summary>
/// <param name="toolName">Invoked tool's function name.</param>
/// <param name="callId">Provider call identifier of the originating call.</param>
/// <param name="arguments">The call's arguments as supplied by the model.</param>
internal sealed class MindTranscriptToolInvocation(
    string toolName,
    string callId,
    IDictionary<string, object?>? arguments)
{
    public string ToolName { get; } = toolName;

    public string CallId { get; } = callId;

    public IDictionary<string, object?>? Arguments { get; } = arguments;

    public MindTranscriptToolInvocationStatus Status
    {
        get;
        set;
    } = MindTranscriptToolInvocationStatus.InProgress;

    /// <summary>
    /// The result delivered back to the model, including runner-generated canonical cancellation results; null when
    /// the invocation was interrupted before a result existed.
    /// </summary>
    public object? Result
    {
        get;
        set;
    }
}

/// <summary>
/// Mutable capture of one provider request cycle (AI-011 TR-6): the runner fills it while the cycle runs and reports
/// it to <see cref="IAgentSessionTranscriptSink" /> once the cycle's outcome resolves.
/// </summary>
/// <remarks>
/// Every value references the live .NET objects the runner holds — the frozen request transcript, the settled
/// response, and the delivered tool results — so rendering preserves real newlines by construction (AI-011 TR-8).
/// </remarks>
internal sealed class MindSessionCycleTranscript
{
    public required int CycleIndex
    {
        get;
        init;
    }

    public required DateTimeOffset StartedAt
    {
        get;
        init;
    }

    public required string Instructions
    {
        get;
        init;
    }

    public required IReadOnlyList<ChatMessage> RequestMessages
    {
        get;
        init;
    }

    public required MindTranscriptRequestOptions RequestOptions
    {
        get;
        init;
    }

    public ChatResponse? Response
    {
        get;
        set;
    }

    /// <summary>
    /// Whether the cycle reached provider interaction — at least one attempt issued
    /// <c>GetResponseAsync</c>. Cycles that never reach the provider are not recorded and consume no transcript
    /// turn number (AI-011 TR-6).
    /// </summary>
    public bool ProviderInteractionStarted
    {
        get;
        set;
    }

    public TimeSpan Latency
    {
        get;
        set;
    }

    public MindTranscriptCycleOutcome Outcome
    {
        get;
        set;
    }

    public string? OutcomeDetail
    {
        get;
        set;
    }

    public MindTranscriptExchangeDisposal ExchangeDisposal
    {
        get;
        set;
    }

    public List<MindTranscriptAnnotation> Annotations { get; } = [];

    public List<MindTranscriptToolInvocation> ToolInvocations { get; } = [];

    /// <summary>Annotates one anomalous event of the cycle.</summary>
    public void Annotate(MindTranscriptAnnotationKind kind, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Annotations.Add(new MindTranscriptAnnotation(kind, message));
    }
}
