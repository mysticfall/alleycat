using AlleyCat.Mind.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Default session parking judged on actual invocation outcomes (TQ-B3) and the fabricated park exchange's
/// session-unique call ID (TQ-B5, AI-002 TR-18): a park-causing action skipped by fresh-turn invalidation before
/// invocation never invents a pause, an executed action that crossed its commit boundary still parks when a
/// trailing explicit <c>wait</c> was skipped, an explicit <c>wait</c> that actually ran keeps suppressing the
/// duplicate park, and the fabricated park ID never collides with a provider-consumed ID.
/// </summary>
public sealed class AgentSessionRunnerParkingTests
{
    private const string SpeakToolName = "speak";
    private const string WaitToolName = "wait";
    private const string OtherToolName = "other_action";
    private const string ParkResult = "Wait ended: scripted park.";

    private static readonly IInvalidResponseRecoveryPolicy _immediateRecovery =
        new InvalidResponseRecoveryPolicy(InvalidResponseRecoveryPolicy.DefaultConsecutiveFailureBudget, [TimeSpan.Zero]);

    /// <summary>
    /// The batch's only park-causing action (<c>speak</c>) is skipped by a fresh-turn invalidation before it is
    /// invoked: no action was committed, so no pause is invented — the fresh replacement request follows directly
    /// and carries no fabricated park exchange.
    /// </summary>
    [Fact]
    public async Task SkippedParkCausingAction_DoesNotParkAndTheFreshReplacementProceeds()
    {
        GatedTool other = new(OtherToolName);
        GatedTool speak = new(SpeakToolName);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(Batch(("other-call", OtherToolName), ("speak-call", SpeakToolName))),
            EndQuietly(lifetime));
        int parks = 0;
        AgentSessionRunner runner = CreateRunner(
            client,
            [other.Function, AgentSessionParkingPolicy.SessionParking.Bind(speak.Function)],
            _ =>
            {
                parks++;
                return Task.FromResult(ParkResult);
            });

