using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>One request captured by a scripted chat client, in arrival order.</summary>
internal sealed record LiveLLMCapturedRequest(
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions? Options,
    CancellationToken CancellationToken);

/// <summary>
/// Deterministic multi-call fake chat client for live LLM harness tests: it records every request in order
/// and returns queued responses, so scenarios that make several provider calls stay fully scripted. It never
/// contacts a network.
/// </summary>
internal sealed class LiveLLMScriptedChatClient(string name) : IChatClient
{
    private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _responders = [];

    /// <summary>Name used in diagnostics; distinguishes target, continuation, and judge fakes.</summary>
    public string Name { get; } = name;

    /// <summary>Every request received, in order.</summary>
    public List<LiveLLMCapturedRequest> Requests { get; } = [];

    /// <summary>Whether this client was disposed.</summary>
    public bool Disposed
    {
        get; private set;
    }

    /// <summary>Queues a fixed response for the next request.</summary>
    public LiveLLMScriptedChatClient Enqueue(ChatResponse response)
    {
        _responders.Enqueue(_ => Task.FromResult(response));
        return this;
    }

    /// <summary>Queues a responder for the next request; it may honour the cancellation token.</summary>
    public LiveLLMScriptedChatClient Enqueue(Func<CancellationToken, Task<ChatResponse>> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    /// <summary>Queues a failure for the next request.</summary>
    public LiveLLMScriptedChatClient EnqueueError(Exception exception)
    {
        _responders.Enqueue(_ => Task.FromException<ChatResponse>(exception));
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(new LiveLLMCapturedRequest([.. messages], options, cancellationToken));

        return _responders.Count > 0 && _responders.Dequeue() is { } respond
            ? respond(cancellationToken)
            : Task.FromException<ChatResponse>(
                new InvalidOperationException($"The scripted client '{Name}' has no queued response for request {Requests.Count}."));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The live LLM experiment harness never uses streaming requests.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Deterministic evaluator stub returning a pre-built result, for metric-presence, metric-type, and
/// declared-metric-count coverage. It never contacts a judge client.
/// </summary>
internal sealed class LiveLLMStubbedEvaluator(EvaluationResult result, params string[] metricNames) : IEvaluator
{
    public IReadOnlyCollection<string> EvaluationMetricNames { get; } = metricNames;

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(result);
}
