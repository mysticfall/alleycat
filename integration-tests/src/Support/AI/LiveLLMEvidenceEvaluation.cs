using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Judge evaluation over captured scenario evidence, without re-running the target to obtain it.
/// </summary>
/// <remarks>
/// <para>
/// The evaluation stage consumes the evidence trace of a completed execution: the conversation history of
/// the final target exchange and its captured response are reconstructed from the immutable trace records
/// and handed to the judge. The target client is never contacted.
/// </para>
/// <para>
/// Judge results are validated fail-closed before scoring, mirroring <see cref="LiveLLMEvaluation" />: the
/// evaluator declares exactly one metric; the metric is present and a <see cref="NumericMetric" />; its
/// value is finite and within the documented 1 to 5 rubric; its reason is non-empty; it carries no
/// error-severity diagnostics; and its interpretation is not marked failed. The threshold comparison is
/// inclusive. Malformed judge output yields an <see cref="LiveLLMJudgeVerdictStatus.InvalidEvaluation" />
/// verdict and can never pass, whatever the aggregation rule.
/// </para>
/// <para>
/// The supplied <see cref="LiveLLMClientPair" /> is borrowed, never disposed: the experiment or test owns
/// the pair for its complete lifetime. Judge-stage transport failures propagate to the caller for
/// execution-failure classification; they are never recorded as scores.
/// </para>
/// </remarks>
public static class LiveLLMEvidenceEvaluation
{
    /// <summary>Lowest score the documented evaluation rubric produces.</summary>
    public const double MinimumScore = LiveLLMEvaluation.MinimumScore;

    /// <summary>Highest score the documented evaluation rubric produces.</summary>
    public const double MaximumScore = LiveLLMEvaluation.MaximumScore;

    /// <summary>
    /// Judges the captured evidence of a completed execution against one judge-produced numeric metric and
    /// an explicit inclusive threshold.
    /// </summary>
    /// <param name="execution">A completed execution whose captured evidence is judged.</param>
    /// <param name="clients">Client pair whose judge client is borrowed for this evaluation; never disposed.</param>
    /// <param name="evaluator">Evaluator that declares exactly one numeric metric.</param>
    /// <param name="evaluationContext">
    /// Evaluator-specific context (for example <c>GroundednessEvaluatorContext</c>) passed explicitly to the judge.
    /// </param>
    /// <param name="threshold">Inclusive pass threshold within the documented 1 to 5 score range.</param>
    /// <param name="cancellationToken">Token forwarded to the judge boundary.</param>
    /// <param name="readCategory">
    /// Optional reader extracting a categorical label from the evaluation result — for judges that return a
    /// category beside their numeric score. When supplied, the verdict is valid only if the reader yields a
    /// non-empty label; a reader that throws or yields nothing classifies the verdict as an invalid
    /// evaluation.
    /// </param>
    /// <returns>The structured verdict of this evaluation.</returns>
    public static async Task<LiveLLMJudgeVerdict> EvaluateAsync(
        LiveLLMScenarioExecution execution,
        LiveLLMClientPair clients,
        IEvaluator evaluator,
        EvaluationContext evaluationContext,
        double threshold,
        CancellationToken cancellationToken = default,
        Func<EvaluationResult, string?>? readCategory = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(evaluationContext);
        ValidateThreshold(threshold);

        if (!execution.Succeeded)
        {
            throw new InvalidOperationException(
                "Judge evaluation requires captured evidence from a completed execution; this execution failed.");
        }

        LiveLLMTraceEntry? finalExchange = execution.Trace.Entries.LastOrDefault(static entry =>
            entry.Kind == LiveLLMTraceEntryKind.Exchange
            && entry.Purpose == LiveLLMPurpose.Target
            && entry.Response is not null);

        if (finalExchange?.Request is not { } request || finalExchange.Response is not { } responseRecord)
        {
            return Invalid(
                threshold,
                "The captured evidence contains no completed target exchange to evaluate.");
        }

        List<ChatMessage> history = [];
        foreach (LiveLLMMessageRecord messageRecord in request.Messages)
        {
            history.Add(Reconstruction.ToChatMessage(messageRecord));
        }

        var response = Reconstruction.ToChatResponse(responseRecord);

        EvaluationResult result = await evaluator.EvaluateAsync(
            history,
            response,
            new ChatConfiguration(clients.Judge),
            [evaluationContext],
            cancellationToken).ConfigureAwait(false);

        return ValidateMetric(result, evaluator, threshold, readCategory);
    }

