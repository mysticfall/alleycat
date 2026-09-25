using AlleyCat.TestFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Xunit;
using Xunit.Sdk;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Deterministic fake-client coverage for <see cref="LiveLLMEvaluation" />: ordering, single-shot call counts,
/// fail-closed metric validation, cancellation propagation, disposal, and secret-free failure messages. The
/// target and judge are fake <see cref="IChatClient" />s injected through the public
/// <see cref="LiveLLMClientPair" /> constructor; the judge path exercises the real
/// <see cref="GroundednessEvaluator" />. No live backend is contacted.
/// </summary>
[Headless]
public sealed class LiveLLMEvaluationIntegrationTests
{
    private const string TargetAnswer =
        "Her file shows steady employment, no prior breaches, and no flagged associations. "
        + "I write the recommendation; the Office makes the final determination.";

    private const string GroundingContext =
        "Vadim's available record for Ally: steady employment, no prior breaches, no flagged associations. "
        + "Vadim's involvement ends at the recommendation; the Office makes the determination.";

    private const string JudgeReason =
        "The response restates every fact in the grounding context and adds nothing.";

    private const string PlayerQuestion =
        "What does Ally's record say about prior breaches and flagged associations, and who makes the final determination?";

    /// <summary>
    /// A valid score-5 judge verdict passes at threshold 5 after exactly one target call followed by exactly one
    /// judge call, and returns the validated metric details.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithValidScoreAtThreshold_CallsTargetOnceThenJudgeOnceAndPasses()
    {
        List<string> callOrder = [];
        FakeChatClient target = new("target", callOrder);
        FakeChatClient judge = new("judge", callOrder);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse(JudgeResponse(score: "5")));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        LiveLLMEvaluationOutcome outcome = await LiveLLMEvaluation.EvaluateAsync(
            Messages(),
            clients,
            new GroundednessEvaluator(),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 5);

