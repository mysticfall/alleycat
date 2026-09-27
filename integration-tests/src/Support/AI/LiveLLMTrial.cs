namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Category of one attempted trial in a fixed declared batch. Every attempted trial is retained and
/// reported in exactly one category.
/// </summary>
public enum LiveLLMTrialCategory
{
    /// <summary>The execution completed and every judge evaluation met its inclusive threshold.</summary>
    Success,

    /// <summary>A valid but unsuccessful trial, for example a below-threshold score.</summary>
    Failure,

    /// <summary>An invalid evaluation, for example malformed judge output. Can never pass.</summary>
    InvalidEvaluation,

    /// <summary>The scenario execution failed, or the trial itself failed outside evaluation.</summary>
    ExecutionFailure,
}

/// <summary>
/// One attempted trial: its category, the execution evidence, and the judge verdicts produced over that
/// evidence. A trial that never ran is not a trial record — the batch reports it as unattempted.
/// </summary>
/// <remarks>
/// <para>
/// Trial classification composes the execution and verdict stages: a failed execution is an
/// <see cref="LiveLLMTrialCategory.ExecutionFailure" />; otherwise any invalid evaluation makes the trial an
/// <see cref="LiveLLMTrialCategory.InvalidEvaluation" />; otherwise the trial succeeds only when every
/// judge evaluation passed its inclusive threshold, and a valid below-threshold score is recorded as a
/// <see cref="LiveLLMTrialCategory.Failure" /> rather than an abort.
/// </para>
/// <para>
/// Failure messages are sanitised through the assertion-output rules, so they never carry raw provider
/// exceptions, diagnostic collections, configuration records, or credential-shaped values.
/// </para>
/// </remarks>
public sealed record LiveLLMTrial(
    int Index,
    LiveLLMTrialCategory Category,
    LiveLLMScenarioExecution? Execution,
    IReadOnlyList<LiveLLMJudgeVerdict> Verdicts,
    string? FailureMessage)
{
    /// <summary>
    /// Records a failed execution as an execution-failure trial.
    /// </summary>
    /// <param name="index">Zero-based trial index within the declared batch.</param>
    /// <param name="execution">The failed execution whose partial evidence is retained.</param>
    /// <returns>The classified trial.</returns>
    public static LiveLLMTrial FromExecution(int index, LiveLLMScenarioExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return execution.Succeeded
            ? throw new InvalidOperationException(
                "A succeeded execution must be recorded through its judge verdicts, not as an execution failure.")
            : FromFailure(index, execution.Failure!, execution);
    }

    /// <summary>
    /// Records a trial that failed outside scenario evaluation, for example an exception escaping the
    /// trial's own orchestration code.
    /// </summary>
    /// <param name="index">Zero-based trial index within the declared batch.</param>
    /// <param name="failure">Sanitised failure record classifying the trial failure.</param>
    /// <param name="execution">Optional execution evidence captured before the failure.</param>
    /// <returns>The classified trial.</returns>
    public static LiveLLMTrial FromFailure(int index, LiveLLMFailureRecord failure, LiveLLMScenarioExecution? execution = null)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new LiveLLMTrial(
            index,
            LiveLLMTrialCategory.ExecutionFailure,
            execution,
            [],
            $"Execution failure ({failure.Classification}): {failure.ExceptionType}: "
            + $"{LiveLLMEvaluation.Sanitise(failure.SafeMessage)}");
    }

    /// <summary>
    /// Classifies a completed execution from the judge verdicts produced over its captured evidence.
    /// </summary>
    /// <param name="index">Zero-based trial index within the declared batch.</param>
    /// <param name="execution">The completed execution whose evidence was judged.</param>
    /// <param name="verdicts">
    /// Every judge evaluation for this trial; each is its own single-metric evaluation. May be empty only
    /// when diagnosing — an empty verdict list classifies as an invalid evaluation.
    /// </param>
    /// <returns>The classified trial.</returns>
    public static LiveLLMTrial FromVerdicts(
        int index,
        LiveLLMScenarioExecution execution,
        IReadOnlyList<LiveLLMJudgeVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(verdicts);

        if (!execution.Succeeded)
        {
            throw new InvalidOperationException(
                "A failed execution must be recorded with its failure, not with judge verdicts.");
        }

        if (verdicts.Count == 0)
        {
            return new LiveLLMTrial(
                index,
                LiveLLMTrialCategory.InvalidEvaluation,
                execution,
                verdicts,
                "The trial recorded no judge evaluation.");
        }

        if (verdicts.Any(static verdict => verdict.Status == LiveLLMJudgeVerdictStatus.InvalidEvaluation))
        {
            LiveLLMJudgeVerdict invalid = verdicts.First(
                static verdict => verdict.Status == LiveLLMJudgeVerdictStatus.InvalidEvaluation);
            return new LiveLLMTrial(
                index,
                LiveLLMTrialCategory.InvalidEvaluation,
                execution,
                verdicts,
                invalid.FailureMessage);
        }

        if (verdicts.All(static verdict => verdict.Status == LiveLLMJudgeVerdictStatus.Success))
        {
            return new LiveLLMTrial(index, LiveLLMTrialCategory.Success, execution, verdicts, FailureMessage: null);
        }

        LiveLLMJudgeVerdict failing = verdicts.First(static verdict => verdict.Status != LiveLLMJudgeVerdictStatus.Success);
        return new LiveLLMTrial(index, LiveLLMTrialCategory.Failure, execution, verdicts, failing.FailureMessage);
    }
}
