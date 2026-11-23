using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Core.Threading;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Navigation;
using AlleyCat.Scene;
using AlleyCat.Scene.MiniMap;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlleyCat.IntegrationTests.Mind.AI.Navigation.CoordinateNavigationGate;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Mind.AI.Navigation;

/// <summary>
/// AI-015 TR-21 live gate on the shipped contract: the five <see cref="CoordinateNavigationGate" /> tasks run against
/// the model-facing
/// request that production composes for the minimal reference-female subject — the instructions rendered by the
/// subject's <see cref="AgenticMind" /> from the production NPC prompt stack, the session's own composed
/// <c>speak</c>, <c>wait</c>, <c>minimap</c>, <c>walk_to</c>, <c>turn_to</c> and <c>stop</c> functions (names,
/// descriptions, schemas), live <c>minimap</c> renders through the production tool with the TR-17 1 m grid, production
/// <c>walk_to</c> result texts, and the production <c>arrived</c> outcome delivered through a production
/// <c>scene_update</c> exchange with no image. Movement is simulated: navigation calls are recorded and scored, never
/// walked. Scoring is exactly <see cref="CoordinateNavigationScoring" />, including the two-step heading path.
/// </summary>
/// <remarks>
/// Harness divergences from a live session, each fixed for every trial: <c>read_lore</c> and <c>capture_view</c> are
/// left out of the offered inventory (the minimal subject has no eyes and the scene no lore); <c>speak</c> and
/// <c>wait</c> results are fixed simulated texts (the subject has no voice and the harness no wait gate); the
/// current-scene message is a fixed production-shaped text (the subject has no perceptions to attend the companion);
/// and tool images are presented in the runner's exchange-adjacent label format, which the runner keeps private.
/// </remarks>
public sealed class CoordinateNavigationFinalGateTests
{
    private const string EvidenceRoot = "res://temp/agent-nav-tools/final-gate";
    private const string SceneCheckRoot = "res://temp/agent-nav-tools/scene-checks";
    private const string PromptStackPath = "res://assets/characters/prompts/generic_npc_prompt_stack.tres";
    private const string FinalRun = "Final";

    /// <summary>Per-trial scenario bounds: the opening requests plus the two-step arrival follow-ups.</summary>
    private static LiveLLMScenarioBounds Bounds => new(
        MaxTargetRequests: MaxRequestsPerTrial + ArrivalFollowUpRequests,
        Timeout: TimeSpan.FromMinutes(6),
        MaxToolResults: 32);

    private static readonly string[] _offeredTools =
    [
        SpeakName, WaitName, MinimapName, WalkToName, TurnToName, StopName,
    ];

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Deterministic precondition (scripted client, no network): the harness delivers the production contract — the
    /// composed tool declarations and instructions, a live production minimap render, the production walk result,
    /// and the production arrival outcome in a <c>scene_update</c> without an image — and scores the T5 two-step path.
    /// </summary>
    [Fact]
    public async Task FinalGateHarness_DeliversTheProductionContract()
    {
        await using FinalGateScene scene = await FinalGateScene.LoadAsync();
        NavigationGateTask task = Tasks.Single(static t => t.Id == "T5-bench-face-me");
        LiveLLMScriptedChatClient client = new LiveLLMScriptedChatClient("scripted")
            .Enqueue(Respond(new FunctionCallContent("c1", MinimapName, new Dictionary<string, object?>())))
            .Enqueue(Respond(new FunctionCallContent("c2", WalkToName, new Dictionary<string, object?> { ["x"] = -2d, ["z"] = 4d })))
            .Enqueue(Respond(new FunctionCallContent("c3", MinimapName, new Dictionary<string, object?>())))
            .Enqueue(Respond(new FunctionCallContent("c4", TurnToName, new Dictionary<string, object?> { ["heading"] = 10d })));
        FinalGateCapture capture = new();

        LiveLLMScenarioExecution execution = await new LiveLLMScenarioExecutor(client).ExecuteAsync(
            "final-gate-contract",
            Bounds,
            context => scene.RunTrialAsync(context, task, capture));

        Assert.True(execution.Succeeded, execution.Failure?.SafeMessage);
        ChatOptions options = client.Requests[0].Options!;
        Assert.Equal(scene.Instructions, options.Instructions);
        Assert.Contains("<Scenario>", scene.Instructions, StringComparison.Ordinal);
        Assert.Contains("rehearsing stage blocking", scene.Instructions, StringComparison.Ordinal);
        Assert.Equal(_offeredTools, options.Tools!.Select(static tool => tool.Name));
        Assert.Equal(WalkToTool.ProductionToolDescription, options.Tools!.Single(static t => t.Name == WalkToName).Description);
        Assert.Equal(TurnToTool.ProductionToolDescription, options.Tools!.Single(static t => t.Name == TurnToName).Description);
        Assert.Equal(StopTool.ProductionToolDescription, options.Tools!.Single(static t => t.Name == StopName).Description);
        Assert.Equal(MinimapTool.ProductionToolDescription, options.Tools!.Single(static t => t.Name == MinimapName).Description);

        // The minimap result and its image come from the production tool at the subject's position.
        IReadOnlyList<ChatMessage> second = client.Requests[1].Messages;
        Assert.Contains(ResultTexts(second), static text => text.StartsWith("Minimap delivered: minimap image centred at (1, 1.5)", StringComparison.Ordinal));
        VisualContextImage start = Assert.Single(second.SelectMany(static m => m.Contents).OfType<VisualContextImage>());
        Assert.Equal(1d, start.CentreX!.Value, 2);

        // The walk result and the arrival outcome are production texts; the arrival carries no image.
        IReadOnlyList<ChatMessage> arrivalRequest = client.Requests[2].Messages;
        Assert.Contains(ResultTexts(arrivalRequest), static text => text.StartsWith("Started walking to (-2, 4) at walk pace.", StringComparison.Ordinal));
        string arrival = Assert.Single(ResultTexts(arrivalRequest), static text => text.Contains("move arrived", StringComparison.Ordinal));
        Assert.Contains("Your `walk_to` move arrived: you reached the destination. You are at (-2.0, 4.0), facing ", arrival, StringComparison.Ordinal);
        Assert.Equal(arrival, capture.ArrivalText);
        // Only the start minimap image is in the arrival request: the arrival update attaches none.
        _ = Assert.Single(arrivalRequest.SelectMany(static m => m.Contents).OfType<VisualContextImage>());

        // The follow-up minimap renders live from the arrival position.
        VisualContextImage atArrival = client.Requests[3].Messages.SelectMany(static m => m.Contents).OfType<VisualContextImage>().Last();
        Assert.Equal(-2d, atArrival.CentreX!.Value, 2);
        Assert.Equal(4d, atArrival.CentreY!.Value, 2);

        NavigationTrialRecord record = BuildRecord(task, 0, scene, execution, capture, "scripted");
        Assert.Equal("case2-follow-up-turn_to", record.HeadingPath);
        Assert.True(record.PositionPass);
        Assert.True(record.HeadingPass);
    }