        Assert.Equal(GroundednessEvaluator.GroundednessMetricName, outcome.MetricName);
        Assert.Equal(5d, outcome.Score);
        Assert.Equal(JudgeReason, outcome.Reason);
        Assert.Equal(TargetAnswer, outcome.TargetResponse.Text);
        Assert.Equal(["target", "judge"], callOrder);
        _ = Assert.Single(target.Requests);
        _ = Assert.Single(judge.Requests);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A score-4 verdict passes the library's own greater-than-or-equal-to-4 interpretation but still fails the
    /// explicit threshold 5, with score, threshold, and judge reasoning in the failure message.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithScoreFourAtThresholdFive_FailsWithScoreThresholdAndReason()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse(JudgeResponse(score: "4")));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains(GroundednessEvaluator.GroundednessMetricName, error.Message, StringComparison.Ordinal);
        Assert.Contains("4", error.Message, StringComparison.Ordinal);
        Assert.Contains("5", error.Message, StringComparison.Ordinal);
        Assert.Contains(JudgeReason, error.Message, StringComparison.Ordinal);
        _ = Assert.Single(target.Requests);
        _ = Assert.Single(judge.Requests);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A failing target request propagates without any judge call and still disposes both clients.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenTargetClientFails_PropagatesWithoutJudgingAndDisposes()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => throw new InvalidOperationException("synthetic-target-failure");
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Equal("synthetic-target-failure", error.Message);
        Assert.Empty(judge.Requests);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// The judge sees the actual target response and the explicitly supplied grounding context, plus the player
    /// question as the query.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_JudgeReceivesActualTargetResponseAndExplicitGroundingContext()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse(JudgeResponse(score: "5")));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        _ = await LiveLLMEvaluation.EvaluateAsync(
            Messages(),
            clients,
            new GroundednessEvaluator(),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 5);

        CapturedRequest judgeRequest = Assert.Single(judge.Requests);
        Assert.Equal([ChatRole.System, ChatRole.User], judgeRequest.Messages.Select(static message => message.Role));
        string judgePrompt = judgeRequest.Messages[1].Text;
        // The grounding context is passed verbatim; the response and query are role-prefixed renderings, so the
        // assertion checks the marker plus the carried text rather than an exact formatting match.
        Assert.Contains($"CONTEXT: {GroundingContext}", judgePrompt, StringComparison.Ordinal);
        Assert.Contains("RESPONSE:", judgePrompt, StringComparison.Ordinal);
        Assert.Contains(TargetAnswer, judgePrompt, StringComparison.Ordinal);
        Assert.Contains("QUERY:", judgePrompt, StringComparison.Ordinal);
        Assert.Contains(PlayerQuestion, judgePrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A text-free target response is rejected before judging and both clients are disposed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenTargetResponseHasNoText_FailsWithoutJudging()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(new ChatResponse());
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains("no assistant text", error.Message, StringComparison.Ordinal);
        Assert.Empty(judge.Requests);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A tool-calls-only target response is rejected before judging and both clients are disposed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenTargetResponseIsToolCallsOnly_FailsWithoutJudging()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent("call-1", "speak", new Dictionary<string, object?>())])));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains("function call", error.Message, StringComparison.Ordinal);
        Assert.Empty(judge.Requests);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A judge verdict without the score tag yields error diagnostics and fails closed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenJudgeOmitsScoreTag_FailsClosed()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse("<S0>Let's think step by step.</S0>, <S1>partial</S1>"));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains("error-severity", error.Message, StringComparison.Ordinal);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A judge score tag that is not a number yields error diagnostics and fails closed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenJudgeScoreIsNotNumeric_FailsClosed()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse(JudgeResponse(score: "excellent")));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains("error-severity", error.Message, StringComparison.Ordinal);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A verdict with a score but no judge reasoning fails closed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenJudgeReasonIsMissing_FailsClosed()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse("<S0>Let's think step by step.</S0>, <S2>5</S2>"));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains("no judge reasoning", error.Message, StringComparison.Ordinal);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A numeric score outside the documented 1 to 5 range fails closed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenJudgeScoreIsOutOfRange_FailsClosed()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse(JudgeResponse(score: "7")));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains("documented range", error.Message, StringComparison.Ordinal);
        Assert.Contains("7", error.Message, StringComparison.Ordinal);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A score that parses as a non-finite number fails closed instead of comparing against the threshold.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenJudgeScoreIsNonFinite_FailsClosed()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse(JudgeResponse(score: "NaN")));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains("non-finite", error.Message, StringComparison.Ordinal);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// An evaluation result that lacks the declared metric fails closed, naming the metrics that are present.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenMetricIsAbsent_FailsClosed()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);
        StubEvaluator evaluator = new(
            new EvaluationResult(new NumericMetric("Unrelated", 5, JudgeReason)),
            "Groundedness");

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                evaluator,
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains("absent", error.Message, StringComparison.Ordinal);
        Assert.Contains("Unrelated", error.Message, StringComparison.Ordinal);
        _ = Assert.Single(target.Requests);
        Assert.Empty(judge.Requests);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A declared metric of a non-numeric type fails closed, naming both types.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenMetricIsWrongType_FailsClosed()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);
        StubEvaluator evaluator = new(
            new EvaluationResult(new BooleanMetric("Groundedness", true, JudgeReason)),
            "Groundedness");

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                evaluator,
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains(nameof(BooleanMetric), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NumericMetric), error.Message, StringComparison.Ordinal);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A score the library's own interpretation marks as failed fails closed before the threshold comparison.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenInterpretationMarksFailure_FailsClosed()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse(JudgeResponse(score: "2")));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.Contains("interpretation", error.Message, StringComparison.Ordinal);
        Assert.Contains("Poor", error.Message, StringComparison.Ordinal);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// A pre-cancelled token reaches the target boundary, no judge call happens, and both clients are disposed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ForwardsCancellationToTheTargetBoundary()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        target.Responder = token => Task.FromException<ChatResponse>(new OperationCanceledException(token));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5,
                cancellation.Token));

        Assert.Empty(judge.Requests);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// Cancellation raised after the target call still reaches the judge boundary and both clients are disposed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ForwardsCancellationToTheJudgeBoundary()
    {
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        using CancellationTokenSource cancellation = new();
        target.Responder = _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(TextResponse(TargetAnswer));
        };
        judge.Responder = token => token.IsCancellationRequested
            ? Task.FromException<ChatResponse>(new OperationCanceledException(token))
            : Task.FromResult(TextResponse(JudgeResponse(score: "5")));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5,
                cancellation.Token));

        _ = Assert.Single(target.Requests);
        _ = Assert.Single(judge.Requests);
        Assert.True(judge.Requests[0].CancellationToken.IsCancellationRequested);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    /// <summary>
    /// Synthetic credential sentinels placed in judge reasoning never appear in the reported failure message,
    /// which surfaces the sanitised reasoning instead.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_FailureMessagesNeverContainCredentialSentinels()
    {
        const string sentinelToken = "sk-LIVETESTSENTINEL0123456789abcdef012345";
        const string sentinelAssignedSecret = "ApiKey=LIVETESTSYNTHETICSECRETPASSWORD1234567890";
        const string sentinelUrlSecret = "https://user:LIVETESTURLPASSWORD9876543210abcdef@example.test/v1";
        FakeChatClient target = new("target", []);
        FakeChatClient judge = new("judge", []);
        target.Responder = _ => Task.FromResult(TextResponse(TargetAnswer));
        judge.Responder = _ => Task.FromResult(TextResponse(
            $"<S0>Let's think step by step.</S0>, <S1>{sentinelToken} {sentinelAssignedSecret} {sentinelUrlSecret}</S1>, <S2>4</S2>"));
        // The evaluation call owns the pair and disposes both clients on every path.
        LiveLLMClientPair clients = new(target, judge);

        XunitException error = await Assert.ThrowsAsync<XunitException>(
            () => LiveLLMEvaluation.EvaluateAsync(
                Messages(),
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 5));

        Assert.DoesNotContain("LIVETESTSENTINEL", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("LIVETESTSYNTHETICSECRETPASSWORD", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("LIVETESTURLPASSWORD", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", error.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", error.Message, StringComparison.Ordinal);
        Assert.Contains("Judge reasoning:", error.Message, StringComparison.Ordinal);
        Assert.True(target.Disposed);
        Assert.True(judge.Disposed);
    }

    private static List<ChatMessage> Messages()
        =>
        [
            new ChatMessage(ChatRole.System, "You are Vadim, a records officer. Answer briefly from the supplied records."),
            new ChatMessage(ChatRole.User, PlayerQuestion),
        ];

    private static ChatResponse TextResponse(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static string JudgeResponse(string score)
        => $"<S0>Let's think step by step: the response matches the context.</S0>, <S1>{JudgeReason}</S1>, <S2>{score}</S2>";

    private sealed record CapturedRequest(
        IReadOnlyList<ChatMessage> Messages,
        ChatOptions? Options,
        CancellationToken CancellationToken);

    /// <summary>
    /// Fake chat client that records requests in order, returns the configured response exactly once, and refuses
    /// any second call so single-shot call counts are enforced by construction.
    /// </summary>
    private sealed class FakeChatClient(string name, List<string> callOrder) : IChatClient
    {
        private int _callCount;

        public List<CapturedRequest> Requests
        {
            get;
        } = [];

        public Func<CancellationToken, Task<ChatResponse>>? Responder
        {
            get; set;
        }

        public bool Disposed
        {
            get; private set;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            int observed = Interlocked.Increment(ref _callCount);
            if (observed > 1)
            {
                throw new InvalidOperationException($"The fake '{name}' client received an unexpected second request.");
            }

            callOrder.Add(name);
            Requests.Add(new CapturedRequest([.. messages], options, cancellationToken));

            return Responder is { } respond ? respond(cancellationToken) : Task.FromResult(TextResponse(TargetAnswer));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Live LLM evaluation never uses streaming requests.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Evaluator stub returning a pre-built result for metric-presence and metric-type coverage.
    /// </summary>
    private sealed class StubEvaluator(EvaluationResult result, params string[] metricNames) : IEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames
        {
            get;
        } = metricNames;

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(result);
    }
}
