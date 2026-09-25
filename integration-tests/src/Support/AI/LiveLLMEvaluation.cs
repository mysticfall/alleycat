using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit.Sdk;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Validated outcome of one successful live LLM evaluation: the target response plus the metric details that
/// passed every fail-closed check.
/// </summary>
public sealed record LiveLLMEvaluationOutcome(
    ChatResponse TargetResponse,
    string MetricName,
    double Score,
    string Reason);

/// <summary>
/// Single-shot target-then-judge evaluation flow for live LLM integration tests.
/// </summary>
/// <remarks>
/// <para>
/// The flow performs exactly one non-streaming target request and exactly one judge evaluation, in that order,
/// with no retries: transport retries are already zeroed by <see cref="LiveLLMClientFactory" />. The target
/// response is rejected before judging when it carries no assistant text or attempts function calls. The judge
/// result is validated fail-closed — metric present, numeric, finite, within the documented 1 to 5 range,
/// carrying judge reasoning, free of error-severity diagnostics, and not marked failed by its own
/// interpretation — before it is compared against the explicit threshold.
/// </para>
/// <para>
/// The supplied <see cref="LiveLLMClientPair" /> is owned by the call and disposed on every path after argument
/// validation. Failure messages carry only metric names, scores, ratings, and sanitised judge reasoning: raw
/// provider exceptions, full diagnostic collections, configuration records, and credential-shaped values are
/// never serialised into assertion output.
/// </para>
/// </remarks>
public static class LiveLLMEvaluation
{
    /// <summary>Lowest score the documented evaluation rubric produces.</summary>
    public const double MinimumScore = 1d;

    /// <summary>Highest score the documented evaluation rubric produces.</summary>
    public const double MaximumScore = 5d;

    private const int MaximumReasonLength = 400;

    private static readonly Regex _credentialAssignmentPattern = new(
        @"(?i)\b(api[-_ ]?key|apikey|authorization|bearer|token|password|passwd|secret|credential)s?\b\s*[:=]\s*\S+",
        RegexOptions.Compiled);

