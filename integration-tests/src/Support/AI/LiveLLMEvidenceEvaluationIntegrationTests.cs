using AlleyCat.TestFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Xunit;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Deterministic fake-client coverage for <see cref="LiveLLMEvidenceEvaluation" />: judging runs purely over
/// captured evidence without re-running the target, fail-closed metric validation mirroring
/// <see cref="LiveLLMEvaluation" />, inclusive thresholds, sanitised failures, and borrowed-client ownership.
/// The judge path exercises the real <see cref="GroundednessEvaluator" />. No live backend is contacted.
/// </summary>
[Headless]
public sealed class LiveLLMEvidenceEvaluationIntegrationTests
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
    /// A valid score-5 verdict passes at threshold 5 after exactly one judge call, purely from captured
    /// evidence: the target client is not contacted again, and neither borrowed client is disposed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_JudgesCapturedEvidenceWithoutReRunningTheTarget()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(TextResponse(JudgeResponse(score: "5")));
        // The test owns the pair for its complete lifetime; evaluation only borrows its clients.
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await LiveLLMEvidenceEvaluation.EvaluateAsync(
            execution,
            clients,
            new GroundednessEvaluator(),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 5);

        Assert.Equal(LiveLLMJudgeVerdictStatus.Success, verdict.Status);
        Assert.True(verdict.Passed);
        Assert.Equal(GroundednessEvaluator.GroundednessMetricName, verdict.MetricName);
        Assert.Equal(5d, verdict.Score);
        Assert.Equal(JudgeReason, verdict.Reason);
        Assert.Equal(5d, verdict.Threshold);
        // Evidence-only judging: the target saw exactly the one scenario request and nothing more.
        _ = Assert.Single(target.Requests);
        _ = Assert.Single(judge.Requests);
        // Borrowed clients are never disposed by evaluation.
        Assert.False(target.Disposed);
        Assert.False(judge.Disposed);
    }

    /// <summary>
    /// The judge receives the reconstructed conversation history and captured target response, not a live
    /// target call.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_JudgeSeesReconstructedCapturedEvidence()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(TextResponse(JudgeResponse(score: "5")));
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        _ = await LiveLLMEvidenceEvaluation.EvaluateAsync(
            execution,
            clients,
            new GroundednessEvaluator(),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 5);

        IReadOnlyList<ChatMessage> judgeMessages = judge.Requests[0].Messages;
        Assert.Equal(
            [ChatRole.System, ChatRole.User],
            judgeMessages.Select(static message => message.Role));
        string judgePrompt = judgeMessages[1].Text;
        Assert.Contains($"CONTEXT: {GroundingContext}", judgePrompt, StringComparison.Ordinal);
        Assert.Contains("RESPONSE:", judgePrompt, StringComparison.Ordinal);
        Assert.Contains(TargetAnswer, judgePrompt, StringComparison.Ordinal);
        Assert.Contains("QUERY:", judgePrompt, StringComparison.Ordinal);
        Assert.Contains(PlayerQuestion, judgePrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A score exactly at the threshold passes: the threshold comparison is inclusive.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithScoreEqualToThreshold_PassesInclusively()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(TextResponse(JudgeResponse(score: "4")));
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await LiveLLMEvidenceEvaluation.EvaluateAsync(
            execution,
            clients,
            new GroundednessEvaluator(),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 4);

        Assert.Equal(LiveLLMJudgeVerdictStatus.Success, verdict.Status);
        Assert.Equal(4d, verdict.Score);
    }

    /// <summary>
    /// A valid below-threshold score is recorded as a valid but unsuccessful verdict, never as an invalid
    /// evaluation, with metric name, score, threshold, rating, and sanitised reasoning in the failure message.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithValidBelowThresholdScore_RecordsUnsuccessfulVerdict()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(TextResponse(JudgeResponse(score: "4")));
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await LiveLLMEvidenceEvaluation.EvaluateAsync(
            execution,
            clients,
            new GroundednessEvaluator(),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 5);

        Assert.Equal(LiveLLMJudgeVerdictStatus.BelowThreshold, verdict.Status);
        Assert.False(verdict.Passed);
        Assert.Equal(4d, verdict.Score);
        Assert.NotNull(verdict.FailureMessage);
        Assert.Contains(GroundednessEvaluator.GroundednessMetricName, verdict.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("4", verdict.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("5", verdict.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(JudgeReason, verdict.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>A judge score that is not a number yields an invalid evaluation that can never pass.</summary>
    [Fact]
    public async Task EvaluateAsync_WhenJudgeScoreIsNotNumeric_IsInvalidEvaluation()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(TextResponse(JudgeResponse(score: "excellent")));
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await EvaluateAsync(clients, execution);

        Assert.Equal(LiveLLMJudgeVerdictStatus.InvalidEvaluation, verdict.Status);
        Assert.False(verdict.Passed);
        Assert.Null(verdict.Score);
    }

    /// <summary>A judge score outside the documented 1 to 5 rubric yields an invalid evaluation.</summary>
    [Fact]
    public async Task EvaluateAsync_WhenJudgeScoreIsOutOfRange_IsInvalidEvaluation()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(TextResponse(JudgeResponse(score: "7")));
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await EvaluateAsync(clients, execution);

        Assert.Equal(LiveLLMJudgeVerdictStatus.InvalidEvaluation, verdict.Status);
        Assert.Contains("documented range", verdict.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("7", verdict.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>A verdict without judge reasoning yields an invalid evaluation.</summary>
    [Fact]
    public async Task EvaluateAsync_WhenJudgeReasonIsMissing_IsInvalidEvaluation()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(TextResponse("<S0>Let's think step by step.</S0>, <S2>5</S2>"));
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await EvaluateAsync(clients, execution);

        Assert.Equal(LiveLLMJudgeVerdictStatus.InvalidEvaluation, verdict.Status);
        Assert.Contains("no judge reasoning", verdict.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>A score the library's own interpretation marks failed yields an invalid evaluation.</summary>
    [Fact]
    public async Task EvaluateAsync_WhenInterpretationMarksFailure_IsInvalidEvaluation()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(TextResponse(JudgeResponse(score: "2")));
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await EvaluateAsync(clients, execution);

        Assert.Equal(LiveLLMJudgeVerdictStatus.InvalidEvaluation, verdict.Status);
        Assert.Contains("interpretation", verdict.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Poor", verdict.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>An evaluator that declares no metric at all yields an invalid evaluation.</summary>
    [Fact]
    public async Task EvaluateAsync_WhenEvaluatorDeclaresNoMetric_IsInvalidEvaluation()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        using LiveLLMClientPair clients = new(target, new LiveLLMScriptedChatClient("judge"));
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await LiveLLMEvidenceEvaluation.EvaluateAsync(
            execution,
            clients,
            new LiveLLMStubbedEvaluator(new EvaluationResult()),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 5);

        Assert.Equal(LiveLLMJudgeVerdictStatus.InvalidEvaluation, verdict.Status);
        Assert.Contains("instead of exactly one", verdict.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>An evaluation result that lacks the declared metric yields an invalid evaluation.</summary>
    [Fact]
    public async Task EvaluateAsync_WhenMetricIsAbsent_IsInvalidEvaluation()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        using LiveLLMClientPair clients = new(target, new LiveLLMScriptedChatClient("judge"));
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await LiveLLMEvidenceEvaluation.EvaluateAsync(
            execution,
            clients,
            new LiveLLMStubbedEvaluator(
                new EvaluationResult(new NumericMetric("Unrelated", 5, JudgeReason)),
                "Groundedness"),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 5);

        Assert.Equal(LiveLLMJudgeVerdictStatus.InvalidEvaluation, verdict.Status);
        Assert.Contains("absent", verdict.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Unrelated", verdict.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>A declared metric of a non-numeric type yields an invalid evaluation naming both types.</summary>
    [Fact]
    public async Task EvaluateAsync_WhenMetricIsWrongType_IsInvalidEvaluation()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        using LiveLLMClientPair clients = new(target, new LiveLLMScriptedChatClient("judge"));
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await LiveLLMEvidenceEvaluation.EvaluateAsync(
            execution,
            clients,
            new LiveLLMStubbedEvaluator(
                new EvaluationResult(new BooleanMetric("Groundedness", true, JudgeReason)),
                "Groundedness"),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 5);

        Assert.Equal(LiveLLMJudgeVerdictStatus.InvalidEvaluation, verdict.Status);
        Assert.Contains(nameof(BooleanMetric), verdict.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(nameof(NumericMetric), verdict.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Credential sentinels placed in judge reasoning never appear in verdict failure messages, which carry
    /// only sanitised details.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_FailureMessagesNeverContainCredentialSentinels()
    {
        const string sentinelToken = "sk-VERDICTSENTINEL0123456789abcdef012345";
        const string sentinelAssignedSecret = "ApiKey=VERDICTSYNTHETICSECRET1234567890";
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(TextResponse(
            $"<S0>Let's think step by step.</S0>, <S1>{sentinelToken} {sentinelAssignedSecret}</S1>, <S2>4</S2>"));
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        LiveLLMJudgeVerdict verdict = await EvaluateAsync(clients, execution);

        Assert.Equal(LiveLLMJudgeVerdictStatus.BelowThreshold, verdict.Status);
        Assert.DoesNotContain("VERDICTSENTINEL", verdict.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("VERDICTSYNTHETICSECRET", verdict.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("[redacted]", verdict.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// A judge-stage transport failure propagates for execution-failure classification instead of becoming a
    /// score, and the borrowed clients stay undisposed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenJudgeClientFails_PropagatesWithoutDisposing()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.EnqueueError(new HttpRequestException("judge unreachable"));
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        _ = await Assert.ThrowsAsync<HttpRequestException>(
            () => EvaluateAsync(clients, execution));

        Assert.False(target.Disposed);
        Assert.False(judge.Disposed);
    }

    /// <summary>An out-of-range threshold is rejected before any judge call.</summary>
    [Fact]
    public async Task EvaluateAsync_WithOutOfRangeThreshold_ThrowsBeforeJudging()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse(TargetAnswer));
        LiveLLMScriptedChatClient judge = new("judge");
        using LiveLLMClientPair clients = new(target, judge);
        LiveLLMScenarioExecution execution = await ExecuteSingleExchangeAsync(target);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => LiveLLMEvidenceEvaluation.EvaluateAsync(
                execution,
                clients,
                new GroundednessEvaluator(),
                new GroundednessEvaluatorContext(GroundingContext),
                threshold: 6));

        Assert.Empty(judge.Requests);
    }

    /// <summary>
    /// A failed execution cannot be judged: evaluation requires captured evidence from a completed execution.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithFailedExecution_ThrowsAsMisuse()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.EnqueueError(new HttpRequestException("target unreachable"));
        using LiveLLMClientPair clients = new(target, new LiveLLMScriptedChatClient("judge"));

        LiveLLMScenarioExecutor executor = new(target);
        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "failed-execution",
            new LiveLLMScenarioBounds(1, TimeSpan.FromSeconds(30)),
            context => context.SendTargetAsync([new ChatMessage(
                ChatRole.User,
                "any")]));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EvaluateAsync(clients, execution));
    }

    /// <summary>
    /// A completed execution whose evidence holds no target exchange yields an invalid evaluation rather
    /// than a pass.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithoutTargetExchanges_IsInvalidEvaluation()
    {
        LiveLLMScriptedChatClient target = new("target");
        using LiveLLMClientPair clients = new(target, new LiveLLMScriptedChatClient("judge"));

        LiveLLMScenarioExecutor executor = new(target);
        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "no-exchanges",
            new LiveLLMScenarioBounds(1, TimeSpan.FromSeconds(30)),
            context =>
            {
                _ = context.RecordScriptedContinuation(
                    new ChatMessage(ChatRole.User, "unused"));
                return Task.CompletedTask;
            });

        Assert.True(execution.Succeeded);

        LiveLLMJudgeVerdict verdict = await EvaluateAsync(clients, execution);

        Assert.Equal(LiveLLMJudgeVerdictStatus.InvalidEvaluation, verdict.Status);
        Assert.Contains("no completed target exchange", verdict.FailureMessage, StringComparison.Ordinal);
    }

    private static Task<LiveLLMJudgeVerdict> EvaluateAsync(LiveLLMClientPair clients, LiveLLMScenarioExecution execution)
        => LiveLLMEvidenceEvaluation.EvaluateAsync(
            execution,
            clients,
            new GroundednessEvaluator(),
            new GroundednessEvaluatorContext(GroundingContext),
            threshold: 5);

    private static async Task<LiveLLMScenarioExecution> ExecuteSingleExchangeAsync(LiveLLMScriptedChatClient target)
    {
        LiveLLMScenarioExecutor executor = new(target);
        return await executor.ExecuteAsync(
            "evidence-evaluation",
            new LiveLLMScenarioBounds(1, TimeSpan.FromSeconds(30)),
            context => context.SendTargetAsync(
                [
                    new ChatMessage(
                        ChatRole.System,
                        "You are Vadim, a records officer. Answer briefly from the supplied records."),
                    new ChatMessage(ChatRole.User, PlayerQuestion),
                ]));
    }

    private static ChatResponse TextResponse(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));

    private static string JudgeResponse(string score)
        => $"<S0>Let's think step by step: the response matches the context.</S0>, <S1>{JudgeReason}</S1>, <S2>{score}</S2>";
}
