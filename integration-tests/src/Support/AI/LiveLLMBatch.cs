namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Fixed declared batch of trials for one live LLM experiment.
/// </summary>
/// <remarks>
/// <para>
/// The trial count and the aggregation rule — the required success rate — are declared before execution
/// starts and are immutable afterwards; neither can be adapted to the results observed so far. Trials run
/// serially to the declared end: a failed trial is a recorded outcome, the batch neither retries failures
/// nor stops early once enough successes accumulate, and an interrupted batch keeps its unattempted trials
/// counted against the declared batch.
/// </para>
/// <para>
/// The success rate divides successful trials by the <em>declared</em> trial count, so trials that never
/// ran count against the batch rather than being absent. A batch passes only when it is complete and its
/// success rate meets the declared requirement.
/// </para>
/// </remarks>
public sealed class LiveLLMBatch
{
    /// <summary>
    /// Declares a fixed batch.
    /// </summary>
    /// <param name="declaredTrialCount">Number of trials the batch will run; at least one.</param>
    /// <param name="requiredSuccessRate">
    /// Declared inclusive requirement on the success rate — successful trials over the declared trial
    /// count — expressed as a fraction between 0 and 1.
    /// </param>
    public LiveLLMBatch(int declaredTrialCount, double requiredSuccessRate)
    {
        if (declaredTrialCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(declaredTrialCount),
                declaredTrialCount,
                "A declared batch must contain at least one trial.");
        }

        if (!double.IsFinite(requiredSuccessRate) || requiredSuccessRate is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredSuccessRate),
                requiredSuccessRate,
                "The declared required success rate must be a finite fraction between 0 and 1.");
        }

        DeclaredTrialCount = declaredTrialCount;
        RequiredSuccessRate = requiredSuccessRate;
    }

    /// <summary>Number of trials declared before execution; the denominator of the success rate.</summary>
    public int DeclaredTrialCount
    {
        get;
    }

    /// <summary>Declared inclusive success-rate requirement, as a fraction between 0 and 1.</summary>
    public double RequiredSuccessRate
    {
        get;
    }

    /// <summary>
    /// Runs the declared batch serially, retaining every attempted trial.
    /// </summary>
    /// <param name="trial">
    /// Delegate producing one trial per index. Exceptions it throws are recorded as execution-failure
    /// trials; they neither abort the batch nor remove the trial from its category.
    /// </param>
    /// <param name="cancellationToken">
    /// Token that interrupts the batch; remaining trials stay unattempted and the batch is incomplete.
    /// </param>
    /// <returns>The aggregate batch result.</returns>
    public async Task<LiveLLMBatchResult> RunAsync(
        Func<int, CancellationToken, Task<LiveLLMTrial>> trial,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trial);

        var trials = new LiveLLMTrial?[DeclaredTrialCount];
        for (int index = 0; index < DeclaredTrialCount; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                trials[index] = await trial(index, cancellationToken).ConfigureAwait(false)
                    ?? LiveLLMTrial.FromFailure(
                        index,
                        new LiveLLMFailureRecord(
                            LiveLLMFailureClassification.Unknown,
                            "MissingTrial",
                            "The trial delegate returned no trial."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                trials[index] = LiveLLMTrial.FromFailure(index, LiveLLMFailureRecord.FromException(ex, timedOut: false));
            }
        }

        return new LiveLLMBatchResult(DeclaredTrialCount, RequiredSuccessRate, [.. trials]);
    }
}

/// <summary>
/// Aggregate outcome of one fixed declared batch: every attempted trial in its category, the derived
/// counts, and the batch-level pass decision.
/// </summary>
public sealed record LiveLLMBatchResult(
    int DeclaredTrialCount,
    double RequiredSuccessRate,
    IReadOnlyList<LiveLLMTrial?> Trials)
{
    /// <summary>Trials that ran, whatever their category.</summary>
    public int AttemptedTrials => Trials.Count(static trial => trial is not null);

    /// <summary>Trials whose execution completed and every judge evaluation passed its threshold.</summary>
    public int SuccessfulTrials => Trials.Count(static trial => trial?.Category == LiveLLMTrialCategory.Success);

    /// <summary>Valid but unsuccessful trials, for example below-threshold scores.</summary>
    public int UnsuccessfulTrials => Trials.Count(static trial => trial?.Category == LiveLLMTrialCategory.Failure);

    /// <summary>Trials with invalid evaluations, for example malformed judge output.</summary>
    public int InvalidEvaluationTrials
        => Trials.Count(static trial => trial?.Category == LiveLLMTrialCategory.InvalidEvaluation);

    /// <summary>Trials whose scenario execution — or trial orchestration — failed.</summary>
    public int ExecutionFailureTrials
        => Trials.Count(static trial => trial?.Category == LiveLLMTrialCategory.ExecutionFailure);

    /// <summary>Declared trials that never ran; they count against the declared batch.</summary>
    public int UnattemptedTrials => DeclaredTrialCount - AttemptedTrials;

    /// <summary>Successful trials divided by the declared trial count.</summary>
    public double SuccessRate => SuccessfulTrials / (double)DeclaredTrialCount;

    /// <summary>Whether every declared trial was attempted.</summary>
    public bool IsComplete => UnattemptedTrials == 0;

    /// <summary>Whether the success rate meets the declared inclusive requirement.</summary>
    public bool MeetsDeclaredRule => SuccessRate >= RequiredSuccessRate;

    /// <summary>
    /// Whether the batch passes: it must be complete and meet its declared rule. An incomplete batch can
    /// never pass, whatever its attempted successes.
    /// </summary>
    public bool Passed => IsComplete && MeetsDeclaredRule;
}
