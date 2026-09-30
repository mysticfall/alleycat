using System.Text.RegularExpressions;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for AI-011 mind session transcripts: one self-contained file per provider request cycle
/// under the documented layout, visible anomaly annotations for transport retry, invalid-response recovery, and
/// fresh-turn invalidation discards, the exchange-disposal decision, write-failure containment, the disabled
/// null-object path, runner-generated canonical cancellation results, and turn numbering that survives mind
/// re-creation (TR-14).
/// </summary>
/// <remarks>
/// Every fact owns one recorder-seam lifetime: the seams are installed inside the protected region before any
/// setup can fail, the session is terminated and awaited to full completion — including every flushed transcript
/// write — before the override is released, quiescence failures are loud rather than silent timeouts, and seam
/// restoration runs in a nested <c>finally</c> that no cleanup exception can bypass and deactivates every live
/// recorder, so no session — not even one abandoned mid-flight by a failed cleanup — can ever record outside its
/// temporary root (TR-12 seam safety).
/// </remarks>
[Headless]
public sealed partial class MindSessionTranscriptIntegrationTests
{
    private const string CharacterFolderName = "owner";

    private const string SceneStatusText = "Scene status: the courtyard is quiet.";

    private const string LineFeed = "\n";

    /// <summary>
    /// An enabled scripted session writes one self-contained Markdown file per provider request cycle under
    /// <c>&lt;root&gt;/&lt;run-timestamp&gt;/&lt;character&gt;/turn-NNNN.md</c>: metadata header, rendered instructions, the
    /// role-labelled request transcript including the prefix timeline and scene-status messages, response contents,
    /// and one block per tool invocation (AI-011 UR-1/UR-2, TR-2, TR-11, TR-14).
    /// </summary>
    [Fact]
    public async Task EnabledSession_WritesOneSelfContainedFilePerRequestCycle()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool tool = fixtures.Add(new CapturingTool());
            TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseFirstGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueHoldUntilReleasedCall(firstRequestStarted, releaseFirstGeneration, CapturingTool.ToolNameValue);
            clientProvider.EnqueueCall(CapturingTool.ToolNameValue);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Observing while the first generation holds guarantees the event precedes the second request's context
            // materialisation, so the second cycle's transcript deterministically renders it (AI-011 TR-6).
            mind.ObserveForTest(new TestObservation(1f, "distinctive-timeline-event"));
            _ = releaseFirstGeneration.TrySetResult();
            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 2);

            string characterDirectory = GetCharacterDirectory(root);
            Assert.Equal(
                ["turn-0001.md", "turn-0002.md"],
                ListTranscriptFiles(characterDirectory));

            string firstCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md"));
            Assert.Contains("# Mind Session Transcript — Turn 0001", firstCycle);
            Assert.Contains("- **Character:** owner", firstCycle);
            Assert.Contains("- **Request Cycle:** 1", firstCycle);
            Assert.Contains("- **Outcome:** Accepted", firstCycle);
            // The instructions render as a Markdown heading hierarchy, not one escaped pseudo-XML line.
            Assert.Contains("## Instructions", firstCycle);
            Assert.Contains("### Static", firstCycle);
            Assert.DoesNotContain("<Static>", firstCycle);
            // The role-labelled request transcript carries the prefix timeline and scene-status messages ahead of
            // the bootstrap input (AI-002 TR-2).
            Assert.Contains("## Request Transcript", firstCycle);
            Assert.Contains("### Message 1 of 3 — user", firstCycle);
            Assert.Contains("### Message 2 of 3 — user", firstCycle);
            Assert.Contains("### Message 3 of 3 — user", firstCycle);
            // The timeline message body renders its pseudo-XML sections as headings with no raw tags visible
            // (AI-011 TR-9).
            Assert.Contains("#### Established Event History", firstCycle);
            Assert.Contains("#### New Since Your Previous Response", firstCycle);
            Assert.DoesNotContain("<Established Event History>", firstCycle);
            Assert.Contains(AgenticMind.SessionBootstrapInput, firstCycle);
            Assert.Contains(SceneStatusText, firstCycle);
            Assert.Contains("## Request Options", firstCycle);
            Assert.Contains("## Response", firstCycle);
            Assert.Contains("**Function Call:** capture_context", firstCycle);
            // One block per tool invocation shows the tool name, JSON arguments, and the delivered result.
            Assert.Contains("## Tool Invocations", firstCycle);
            Assert.Contains("### capture_context — call `call-1`", firstCycle);
            Assert.Contains("**Arguments**", firstCycle);
            Assert.Contains("```json", firstCycle);
            Assert.Contains("**Result**", firstCycle);

            // The second cycle's transcript captures the distinctive timeline event observed between requests and
            // the distinctive current-scene-status content (AI-011 TR-6, technical acceptance criterion 5).
            string secondCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0002.md"));
            Assert.Contains("- distinctive-timeline-event", secondCycle);
            Assert.Contains(SceneStatusText, secondCycle);

            // Node exit ends the still-held third request; its interrupted cycle is still recorded — no enabled
            // cycle is silently missing.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            await WaitUntilAsync(sceneTree, () => ListTranscriptFiles(characterDirectory).Length == 3);

            Assert.Equal(3, clientProvider.Requests.Count);
            Assert.Equal(
                ["turn-0001.md", "turn-0002.md", "turn-0003.md"],
                ListTranscriptFiles(characterDirectory));
            Assert.Contains("- **Outcome:** Interrupted", File.ReadAllText(Path.Combine(characterDirectory, "turn-0003.md")));
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    /// <summary>
    /// Anomalous cycles stay first-class files with visible annotations: a transient transport failure retries
    /// within one recorded cycle, an invalid response shape records its recovery with budget state, and every cycle
    /// that reached the provider is present (AI-011 UR-2, TR-6, TR-14).
    /// </summary>
    [Fact]
    public async Task AnomalousCycles_ReceiveVisibleAnnotationsWithoutMissingCycles()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool tool = fixtures.Add(new CapturingTool());
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueTransientFailure();
            clientProvider.EnqueueCall(CapturingTool.ToolNameValue);
            clientProvider.EnqueueInvalidResponse();
            clientProvider.EnqueueCall(CapturingTool.ToolNameValue);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            mind.InvalidResponseRecoveryBudget = 2;
            mind.InvalidResponseRecoveryBackoffSeconds = [0f];
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

            // The transport retry waits out the runner's default one-second backoff before succeeding.
            await TestUtils.WaitForSecondsAsync(sceneTree, 1.5);
            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 2, maxFrames: 2000);

            string characterDirectory = GetCharacterDirectory(root);
            string retryCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md"));
            Assert.Contains("### Anomaly Annotations", retryCycle);
            Assert.Contains("- **Transport Retry:**", retryCycle);
            Assert.Contains("HttpRequestException", retryCycle);
            Assert.Contains("- **Outcome:** Accepted", retryCycle);

            // The invalid response is its own recorded cycle, annotated with its recovery budget state.
            string recoveryCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0002.md"));
            Assert.Contains("- **Invalid Response Recovery:**", recoveryCycle);
            Assert.Contains("1/2", recoveryCycle);
            Assert.Contains("- **Outcome:** Invalid Response — Recovery Scheduled", recoveryCycle);

            string secondAcceptedCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0003.md"));
            Assert.Contains("- **Outcome:** Accepted", secondAcceptedCycle);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            await WaitUntilAsync(
                sceneTree,
                () => ListTranscriptFiles(characterDirectory).Length == 4,
                maxFrames: 2000);

            // Five provider requests — the failed transport attempt, its retry, the invalid response, its fresh
            // replacement, and the final held request — resolve into exactly four recorded cycles, none missing.
            Assert.Equal(5, clientProvider.Requests.Count);
            Assert.Equal(
                ["turn-0001.md", "turn-0002.md", "turn-0003.md", "turn-0004.md"],
                ListTranscriptFiles(characterDirectory));
            Assert.Contains("- **Outcome:** Interrupted", File.ReadAllText(Path.Combine(characterDirectory, "turn-0004.md")));
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    /// <summary>
    /// A request discarded by fresh-turn invalidation produces its own visibly annotated file: the superseded
    /// cycle records the invalidation, and the replacement request continues the numbering (AI-011 UR-2, TR-6,
    /// TR-14; AI-002 TR-4/5).
    /// </summary>
    [Fact]
    public async Task FreshInvalidationDiscard_ProducesAVisiblyAnnotatedFile()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CapturingTool tool = fixtures.Add(new CapturingTool());
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueHold(firstRequestStarted);
            clientProvider.EnqueueCall(CapturingTool.ToolNameValue);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new TestObservation(1f, "fresh-event", Fresh: true));

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 1);

            string characterDirectory = GetCharacterDirectory(root);
            string discardedCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md"));
            Assert.Contains("- **Outcome:** Discarded Before Response", discardedCycle);
            Assert.Contains("- **Fresh-Turn Invalidation:**", discardedCycle);
            string replacementCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0002.md"));
            Assert.Contains("- **Outcome:** Accepted", replacementCycle);
            Assert.Contains("fresh-event", replacementCycle);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, () => ListTranscriptFiles(characterDirectory).Length == 3);
            Assert.Equal(
                ["turn-0001.md", "turn-0002.md", "turn-0003.md"],
                ListTranscriptFiles(characterDirectory));
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    /// <summary>
    /// A cycle whose whole tool batch targets a disposal-opted tool records the exchange-disposal decision
    /// (AI-002 TR-17; AI-011 TR-6, TR-14).
    /// </summary>
    [Fact]
    public async Task DisposalOptedToolBatch_RecordsTheExchangeDisposalDecision()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            DisposingCapturingTool tool = fixtures.Add(new DisposingCapturingTool());
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueCall(DisposingCapturingTool.DisposingToolNameValue);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 1);

            string characterDirectory = GetCharacterDirectory(root);
            string disposedCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md"));
            Assert.Contains("- **Outcome:** Accepted", disposedCycle);
            Assert.Contains("- **Exchange Disposal:** Disposed", disposedCycle);
            Assert.Contains($"### {DisposingCapturingTool.DisposingToolNameValue} — call `call-1`", disposedCycle);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            await WaitUntilAsync(sceneTree, () => ListTranscriptFiles(characterDirectory).Length == 2);
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    /// <summary>
    /// An invalidated tool batch delivers the runner-generated canonical cancellation results — recorded verbatim in
    /// the cycle's tool invocation blocks — for both the co-operatively cancelled invocation and the skipped
    /// remaining call (AI-002 TR-4/5, TR-19; AI-011 TR-6, TR-14).
    /// </summary>
    [Fact]
    public async Task InvalidatedToolBatch_RecordsRunnerGeneratedCanonicalCancellationResults()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CancellableTool blockingTool = fixtures.Add(new CancellableTool());
            CapturingTool trailingTool = fixtures.Add(new CapturingTool());
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueCallBatch(
                new FunctionCallContent("blocking-call", CancellableTool.ToolNameValue, new Dictionary<string, object?>()),
                new FunctionCallContent("trailing-call", CapturingTool.ToolNameValue, new Dictionary<string, object?>()));
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, blockingTool, trailingTool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

            await blockingTool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new TestObservation(1f, "fresh-invalidates-batch", Fresh: true));

            await WaitUntilAsync(sceneTree, () => blockingTool.ObservedCancellation);
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            string characterDirectory = GetCharacterDirectory(root);
            await WaitUntilAsync(sceneTree, () => ListTranscriptFiles(characterDirectory).Length == 1);

            string invalidatedCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md"));
            Assert.Contains("- **Outcome:** Accepted", invalidatedCycle);
            Assert.Contains("- **Fresh-Turn Invalidation:**", invalidatedCycle);
            // The co-operatively cancelled invocation and the skipped trailing call both record the runner's
            // canonical cancellation result — never a hand-supplied string.
            Assert.Contains($"### {CancellableTool.ToolNameValue} — call `blocking-call`", invalidatedCycle);
            Assert.Contains($"### {CapturingTool.ToolNameValue} — call `trailing-call` (skipped — batch invalidated)", invalidatedCycle);
            Assert.Contains($"**Result**{LineFeed}{LineFeed}{AgentSessionRunner.CancelledActionResult}", invalidatedCycle);
            Assert.Empty(trailingTool.CapturedContexts);
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    /// <summary>
    /// A failing transcript write is contained: the session continues normally — later requests and tool work
    /// still happen — and the failure surfaces only as a warning (AI-011 UR-5, TR-7, TR-14).
    /// </summary>
    [Fact]
    public async Task TranscriptWriteFailure_IsContainedAsAWarningWhileTheSessionContinues()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        RecordingLoggerProvider? loggerProvider = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            _ = Directory.CreateDirectory(root);
            string blockingFile = Path.Combine(root, "blocked");
            File.WriteAllText(blockingFile, "not a directory");
            // The override points at the blocking file itself, so creating the run directory beneath it must fail.
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(blockingFile);
            loggerProvider = new RecordingLoggerProvider();
            Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool tool = fixtures.Add(new CapturingTool());
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueCall(CapturingTool.ToolNameValue);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

            // The first cycle's write fails, yet the session continues to the held second request.
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 1);

            Assert.Contains(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Warning
                    && entry.Message.Contains("Failed to record the mind session transcript", StringComparison.Ordinal));
            Assert.DoesNotContain(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            // The blocked root received no transcript tree at all.
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            loggerProvider?.Dispose();
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    /// <summary>
    /// With the toggle disabled, a full scripted session performs no transcript file input or output at all
    /// (AI-011 UR-1/UR-4, TR-5, TR-14). The disabled settings are injected hermetically: a developer machine's
    /// user override may legitimately enable the toggle, so the production default loader is not a stable
    /// disabled-state source for this fact.
    /// </summary>
    [Fact]
    public async Task DisabledToggle_WritesNoTranscriptFiles()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool tool = fixtures.Add(new CapturingTool());
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueCall(CapturingTool.ToolNameValue);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(
                owner,
                player,
                clientProvider,
                [tool],
                enableTranscriptLogging: false);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 1);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            // The null-object path never touched the filesystem: the redirected root was never even created.
            Assert.False(Directory.Exists(root));
            Assert.Equal(2, clientProvider.Requests.Count);
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    /// <summary>
    /// Turn numbering is flat per character across mind re-creation within one game run: the re-created mind's
    /// first cycle continues from the previous counter value under the same run directory (AI-011 UR-3, TR-4,
    /// TR-14).
    /// </summary>
    [Fact]
    public async Task MindReCreation_ContinuesTurnNumberingWithinOneRun()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? firstMind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool firstTool = fixtures.Add(new CapturingTool());
            ScriptedSessionClientProvider firstProvider = fixtures.Add(new ScriptedSessionClientProvider());
            firstProvider.EnqueueCall(CapturingTool.ToolNameValue);
            firstProvider.EnqueueHoldForever();
            firstMind = CreateTranscriptMind(owner, player, firstProvider, firstTool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(firstMind);
            await WaitUntilAsync(sceneTree, () => firstTool.CapturedContexts.Count == 1);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(firstMind);
            await WaitUntilAsync(sceneTree, firstProvider.EndedByCancellation);

            string characterDirectory = GetCharacterDirectory(root);
            await WaitUntilAsync(sceneTree, () => ListTranscriptFiles(characterDirectory).Length == 2);

            // A re-created mind node for the same character starts a fresh session in the same game run.
            TestAgenticMind? secondMind = null;
            FixtureBag secondFixtures = new();
            try
            {
                CapturingTool secondTool = secondFixtures.Add(new CapturingTool());
                ScriptedSessionClientProvider secondProvider = secondFixtures.Add(new ScriptedSessionClientProvider());
                secondProvider.EnqueueCall(CapturingTool.ToolNameValue);
                secondProvider.EnqueueHoldForever();
                secondMind = CreateTranscriptMind(owner, player, secondProvider, secondTool);
                (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(secondMind);
                await WaitUntilAsync(sceneTree, () => secondTool.CapturedContexts.Count == 1);
                await WaitUntilAsync(sceneTree, () => ListTranscriptFiles(characterDirectory).Length == 3);

                Assert.Equal(
                    ["turn-0001.md", "turn-0002.md", "turn-0003.md"],
                    ListTranscriptFiles(characterDirectory));
                Assert.Contains(
                    "# Mind Session Transcript — Turn 0003",
                    File.ReadAllText(Path.Combine(characterDirectory, "turn-0003.md")));

                (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(secondMind);
                await WaitUntilAsync(sceneTree, secondProvider.EndedByCancellation);
                await WaitUntilAsync(sceneTree, () => ListTranscriptFiles(characterDirectory).Length == 4);
                Assert.Equal(
                    ["turn-0001.md", "turn-0002.md", "turn-0003.md", "turn-0004.md"],
                    ListTranscriptFiles(characterDirectory));
            }
            finally
            {
                if (secondMind is not null)
                {
                    await EndSessionForCleanupAsync(sceneTree, secondMind);
                    secondMind.Free();
                }

                secondFixtures.FreeAll();
            }
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, firstMind, fixtures, root);
        }
    }

    /// <summary>
    /// A test failing mid-session exercises the cleanup-protected region: the outer <c>finally</c> terminates and
    /// awaits the session while the temporary root override is still installed, so no outstanding recorder activity
    /// survives cleanup, nothing can reach the production <c>user://</c> tree, and subsequent sessions in the same
    /// process run normally (AI-011 TR-12, TR-14).
    /// </summary>
    [Fact]
    public async Task EarlyFailureCleanup_ReleasesRecorderActivityAndSubsequentSessionsRun()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string firstRoot = CreateTranscriptRoot();
        try
        {
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                () => RunSessionUntilEarlyFailureAsync(sceneTree, firstRoot));

            // Cleanup fully quiesced the recorder: further frames write nothing more to the completed session's
            // tree, and the recorded cycles remain readable after the seams were restored.
            string firstCharacterDirectory = GetCharacterDirectory(firstRoot);
            int settledFileCount = ListTranscriptFiles(firstCharacterDirectory).Length;
            await TestUtils.WaitForFramesAsync(sceneTree, 10);
            Assert.Equal(settledFileCount, ListTranscriptFiles(firstCharacterDirectory).Length);
            Assert.True(settledFileCount >= 2, "The early-failed session must have flushed its completed and interrupted cycles.");
            Assert.Contains(
                "- **Outcome:** Accepted",
                File.ReadAllText(Path.Combine(firstCharacterDirectory, "turn-0001.md")));
        }
        finally
        {
            try
            {
                Directory.Delete(firstRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing was ever written under this root.
            }
        }

        await RunSuccessfulSubsequentSessionAsync(sceneTree);
    }

    /// <summary>
    /// Cleanup waits out a non-cooperative provider that keeps its request open past node-lifetime cancellation:
    /// quiescence is achieved only after the provider settles, the interrupted cycle — including its retained
    /// response contents — is flushed before the seams are released, and the production root is never touched
    /// (AI-011 TR-6, TR-12).
    /// </summary>
    [Fact]
    public async Task Cleanup_WaitsForNonCooperativeProviderCompletion_BeforeRestoringSeams()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        bool realTreeExistedBefore = RealTranscriptTreeExists();
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool tool = fixtures.Add(new CapturingTool());
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            // The first request ignores every cancellation and only settles after a delay that outlives the test
            // body, so cleanup must wait for genuine session completion.
            clientProvider.EnqueueNonCooperativeDelayedCall(TimeSpan.FromSeconds(0.5), CapturingTool.ToolNameValue);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 1);
        }
        finally
        {
            // Quiescence is required, not assumed: the interrupted cycle's file only exists because cleanup waited
            // for the delayed provider before restoring the seams. The tree is retained for post-cleanup asserts.
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root, deleteTree: false);
        }

        string characterDirectory = GetCharacterDirectory(root);
        Assert.Equal(["turn-0001.md"], ListTranscriptFiles(characterDirectory));
        string interruptedCycle = File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md"));
        Assert.Contains("- **Outcome:** Interrupted", interruptedCycle);
        // The response returned after cancellation is retained for diagnostics (AI-011 TR-6) and was flushed
        // before seam restoration.
        Assert.Contains("**Function Call:** capture_context", interruptedCycle);
        Assert.Equal(realTreeExistedBefore, RealTranscriptTreeExists());
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was ever written under this root.
        }
    }

    /// <summary>
    /// When quiescence is impossible — a provider that never settles — cleanup fails loudly instead of silently
    /// restoring the production root, seam restoration still runs despite the cleanup failure, a setup failure
    /// mid-protected-region leaves no tainted state, and subsequent sessions run normally (AI-011 TR-12).
    /// </summary>
    [Fact]
    public async Task Cleanup_WhenQuiescenceIsImpossible_FailsLoudlyRestoresSeamsAndSubsequentSessionsRun()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        bool realTreeExistedBefore = RealTranscriptTreeExists();

        // A setup failure inside the protected region — after the seams are installed, before the session starts —
        // still restores the seams.
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunFailingSetupScopeAsync(sceneTree, CreateTranscriptRoot()));

        // A session that can never end makes quiescence impossible: cleanup fails loudly, yet the seams are
        // restored by the unbypassable inner finally. The parked provider step never completes, so the inert
        // session can never record again after the override is released.
        _ = await Assert.ThrowsAnyAsync<Exception>(() => RunUnendingSessionScopeAsync(sceneTree, CreateTranscriptRoot()));

        Assert.Equal(realTreeExistedBefore, RealTranscriptTreeExists());
        await RunSuccessfulSubsequentSessionAsync(sceneTree);
    }

    /// <summary>
    /// A late non-cooperative provider completion arriving after session end and seam restoration is a contained
    /// no-op: the zombie continuation records nowhere — neither the sentinel root that would catch a stray write
    /// nor the production root — and the session ends inertly (AI-011 TR-7, TR-12).
    /// </summary>
    [Fact]
    public async Task LateProviderCompletionAfterSeamRestoration_PerformsNoWriteAnywhere()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        bool realTreeExistedBefore = RealTranscriptTreeExists();
        string scopeRoot = CreateTranscriptRoot();
        string sentinelRoot = CreateTranscriptRoot();
        _ = Directory.CreateDirectory(sentinelRoot);
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        TaskCompletionSource releaseZombie = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? cleanupFailure = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(scopeRoot);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool tool = fixtures.Add(new CapturingTool());
            TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueNonCooperativeHold(requestStarted, releaseZombie);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try
            {
                // Cleanup cannot achieve quiescence while the zombie request stays open, so it fails loudly —
                // the expected outcome here — while the unbypassable inner finally restores the seams.
                await CleanupSessionScopeAsync(sceneTree, mind, fixtures, scopeRoot);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            finally
            {
                // Redirect any stray zombie write into the sentinel so the production root stays untouched and a
                // stray write stays observable.
                MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(sentinelRoot);
            }
        }

        Assert.NotNull(cleanupFailure);

        // The zombie continuation now settles after the session ended and the seams were restored: its capture
        // attempt must be a contained no-op, and the session must still end inertly.
        _ = releaseZombie.TrySetResult();
        Assert.NotNull(mind);
        await WaitUntilAsync(sceneTree, () => mind.ActiveRunner is null, maxFrames: 2000);

        Assert.Empty(Directory.GetFileSystemEntries(sentinelRoot));
        Assert.False(Directory.Exists(scopeRoot), "The deleted scope root must stay deleted.");
        Assert.Equal(realTreeExistedBefore, RealTranscriptTreeExists());

        mind.Free();
        fixtures.FreeAll();
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        try
        {
            Directory.Delete(sentinelRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was ever written under the sentinel root.
        }
    }

    /// <summary>
    /// Runs one enabled session and fails deliberately once its first transcript file exists, so the fact's
    /// cleanup-protected <c>finally</c> executes exactly as it would for a genuine mid-session assertion failure.
    /// </summary>
    private static async Task RunSessionUntilEarlyFailureAsync(SceneTree sceneTree, string root)
    {
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool tool = fixtures.Add(new CapturingTool());
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueCall(CapturingTool.ToolNameValue);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 1);
            string characterDirectory = GetCharacterDirectory(root);
            await WaitUntilAsync(sceneTree, () => ListTranscriptFiles(characterDirectory).Length == 1);
            throw new InvalidOperationException("Simulated early assertion failure.");
        }
        finally
        {
            // Terminate and await the session while the temporary root remains installed; restore the seams only
            // after the mind is freed. The tree itself is left in place for the caller's post-cleanup assertions
            // (AI-011 TR-12).
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root, deleteTree: false);
        }
    }

    /// <summary>
    /// Runs a protected scope whose body fails during setup — after the seams are installed but before any session
    /// starts — proving the null-fixture cleanup path still restores the seams (AI-011 TR-12).
    /// </summary>
    private static async Task RunFailingSetupScopeAsync(SceneTree sceneTree, string root)
    {
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            _ = fixtures.Add(new FixturePlayerCharacter());
            throw new InvalidOperationException("Simulated setup failure.");
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    /// <summary>
    /// Runs a protected scope whose provider never settles — ignoring every cancellation — so session quiescence is
    /// impossible and cleanup must fail loudly rather than silently release the production root (AI-011 TR-12).
    /// The recorder's deactivation gate keeps the parked session's eventual activity silent after restoration.
    /// </summary>
    private static async Task RunUnendingSessionScopeAsync(SceneTree sceneTree, string root)
    {
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool tool = fixtures.Add(new CapturingTool());
            TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueNonCooperativeHold(requestStarted);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    /// <summary>
    /// Runs one ordinary successful transcript session — the standing proof that a prior scope's failures left the
    /// recorder seams and the session runtime fully usable (AI-011 TR-12).
    /// </summary>
    private static async Task RunSuccessfulSubsequentSessionAsync(SceneTree sceneTree)
    {
        string root = CreateTranscriptRoot();
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            TestCharacter owner = new();
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            CapturingTool tool = fixtures.Add(new CapturingTool());
            ScriptedSessionClientProvider clientProvider = fixtures.Add(new ScriptedSessionClientProvider());
            clientProvider.EnqueueCall(CapturingTool.ToolNameValue);
            clientProvider.EnqueueHoldForever();
            mind = CreateTranscriptMind(owner, player, clientProvider, tool);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 1);

            string characterDirectory = GetCharacterDirectory(root);
            await WaitUntilAsync(sceneTree, () => ListTranscriptFiles(characterDirectory).Length >= 1);
            Assert.Contains("- **Outcome:** Accepted", File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md")));
        }
        finally
        {
            await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root);
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex SortableRunTimestamp();

    private static string CreateTranscriptRoot()
        => Path.Combine(
            Path.GetTempPath(),
            "AlleyCat.MindSessionTranscriptIntegrationTests",
            Guid.NewGuid().ToString("N"));

    private static bool RealTranscriptTreeExists()
        => Directory.Exists(ProjectSettings.GlobalizePath(MindSessionTranscriptRecorder.UserRootPath));

    /// <summary>
    /// Ends one protected transcript scope (AI-011 TR-12 seam safety): terminates and awaits the session, frees the
    /// mind and fixtures, and restores the recorder seams in a nested <c>finally</c> that no earlier cleanup
    /// exception can bypass.
    /// </summary>
    private static async Task CleanupSessionScopeAsync(
        SceneTree sceneTree,
        TestAgenticMind? mind,
        FixtureBag fixtures,
        string root,
        bool deleteTree = true)
    {
        try
        {
            if (mind is not null)
            {
                await EndSessionForCleanupAsync(sceneTree, mind);
                mind.Free();
            }

            fixtures.FreeAll();
        }
        finally
        {
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            if (deleteTree)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                    // Nothing was ever written under this root.
                }
            }
        }
    }

    /// <summary>
    /// Creates the mind fixture wired to the scripted provider with hermetically injected diagnostics settings
    /// (AI-011 TR-5): a developer machine's user override may legitimately enable the transcript toggle, so tests
    /// never depend on the merged production configuration.
    /// </summary>
    private static TestAgenticMind CreateTranscriptMind(
        TestCharacter owner,
        FixturePlayerCharacter player,
        ScriptedSessionClientProvider clientProvider,
        params AgentTool[] tools)
        => CreateTranscriptMind(owner, player, clientProvider, tools, enableTranscriptLogging: true);

    private static TestAgenticMind CreateTranscriptMind(
        TestCharacter owner,
        FixturePlayerCharacter player,
        ScriptedSessionClientProvider clientProvider,
        AgentTool[] tools,
        bool enableTranscriptLogging)
    {
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack
            {
                Sections = [new TextPromptSection { Text = "Static session guidance.", Name = "Static" }],
            },
            CurrentSceneStatus = new PromptStack
            {
                Sections = [new TextPromptSection { Text = SceneStatusText, Name = "Status" }],
            },
            ClientProvider = clientProvider,
            Tools = [.. tools],
            ObservationImportanceThreshold = 1f,
        };
        mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(
            EnableRequestResponseLogging: false,
            EnableReasoningLogging: true,
            EnableSessionTranscriptLogging: enableTranscriptLogging));
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        return mind;
    }

    /// <summary>Resolves the single character directory under the one sortable run directory of the root.</summary>
    private static string GetCharacterDirectory(string root)
    {
        string runDirectory = Assert.Single(Directory.GetDirectories(root));
        Assert.Matches(SortableRunTimestamp(), Path.GetFileName(runDirectory));
        return Assert.Single(Directory.GetDirectories(runDirectory, CharacterFolderName));
    }

    private static string[] ListTranscriptFiles(string characterDirectory)
        => [.. Directory.GetFiles(characterDirectory)
            .Order()
            .Select(static path => Path.GetFileName(path)!)];

    /// <summary>
    /// Terminates the session and awaits its completion — including every flushed transcript write — while the
    /// temporary root override is still installed. Quiescence is required, not assumed: failing to reach it fails
    /// loudly rather than letting cleanup silently release the production root (AI-011 TR-12 seam safety).
    /// </summary>
    private static async Task EndSessionForCleanupAsync(SceneTree sceneTree, TestAgenticMind mind)
    {
        if (mind.GetParent() is not null)
        {
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
        }

        for (int frame = 0; frame < 2000 && mind.ActiveRunner is not null; frame++)
        {
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(
            mind.ActiveRunner is null,
            "The transcript session must fully end before cleanup restores the recorder seams.");
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames = 600)
    {
        for (int frame = 0; frame < maxFrames && !predicate(); frame++)
        {
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private sealed record TestObservation(float Importance, string Value, bool Fresh = false) : AgentObservation
    {
        public override string TypeKey => ObservedSpeech.TypeKeyValue;

        protected override string RenderBody(ICharacter character)
        {
            ArgumentNullException.ThrowIfNull(character);
            return $"- {Value}";
        }

        public override float CalculateImportance(ObservationContext context) => Importance;

        public override bool RequiresFreshTurn(ObservationContext context) => Fresh;
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>Godot fixtures owned by one transcript scope, freed in creation order during cleanup.</summary>
    private sealed class FixtureBag
    {
        private readonly List<GodotObject> _fixtures = [];

        public T Add<T>(T fixture)
            where T : GodotObject
        {
            _fixtures.Add(fixture);
            return fixture;
        }

        public void FreeAll()
        {
            foreach (GodotObject fixture in _fixtures)
            {
                fixture.Free();
            }

            _fixtures.Clear();
        }
    }

    /// <summary>Ordinary retaining tool whose invocations the test counts.</summary>
    private partial class CapturingTool : AgentTool
    {
        public const string ToolNameValue = "capture_context";

        public CapturingTool()
        {
            ToolName = ToolNameValue;
            ToolDescription = "Capture the trusted session context.";
        }

        public List<ScenarioContext> CapturedContexts { get; } = [];

        protected override Delegate CreateDelegate() => Capture;

        private ValueTask<AgentToolResult> Capture(ScenarioContext context)
        {
            CapturedContexts.Add(context);
            return ValueTask.FromResult(new AgentToolResult());
        }
    }

    /// <summary>
    /// Disposal-opted capture: a whole batch of its calls has its completed exchange disposed (AI-002 TR-17).
    /// </summary>
    private sealed partial class DisposingCapturingTool : CapturingTool
    {
        public const string DisposingToolNameValue = "disposing_capture";

        public DisposingCapturingTool()
        {
            ToolName = DisposingToolNameValue;
            DisposesExchangeOnCompletion = true;
        }
    }

    /// <summary>
    /// Tool that blocks until cancelled, so a fresh-turn invalidation can cancel it co-operatively mid-flight; it
    /// observes the cancellation and rethrows, modelling an in-flight action whose result never existed.
    /// </summary>
    private sealed partial class CancellableTool : AgentTool
    {
        public const string ToolNameValue = "cancellable_action";

        public CancellableTool()
        {
            ToolName = ToolNameValue;
            ToolDescription = "Block until cancelled.";
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation
        {
            get;
            private set;
        }

        protected override Delegate CreateDelegate() => Block;

        private async ValueTask<AgentToolResult> Block(ScenarioContext context, CancellationToken cancellationToken)
        {
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

            return new AgentToolResult();
        }
    }

    /// <summary>
    /// Scripted provider whose client records every request and serves enqueued steps; unscripted requests fail
    /// loudly so unexpected session activity surfaces in assertions.
    /// </summary>
    private sealed partial class ScriptedSessionClientProvider : ClientProvider
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new();

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public void EnqueueHold(TaskCompletionSource started)
            => EnqueueHoldStep(started);

        public void EnqueueHoldForever()
            => EnqueueHoldStep(null);

        /// <summary>
        /// Enqueues a step that ignores every cancellation and holds its request until the supplied release (or
        /// forever when null), so a session can be made genuinely unable to end — and later released to model a
        /// late non-cooperative completion.
        /// </summary>
        public void EnqueueNonCooperativeHold(TaskCompletionSource started, TaskCompletionSource? release = null)
            => _steps.Enqueue(async cancellationToken =>
            {
                _ = started.TrySetResult();
                _ = cancellationToken;
                await (release?.Task ?? new TaskCompletionSource().Task);
                return new ChatResponse();
            });

        /// <summary>
        /// Enqueues a step that ignores every cancellation and settles only after the supplied delay, modelling a
        /// non-cooperative provider returning late after node-lifetime cancellation.
        /// </summary>
        public void EnqueueNonCooperativeDelayedCall(TimeSpan delay, string toolName)
            => _steps.Enqueue(async cancellationToken =>
            {
                _ = cancellationToken;
                await Task.Delay(delay);
                return new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call-{Requests.Count}", toolName, new Dictionary<string, object?>())]));
            });

        /// <summary>
        /// Enqueues a step that holds its request until released and then returns one valid call for the supplied
        /// tool, so a fixture can observe world state between a held generation and the next request boundary.
        /// </summary>
        public void EnqueueHoldUntilReleasedCall(
            TaskCompletionSource started,
            TaskCompletionSource release,
            string toolName)
            => _steps.Enqueue(async cancellationToken =>
            {
                _ = started.TrySetResult();
                _ = cancellationToken;
                await release.Task;
                return new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call-{Requests.Count}", toolName, new Dictionary<string, object?>())]));
            });

        public void EnqueueCall(string toolName)
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    // Call identifiers are minted at dequeue time so upfront-scripted batches stay unique.
                    [new FunctionCallContent($"call-{Requests.Count}", toolName, new Dictionary<string, object?>())]))));

        /// <summary>Enqueues one complete multi-call batch so an invalidation can skip its remaining calls.</summary>
        public void EnqueueCallBatch(params FunctionCallContent[] calls)
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, calls))));

        public void EnqueueInvalidResponse()
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "invalid ordinary assistant text"))));

        public void EnqueueTransientFailure()
            => _steps.Enqueue(cancellationToken => Task.FromException<ChatResponse>(new HttpRequestException("reset")));

        public bool EndedByCancellation()
            => Volatile.Read(ref _endedByCancellation) != 0;

        public override IChatClient CreateChatClient() => new ScriptedClient(this);

        private void EnqueueHoldStep(TaskCompletionSource? started)
        {
            _steps.Enqueue(async cancellationToken =>
            {
                _ = (started?.TrySetResult());
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Volatile.Write(ref _endedByCancellation, 1);
                    throw;
                }

                return new ChatResponse();
            });
        }

        private int _endedByCancellation;

        private sealed class ScriptedClient(ScriptedSessionClientProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _ = options;
                owner.Requests.Add([.. messages]);
                cancellationToken.ThrowIfCancellationRequested();
                return owner._steps.Count == 0
                    ? throw new InvalidOperationException(
                        "The scripted session client received an unexpected request.")
                    : owner._steps.Dequeue()(cancellationToken);
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

    private sealed class TestCharacter(params IComponent[] components) : ICharacter
    {
        public string Id { get; set; } = CharacterFolderName;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _lock = new();
        private readonly List<LogEntry> _entries = [];
        private bool _disposed;

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose() => _disposed = true;

        private void Record(LogLevel level, string message, Exception? exception)
        {
            if (_disposed)
            {
                return;
            }

            lock (_lock)
            {
                _entries.Add(new LogEntry(level, message, exception));
            }
        }

        private sealed class RecordingLogger(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _ = eventId;
                provider.Record(logLevel, formatter(state, exception), exception);
            }
        }

        public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
    }
}