        Task runTask = runner.RunAsync(lifetime.Token);
        await other.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.InvalidateForFreshTurn();
        _ = other.Commit.TrySetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(speak.Started.Task.IsCompleted, "The stale speak must never be invoked.");
        Assert.Equal(0, parks);
        Assert.Equal(2, client.Requests.Count);
        Assert.DoesNotContain(
            client.Requests[1].SelectMany(static message => message.Contents).OfType<FunctionCallContent>(),
            static call => call.CallId.StartsWith("runtime-session-park-", StringComparison.Ordinal));
    }

    /// <summary>
    /// An executed <c>speak</c> crossed its commit boundary (natural result) while a fresh-turn invalidation
    /// skipped the trailing explicit <c>wait</c>: the skipped wait never ran, so it cannot stand in for the
    /// session's pause — the committed action still parks the session, recorded as a fabricated wait exchange.
    /// </summary>
    [Fact]
    public async Task ExecutedSpeakWithSkippedTrailingWait_StillParks()
    {
        GatedTool speak = new(SpeakToolName);
        GatedTool wait = new(WaitToolName);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(Batch(("speak-call", SpeakToolName), ("wait-call", WaitToolName))),
            EndQuietly(lifetime));
        int parks = 0;
        AgentSessionRunner runner = CreateRunner(
            client,
            [
                AgentSessionParkingPolicy.SessionParking.Bind(speak.Function),
                AgentSessionParkingPolicy.SessionParkRepresentation.Bind(wait.Function),
            ],
            _ =>
            {
                parks++;
                return Task.FromResult(ParkResult);
            });

        Task runTask = runner.RunAsync(lifetime.Token);
        await speak.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.InvalidateForFreshTurn();
        _ = speak.Commit.TrySetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(wait.Started.Task.IsCompleted, "The trailing stale wait must never be invoked.");
        Assert.Equal(1, parks);
        Assert.Equal(2, client.Requests.Count);
        FunctionResultContent speakResult = Assert.Single(
            client.Requests[1].SelectMany(static message => message.Contents).OfType<FunctionResultContent>(),
            static result => result.CallId == "speak-call");
        Assert.Equal($"{SpeakToolName} committed.", speakResult.Result?.ToString());
        Assert.Contains(
            client.Requests[1].SelectMany(static message => message.Contents).OfType<FunctionResultContent>(),
            static result => result.CallId.StartsWith("runtime-session-park-", StringComparison.Ordinal)
                && result.Result?.ToString() == ParkResult);
    }

    /// <summary>
    /// An explicit <c>wait</c> that actually ran in the batch is itself the session's pause: the executed
    /// <c>speak</c> beside it never parks a second time.
    /// </summary>
    [Fact]
    public async Task ExecutedSpeakWithExecutedWait_DoesNotParkTwice()
    {
        GatedTool speak = new(SpeakToolName);
        GatedTool wait = new(WaitToolName);
        _ = speak.Commit.TrySetResult();
        _ = wait.Commit.TrySetResult();
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(Batch(("speak-call", SpeakToolName), ("wait-call", WaitToolName))),
            EndQuietly(lifetime));
        int parks = 0;
        AgentSessionRunner runner = CreateRunner(
            client,
            [
                AgentSessionParkingPolicy.SessionParking.Bind(speak.Function),
                AgentSessionParkingPolicy.SessionParkRepresentation.Bind(wait.Function),
            ],
            _ =>
            {
                parks++;
                return Task.FromResult(ParkResult);
            });

        await runner.RunAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(wait.Started.Task.IsCompleted);
        Assert.Equal(0, parks);
        Assert.Equal(2, client.Requests.Count);
    }

    /// <summary>
    /// An explicit <c>wait</c> that started and was then cut short by a fresh-turn invalidation still stands for
    /// the batch's pause: the fresh replacement follows directly instead of a second park delaying it.
    /// </summary>
    [Fact]
    public async Task ExecutedSpeakWithInterruptedWait_DoesNotParkAndTheFreshReplacementProceeds()
    {
        GatedTool speak = new(SpeakToolName);
        CooperativeTool wait = new(WaitToolName);
        _ = speak.Commit.TrySetResult();
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(Batch(("speak-call", SpeakToolName), ("wait-call", WaitToolName))),
            EndQuietly(lifetime));
        int parks = 0;
        AgentSessionRunner runner = CreateRunner(
            client,
            [
                AgentSessionParkingPolicy.SessionParking.Bind(speak.Function),
                AgentSessionParkingPolicy.SessionParkRepresentation.Bind(wait.Function),
            ],
            _ =>
            {
                parks++;
                return Task.FromResult(ParkResult);
            });

        Task runTask = runner.RunAsync(lifetime.Token);
        await wait.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.InvalidateForFreshTurn();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(wait.ObservedCancellation);
        Assert.Equal(0, parks);
        Assert.Equal(2, client.Requests.Count);
    }

    /// <summary>
    /// The provider already consumed <c>runtime-session-park-1</c> as an ordinary call ID: the fabricated park
    /// exchange reserves a different, session-unique ID, so every call in the next request pairs with exactly one
    /// result under distinct IDs (AI-002 TR-18).
    /// </summary>
    [Fact]
    public async Task FabricatedParkID_SkipsAProviderConsumedID()
    {
        GatedTool speak = new(SpeakToolName);
        GatedTool wait = new(WaitToolName);
        _ = speak.Commit.TrySetResult();
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(Batch(("runtime-session-park-1", SpeakToolName))),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [
                AgentSessionParkingPolicy.SessionParking.Bind(speak.Function),
                AgentSessionParkingPolicy.SessionParkRepresentation.Bind(wait.Function),
            ],
            _ => Task.FromResult(ParkResult));

        await runner.RunAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, client.Requests.Count);
        List<AIContent> contents = [.. client.Requests[1].SelectMany(static message => message.Contents)];
        List<FunctionCallContent> calls = [.. contents.OfType<FunctionCallContent>()];
        List<FunctionResultContent> results = [.. contents.OfType<FunctionResultContent>()];
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls.Count, calls.Select(static call => call.CallId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(results.Count, results.Select(static result => result.CallId).Distinct(StringComparer.Ordinal).Count());
        FunctionCallContent provider = Assert.Single(calls, static call => call.Name == SpeakToolName);
        FunctionCallContent park = Assert.Single(calls, static call => call.Name == WaitToolName);
        Assert.Equal("runtime-session-park-1", provider.CallId);
        Assert.NotEqual(provider.CallId, park.CallId);
        Assert.StartsWith("runtime-session-park-", park.CallId, StringComparison.Ordinal);
        Assert.Equal($"{SpeakToolName} committed.", Assert.Single(results, r => r.CallId == provider.CallId).Result?.ToString());
        Assert.Equal(ParkResult, Assert.Single(results, r => r.CallId == park.CallId).Result?.ToString());
    }

    private static ChatMessage Batch(params (string CallId, string Name)[] calls)
        => new(
            ChatRole.Assistant,
            [.. calls.Select(static call => (AIContent)new FunctionCallContent(
                call.CallId,
                call.Name,
                new Dictionary<string, object?>()))]);

    private static Func<CancellationToken, Task<ChatResponse>> Respond(ChatMessage message)
        => _ => Task.FromResult(new ChatResponse(message));

    private static Func<CancellationToken, Task<ChatResponse>> EndQuietly(CancellationTokenSource lifetime)
        => cancellationToken =>
        {
            lifetime.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        };

    private static AgentSessionRunner CreateRunner(
        ScriptedSessionClient client,
        IList<AITool> tools,
        Func<CancellationToken, Task<string>> park)
        => new(
            client,
            "Private test instructions.",
            [new ChatMessage(ChatRole.User, "Run input.")],
            tools,
            NullLogger.Instance,
            invalidResponseRecoveryPolicy: _immediateRecovery,
            parkUntilNextTurn: park);

    /// <summary>
    /// Non-cooperative tool that ignores cancellation and commits once released — modelling an action that crossed
    /// its commit (playback hand-off) boundary before a fresh-turn invalidation arrived. It raises the runner's typed
    /// boundary signal exactly as the production tools do: a behaviour tool marks its action committed at hand-off,
    /// the wait marks gate entry when it starts.
    /// </summary>
    private sealed class GatedTool(string name)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Commit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AIFunction Function => AIFunctionFactory.Create(InvokeAsync, name);

        private async Task<string> InvokeAsync(AIFunctionArguments arguments)
        {
            var signals = AgentSessionInvocationSignals.From(arguments);
            _ = Started.TrySetResult();
            if (name == WaitToolName)
            {
                signals.MarkSessionParkEntered();
            }

            await Commit.Task;
            if (name != WaitToolName)
            {
                signals.MarkActionCommitted();
            }

            return $"{name} committed.";
        }
    }

    /// <summary>Co-operative tool that runs until its phase token fires, then throws its cancellation.</summary>
    private sealed class CooperativeTool(string name)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation
        {
            get;
            private set;
        }

        public AIFunction Function => AIFunctionFactory.Create(InvokeAsync, name);

        private async Task<string> InvokeAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // The wait entered its gate before being cut short.
            AgentSessionInvocationSignals.From(arguments).MarkSessionParkEntered();
            _ = Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }

            return $"{name} completed.";
        }
    }

    private sealed class ScriptedSessionClient(params Func<CancellationToken, Task<ChatResponse>>[] steps)
        : IChatClient
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new(steps);

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            return _steps.Count == 0
                ? throw new InvalidOperationException("The scripted session client ran out of scripted steps.")
                : _steps.Dequeue()(cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