    private static readonly Regex _credentialUrlPattern = new(
        @"[a-z][a-z0-9+.-]*://[^\s/@:]+:[^\s/@]+@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _opaqueTokenPattern = new(@"[A-Za-z0-9_+/=-]{24,}", RegexOptions.Compiled);

    /// <summary>
    /// Evaluates one live target response against one judge-produced numeric metric and an explicit threshold.
    /// </summary>
    /// <param name="messages">Conversation sent to the target; also forwarded to the evaluator as history.</param>
    /// <param name="clients">
    /// Target and judge clients. Owned by this call and disposed on every path after argument validation.
    /// </param>
    /// <param name="evaluator">Evaluator that produces exactly one numeric metric.</param>
    /// <param name="evaluationContext">
    /// Evaluator-specific context (for example <c>GroundednessEvaluatorContext</c>) passed explicitly to the judge.
    /// </param>
    /// <param name="threshold">Inclusive pass threshold within the documented 1 to 5 score range.</param>
    /// <param name="cancellationToken">Token forwarded to both the target and the judge boundary.</param>
    /// <returns>The validated evaluation outcome.</returns>
    public static async Task<LiveLLMEvaluationOutcome> EvaluateAsync(
        IReadOnlyList<ChatMessage> messages,
        LiveLLMClientPair clients,
        IEvaluator evaluator,
        EvaluationContext evaluationContext,
        double threshold,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(evaluationContext);

        try
        {
            ValidateThreshold(threshold);

            ChatResponse response = await clients.Target.GetResponseAsync(messages, options: null, cancellationToken)
                .ConfigureAwait(false);
            ValidateTargetResponse(response);

            EvaluationResult result = await evaluator.EvaluateAsync(
                messages,
                response,
                new ChatConfiguration(clients.Judge),
                [evaluationContext],
                cancellationToken).ConfigureAwait(false);

            return ValidateMetric(result, evaluator, threshold, response);
        }
        finally
        {
            clients.Dispose();
        }
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

    private static void ValidateTargetResponse(ChatResponse response)
    {
        bool hasText = !string.IsNullOrWhiteSpace(response.Text);
        int functionCallCount = response.Messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionCallContent>()
            .Count();

        if (!hasText || functionCallCount > 0)
        {
            string reasons = hasText ? string.Empty : "no assistant text; ";
            reasons += $"{functionCallCount} function call(s)";

            throw new XunitException(
                $"Live LLM evaluation rejected the target response before judging: {reasons}.");
        }
    }

    private static LiveLLMEvaluationOutcome ValidateMetric(
        EvaluationResult result,
        IEvaluator evaluator,
        double threshold,
        ChatResponse response)
    {
        string metricName = ResolveSingleMetricName(evaluator);

        if (!result.Metrics.TryGetValue(metricName, out EvaluationMetric? metric))
        {
            string presentMetrics = result.Metrics.Count == 0
                ? "(none)"
                : string.Join(", ", result.Metrics.Keys.Order());
            throw new XunitException(
                $"Live LLM evaluation failed before scoring: metric '{metricName}' was absent from the evaluation result. "
                + $"Metrics present: {presentMetrics}.");
        }

        if (metric is not NumericMetric numeric)
        {
            throw new XunitException(
                $"Live LLM evaluation failed before scoring: metric '{metricName}' was of type '{metric.GetType().Name}' "
                + $"instead of '{nameof(NumericMetric)}'.");
        }

        int errorDiagnostics = numeric.Diagnostics?.Count(
            static diagnostic => diagnostic.Severity == EvaluationDiagnosticSeverity.Error) ?? 0;
        if (errorDiagnostics > 0)
        {
            throw new XunitException(
                $"Live LLM evaluation failed closed: metric '{metricName}' carries {errorDiagnostics} error-severity "
                + "diagnostic(s), so its score cannot be trusted.");
        }

        if (numeric.Value is not double score)
        {
            throw new XunitException(
                $"Live LLM evaluation failed closed: metric '{metricName}' carried no score.");
        }

        if (!double.IsFinite(score))
        {
            throw new XunitException(
                $"Live LLM evaluation failed closed: metric '{metricName}' carried a non-finite score.");
        }

        if (score is < MinimumScore or > MaximumScore)
        {
            throw new XunitException(
                $"Live LLM evaluation failed closed: metric '{metricName}' scored outside the documented range "
                + $"{FormatScore(MinimumScore)} to {FormatScore(MaximumScore)} (actual: {FormatScore(score)}).");
        }

        if (string.IsNullOrWhiteSpace(numeric.Reason))
        {
            throw new XunitException(
                $"Live LLM evaluation failed closed: metric '{metricName}' carried no judge reasoning.");
        }

        if (numeric.Interpretation is { Failed: true } interpretation)
        {
            string reason = string.IsNullOrWhiteSpace(interpretation.Reason)
                ? string.Empty
                : $" {Sanitise(interpretation.Reason)}";
            throw new XunitException(
                $"Live LLM evaluation failed closed: metric '{metricName}' is marked failed by its own interpretation "
                + $"(rating: {interpretation.Rating}).{reason}");
        }

        if (score < threshold)
        {
            string rating = numeric.Interpretation?.Rating.ToString() ?? "unavailable";
            throw new XunitException(
                $"Live LLM evaluation failed the required threshold: metric '{metricName}' scored {FormatScore(score)} "
                + $"against threshold {FormatScore(threshold)} (interpretation rating: {rating}). "
                + $"Judge reasoning: {Sanitise(numeric.Reason)}");
        }

        return new LiveLLMEvaluationOutcome(response, metricName, score, numeric.Reason!);
    }

    private static string ResolveSingleMetricName(IEvaluator evaluator)
    {
        IReadOnlyCollection<string> names = evaluator.EvaluationMetricNames;
        return names.Count == 1
            ? names.First()
            : throw new XunitException(
                $"Live LLM evaluation requires an evaluator that declares exactly one metric; this evaluator declared "
                + $"{names.Count}: {string.Join(", ", names.Order())}.");
    }

    /// <summary>
    /// Redacts credential-shaped values and bounds length so judge text can be surfaced in failure output safely.
    /// </summary>
    internal static string Sanitise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string bounded = text.Length <= MaximumReasonLength
            ? text
            : $"{text[..MaximumReasonLength]}[truncated]";
        bounded = _credentialAssignmentPattern.Replace(bounded, "$1=[redacted]");
        bounded = _credentialUrlPattern.Replace(bounded, "[redacted]://");
        bounded = _opaqueTokenPattern.Replace(bounded, "[redacted]");
        return bounded;
    }

    private static string FormatScore(double score) => score.ToString(CultureInfo.InvariantCulture);
}