    private static void ValidateThreshold(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold < MinimumScore || threshold > MaximumScore)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                threshold,
                $"The evaluation threshold must be a finite score between {FormatScore(MinimumScore)} and {FormatScore(MaximumScore)}.");
        }
    }

    private static LiveLLMJudgeVerdict ValidateMetric(
        EvaluationResult result,
        IEvaluator evaluator,
        double threshold,
        Func<EvaluationResult, string?>? readCategory)
    {
        IReadOnlyCollection<string> declaredNames = evaluator.EvaluationMetricNames;
        if (declaredNames.Count != 1)
        {
            return Invalid(
                threshold,
                $"The evaluator declared {declaredNames.Count} metrics instead of exactly one: "
                + $"{string.Join(", ", declaredNames.Order())}.");
        }

        string metricName = declaredNames.First();

        if (!result.Metrics.TryGetValue(metricName, out EvaluationMetric? metric))
        {
            string presentMetrics = result.Metrics.Count == 0
                ? "(none)"
                : string.Join(", ", result.Metrics.Keys.Order());
            return Invalid(
                threshold,
                $"Metric '{metricName}' was absent from the evaluation result. Metrics present: {presentMetrics}.");
        }

        if (metric is not NumericMetric numeric)
        {
            return Invalid(
                threshold,
                $"Metric '{metricName}' was of type '{metric.GetType().Name}' instead of '{nameof(NumericMetric)}'.");
        }

        int errorDiagnostics = numeric.Diagnostics?.Count(
            static diagnostic => diagnostic.Severity == EvaluationDiagnosticSeverity.Error) ?? 0;
        if (errorDiagnostics > 0)
        {
            return Invalid(
                threshold,
                $"Metric '{metricName}' carries {errorDiagnostics} error-severity diagnostic(s), so its score cannot be trusted.");
        }

        if (numeric.Value is not double score)
        {
            return Invalid(threshold, $"Metric '{metricName}' carried no score.");
        }

        if (!double.IsFinite(score))
        {
            return Invalid(threshold, $"Metric '{metricName}' carried a non-finite score.");
        }

        if (score is < MinimumScore or > MaximumScore)
        {
            return Invalid(
                threshold,
                $"Metric '{metricName}' scored outside the documented range {FormatScore(MinimumScore)} to "
                + $"{FormatScore(MaximumScore)} (actual: {FormatScore(score)}).");
        }

        if (string.IsNullOrWhiteSpace(numeric.Reason))
        {
            return Invalid(threshold, $"Metric '{metricName}' carried no judge reasoning.");
        }

        if (numeric.Interpretation is { Failed: true } interpretation)
        {
            string reason = string.IsNullOrWhiteSpace(interpretation.Reason)
                ? string.Empty
                : $" {LiveLLMEvaluation.Sanitise(interpretation.Reason)}";
            return Invalid(
                threshold,
                $"Metric '{metricName}' is marked failed by its own interpretation (rating: {interpretation.Rating}).{reason}");
        }

        string rating = numeric.Interpretation?.Rating.ToString() ?? "unavailable";
        string sanitisedReason = LiveLLMEvaluation.Sanitise(numeric.Reason);
        bool metThreshold = score >= threshold;

        string? category = null;
        if (readCategory is not null)
        {
            try
            {
                category = readCategory(result);
            }
            catch (Exception ex)
            {
                return Invalid(threshold, $"The judge category could not be read: {ex.GetType().Name}.");
            }

            if (string.IsNullOrWhiteSpace(category))
            {
                return Invalid(threshold, $"Metric '{metricName}' carried no readable judge category.");
            }
        }

        return new LiveLLMJudgeVerdict(
            metThreshold ? LiveLLMJudgeVerdictStatus.Success : LiveLLMJudgeVerdictStatus.BelowThreshold,
            threshold,
            metricName,
            score,
            rating,
            sanitisedReason,
            metThreshold
                ? null
                : $"Metric '{metricName}' scored {FormatScore(score)} against threshold {FormatScore(threshold)} "
                + $"(interpretation rating: {rating}). Judge reasoning: {sanitisedReason}",
            category);
    }

    private static LiveLLMJudgeVerdict Invalid(double threshold, string message)
        => new(LiveLLMJudgeVerdictStatus.InvalidEvaluation, threshold, null, null, null, null, message);

    private static string FormatScore(double score) => score.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Rebuilds <see cref="ChatMessage" /> and <see cref="ChatResponse" /> objects from immutable trace
    /// records, so judging consumes exactly the captured evidence.
    /// </summary>
    private static class Reconstruction
    {
        public static ChatMessage ToChatMessage(LiveLLMMessageRecord record)
        {
            List<AIContent> contents = [];
            foreach (LiveLLMContentRecord contentRecord in record.Contents)
            {
                if (ToContent(contentRecord) is { } content)
                {
                    contents.Add(content);
                }
            }

            return new ChatMessage(new ChatRole(record.Role), contents);
        }

        public static ChatResponse ToChatResponse(LiveLLMResponseRecord record)
            => new([.. record.Messages.Select(ToChatMessage)])
            {
                ResponseId = record.ResponseId,
                ModelId = record.ModelId,
                CreatedAt = record.CreatedAt ?? DateTimeOffset.UtcNow,
                Usage = record.Usage is { } usage
                    ? new UsageDetails
                    {
                        InputTokenCount = usage.InputTokenCount,
                        OutputTokenCount = usage.OutputTokenCount,
                        TotalTokenCount = usage.TotalTokenCount,
                    }
                    : null,
            };

        private static AIContent? ToContent(LiveLLMContentRecord record)
        {
            switch (record.Kind)
            {
                case "text":
                    return record.Text is { } text ? new TextContent(text) : null;
                case "functionCall":
                    Dictionary<string, object?> arguments = [];
                    if (record.Data is JsonObject argumentObject)
                    {
                        foreach (KeyValuePair<string, JsonNode?> argument in argumentObject)
                        {
                            arguments[argument.Key] = argument.Value?.DeepClone();
                        }
                    }

                    return new FunctionCallContent(record.CallId ?? string.Empty, record.Name ?? string.Empty, arguments);
                case "functionResult":
                    return new FunctionResultContent(record.CallId ?? string.Empty, record.Data?.DeepClone());
                default:
                    return record.Text is { } fallback ? new TextContent(fallback) : null;
            }
        }
    }
}
