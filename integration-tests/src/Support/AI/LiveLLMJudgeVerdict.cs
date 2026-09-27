namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>Outcome classification of one judge evaluation over captured evidence.</summary>
public enum LiveLLMJudgeVerdictStatus
{
    /// <summary>The single declared metric passed every fail-closed check and met the inclusive threshold.</summary>
    Success,

    /// <summary>
    /// The metric passed every fail-closed check but scored below the inclusive threshold: a valid,
    /// unsuccessful evaluation.
    /// </summary>
    BelowThreshold,

    /// <summary>
    /// Malformed judge output or an unusable evaluation result. An invalid evaluation can never pass,
    /// whatever the aggregation rule.
    /// </summary>
    InvalidEvaluation,
}

/// <summary>
/// Structured verdict of one judge evaluation performed over captured evidence, without re-running the
/// target. Failure messages carry only metric names, scores, interpretation ratings, thresholds, and
/// bounded sanitised judge reasoning.
/// </summary>
/// <remarks>
/// The optional <see cref="Category" /> carries a categorical label extracted from the judge output by a
/// caller-supplied category reader — for example a behavioural category alongside the numeric score. It is
/// <see langword="null" /> when no reader was supplied; a reader that yields nothing classifies the verdict
/// as an invalid evaluation.
/// </remarks>
public sealed record LiveLLMJudgeVerdict(
    LiveLLMJudgeVerdictStatus Status,
    double Threshold,
    string? MetricName,
    double? Score,
    string? Rating,
    string? Reason,
    string? FailureMessage,
    string? Category = null)
{
    /// <summary>Whether this verdict is a passing evaluation.</summary>
    public bool Passed => Status == LiveLLMJudgeVerdictStatus.Success;
}
