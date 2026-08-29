using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for the AgenticMind session lifecycle: fire-and-forget start with containment, the
/// urgency-aware observation-delivery bridge — ordinary boundary injection versus fresh-turn invalidation, and
/// wait-owned fresh delivery through the wait's natural result — and node-exit cancellation of generation and tool
/// work.
/// </summary>
[Headless]
public sealed partial class AgenticMindSessionLifecycleIntegrationTests
{
    /// <summary>
    /// Missing session configuration is contained: the failure is logged once, the session never issues a
    /// request, and it stays ended for the node's remaining lifetime (AI-002 TR-1/2).
    /// </summary>
    [Fact]
    public async Task SessionFailure_WithMissingConfiguration_IsContainedLoggedOnceAndStaysEnded()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        ScriptedSessionClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(
                sceneTree,
                () => loggerProvider.Entries.Any(entry =>
                    entry.Level == LogLevel.Error
                    && entry.Exception is InvalidOperationException
                    && entry.Exception.Message.Contains("SystemInstruction prompt stack", StringComparison.Ordinal)));
            mind.ObserveForTest(new TestObservation(1f, "after-failure"));
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            _ = Assert.Single(loggerProvider.Entries, entry =>
                    entry.Level == LogLevel.Error
                    && entry.Exception is InvalidOperationException);
            Assert.Empty(clientProvider.Requests);
            Assert.Empty(mind.GetLatestRenderContext());
            Assert.Equal(["after-failure"], TimelineValues(mind));
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// An ordinary notable signal during held generation never cancels the request: the generation completes, its
    /// tool batch executes, and the rendered notable summary lands as one injected user message at the next natural
    /// request boundary without any extra model request (AI-001 TR-6, AI-002 TR-39).
    /// </summary>
    [Fact]
    public async Task NotableSignal_DuringGeneration_CompletesGenerationAndInjectsAtNextBoundary()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHoldUntilReleasedCall(firstRequestStarted, releaseGeneration, "capture_context");
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            // The authored fragment proves the injection carries genuinely rendered record content instead of a
            // count-only summary.
            EventHistoryPath = "res://assets/testing/prompts/test_event_history_lifecycle.md",
            ClientProvider = clientProvider,
            Tools = [tool],
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new TestObservation(1f, "bridge"));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            // No cancellation and no separate delivery request: the held generation is still the only request.
            _ = Assert.Single(clientProvider.Requests);
            _ = releaseGeneration.TrySetResult();

            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 1);
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            IReadOnlyList<ChatMessage> nextRequest = clientProvider.Requests[1];
            // The naturally-next request replays the completed exchange and then the injected notable summary,
            // whose text must be the exact rendered event-history fragment output (AI-002 TR-7/39).
            Assert.Equal(
                [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
                nextRequest.Select(message => message.Role));
            ChatMessage injected = nextRequest[3];
            Assert.Equal(AgenticMind.SessionBootstrapInput, nextRequest[0].Text);
            Assert.Equal("Important scene events require your attention:\n- bridge\n", injected.Text);
            Assert.DoesNotContain("notable observation(s)", injected.Text, StringComparison.Ordinal);

            // Node exit ends the held second request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            Assert.Equal(2, clientProvider.Requests.Count);
            Assert.Equal(["bridge"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// Non-self observed speech during held generation requires a fresh turn immediately: the stale request is
    /// cancelled at once and replaced by a single fresh request whose injected user message carries the rendered
    /// speech observation (AI-001 TR-43, AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task ExternalSpeech_DuringGeneration_InvalidatesStaleGenerationImmediately()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            EventHistoryPath = "res://prompts/event_history.md",
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new ObservedSpeech("char:someone-else", "voice-1", "You there?"));

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            IReadOnlyList<ChatMessage> freshRequest = clientProvider.Requests[1];
            // The fresh replacement request carries the bootstrap input followed by exactly one injected user
            // message with the rendered heard-speech record (AI-002 TR-7/40).
            Assert.Equal(
                [ChatRole.User, ChatRole.User],
                freshRequest.Select(message => message.Role));
            Assert.Equal(AgenticMind.SessionBootstrapInput, freshRequest[0].Text);
            Assert.StartsWith(
                "Important scene events require your attention:",
                freshRequest[1].Text,
                StringComparison.Ordinal);
            Assert.Contains("Heard char:someone-else say: You there?", freshRequest[1].Text, StringComparison.Ordinal);

            // Node exit ends the held replacement request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// External non-self speech observed mid-wait fulfils the wait through its normal completion mechanism: the
    /// wait's own tool result carries the fresh speech plus its preceding FIFO accumulation — never generic
    /// action-interrupted wording — the batch's trailing call is skipped with the canonical cancellation result,
    /// no duplicate injected message exists for the wait-owned window, and exactly one replacement request
    /// follows (AI-001 TR-43, AI-002 TR-40/41).
    /// </summary>
    [Fact]
    public async Task ExternalSpeech_DuringActiveWait_DeliversWindowThroughNaturalWaitResultWithSingleReplacement()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("wait-call", "wait", new Dictionary<string, object?> { ["seconds"] = 5f }),
            new FunctionCallContent("trailing-call", "capture_context", new Dictionary<string, object?>()));
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            EventHistoryPath = "res://prompts/event_history.md",
            ClientProvider = clientProvider,
            Tools = [tool],
            AllowMultipleToolCalls = true,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 1);
            // The observation must land while the wait owns delivery, so synchronise on its registration.
            await WaitUntilAsync(sceneTree, () => mind.HasActiveObservationWait);

            // A sub-threshold ordinary observation accumulates without waking the wait, then external non-self
            // speech upgrades the complete window to fresh urgency and wakes the wait through its normal
            // completion mechanism.
            mind.ObserveForTest(new RouteObservation("world.before-speech"));
            mind.ObserveForTest(new ObservedSpeech("char:someone-else", "voice-1", "You there?"));

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            // Exactly one replacement request replays the complete exchange: bootstrap input, the validated
            // batch's assistant calls, and one protocol-valid tool-result message — with no injected user message
            // duplicating the wait-owned window.
            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[1];
            Assert.Equal(2, clientProvider.Requests.Count);
            Assert.Equal(
                [ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                replacement.Select(message => message.Role));

            FunctionResultContent waitResult = Assert.IsType<FunctionResultContent>(replacement[2].Contents[0]);
            FunctionResultContent trailingResult = Assert.IsType<FunctionResultContent>(replacement[2].Contents[1]);
            string waitText = waitResult.Result?.ToString() ?? string.Empty;

            // The wait result is its window's sole delivery channel: the fresh speech arrives with its preceding
            // accumulation in FIFO order and the fresh-wake lead — never an empty or interrupted notice.
            Assert.Equal("wait-call", waitResult.CallId);
            Assert.Contains("Fresh events arrived", waitText, StringComparison.Ordinal);
            int preceding = waitText.IndexOf("((Received world.before-speech event.))", StringComparison.Ordinal);
            int speech = waitText.IndexOf("Heard char:someone-else say: You there?", StringComparison.Ordinal);
            Assert.True(
                preceding >= 0 && speech > preceding,
                $"The wait result must deliver the accumulation before the speech in FIFO order: '{waitText}'");

            // No generic action-interrupted wording anywhere in the exchange: the wait reports its natural
            // delivery, and the canonical cancelled result appears exactly once — only for the never-started
            // trailing call the stale batch required the runner to skip.
            Assert.DoesNotContain("cancelled", waitText, StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing notable happened", waitText, StringComparison.Ordinal);
            Assert.Empty(tool.CapturedContexts);
            Assert.Equal("trailing-call", trailingResult.CallId);
            Assert.Equal("The action was cancelled before it completed.", trailingResult.Result?.ToString());
            Assert.Contains(
                replacement.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
                result => result.Result?.ToString()?.Contains("cancelled before it completed", StringComparison.Ordinal) == true
                    && result.CallId == "trailing-call");

            // The wait consumed its window: nothing stays claimable for a duplicate injected delivery.
            Assert.False(mind.HasActiveObservationWait);
            Assert.Null(mind.TryClaimDeliveryForTest());

            // Node exit ends the held replacement request quietly without issuing another request.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            Assert.Equal(2, clientProvider.Requests.Count);
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// An ordinary visual description during held generation is delivered without cancellation, rendered through
    /// the production-authored NPC event-history document in accumulation order at the next natural boundary, and
    /// without falling back to generic event wording (AI-002 TR-39, AI-003 AC-18).
    /// </summary>
    [Fact]
    public async Task NotableVisualDescription_DuringGeneration_UsesAuthoredFragmentChronologically()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHoldUntilReleasedCall(firstRequestStarted, releaseGeneration, "capture_context");
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            EventHistoryPath = "res://prompts/event_history.md",
            ClientProvider = clientProvider,
            Tools = [tool],
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new RouteObservation("world.changed"));
            mind.ObserveForTest(new ObservedVisualDescription("char:coat", "A weathered red coat."));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            // The visual description crossed the threshold as an ordinary delivery: the generation still holds.
            _ = Assert.Single(clientProvider.Requests);
            _ = releaseGeneration.TrySetResult();

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            ChatMessage injected = clientProvider.Requests[1][3];
            int preceding = injected.Text.IndexOf("((Received world.changed event.))", StringComparison.Ordinal);
            int visual = injected.Text.IndexOf(
                "Observed char:coat: A weathered red coat.",
                StringComparison.Ordinal);

            Assert.Equal(ChatRole.User, injected.Role);
            Assert.True(
                preceding >= 0 && visual > preceding,
                "The authored visual description must follow the earlier accumulated event.");
            Assert.DoesNotContain(
                "((Received vision.description event.))",
                injected.Text,
                StringComparison.Ordinal);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            IReadOnlyList<AgentObservation> timeline = mind.GetTimelineForTest();
            Assert.Collection(
                timeline,
                observation => Assert.IsType<RouteObservation>(observation),
                observation => Assert.IsType<ObservedVisualDescription>(observation));
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// A failed ordinary notable-summary render is a hard, contained failure: the fault surfaces exactly once
    /// through Error logging with the full exception, no injection reaches the backend, the held generation
    /// continues, and the window's delivery ownership is restored to Mind rather than silently lost (AI-001 TR-44,
    /// AI-002 TR-39).
    /// </summary>
    [Fact]
    public async Task NotableSignal_RenderFailure_RetainsSchedulingOwnershipWithoutInjecting()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.SetActiveHistoryRendererForTesting(CreateThrowingHistoryRenderer());
            mind.ObserveForTest(new TestObservation(1f, "bridge"));

            await WaitUntilAsync(sceneTree, () => loggerProvider.Entries.Any(entry =>
                entry.Level == LogLevel.Error
                && entry.Exception?.GetBaseException().Message.Contains(
                    FaultingRenderMessage, StringComparison.Ordinal) == true));
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            // The render fault is contained as exactly one Error entry, and no second request ever leaves: the
            // injection was not queued and the session continues on its original request.
            _ = Assert.Single(loggerProvider.Entries, entry => entry.Level == LogLevel.Error);
            _ = Assert.Single(clientProvider.Requests);

            // The abandoned window stays claimable: scheduling ownership was restored, never silently lost.
            MindBase.ObservationDeliveryClaim? restored = mind.TryClaimDeliveryForTest();
            Assert.NotNull(restored);
            Assert.Equal(["bridge"], ClaimValues(restored));
            mind.CompleteDeliveryForTest(restored);

            // Node exit ends the still-held generation quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            Assert.Equal(["bridge"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// A missing active renderer during an ordinary notable signal trips the hard guard: the fault surfaces
    /// through Error logging as an <see cref="InvalidOperationException" /> naming the missing renderer, nothing is
    /// injected, and the window's ownership is restored to Mind (AI-001 TR-44, AI-002 TR-39).
    /// </summary>
    [Fact]
    public async Task NotableSignal_WithoutActiveHistoryRenderer_RestoresOwnershipAndNeverInjects()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.SetActiveHistoryRendererForTesting(null);
            mind.ObserveForTest(new TestObservation(1f, "bridge"));

            await WaitUntilAsync(sceneTree, () => loggerProvider.Entries.Any(entry =>
                entry.Level == LogLevel.Error
                && entry.Exception?.GetBaseException() is InvalidOperationException invalidOperationException
                && invalidOperationException.Message.Contains(
                    "ObservationHistoryRenderer", StringComparison.Ordinal)));
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            // The guard fault is contained as exactly one Error entry, and no second request ever leaves.
            _ = Assert.Single(loggerProvider.Entries, entry => entry.Level == LogLevel.Error);
            _ = Assert.Single(clientProvider.Requests);

            // The abandoned window stays claimable: scheduling ownership was restored, never silently lost.
            MindBase.ObservationDeliveryClaim? restored = mind.TryClaimDeliveryForTest();
            Assert.NotNull(restored);
            Assert.Equal(["bridge"], ClaimValues(restored));
            mind.CompleteDeliveryForTest(restored);

            // Node exit ends the still-held generation quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            Assert.Equal(["bridge"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// A failed fresh-speech render still invalidates the stale generation, but the replacement request proceeds
    /// without an injected message: the rendering barrier is released, and the fresh window — with its fresh
    /// urgency — is restored to Mind instead of being silently lost (AI-001 TR-43/44, AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task FreshSpeech_RenderFailure_SupersedesGenerationWithoutPayloadAndRetainsOwnership()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.SetActiveHistoryRendererForTesting(CreateThrowingHistoryRenderer());
            mind.ObserveForTest(new ObservedSpeech("char:someone-else", "voice-1", "You there?"));

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await WaitUntilAsync(sceneTree, () => loggerProvider.Entries.Any(entry =>
                entry.Level == LogLevel.Error
                && entry.Exception?.GetBaseException().Message.Contains(
                    FaultingRenderMessage, StringComparison.Ordinal) == true));
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            // The stale generation was invalidated and replaced, but the replacement carries no injected message:
            // the fresh payload never rendered.
            Assert.Equal(
                [ChatRole.User],
                clientProvider.Requests[1].Select(message => message.Role));
            _ = Assert.Single(loggerProvider.Entries, entry => entry.Level == LogLevel.Error);

            // The fresh window stays claimable with its fresh urgency: ownership was restored, never lost.
            MindBase.ObservationDeliveryClaim? restored = mind.TryClaimDeliveryForTest();
            Assert.NotNull(restored);
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Fresh, restored.Urgency);
            mind.CompleteDeliveryForTest(restored);

            // Node exit ends the still-held replacement request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// An ordinary pending window followed by a fresh observation coalesces in FIFO order into exactly one
    /// injected user message on the single fresh replacement request (AI-002 TR-39/40).
    /// </summary>
    [Fact]
    public async Task OrdinaryAndFreshObservations_CoalesceFIFOIntoOneFreshReplacementRequest()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            EventHistoryPath = "res://assets/testing/prompts/test_event_history_lifecycle.md",
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new TestObservation(1f, "ordinary-event"));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            mind.ObserveForTest(new TestObservation(1f, "fresh-event", Fresh: true));

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            IReadOnlyList<ChatMessage> freshRequest = clientProvider.Requests[1];
            Assert.Equal(
                [ChatRole.User, ChatRole.User],
                freshRequest.Select(message => message.Role));
            // Both windows coalesced into the single injected user message in FIFO order: the ordinary event's
            // rendered record precedes the fresh event's record inside the one message.
            int ordinary = freshRequest[1].Text.IndexOf("- ordinary-event", StringComparison.Ordinal);
            int fresh = freshRequest[1].Text.IndexOf("- fresh-event", StringComparison.Ordinal);
            Assert.True(
                ordinary >= 0 && fresh > ordinary,
                $"The coalesced injection must render both windows in FIFO order: '{freshRequest[1].Text}'");

            // Node exit ends the held replacement request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// Node exit cancels an in-flight generation request: the session ends quietly without retry and without
    /// backend-failure diagnostics, while the Mind timeline persists (AI-002 TR-44, AI-001 TR-18).
    /// </summary>
    [Fact]
    public async Task NodeExit_DuringGeneration_EndsSessionQuietlyWithoutRetryOrBackendFailure()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(requestStarted);
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        // Sub-threshold: the observation must not interrupt the held generation, unlike node exit.
        mind.ObserveForTest(new TestObservation(0.5f, "persisted"));

        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            _ = Assert.Single(clientProvider.Requests);
            Assert.DoesNotContain(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error);
            Assert.Equal(["persisted"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// Node exit while a tool is in flight settles the tool work without successful observation, and the session
    /// ends quietly without backend-failure diagnostics (AI-002 TR-39/44/45).
    /// </summary>
    [Fact]
    public async Task NodeExit_DuringToolPhase_SettlesToolWorkWithoutObservation()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        BlockingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCall(BlockingTool.ToolNameValue);
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            Tools = [tool],
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, () => tool.ObservedCancellation);
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            Assert.True(tool.ObservedCancellation, "Node exit must cancel the in-flight tool work.");
            // The session ended without another request: the tool phase never resumed generation.
            _ = Assert.Single(clientProvider.Requests);
            Assert.Empty(TimelineValues(mind));
            Assert.DoesNotContain(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error);
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// The Mind forwards its configured invalid-response recovery budget to the active runner. Invalid provider
    /// output is discarded without tool effects, and exhaustion remains contained by <see cref="AgenticMind" />.
    /// </summary>
    [Fact]
    public async Task Session_InvalidResponseRecoveryBudget_IsForwardedToContainedLifecycleFailure()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueInvalidResponse();
        clientProvider.EnqueueInvalidResponse();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            Tools = [tool],
            ObservationImportanceThreshold = 1f,
            InvalidResponseRecoveryBudget = 2,
            InvalidResponseRecoveryBackoffSeconds = [0f],
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(
                sceneTree,
                () => loggerProvider.Entries.Any(entry =>
                    entry.Level == LogLevel.Error
                    && entry.Exception is AgentSessionException
                    && entry.Exception.Message.Contains("invalid response recovery budget", StringComparison.Ordinal)));

            Assert.Equal(2, clientProvider.Requests.Count);
            Assert.Empty(tool.CapturedContexts);
            Assert.All(
                clientProvider.Requests,
                request => Assert.Equal([ChatRole.User], request.Select(message => message.Role)));
            _ = Assert.Single(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error && entry.Exception is AgentSessionException);
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    private const string FaultingRenderMessage = "Synthetic event-history template render failure.";

    /// <summary>
    /// Builds a session renderer whose every compiled template faults at render time, standing in for an
    /// authoring or engine failure inside the event-history contract.
    /// </summary>
    private static ObservationHistoryRenderer CreateThrowingHistoryRenderer()
        => ObservationHistoryRenderer.Create(
            eventHistory: null,
            new FaultingTemplateCompiler(),
            new TestCharacter());

    private sealed class FaultingTemplateCompiler : ITemplateCompiler
    {
        public ITemplate Compile(string source)
        {
            _ = source;
            return new FaultingTemplate();
        }

        private sealed class FaultingTemplate : IRootedTemplate
        {
            public ValueTask<string> RenderAsync(IReadOnlyDictionary<string, object?> context)
                => throw new InvalidOperationException(FaultingRenderMessage);

            public ValueTask<string> RenderRootedAsync(object root, IReadOnlyDictionary<string, object?> namedValues)
                => throw new InvalidOperationException(FaultingRenderMessage);
        }
    }

    private static IReadOnlyList<string> TimelineValues(TestAgenticMind mind)
        => [.. mind.GetTimelineForTest().Cast<TestObservation>().Select(static observation => observation.Value)];

    private static IReadOnlyList<string> ClaimValues(MindBase.ObservationDeliveryClaim claim)
        => [.. claim.Observations.Cast<TestObservation>().Select(static observation => observation.Value)];

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames = 300)
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

        public override float CalculateImportance(ObservationContext context) => Importance;

        public override bool RequiresFreshTurn(ObservationContext context) => Fresh;
    }

    private sealed record RouteObservation(string Key) : AgentObservation
    {
        public override string TypeKey => Key;

        public override float CalculateImportance(ObservationContext context) => 0f;
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        public ObservationDeliveryClaim? TryClaimDeliveryForTest() => TryClaimPendingObservationDelivery();

        public void CompleteDeliveryForTest(ObservationDeliveryClaim claim)
            => CompleteObservationDelivery(claim);

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed partial class CapturingTool : AgentTool
    {
        public CapturingTool()
        {
            ToolName = "capture_context";
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

    /// <summary>Tool that blocks until cancelled, mirroring an in-flight action at node exit.</summary>
    private sealed partial class BlockingTool : AgentTool
    {
        public const string ToolNameValue = "block_until_exit";

        public BlockingTool()
        {
            ToolName = ToolNameValue;
            ToolDescription = "Block until the session ends.";
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation
        {
            get; private set;
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
        /// Enqueues a step that holds its request — ignoring cancellation, so ordinary observation delivery never
        /// disturbs it — until released, then returns one valid call for the supplied tool.
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
                    [new FunctionCallContent($"call-{Requests.Count + 1}", toolName, new Dictionary<string, object?>())]));
            });

        public void EnqueueCall(string toolName)
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call-{Requests.Count + 1}", toolName, new Dictionary<string, object?>())]))));

        /// <summary>
        /// Enqueues a step returning one complete multi-call batch — validated whole before any call executes — so
        /// a trailing call can prove the batch's remaining calls are skipped after an invalidation.
        /// </summary>
        public void EnqueueCallBatch(params FunctionCallContent[] calls)
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, calls))));

        public void EnqueueInvalidResponse()
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "invalid ordinary assistant text"))));

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

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "owner";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
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
