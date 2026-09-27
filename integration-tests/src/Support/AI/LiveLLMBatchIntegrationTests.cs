using System.Text.Json;
using AlleyCat.TestFramework;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Deterministic coverage for trial classification, fixed-batch aggregation, and trace artefacts: every
/// attempted trial is retained in its category, malformed judge output and provider failures never pass,
/// incomplete batches cannot pass, no early stop or retry occurs, and written traces are versioned,
/// sanitised JSON. No live backend is contacted.
/// </summary>
[Headless]
public sealed class LiveLLMBatchIntegrationTests
{
    /// <summary>
    /// A mixed four-trial batch reports every attempted trial in its distinct category with the correct
    /// counts, success rate over the declared count, and a pass only when the declared rule is met.
    /// </summary>
    [Fact]
    public async Task RunAsync_ReportsEveryAttemptedTrialInItsCategory()
    {
        LiveLLMBatch batch = new(declaredTrialCount: 4, requiredSuccessRate: 0.25);

        LiveLLMBatchResult result = await batch.RunAsync((index, _) => Task.FromResult(IndexedTrial(index)));

        Assert.Equal(4, result.DeclaredTrialCount);
        Assert.Equal(4, result.AttemptedTrials);
        Assert.Equal(0, result.UnattemptedTrials);
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.SuccessfulTrials);
        Assert.Equal(1, result.UnsuccessfulTrials);
        Assert.Equal(1, result.InvalidEvaluationTrials);
        Assert.Equal(1, result.ExecutionFailureTrials);
        Assert.Equal(0.25, result.SuccessRate);
        Assert.True(result.MeetsDeclaredRule);
        Assert.True(result.Passed);
        Assert.Equal(
            [0, 1, 2, 3],
            result.Trials.Where(static trial => trial is not null).Select(static trial => trial!.Index));
    }

    /// <summary>
    /// Malformed judge output is an invalid evaluation that can never pass, even when the declared rule
    /// would accept every other outcome.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithMalformedJudgeOutput_CannotPass()
    {
        LiveLLMBatch batch = new(declaredTrialCount: 2, requiredSuccessRate: 0);

        LiveLLMBatchResult result = await batch.RunAsync((index, _) => Task.FromResult(
            LiveLLMTrial.FromVerdicts(
                index,
                CompletedExecution(),
                [Verdict(LiveLLMJudgeVerdictStatus.InvalidEvaluation)])));

        Assert.Equal(2, result.InvalidEvaluationTrials);
        Assert.Equal(0, result.SuccessfulTrials);
        Assert.Equal(0d, result.SuccessRate);
        Assert.True(result.MeetsDeclaredRule);
        // The attempted trials never became successes; the invalid evaluations are reported, not passed.
        Assert.All(result.Trials, trial => Assert.Equal(LiveLLMTrialCategory.InvalidEvaluation, trial!.Category));
        Assert.DoesNotContain(result.Trials, static trial => trial!.Category == LiveLLMTrialCategory.Success);
    }

    /// <summary>
    /// Malformed judge output cannot pass under a non-degenerate rule either: with a required success rate
    /// of 1, a batch of one success verdict and one invalid verdict is complete yet fails, because the
    /// invalid evaluation can never count as the success the rule needed.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithMalformedJudgeOutput_CannotPassUnderANonDegenerateRule()
    {
        LiveLLMBatch batch = new(declaredTrialCount: 2, requiredSuccessRate: 1);

        LiveLLMBatchResult result = await batch.RunAsync((index, _) => Task.FromResult(
            LiveLLMTrial.FromVerdicts(
                index,
                CompletedExecution(),
                [Verdict(index == 0 ? LiveLLMJudgeVerdictStatus.Success : LiveLLMJudgeVerdictStatus.InvalidEvaluation)])));

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.SuccessfulTrials);
        Assert.Equal(1, result.InvalidEvaluationTrials);
        Assert.Equal(0.5, result.SuccessRate);
        Assert.False(result.MeetsDeclaredRule);
        // Passing was possible under this rule — both trials as successes — and the malformed judge output denied it.
        Assert.False(result.Passed);
    }

    /// <summary>
    /// The batch runs to its declared end without early stop or retry: a final failing trial still runs
    /// after enough successes accumulated to satisfy the declared rule.
    /// </summary>
    [Fact]
    public async Task RunAsync_DoesNotStopEarlyOnceEnoughSuccessesAccumulate()
    {
        LiveLLMBatch batch = new(declaredTrialCount: 4, requiredSuccessRate: 0.5);
        List<int> invoked = [];

        LiveLLMBatchResult result = await batch.RunAsync((index, _) =>
        {
            invoked.Add(index);
            LiveLLMJudgeVerdict verdict = index < 3
                ? Verdict(LiveLLMJudgeVerdictStatus.Success)
                : Verdict(LiveLLMJudgeVerdictStatus.BelowThreshold);
            return Task.FromResult(LiveLLMTrial.FromVerdicts(index, CompletedExecution(), [verdict]));
        });

        Assert.Equal([0, 1, 2, 3], invoked);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.SuccessfulTrials);
        Assert.Equal(1, result.UnsuccessfulTrials);
        Assert.Equal(0.75, result.SuccessRate);
        Assert.True(result.Passed);
    }

    /// <summary>
    /// A trial delegate failure is recorded as an execution failure and the batch continues to its declared
    /// end.
    /// </summary>
    [Fact]
    public async Task RunAsync_RecordsTrialDelegateFailuresAndContinues()
    {
        LiveLLMBatch batch = new(declaredTrialCount: 3, requiredSuccessRate: 1);
        List<int> invoked = [];

        LiveLLMBatchResult result = await batch.RunAsync((index, _) =>
        {
            invoked.Add(index);
            return index == 1
                ? Task.FromException<LiveLLMTrial>(new HttpRequestException("trial transport failure"))
                : Task.FromResult(LiveLLMTrial.FromVerdicts(
                    index,
                    CompletedExecution(),
                    [Verdict(LiveLLMJudgeVerdictStatus.Success)]));
        });

        Assert.Equal([0, 1, 2], invoked);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.SuccessfulTrials);
        LiveLLMTrial failed = result.Trials[1]!;
        Assert.Equal(LiveLLMTrialCategory.ExecutionFailure, failed.Category);
        Assert.Null(failed.Execution);
        Assert.Contains("Execution failure (Transport)", failed.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", failed.FailureMessage, StringComparison.Ordinal);
        Assert.False(result.Passed);
    }

    /// <summary>
    /// An interrupted batch keeps its unattempted trials counted against the declared batch: it cannot pass
    /// even when the attempted subset satisfies the declared rule.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenInterrupted_CannotPassAsASuccessfulSubset()
    {
        using CancellationTokenSource cancellation = new();
        LiveLLMBatch batch = new(declaredTrialCount: 4, requiredSuccessRate: 0.5);

        LiveLLMBatchResult result = await batch.RunAsync((index, token) =>
        {
            if (index == 2)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.FromResult(LiveLLMTrial.FromVerdicts(
                index,
                CompletedExecution(),
                [Verdict(LiveLLMJudgeVerdictStatus.Success)]));
        }, cancellation.Token);

        Assert.Equal(2, result.AttemptedTrials);
        Assert.Equal(2, result.UnattemptedTrials);
        Assert.Null(result.Trials[2]);
        Assert.Null(result.Trials[3]);
        Assert.False(result.IsComplete);
        Assert.Equal(0.5, result.SuccessRate);
        Assert.True(result.MeetsDeclaredRule);
        Assert.False(result.Passed);
    }

    /// <summary>The declared batch rejects impossible declarations up front.</summary>
    [Fact]
    public void Ctor_WithImpossibleDeclaration_Throws()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LiveLLMBatch(declaredTrialCount: 0, requiredSuccessRate: 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LiveLLMBatch(declaredTrialCount: 2, requiredSuccessRate: 1.5));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LiveLLMBatch(declaredTrialCount: 2, requiredSuccessRate: -0.1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LiveLLMBatch(declaredTrialCount: 2, requiredSuccessRate: double.NaN));
    }

    /// <summary>
    /// A multi-verdict trial succeeds only when every judge evaluation passes; any invalid evaluation makes
    /// the trial an invalid evaluation, and an empty verdict list is an invalid evaluation.
    /// </summary>
    [Fact]
    public void FromVerdicts_ClassifiesMultiVerdictTrials()
    {
        LiveLLMScenarioExecution execution = CompletedExecution();

        var allPass = LiveLLMTrial.FromVerdicts(
            0,
            execution,
            [Verdict(LiveLLMJudgeVerdictStatus.Success), Verdict(LiveLLMJudgeVerdictStatus.Success)]);
        var oneBelow = LiveLLMTrial.FromVerdicts(
            1,
            execution,
            [Verdict(LiveLLMJudgeVerdictStatus.Success), Verdict(LiveLLMJudgeVerdictStatus.BelowThreshold)]);
        var anyInvalid = LiveLLMTrial.FromVerdicts(
            2,
            execution,
            [Verdict(LiveLLMJudgeVerdictStatus.Success), Verdict(LiveLLMJudgeVerdictStatus.InvalidEvaluation)]);
        var noVerdicts = LiveLLMTrial.FromVerdicts(3, execution, []);

        Assert.Equal(LiveLLMTrialCategory.Success, allPass.Category);
        Assert.Null(allPass.FailureMessage);
        Assert.Equal(LiveLLMTrialCategory.Failure, oneBelow.Category);
        Assert.Equal(LiveLLMTrialCategory.InvalidEvaluation, anyInvalid.Category);
        Assert.Equal(LiveLLMTrialCategory.InvalidEvaluation, noVerdicts.Category);
        Assert.Contains("no judge evaluation", noVerdicts.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>Recording a succeeded execution as an execution failure is rejected as misuse.</summary>
    [Fact]
    public void FromExecution_WithSucceededExecution_Throws()
        => Assert.Throws<InvalidOperationException>(() => LiveLLMTrial.FromExecution(0, CompletedExecution()));

    /// <summary>
    /// Writing a trace artefact produces a versioned, sanitised JSON file under the writer's root, and the
    /// default root is the ignored <c>game/temp/live-traces</c> directory.
    /// </summary>
    [Fact]
    public void Write_ProducesVersionedSanitisedArtefact()
    {
        DirectoryInfo root = CreateScratchRoot();
        try
        {
            LiveLLMTraceWriter writer = new(root);
            LiveLLMTrace trace = new(
                "writer scenario/with:unsafe",
                LiveLLMTraceStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Failure: null,
                Entries:
                [
                    new LiveLLMTraceEntry(
                        0,
                        LiveLLMTraceEntryKind.Exchange,
                        LiveLLMPurpose.Target,
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromMilliseconds(2),
                        Request: null,
                        Response: null,
                        SuppliedMessage: null,
                        ToolResult: null,
                        Failure: null),
                ]);

            FileInfo artefact = writer.Write(trace, label: "trial-0");

            Assert.True(artefact.Exists);
            Assert.Equal(root.FullName, artefact.Directory!.FullName);
            Assert.EndsWith(".json", artefact.Name, StringComparison.Ordinal);
            Assert.StartsWith("writer_scenario_with_unsafe", artefact.Name, StringComparison.Ordinal);
            Assert.Contains("trial-0", artefact.Name, StringComparison.Ordinal);

            string json = File.ReadAllText(artefact.FullName);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("writer scenario/with:unsafe", document.RootElement.GetProperty("scenarioName").GetString());
            Assert.Equal("Completed", document.RootElement.GetProperty("status").GetString());
            _ = Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Written artefacts never contain credential-shaped values from captured evidence.</summary>
    [Fact]
    public async Task Write_RedactsCredentialShapedValuesInArtefactsAsync()
    {
        const string sentinel = "sk-ARTEFACTSENTINEL0123456789abcdef";
        DirectoryInfo root = CreateScratchRoot();
        try
        {
            LiveLLMTraceWriter writer = new(root);
            LiveLLMScenarioExecution execution;
            {
                LiveLLMScriptedChatClient target = new("target");
                _ = target.Enqueue(new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, $"Answer after checking ApiKey={sentinel}.")));
                LiveLLMScenarioExecutor executor = new(target);
                execution = await executor.ExecuteAsync(
                    "redaction",
                    new LiveLLMScenarioBounds(1, TimeSpan.FromSeconds(30)),
                    context => context.SendTargetAsync([new ChatMessage(ChatRole.User, "question")]));
            }

            Assert.True(execution.Succeeded);
            FileInfo artefact = writer.Write(execution.Trace);
            string json = File.ReadAllText(artefact.FullName);

            Assert.DoesNotContain(sentinel, json, StringComparison.Ordinal);
            Assert.Contains("[redacted]", json, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>The default writer root resolves to the ignored game/temp/live-traces directory.</summary>
    [Fact]
    public void ResolveDefaultRoot_PointsAtTheGameTempLiveTracesDirectory()
    {
        DirectoryInfo root = LiveLLMTraceWriter.ResolveDefaultRoot();

        Assert.Equal(LiveLLMTraceWriter.RootDirectoryName, root.Name);
        Assert.Equal("temp", root.Parent!.Name);
        Assert.Equal("game", root.Parent!.Parent!.Name);
    }

    private static LiveLLMTrial IndexedTrial(int index)
        => index switch
        {
            0 => LiveLLMTrial.FromVerdicts(index, CompletedExecution(), [Verdict(LiveLLMJudgeVerdictStatus.Success)]),
            1 => LiveLLMTrial.FromVerdicts(
                index,
                CompletedExecution(),
                [Verdict(LiveLLMJudgeVerdictStatus.BelowThreshold)]),
            2 => LiveLLMTrial.FromVerdicts(
                index,
                CompletedExecution(),
                [Verdict(LiveLLMJudgeVerdictStatus.InvalidEvaluation)]),
            _ => LiveLLMTrial.FromFailure(
                index,
                new LiveLLMFailureRecord(
                    LiveLLMFailureClassification.Transport,
                    "HttpRequestException",
                    "synthetic transport failure")),
        };

    private static LiveLLMScenarioExecution CompletedExecution()
        => new(
            "batch",
            LiveLLMScenarioExecutionStatus.Completed,
            Failure: null,
            Trace: new LiveLLMTrace(
                "batch",
                LiveLLMTraceStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Failure: null,
                Entries: []));

    private static LiveLLMJudgeVerdict Verdict(LiveLLMJudgeVerdictStatus status)
        => new(
            status,
            Threshold: 5,
            MetricName: "Groundedness",
            Score: status == LiveLLMJudgeVerdictStatus.Success ? 5 : 4,
            Rating: "Exceptional",
            Reason: "deterministic reason",
            FailureMessage: status == LiveLLMJudgeVerdictStatus.Success ? null : "below threshold");

    private static DirectoryInfo CreateScratchRoot()
    {
        // Scratch artefacts stay under the repo's ignored game/temp directory and are cleaned up per test.
        // Anchor on this assembly's location: tests run inside a Godot host whose base directory is the game's.
        string assemblyLocation = typeof(LiveLLMBatchIntegrationTests).Assembly.Location;
        DirectoryInfo? current = new(Path.GetDirectoryName(assemblyLocation)!);
        while (current is not null && current.Name != "integration-tests")
        {
            current = current.Parent;
        }

        string basePath = current?.Parent is { } repositoryRoot
            ? Path.Combine(repositoryRoot.FullName, "game", "temp")
            : Path.GetTempPath();
        string scratchRoot = Path.Combine(basePath, $"live-llm-trace-tests-{Guid.NewGuid():N}");
        return Directory.CreateDirectory(scratchRoot);
    }
}