    /// <summary>
    /// Deterministic scene check (no network): the scene matches the declared ground truth and subject pose, and the
    /// production AI minimap composition resolves a 1 m grid at the scene's 53.75 px/m framing (AI-015 TR-17). The
    /// capture is saved to this run's own scene-check folder.
    /// </summary>
    [Fact]
    public async Task Scene_MatchesDeclaredGroundTruthAndRendersTheOneMetreMinimap()
    {
        await using FinalGateScene scene = await FinalGateScene.LoadAsync();
        Assert.Equal(DeclaredSubjectPose.X, scene.SubjectPose.X, 2);
        Assert.Equal(DeclaredSubjectPose.Z, scene.SubjectPose.Z, 2);
        Assert.True(AngularError(DeclaredSubjectPose.Bearing, scene.SubjectPose.Bearing) < 0.5d);
        Assert.Equal(Tasks.Count, scene.Truths.Count);

        MiniMapRenderer renderer = Game.Instance.GetRequiredService<MiniMapRenderer>();
        MiniMapRenderResult? result = null;
        VisualContextCaptureOutcome outcome = VisualContextCapture.TryRenderMinimap(
            (centre, settings) => result = renderer.Render(centre, settings),
            renderer.Defaults.MaximumDistance,
            scene.Subject,
            clock: null);
        VisualContextImage image = outcome.Image ?? throw new InvalidOperationException(outcome.Failure);
        Assert.Equal(1f, result!.GridStep);
        Assert.Equal(53.75f, result.Scale, 0.01f);

        string directory = Directory.CreateDirectory(Path.Combine(
            ProjectSettings.GlobalizePath(SceneCheckRoot),
            DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture))).FullName;
        await File.WriteAllBytesAsync(Path.Combine(directory, "minimap.png"), image.Data.ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory, "ground-truth.json"), JsonSerializer.Serialize(new
        {
            subject = new
            {
                x = scene.SubjectPose.X,
                z = scene.SubjectPose.Z,
                bearing = scene.SubjectPose.Bearing
            },
            gridStepMetres = result.GridStep,
            scalePixelsPerMetre = result.Scale,
            tasks = scene.Truths,
        }, _jsonOptions));
    }

    /// <summary>The declared final gate: ten interleaved trials per task on the configured live model.</summary>
    [Fact]
    [LiveLlm]
    public async Task LiveFinalGate_ProductionToolsOnMinimalSubject()
    {
        IConfiguration configuration = Game.Instance.GetRequiredService<IConfiguration>();
        await using FinalGateScene scene = await FinalGateScene.LoadAsync();
        string runRoot = Directory.CreateDirectory(Path.Combine(
            ProjectSettings.GlobalizePath(EvidenceRoot),
            DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture))).FullName;
        DirectoryInfo traces = Directory.CreateDirectory(Path.Combine(runRoot, "traces"));
        string trialsPath = Path.Combine(runRoot, "trials.jsonl");
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        List<NavigationTrialRecord> records = [];
        long inputTokens = 0, outputTokens = 0;
        bool deliveredWritten = false;

        using LiveLLMClientPair clients = LiveLLMClientFactory.CreateLiveClients();
        LiveLLMScenarioExecutor executor = new(clients.Target);
        LiveLLMTraceWriter writer = new(traces);
        for (int trial = 0; trial < TrialsPerTask; trial++)
        {
            foreach (NavigationGateTask task in Tasks)
            {
                FinalGateCapture capture = new();
                LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
                    $"agent-nav-final-{task.Id}-{trial}",
                    Bounds,
                    context => scene.RunTrialAsync(context, task, capture));
                FileInfo traceFile = writer.Write(execution.Trace);
                inputTokens += capture.InputTokens;
                outputTokens += capture.OutputTokens;
                if (!deliveredWritten && capture.ArrivalText is not null && capture.LastRequest is { } delivered)
                {
                    await File.WriteAllTextAsync(Path.Combine(runRoot, "delivered-request.md"), DescribeRequest(scene, delivered));
                    deliveredWritten = true;
                }

                NavigationTrialRecord record = BuildRecord(task, trial, scene, execution, capture, traceFile.Name);
                records.Add(record);
                await File.AppendAllTextAsync(trialsPath, JsonSerializer.Serialize(record) + "\n");
            }
        }

        string summary = Summarise(records, out object summaryJson);
        await File.WriteAllTextAsync(Path.Combine(runRoot, "summary.md"), summary);
        await File.WriteAllTextAsync(Path.Combine(runRoot, "summary.json"), JsonSerializer.Serialize(summaryJson, _jsonOptions));
        await File.WriteAllBytesAsync(Path.Combine(runRoot, "minimap-start.png"), scene.StartMinimap.Data.ToArray());
        await File.WriteAllTextAsync(Path.Combine(runRoot, "run-metadata.json"), JsonSerializer.Serialize(new
        {
            gate = "AI-015 TR-21 final gate",
            trialsPerTask = TrialsPerTask,
            model = configuration["AITest:Model"]?.Trim(),
            hostSHA256 = Sha(configuration["AITest:Host"] ?? string.Empty),
            timeoutSeconds = configuration["AITest:Timeout"],
            requestSettings = new
            {
                toolMode = "RequireAny",
                allowMultipleToolCalls = true,
                maxRequestsPerTrial = MaxRequestsPerTrial,
                arrivalFollowUpRequests = ArrivalFollowUpRequests
            },
            instructionsSHA256 = Sha(scene.Instructions),
            toolDescriptionsSHA256 = Sha(string.Join("\n", scene.Tools.Select(static tool => $"{tool.Name}:{tool.Description}"))),
            toolSchemas = scene.Tools.OfType<AIFunction>().ToDictionary(static f => f.Name, static f => f.JsonSchema.GetRawText()),
            startMinimapSHA256 = Convert.ToHexStringLower(SHA256.HashData(scene.StartMinimap.Data.Span)),
            tasks = Tasks,
            criteria = new
            {
                validity = "100% of navigation calls (terminal and follow-up) schema-valid; JSON null for an optional parameter counts as omitted",
                position = $"≥ {RequiredPassRate:P0} of walk_to trials within {PositionToleranceMetres} m (+{StandOffAllowanceMetres} m stand-off)",
                heading = $"≥ {RequiredPassRate:P0} of T3 + T4 heading trials within ±{HeadingToleranceDegrees}°",
                notGated = "T5 heading (known limitation, AI-015 TR-18)",
            },
            inputTokens,
            outputTokens,
            startedAt,
            finishedAt = DateTimeOffset.UtcNow,
        }, _jsonOptions));

        // Harness completeness only: every declared slot was attempted and recorded. The gate verdict is in the summary.
        Assert.Equal(Tasks.Count * TrialsPerTask, records.Count);
    }

    private static ChatResponse Respond(FunctionCallContent call) => new(new ChatMessage(ChatRole.Assistant, [call]));

    private static IEnumerable<string> ResultTexts(IReadOnlyList<ChatMessage> messages)
        => messages.SelectMany(static m => m.Contents).OfType<FunctionResultContent>()
            .Select(static r => r.Result?.ToString() ?? string.Empty);

    private static NavigationTrialRecord BuildRecord(
        NavigationGateTask task,
        int trial,
        FinalGateScene scene,
        LiveLLMScenarioExecution execution,
        FinalGateCapture capture,
        string traceFile)
    {
        bool navigated = capture.TerminalNavigationCalls.Count > 0;
        string outcome = navigated
            ? CoordinateNavigationScoring.OutcomeNavigated
            : execution.Succeeded ? CoordinateNavigationScoring.OutcomeNoNavigation
            : CoordinateNavigationScoring.OutcomeExecutionFailure;
        (bool toolChoiceCorrect, double? positionError, bool? positionPass, double? headingError, bool? headingPass,
            string? positionPattern, string? headingPattern, string? headingPath) = CoordinateNavigationScoring.Score(
            task,
            scene.Truths[task.Id],
            capture.TerminalNavigationCalls,
            scene.SubjectPose,
            capture.ArrivalText is null ? null : capture.FollowUpNavigationCalls);
        return new NavigationTrialRecord(
            FinalRun,
            task.Id,
            trial,
            outcome,
            capture.Requests,
            capture.MinimapCalled,
            capture.MinimapCalledBeforeNavigation,
            [.. capture.TerminalNavigationCalls],
            [.. capture.OtherCalls],
            toolChoiceCorrect,
            positionError,
            positionPass,
            headingError,
            headingPass,
            positionPattern,
            headingPattern,
            execution.Failure is null ? null : $"{execution.Failure.Classification}: {execution.Failure.SafeMessage}",
            traceFile,
            headingPath,
            capture.ArrivalText,
            capture.ArrivalText is null ? null : [.. capture.FollowUpCalls],
            capture.ArrivalText is null ? null : [.. capture.FollowUpNavigationCalls],
            capture.ArrivalText is null ? null : capture.MinimapCalledAfterArrival,
            capture.ArrivalText is null ? null : false);
    }

    private static string Summarise(IReadOnlyList<NavigationTrialRecord> records, out object json)
    {
        NavigationCallObservation[] calls =
        [
            .. records.SelectMany(static r => r.NavigationCalls.Concat(r.FollowUpNavigationCalls ?? [])),
        ];
        int validCalls = calls.Count(static call => call.Valid);
        NavigationTrialRecord[] positionTrials = [.. records.Where(static r => r.PositionPass is not null)];
        NavigationTrialRecord[] gatedHeading = [.. records.Where(static r => r.HeadingPass is not null && IsGatedHeadingTask(r.TaskId))];
        NavigationTrialRecord[] t5Heading = [.. records.Where(static r => r.HeadingPass is not null && !IsGatedHeadingTask(r.TaskId))];
        int positionPasses = positionTrials.Count(static r => r.PositionPass == true);
        int headingPasses = gatedHeading.Count(static r => r.HeadingPass == true);
        double positionRate = positionTrials.Length == 0 ? 0d : positionPasses / (double)positionTrials.Length;
        double headingRate = gatedHeading.Length == 0 ? 0d : headingPasses / (double)gatedHeading.Length;
        bool validityPass = calls.Length > 0 && validCalls == calls.Length;
        bool passed = validityPass && positionRate >= RequiredPassRate && headingRate >= RequiredPassRate;

        StringBuilder text = new();
        _ = text.AppendLine("# Coordinate Navigation — Final Live Gate (Production Contract)");
        _ = text.AppendLine();
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Trials: {records.Count} ({string.Join(", ", records.GroupBy(static r => r.Outcome).Select(static g => $"{g.Key} {g.Count()}"))})");
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Validity (terminal + follow-up navigation calls): {validCalls}/{calls.Length} — {(validityPass ? "PASS" : "FAIL")}");
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Position (walk_to trials T1, T2, T4, T5): {positionPasses}/{positionTrials.Length} ({positionRate:P1}) — {(positionRate >= RequiredPassRate ? "PASS" : "FAIL")}");
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Heading (T3 + T4): {headingPasses}/{gatedHeading.Length} ({headingRate:P1}) — {(headingRate >= RequiredPassRate ? "PASS" : "FAIL")}");
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- T5 heading (measured, not gated): {t5Heading.Count(static r => r.HeadingPass == true)}/{t5Heading.Length}");
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Gate: {(passed ? "PASS" : "FAIL")}");
        _ = text.AppendLine();
        _ = text.AppendLine("| Task | Valid calls | Position pass | Median pos err (m) | Heading pass | Median heading err (°) | Minimap first |");
        _ = text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (IGrouping<string, NavigationTrialRecord> group in records.GroupBy(static r => r.TaskId))
        {
            NavigationCallObservation[] taskCalls = [.. group.SelectMany(static r => r.NavigationCalls.Concat(r.FollowUpNavigationCalls ?? []))];
            NavigationTrialRecord[] pos = [.. group.Where(static r => r.PositionPass is not null)];
            NavigationTrialRecord[] head = [.. group.Where(static r => r.HeadingPass is not null)];
            _ = text.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {taskCalls.Count(static c => c.Valid)}/{taskCalls.Length} "
                + $"| {(pos.Length == 0 ? "—" : $"{pos.Count(static r => r.PositionPass == true)}/{pos.Length}")} "
                + $"| {Format(Median(pos.Select(static r => r.PositionErrorMetres)))} "
                + $"| {(head.Length == 0 ? "—" : $"{head.Count(static r => r.HeadingPass == true)}/{head.Length}")} "
                + $"| {Format(Median(head.Select(static r => r.HeadingErrorDegrees)))} "
                + $"| {group.Count(static r => r.MinimapCalledBeforeNavigation)}/{group.Count()} |");
        }

        _ = text.AppendLine();
        _ = text.AppendLine("## Heading Paths (Walk-And-Face Tasks)");
        _ = text.AppendLine();
        foreach (IGrouping<string, NavigationTrialRecord> group in records
                     .Where(static r => Tasks.First(t => t.Id == r.TaskId).Kind == NavigationTaskKind.WalkAndFace)
                     .GroupBy(static r => $"{r.TaskId} {r.HeadingPath ?? "no-heading-no-follow-up"}"))
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key}: {group.Count()} (heading pass {group.Count(static r => r.HeadingPass == true)})");
        }

        _ = text.AppendLine();
        _ = text.AppendLine("## Failure Patterns");
        _ = text.AppendLine();
        foreach (IGrouping<string, string> pattern in records
                     .SelectMany(static r => new[] { r.PositionFailurePattern is { } p ? $"{r.TaskId} position:{p}" : null, r.HeadingFailurePattern is { } h ? $"{r.TaskId} heading:{h}" : null })
                     .OfType<string>()
                     .GroupBy(static p => p)
                     .OrderByDescending(static g => g.Count()))
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"- {pattern.Key}: {pattern.Count()}");
        }

        _ = text.AppendLine();
        _ = text.AppendLine("## Invalid Calls");
        _ = text.AppendLine();
        foreach (NavigationCallObservation call in calls.Where(static call => !call.Valid))
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"- {call.Name} {call.Arguments}: {call.InvalidReason}");
        }

        json = new
        {
            trials = records.Count,
            navigationCalls = calls.Length,
            validCalls,
            validityPass,
            positionTrials = positionTrials.Length,
            positionPasses,
            positionRate,
            medianPositionErrorMetres = Median(positionTrials.Select(static r => r.PositionErrorMetres)),
            gatedHeadingTrials = gatedHeading.Length,
            headingPasses,
            headingRate,
            medianGatedHeadingErrorDegrees = Median(gatedHeading.Select(static r => r.HeadingErrorDegrees)),
            t5HeadingTrials = t5Heading.Length,
            t5HeadingPasses = t5Heading.Count(static r => r.HeadingPass == true),
            passed,
        };
        return text.ToString();
    }

    private static bool IsGatedHeadingTask(string taskId) => taskId.StartsWith("T3", StringComparison.Ordinal) || taskId.StartsWith("T4", StringComparison.Ordinal);

    private static string DescribeRequest(FinalGateScene scene, IReadOnlyList<ChatMessage> messages)
    {
        StringBuilder text = new();
        _ = text.AppendLine("# Delivered Request (Final Gate)");
        _ = text.AppendLine();
        _ = text.AppendLine("## Instructions");
        _ = text.AppendLine();
        _ = text.AppendLine(scene.Instructions);
        _ = text.AppendLine();
        _ = text.AppendLine("## Tools");
        _ = text.AppendLine();
        foreach (AIFunction tool in scene.Tools.OfType<AIFunction>())
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"### `{tool.Name}`\n\n{tool.Description}\n\n```json\n{tool.JsonSchema.GetRawText()}\n```\n");
        }

        _ = text.AppendLine("## Messages");
        _ = text.AppendLine();
        foreach (ChatMessage message in messages)
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"### {message.Role}");
            _ = text.AppendLine();
            foreach (AIContent content in message.Contents)
            {
                _ = text.AppendLine(content switch
                {
                    TextContent t => t.Text,
                    FunctionCallContent call => $"call `{call.Name}` ({call.CallId}) {JsonSerializer.Serialize(call.Arguments)}",
                    FunctionResultContent result => $"result ({result.CallId}): {result.Result}",
                    DataContent data => $"[image {data.MediaType}, {data.Data.Length} bytes]",
                    _ => $"[{content.GetType().Name}]",
                });
            }

            _ = text.AppendLine();
        }

        return text.ToString();
    }

    private static double? Median(IEnumerable<double?> values)
    {
        double[] sorted = [.. values.OfType<double>().Order()];
        return sorted.Length == 0 ? null
            : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2d;
    }

    private static string Format(double? value)
        => value is { } number ? number.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>One trial's observations.</summary>
    private sealed class FinalGateCapture
    {
        public int Requests
        {
            get; set;
        }

        public bool MinimapCalled
        {
            get; set;
        }

        public bool MinimapCalledBeforeNavigation
        {
            get; set;
        }

        public bool MinimapCalledAfterArrival
        {
            get; set;
        }

        public List<NavigationCallObservation> TerminalNavigationCalls { get; } = [];

        public List<NavigationCallObservation> FollowUpNavigationCalls { get; } = [];

        public List<string> OtherCalls { get; } = [];

        public List<string> FollowUpCalls { get; } = [];

        public string? ArrivalText
        {
            get; set;
        }

        public long InputTokens
        {
            get; set;
        }

        public long OutputTokens
        {
            get; set;
        }

        public IReadOnlyList<ChatMessage>? LastRequest
        {
            get; set;
        }
    }

    /// <summary>
    /// The agent-navigation scene with the minimal subject, its production-composed session request contract, and the
    /// production pieces each trial uses for tool results.
    /// </summary>
    private sealed class FinalGateScene : IAsyncDisposable
    {
        private readonly Node _root;
        private readonly FixturePlayerCharacter _player = new();
        private readonly HoldingClientProvider _provider = new();
        private readonly FixedScenarioManager _scenarioManager = new();
        private IReadOnlyDictionary<string, AIFunction> _functions = new Dictionary<string, AIFunction>();

        private FinalGateScene(Node root)
        {
            _root = root;
            Subject = root.GetNode<AgentNavigationSubject>(SubjectNodePath);
            Companion = root.GetNode<CharacterHub>(CompanionNodePath);
        }

        public AgentNavigationSubject Subject
        {
            get;
        }

        public CharacterHub Companion
        {
            get;
        }

        public string Instructions { get; private set; } = string.Empty;

        public IReadOnlyList<AITool> Tools { get; private set; } = [];

        public (double X, double Z, double Bearing) SubjectPose
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, NavigationGroundTruth> Truths { get; private set; } = new Dictionary<string, NavigationGroundTruth>();

        public VisualContextImage StartMinimap { get; private set; } = null!;

        public static async Task<FinalGateScene> LoadAsync()
        {
            SceneTree tree = TestUtils.GetSceneTree();
            AgentSessionSuppression.DisableForTesting();
            Node root = AgentNavigationSubject.InstantiateScene();
            FinalGateScene scene = new(root);
            try
            {
                tree.Root.AddChild(root);
                MotionBricksNavigation navigation = scene.Subject.GetChildren().OfType<MotionBricksNavigation>().Single();
                for (int frame = 0; frame < 600 && !scene.MapResolvesSubject(navigation); frame++)
                {
                    await TestUtils.WaitForNextPhysicsFrameAsync(tree);
                }

                Assert.True(scene.MapResolvesSubject(navigation), "The navigation map never resolved the subject.");
                Assert.Equal(SubjectFullId, ((ICharacter)scene.Subject).FullId);
                Assert.Equal(CompanionFullId, ((ICharacter)scene.Companion).FullId);
                Transform3D pose = scene.Subject.GlobalTransform;
                Vector3 forward = -pose.Basis.Z;
                scene.SubjectPose = (pose.Origin.X, pose.Origin.Z, BearingOfForward(forward.X, forward.Z));
                scene.Truths = ComputeGroundTruth(root, scene.SubjectPose);
                await scene.ComposeAsync();
                return scene;
            }
            catch
            {
                await scene.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task? worker = Subject.GetChildren().OfType<MotionBricksNavigation>().SingleOrDefault()?.WorkerCompletion;
            if (GodotObject.IsInstanceValid(_root))
            {
                _root.GetParent()?.RemoveChild(_root);
                _root.QueueFree();
            }

            await TestUtils.WaitForPhysicsFramesAsync(TestUtils.GetSceneTree(), 2);
            if (worker is not null)
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(30));
            }

            _provider.Free();
            _scenarioManager.Free();
            _player.Free();
            AgentSessionSuppression.ResetForTesting();
        }

        /// <summary>
        /// Runs one trial. The companion's line arrives through a production <c>scene_update</c>; <c>minimap</c> calls
        /// invoke the production tool; the first navigation call ends the trial, except a heading-less <c>walk_to</c> on
        /// a walk-and-face task, which receives the production walk result and arrival outcome and up to three
        /// follow-up requests at the arrival position (the two-step heading path).
        /// </summary>
        public async Task RunTrialAsync(LiveLLMScenarioContext context, NavigationGateTask task, FinalGateCapture capture)
        {
            ChatOptions options = new()
            {
                Instructions = Instructions,
                Tools = [.. Tools],
                ToolMode = ChatToolMode.RequireAny,
                AllowMultipleToolCalls = true,
            };
            ObservedSpeech instruction = new(CompanionFullId, task.Instruction, [SubjectFullId]);
            AcceptedObservationEntry speech = Entry(1, instruction);
            List<ChatMessage> history =
            [
                new(ChatRole.User, AgenticMind.SessionBootstrapInput),
                .. await SyntheticSceneUpdateExchange.BuildAsync([speech], 0, Subject, SyntheticSceneUpdateExchange.FormatCallId(1), speech.ObservedAt),
            ];

            for (int request = 0; request < MaxRequestsPerTrial; request++)
            {
                List<ChatMessage> messages = [.. history, new ChatMessage(ChatRole.User, CurrentSceneMessage)];
                capture.LastRequest = messages;
                ChatResponse response = await context.SendTargetAsync(messages, options);
                capture.Requests++;
                capture.InputTokens += response.Usage?.InputTokenCount ?? 0;
                capture.OutputTokens += response.Usage?.OutputTokenCount ?? 0;
                FunctionCallContent[] calls = [.. response.Messages.SelectMany(static m => m.Contents).OfType<FunctionCallContent>()];
                if (calls.Length == 0)
                {
                    capture.OtherCalls.Add("<no tool call>");
                    continue;
                }

                NavigationCallObservation[] navigation = [.. calls.Where(static c => IsNavigation(c.Name)).Select(CoordinateNavigationScoring.Observe)];
                foreach (FunctionCallContent call in calls.Where(static c => !IsNavigation(c.Name)))
                {
                    capture.OtherCalls.Add($"r{request + 1}:{call.Name}({JsonSerializer.Serialize(call.Arguments)})");
                }

                if (calls.Any(static c => c.Name == MinimapName))
                {
                    capture.MinimapCalled = true;
                    capture.MinimapCalledBeforeNavigation |= navigation.Length == 0;
                }

                if (navigation.Length > 0)
                {
                    capture.TerminalNavigationCalls.AddRange(navigation);
                    if (task.Kind == NavigationTaskKind.WalkAndFace
                        && navigation.LastOrDefault(static c => c.Name == WalkToName) is { Valid: true, Heading: null, X: { } x, Z: { } z } walk)
                    {
                        await RunArrivalPhaseAsync(context, options, history, calls, x, z, walk.Pace, speech, capture);
                    }

                    return;
                }

                await AppendResultsAsync(context, history, calls, CurrentGameTimeAfterWait: 15d);
            }
        }

        private async Task RunArrivalPhaseAsync(
            LiveLLMScenarioContext context,
            ChatOptions options,
            List<ChatMessage> history,
            FunctionCallContent[] calls,
            double x,
            double z,
            string? pace,
            AcceptedObservationEntry speech,
            FinalGateCapture capture)
        {
            // Production walk result and arrival outcome: the production controller on a recording navigation that
            // completes at the submitted destination (production arrival facing: the route's final segment).
            ObservedNavigationOutcome? arrived = null;
            LifecycleNavigationDouble recording = new();
            CoordinateNavigationController controller = new(
                recording,
                new GodotCoordinateNavigationEnvironment(Subject, Subject.Navigation!),
                outcome => arrived = outcome);
            CoordinateNavigationToolFunctions functions = new(controller);
            string walkResult = (await functions.WalkTo(x, z, null, pace)).Message ?? string.Empty;
            Transform3D destination = recording.LastDestination;
            _ = recording.CompleteActive();
            controller.Poll();
            ObservedNavigationOutcome outcome = arrived ?? throw new InvalidOperationException($"No arrival outcome; walk result: {walkResult}");

            history.Add(new ChatMessage(ChatRole.Assistant, [.. calls.Select(static c => (AIContent)new FunctionCallContent(c.CallId, c.Name, c.Arguments))]));
            List<AIContent> results = [];
            List<VisualContextImage> images = [];
            foreach (FunctionCallContent call in calls)
            {
                string result;
                if (call.Name == WalkToName)
                {
                    result = walkResult;
                }
                else if (call.Name == MinimapName)
                {
                    (result, VisualContextImage? image) = await InvokeMinimapAsync();
                    if (image is not null)
                    {
                        images.Add(image);
                    }
                }
                else
                {
                    result = SimulatedResult(call.Name, 15d);
                }

                _ = context.RecordSimulatedToolResult(new FunctionCallContent(call.CallId, call.Name), result);
                results.Add(new FunctionResultContent(call.CallId, result));
            }

            history.Add(new ChatMessage(ChatRole.Tool, results));
            AppendPresentation(history, images);

            AcceptedObservationEntry arrival = Entry(2, outcome);
            IReadOnlyList<ChatMessage> arrivalExchange = await SyntheticSceneUpdateExchange.BuildAsync(
                [speech, arrival], speech.SequenceID, Subject, SyntheticSceneUpdateExchange.FormatCallId(2), arrival.ObservedAt);
            capture.ArrivalText = arrivalExchange.SelectMany(static m => m.Contents).OfType<FunctionResultContent>().Single().Result?.ToString();
            history.AddRange(arrivalExchange);

            (double companionX, double companionZ) = (Companion.GlobalPosition.X, Companion.GlobalPosition.Z);
            string currentScene = string.Create(CultureInfo.InvariantCulture,
                $"Current game time: 20 s\nCurrent attended characters:\n- `{CompanionFullId}` is "
                + $"{Math.Sqrt(((companionX - x) * (companionX - x)) + ((companionZ - z) * (companionZ - z))):0.0} m away (observed just now)\n");

            Transform3D original = Subject.GlobalTransform;
            await OnMainThreadAsync(() => Subject.GlobalTransform = new Transform3D(destination.Basis, new Vector3(destination.Origin.X, original.Origin.Y, destination.Origin.Z)));
            try
            {
                for (int request = 0; request < ArrivalFollowUpRequests; request++)
                {
                    List<ChatMessage> messages = [.. history, new ChatMessage(ChatRole.User, currentScene)];
                    capture.LastRequest = messages;
                    ChatResponse response = await context.SendTargetAsync(messages, options);
                    capture.Requests++;
                    capture.InputTokens += response.Usage?.InputTokenCount ?? 0;
                    capture.OutputTokens += response.Usage?.OutputTokenCount ?? 0;
                    FunctionCallContent[] followUp = [.. response.Messages.SelectMany(static m => m.Contents).OfType<FunctionCallContent>()];
                    foreach (FunctionCallContent call in followUp)
                    {
                        capture.FollowUpCalls.Add($"r{request + 1}:{call.Name}({JsonSerializer.Serialize(call.Arguments)})");
                        if (IsNavigation(call.Name))
                        {
                            capture.FollowUpNavigationCalls.Add(CoordinateNavigationScoring.Observe(call));
                        }
                    }

                    if (capture.FollowUpNavigationCalls.Count > 0 || followUp.Length == 0 || request + 1 >= ArrivalFollowUpRequests)
                    {
                        return;
                    }

                    capture.MinimapCalledAfterArrival |= followUp.Any(static c => c.Name == MinimapName);
                    await AppendResultsAsync(context, history, followUp, CurrentGameTimeAfterWait: 23d);
                }
            }
            finally
            {
                await OnMainThreadAsync(() => Subject.GlobalTransform = original);
            }
        }

        private async Task AppendResultsAsync(
            LiveLLMScenarioContext context,
            List<ChatMessage> history,
            FunctionCallContent[] calls,
            double CurrentGameTimeAfterWait)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, [.. calls.Select(static c => (AIContent)new FunctionCallContent(c.CallId, c.Name, c.Arguments))]));
            List<AIContent> results = [];
            List<VisualContextImage> images = [];
            foreach (FunctionCallContent call in calls)
            {
                string result;
                if (call.Name == MinimapName)
                {
                    (result, VisualContextImage? image) = await InvokeMinimapAsync();
                    if (image is not null)
                    {
                        images.Add(image);
                    }
                }
                else
                {
                    result = SimulatedResult(call.Name, CurrentGameTimeAfterWait);
                }

                _ = context.RecordSimulatedToolResult(new FunctionCallContent(call.CallId, call.Name), result);
                results.Add(new FunctionResultContent(call.CallId, result));
            }

            history.Add(new ChatMessage(ChatRole.Tool, results));
            AppendPresentation(history, images);
        }

        /// <summary>The runner's exchange-adjacent image presentation (AI-013 TR-10) in its exact label format.</summary>
        private static void AppendPresentation(List<ChatMessage> history, List<VisualContextImage> images)
        {
            if (images.Count == 0)
            {
                return;
            }

            string label = images.Count == 1
                ? $"Visual context from your tool action: {images[0].DescribeCapture()}."
                : "Visual context from your tool actions:" + string.Concat(images.Select(static image => $"\n- {image.DescribeCapture()}"));
            history.Add(new ChatMessage(ChatRole.User, [new TextContent(label), .. images]));
        }

        private static string SimulatedResult(string name, double currentGameTime) => name switch
        {
            SpeakName => "Spoken through the configured voice.",
            WaitName => string.Create(CultureInfo.InvariantCulture, $"Wait ended: a pause gives you an opening to act. Elapsed game time: 3.0 seconds. Current game time: {currentGameTime:0.0}s."),
            _ => $"Unknown tool '{name}'.",
        };

        /// <summary>Invokes the session's production <c>minimap</c> function at the subject's live position.</summary>
        private async Task<(string Result, VisualContextImage? Image)> InvokeMinimapAsync()
        {
            object? raw = await _functions[MinimapName].InvokeAsync([]);
            return raw is VisualContextDelivery delivery
                ? (delivery.Message ?? string.Empty, delivery.Image)
                : (raw?.ToString() ?? string.Empty, null);
        }

        private static bool IsNavigation(string name) => name is WalkToName or TurnToName or StopName;

        private static AcceptedObservationEntry Entry(long sequence, Observation observation)
        {
            double observedAt = 10d + sequence;
            return new AcceptedObservationEntry(sequence, observedAt, observation with
            {
                ObservedAt = observedAt
            }, new ObservationSchedulingMetadata(1f, false), IsRetained: true);
        }

        private static async Task OnMainThreadAsync(Action action)
            => await Game.Instance.GetRequiredService<IMainThreadDispatcher>().InvokeAsync(action);

        private bool MapResolvesSubject(MotionBricksNavigation navigation)
        {
            Vector3 start = Subject.GlobalPosition;
            Vector3 closest = NavigationServer3D.MapGetClosestPoint(navigation.GetNavigationMap(), start);
            return NavigationServer3D.MapGetIterationId(navigation.GetNavigationMap()) != 0
                && new Vector2(closest.X - start.X, closest.Z - start.Z).Length() < 0.5f;
        }

        /// <summary>
        /// Composes the subject mind's production session: the production NPC prompt stack with the rehearsal scenario
        /// supplied through a scenario manager, the shipped <c>tools</c> visual-context mode, and the composed functions.
        /// </summary>
        private async Task ComposeAsync()
        {
            AgenticMind mind = Subject.GetNode<AgenticMind>("Mind");
            mind.ClientProvider = _provider;
            mind.ScenarioManager = _scenarioManager;
            mind.SystemInstruction = GD.Load<PromptStack>(PromptStackPath);
            mind.CurrentSceneStatus = new PromptStack { Sections = [new TextPromptSection { Text = "status", Name = "Status" }] };
            mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(
                EnableRequestResponseLogging: false,
                EnableReasoningLogging: false,
                EnableSessionTranscriptLogging: false));
            mind.SetVisualContextModeLoaderForTesting(() => VisualContextMode.Tools);
            mind.SetSceneContextLoaderForTesting(() => new SceneContext([Subject, Companion, _player]));
            AgenticMind.AgentSession session = await mind.PrepareSessionAsync(CancellationToken.None);
            Instructions = session.Instructions;
            var functions = session.Tools.OfType<AIFunction>().ToDictionary(static f => f.Name);
            _functions = functions;
            Tools = [.. _offeredTools.Select(name => (AITool)functions[name])];
            (string _, VisualContextImage? start) = await InvokeMinimapAsync();
            StartMinimap = start ?? throw new InvalidOperationException("The production minimap did not render.");
        }
    }

    /// <summary>The feasibility rehearsal scenario, supplied the production way.</summary>
    private sealed partial class FixedScenarioManager : ScenarioManager
    {
        public override ValueTask<Scenario?> GetCurrentScenario(IReadOnlyDictionary<string, object?> coreContext)
            => ValueTask.FromResult<Scenario?>(new Scenario(ScenarioDescription));
    }

    private sealed partial class HoldingClientProvider : ClientProvider
    {
        public override IChatClient CreateChatClient(string subjectFullId) => new HoldingClient();

        private sealed class HoldingClient : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
