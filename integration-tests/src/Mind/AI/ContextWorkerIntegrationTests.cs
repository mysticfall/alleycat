using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>Headless-safe authoring coverage for direct ContextWorker trigger ownership.</summary>
[Headless]
public sealed partial class ContextWorkerIntegrationTests
{
    /// <summary>The initial published snapshot is one empty top-level read-only dictionary passed to workers unchanged.</summary>
    [Fact]
    public async Task InitialWorkerRun_CapturesEmptyTopLevelReadOnlyLatestSnapshotWithoutContextAggregation()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var worker = new RecordingWorker();
        var trigger = new TestRequestTrigger();
        worker.AddChild(trigger);
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            IReadOnlyDictionary<string, object?> initial = mind.GetLatestRenderContext();
            trigger.RequestForTest();
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 1, maxFrames: 60);

            Assert.Empty(initial);
            Assert.Same(initial, worker.Contexts.Single());
            Assert.Equal(0, character.ContextRequestCount);
            _ = Assert.Throws<NotSupportedException>(
                () => ((IDictionary<string, object?>)initial).Add("mutation", null));
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Counts only successful foreground settlements before requesting a run.</summary>
    [Fact]
    public async Task TurnCountTrigger_AfterSuccessfulSettlement_StartsWorkerRun()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var worker = new RecordingWorker();
        worker.AddChild(new TurnCountContextWorkerTrigger { EverySettledForegroundTurns = 2 });
        var mind = new TestAgenticMind();
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("first"));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            Assert.Equal(0, worker.RunCount);

            mind.ObserveForTest(new TestObservation("second"));

            await WaitUntilAsync(sceneTree, () => worker.RunCount == 1, maxFrames: 60);
            Assert.Equal(1, worker.RunCount);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Failed or contained-cancellation foreground turns never advance turn-count worker triggers.</summary>
    [Fact]
    public async Task TurnCountTrigger_OnlyAdvancesAfterGenuineForegroundSuccess()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var worker = new RecordingWorker();
        worker.AddChild(new TurnCountContextWorkerTrigger { EverySettledForegroundTurns = 1 });
        var mind = new SettlementAgenticMind();
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        int successfulTurnEvents = 0;
        mind.ForegroundTurnSucceeded += () => successfulTurnEvents++;
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.Outcome = ForegroundOutcome.Failure;
            mind.ObserveForTest(new TestObservation("failure"));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            Assert.Equal(0, worker.RunCount);
            Assert.Equal(0, successfulTurnEvents);

            mind.Outcome = ForegroundOutcome.ContainedCancellation;
            mind.ObserveForTest(new TestObservation("cancellation"));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            Assert.Equal(0, worker.RunCount);
            Assert.Equal(0, successfulTurnEvents);

            mind.Outcome = ForegroundOutcome.Success;
            mind.ObserveForTest(new TestObservation("success"));
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 1, maxFrames: 60);
            Assert.Equal(1, successfulTurnEvents);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Trigger events route requests and both event subscriptions are detached at the node-lifetime boundary.</summary>
    [Fact]
    public async Task TriggerEvents_AreDirectAndUnsubscribeWithoutPostExitCallbacks()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var worker = new RecordingWorker();
        var trigger = new CountingObservationTrigger();
        worker.AddChild(trigger);
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        int requestEvents = 0;
        trigger.RunRequested += () => requestEvents++;
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("match"));
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 1, maxFrames: 60);
            Assert.Equal(1, trigger.EvaluationCount);
            Assert.Equal(1, requestEvents);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(character);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            mind.PublishCommittedForTest(new TestObservation("match"));
            trigger.RequestForTest();
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(1, trigger.EvaluationCount);
            Assert.Equal(2, requestEvents);
            Assert.Equal(1, worker.RunCount);
        }
        finally
        {
            character.Free();
        }
    }

    /// <summary>Uses the authored zero initial interval delay and stops interval callbacks on exit.</summary>
    [Fact]
    public async Task IntervalTrigger_ZeroInitialDelayRunsAndStopsOnExit()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var worker = new RecordingWorker();
        var trigger = new IntervalContextWorkerTrigger { IntervalSeconds = 0.05f };
        worker.AddChild(trigger);
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            Assert.Equal(0f, trigger.InitialDelaySeconds);
            await WaitUntilAsync(sceneTree, () => worker.RunCount > 0, maxFrames: 60);

            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            int runsAfterExit = worker.RunCount;
            await TestUtils.WaitForFramesAsync(sceneTree, 8);

            Assert.Equal(runsAfterExit, worker.RunCount);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(character))
            {
                character.QueueFree();
                await TestUtils.WaitForFramesAsync(sceneTree, 2);
            }
        }
    }

    /// <summary>Routes committed observations through a synchronous predicate without requiring a foreground turn.</summary>
    [Fact]
    public async Task ObservationTrigger_OnlyRequestsForCommittedPredicateMatches()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var worker = new RecordingWorker();
        worker.AddChild(new MatchingObservationTrigger());
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("ignore"));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            Assert.Equal(0, worker.RunCount);

            mind.ObserveForTest(new TestObservation("match"));
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 1, maxFrames: 60);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Coalesces requests while active to one follow-up using the same published snapshot.</summary>
    [Fact]
    public async Task ActiveWorker_CoalescesToOneFollowUpWithSamePublishedSnapshot()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var worker = new BlockingWorker();
        worker.AddChild(new MatchingObservationTrigger());
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("match"));
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            mind.ObserveForTest(new TestObservation("match"));
            mind.ObserveForTest(new TestObservation("match"));
            worker.ReleaseFirstRun();

            await WaitUntilAsync(sceneTree, () => worker.RunCount == 2, maxFrames: 60);
            Assert.Equal(2, worker.RunCount);
            Assert.Equal(0, worker.ObservationCounts[1]);
            Assert.Same(worker.Contexts[0], worker.Contexts[1]);
        }
        finally
        {
            worker.ReleaseFirstRun();
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Starts independent workers without serialising their blocked asynchronous runs.</summary>
    [Fact]
    public async Task IndependentWorkers_RunInParallelWithoutBlockingForeground()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var first = new BlockingWorker();
        var second = new BlockingWorker();
        first.AddChild(new MatchingObservationTrigger());
        second.AddChild(new MatchingObservationTrigger());
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(first);
        mind.AddChild(second);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("match"));
            await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, first.RunCount);
            Assert.Equal(1, second.RunCount);
        }
        finally
        {
            first.ReleaseFirstRun();
            second.ReleaseFirstRun();
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Retains the atomically published snapshot when a later refresh fails.</summary>
    [Fact]
    public async Task FailedRefresh_RetainsLastSuccessfulProjection()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var worker = new SequencedWorker();
        worker.AddChild(new MatchingObservationTrigger());
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("match"));
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 1, maxFrames: 60);
            mind.ObserveForTest(new TestObservation("match"));
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 2, maxFrames: 60);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            IReadOnlyDictionary<string, object?> projection = worker.GetProjection();
            Assert.Equal("first", projection["projection"]);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>A null refresh result logs an error and retains the exact previously published projection.</summary>
    [Fact]
    public async Task NullRefreshResult_RetainsExactLastSuccessfulProjectionAndLogsError()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        IReadOnlyDictionary<string, object?> expected = new Dictionary<string, object?>
        {
            ["projection"] = "first",
        };
        var worker = new NullAfterProjectionWorker(expected);
        var trigger = new TestRequestTrigger();
        worker.AddChild(trigger);
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            trigger.RequestForTest();
            await WaitUntilAsync(
                sceneTree,
                () => ReferenceEquals(expected, worker.GetProjection()),
                maxFrames: 60);

            trigger.RequestForTest();
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 2, maxFrames: 60);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            IReadOnlyDictionary<string, object?> retained = worker.GetProjection();
            Assert.Same(expected, retained);
            Assert.Equal("first", retained["projection"]);
            _ = Assert.Single(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error && entry.Exception is ArgumentNullException);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Publishes the exact producer dictionary and nested values under the producer immutability convention.</summary>
    [Fact]
    public async Task SuccessfulRun_PublishesExactProducerProjectionReference()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        IReadOnlyDictionary<string, object?> nested = new Dictionary<string, object?> { ["value"] = "stable" };
        IReadOnlyDictionary<string, object?> supplied = new Dictionary<string, object?> { ["nested"] = nested };
        var worker = new ProjectionReferenceWorker(supplied);
        var trigger = new TestRequestTrigger();
        worker.AddChild(trigger);
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            trigger.RequestForTest();
            await WaitUntilAsync(
                sceneTree,
                () => ReferenceEquals(supplied, worker.GetProjection()),
                maxFrames: 60);

            IReadOnlyDictionary<string, object?> published = worker.GetProjection();
            Assert.Same(supplied, published);
            Assert.Same(nested, published["nested"]);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Emits structured debug diagnostics for accepted, coalesced, publication, and follow-up transitions.</summary>
    [Fact]
    public async Task WorkerLifecycle_EmitsStructuredDebugDiagnostics()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        LoggerFilterOptions filterOptions = Game.Instance
            .GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>()
            .CurrentValue;
        LoggerFilterRule debugRule = new(
            providerName: null,
            categoryName: "AlleyCat.Mind.AI.ContextWorker",
            LogLevel.Debug,
            filter: null);
        filterOptions.Rules.Add(debugRule);
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        var worker = new DiagnosticWorker { Name = "diagnostic-worker" };
        var trigger = new TestRequestTrigger();
        worker.AddChild(trigger);
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            trigger.RequestForTest();
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            trigger.RequestForTest();
            trigger.RequestForTest();
            worker.Release();
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 2, maxFrames: 60);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Contains(loggerProvider.Entries, entry =>
                entry.Level == LogLevel.Debug && entry.EventId.Name == "ContextWorkerRunStarted");
            _ = Assert.Single(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Debug && entry.EventId.Name == "ContextWorkerRequestCoalesced");
            Assert.Contains(loggerProvider.Entries, entry =>
                entry.Level == LogLevel.Debug && entry.EventId.Name == "ContextWorkerFollowUpScheduled");
            Assert.Contains(loggerProvider.Entries, entry =>
                entry.Level == LogLevel.Debug
                && entry.EventId.Name == "ContextWorkerProjectionPublished"
                && Equals(entry.Properties["KeyCount"], 2));
        }
        finally
        {
            _ = filterOptions.Rules.Remove(debugRule);
            worker.Release();
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Cancels active worker lifetime work and rejects a late result after Mind exit.</summary>
    [Fact]
    public async Task MindExit_CancelsWorkerAndPreventsLateProjectionPublication()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var worker = new LateWorker();
        worker.AddChild(new MatchingObservationTrigger());
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("match"));
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(character);
            worker.Release();
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            Assert.True(worker.CancellationObserved);
            Assert.Empty(worker.GetProjection());
        }
        finally
        {
            character.Free();
        }
    }

    /// <summary>LLM workers compile their captured prompt once, render fresh dictionaries, and use schema-only calls.</summary>
    [Fact]
    public async Task LLMWorker_UsesCachedPromptAndFreshContextWithSchemaOnlyOptions()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var section = new CountingTextPromptSection("worker={{marker}}");
        var provider = new SchemaClientProvider();
        var worker = new TestLLMWorker
        {
            PromptStack = new PromptStack { Sections = [section] },
            ClientProvider = provider,
        };
        worker.AddChild(new MatchingObservationTrigger());
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 3);

        try
        {
            Assert.Equal(1, section.ContentRequestCount);
            worker.PromptStack = new PromptStack { Sections = [new CountingTextPromptSection("replacement={{marker}}")] };

            _ = await worker.RunForTestAsync(new Dictionary<string, object?> { ["marker"] = "first" });
            _ = await worker.RunForTestAsync(new Dictionary<string, object?> { ["marker"] = "second" });

            Assert.Equal(1, section.ContentRequestCount);
            Assert.Contains("worker=first", provider.Instructions[0], StringComparison.Ordinal);
            Assert.Contains("worker=second", provider.Instructions[1], StringComparison.Ordinal);
            Assert.DoesNotContain("replacement", provider.Instructions[0], StringComparison.Ordinal);
            Assert.DoesNotContain("replacement", provider.Instructions[1], StringComparison.Ordinal);
            Assert.All(provider.Options, options =>
            {
                Assert.Empty(options.Tools!);
                Assert.Null(options.ToolMode);
                Assert.NotNull(options.ResponseFormat);
            });
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Prompt compilation failure is logged once and prevents all schema-provider execution or retry.</summary>
    [Fact]
    public async Task LLMWorker_CompilationFailureLogsOnceAndNeverInvokesProvider()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        var provider = new SchemaClientProvider();
        var section = new ThrowingPromptSection();
        var worker = new TestLLMWorker
        {
            PromptStack = new PromptStack { Sections = [section] },
            ClientProvider = provider,
        };
        worker.AddChild(new MatchingObservationTrigger());
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 3);

        try
        {
            mind.ObserveForTest(new TestObservation("match"));
            mind.ObserveForTest(new TestObservation("match"));
            mind.ObserveForTest(new TestObservation("match"));
            await TestUtils.WaitForFramesAsync(sceneTree, 5);

            Assert.Equal(1, section.ContentRequestCount);
            Assert.Equal(0, provider.InvocationCount);
            Assert.Empty(worker.GetProjection());
            Assert.Equal(
                1,
                loggerProvider.Entries.Count(entry => entry.Message.Contains("prompt compilation failed", StringComparison.Ordinal)));
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Mind exit cancels unfinished prompt compilation without failure diagnostics or later provider work.</summary>
    [Fact]
    public async Task LLMWorker_MindExitCancelsUnfinishedCompilationWithoutPostExitActivity()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        var provider = new SchemaClientProvider();
        var section = new BlockingPromptSection();
        var worker = new TestLLMWorker
        {
            PromptStack = new PromptStack { Sections = [section] },
            ClientProvider = provider,
        };
        var trigger = new TestRequestTrigger();
        worker.AddChild(trigger);
        var mind = new TestAgenticMind { Enabled = false };
        mind.AddChild(worker);
        TestCharacter character = AddFixture(sceneTree, mind);

        try
        {
            await section.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            trigger.RequestForTest();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(character);
            await section.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            trigger.RequestForTest();
            await TestUtils.WaitForFramesAsync(sceneTree, 8);

            Assert.Equal(1, section.ContentRequestCount);
            Assert.Equal(0, provider.InvocationCount);
            Assert.Empty(worker.GetProjection());
            Assert.DoesNotContain(
                loggerProvider.Entries,
                entry => entry.Message.Contains("prompt compilation failed", StringComparison.Ordinal));
        }
        finally
        {
            character.Free();
        }
    }

    /// <summary>A worker projection is visible only after a later foreground render publishes it.</summary>
    [Fact]
    public async Task WorkerProjection_IsDelayedUntilSubsequentForegroundRender()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var prior = new NamedProjectionWorker("prior", "previous");
        var observer = new NamedProjectionWorker("self", "current");
        prior.AddChild(new ValueObservationTrigger("seed"));
        observer.AddChild(new ValueObservationTrigger("seed", "capture"));
        var mind = new TestAgenticMind();
        mind.AddChild(prior);
        mind.AddChild(observer);
        TestCharacter character = AddFixture(sceneTree, mind);
        character.Context["nested"] = new List<object?> { "before" };
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("seed"));
            await WaitUntilAsync(sceneTree, () => prior.RunCount == 1 && observer.RunCount == 1, maxFrames: 60);

            mind.ObserveForTest(new TestObservation("capture"));
            await WaitUntilAsync(sceneTree, () => observer.RunCount == 2 && mind.ForegroundContexts.Count == 2, maxFrames: 60);

            IReadOnlyDictionary<string, object?> foreground = mind.ForegroundContexts[1];
            IReadOnlyDictionary<string, object?> worker = observer.Contexts[1];
            Assert.Same(foreground, worker);
            // Two-phase rendering seals 'scenario' after worker projections, so it enumerates last among the
            // reserved keys.
            Assert.Equal(["character", "characters", "player", "observations", "prior", "self", "scenario"], foreground.Keys);
            Assert.Equal("previous", foreground["prior"]);
            Assert.Equal("current", foreground["self"]);
            Assert.Equal(2, ((IReadOnlyCollection<object?>)foreground["observations"]!).Count);
            _ = Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)worker).Add("other", null));
            Assert.Same(character.Context, worker["character"]);
            Assert.Same(character.Context["nested"], ((IReadOnlyDictionary<string, object?>)worker["character"]!)["nested"]);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>A failed foreground template render leaves the prior exact published dictionary unchanged.</summary>
    [Fact]
    public async Task ForegroundTemplateRenderFailure_RetainsPreviousPublishedSnapshot()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var mind = new TestAgenticMind();
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("published"));
            await WaitUntilAsync(sceneTree, () => mind.ForegroundContexts.Count == 1, maxFrames: 60);
            IReadOnlyDictionary<string, object?> previous = mind.GetLatestRenderContext();
            Assert.Same(mind.ForegroundContexts.Single(), previous);

            mind.FailNextRender = true;
            mind.ObserveForTest(new TestObservation("render-failure"));
            await TestUtils.WaitForFramesAsync(sceneTree, 8);

            Assert.Same(previous, mind.GetLatestRenderContext());
            _ = Assert.Single(mind.ForegroundContexts);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Publication survives a later foreground failure because rendering already succeeded.</summary>
    [Fact]
    public async Task ForegroundFailureAfterSuccessfulRender_RetainsNewPublishedSnapshot()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var mind = new TestAgenticMind { FailAfterRender = true };
        TestCharacter character = AddFixture(sceneTree, mind);
        int successfulTurns = 0;
        mind.ForegroundTurnSucceeded += () => successfulTurns++;
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            IReadOnlyDictionary<string, object?> initial = mind.GetLatestRenderContext();
            mind.ObserveForTest(new TestObservation("post-render-failure"));
            await WaitUntilAsync(sceneTree, () => mind.ForegroundContexts.Count == 1, maxFrames: 60);
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            Assert.NotSame(initial, mind.GetLatestRenderContext());
            Assert.Same(mind.ForegroundContexts.Single(), mind.GetLatestRenderContext());
            Assert.Equal(0, successfulTurns);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Duplicate authored projection keys retain the previous foreground snapshot.</summary>
    [Fact]
    public async Task CreateRenderContext_DuplicateProjectionKeysRetainsPreviousPublishedSnapshot()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        var first = new NamedProjectionWorker("duplicate", "first");
        var second = new NamedProjectionWorker("duplicate", "second");
        var dependent = new RecordingWorker();
        first.AddChild(new ValueObservationTrigger("seed"));
        second.AddChild(new ValueObservationTrigger("seed"));
        dependent.AddChild(new ValueObservationTrigger("collision"));
        var mind = new TestAgenticMind();
        mind.AddChild(first);
        mind.AddChild(second);
        mind.AddChild(dependent);
        TestCharacter character = AddFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation("seed"));
            await WaitUntilAsync(sceneTree, () => first.RunCount == 1 && second.RunCount == 1, maxFrames: 60);
            IReadOnlyDictionary<string, object?> previous = mind.GetLatestRenderContext();
            mind.ObserveForTest(new TestObservation("collision"));
            await TestUtils.WaitForFramesAsync(sceneTree, 5);

            Assert.Equal(1, dependent.RunCount);
            Assert.Same(previous, dependent.Contexts.Single());
            Assert.Same(previous, mind.GetLatestRenderContext());
            Assert.Contains(
                loggerProvider.Entries,
                entry => entry.Exception?.Message.Contains("duplicate context key 'duplicate'", StringComparison.Ordinal) == true);
        }
        finally
        {
            character.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Workers reject zero, multiple, and nested-only triggers.</summary>
    [Fact]
    public void DirectTriggerCardinality_RejectsInvalidAuthoring()
    {
        AgenticMind mind = new();
        TestWorker zero = new();
        mind.AddChild(zero);
        _ = Assert.Throws<InvalidOperationException>(() => zero.Attach(mind));
        zero.Free();

        TestWorker multiple = new();
        multiple.AddChild(new TurnCountContextWorkerTrigger());
        multiple.AddChild(new TurnCountContextWorkerTrigger());
        mind.AddChild(multiple);
        _ = Assert.Throws<InvalidOperationException>(() => multiple.Attach(mind));
        multiple.Free();

        TestWorker nested = new();
        Node container = new();
        container.AddChild(new TurnCountContextWorkerTrigger());
        nested.AddChild(container);
        mind.AddChild(nested);
        _ = Assert.Throws<InvalidOperationException>(() => nested.Attach(mind));
        nested.Free();
        mind.Free();
    }

    private sealed partial class TestWorker : ContextWorker
    {
        protected override Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    private sealed partial class RecordingWorker : ContextWorker
    {
        public List<IReadOnlyDictionary<string, object?>> Contexts { get; } = [];

        public int RunCount
        {
            get; private set;
        }

        protected override Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
        {
            RunCount++;
            Contexts.Add(context);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed partial class ProjectionReferenceWorker(IReadOnlyDictionary<string, object?> projection) : ContextWorker
    {
        protected override Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
            => Task.FromResult(projection);
    }

    private sealed partial class TestRequestTrigger : ContextWorkerTrigger
    {
        public void RequestForTest() => RequestRun();
    }

    private sealed partial class CountingObservationTrigger : ObservationContextWorkerTrigger
    {
        public int EvaluationCount
        {
            get; private set;
        }

        public void RequestForTest() => RequestRun();

        protected override bool ShouldRequestFor(Observation observation)
        {
            EvaluationCount++;
            return observation is TestObservation { Value: "match" };
        }
    }

    private sealed partial class BlockingWorker : ContextWorker
    {
        private readonly TaskCompletionSource _firstRunRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> ObservationCounts { get; } = [];

        public List<IReadOnlyDictionary<string, object?>> Contexts { get; } = [];

        public int RunCount
        {
            get; private set;
        }

        public void ReleaseFirstRun() => _ = _firstRunRelease.TrySetResult();

        protected override async Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
        {
            RunCount++;
            Contexts.Add(context);
            ObservationCounts.Add(context.TryGetValue("observations", out object? observations)
                ? ((IReadOnlyCollection<object?>)observations!).Count
                : 0);
            _ = Started.TrySetResult();
            if (RunCount == 1)
            {
                await _firstRunRelease.Task.WaitAsync(cancellationToken);
            }

            return new Dictionary<string, object?>();
        }
    }

    private sealed partial class DiagnosticWorker : ContextWorker
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RunCount
        {
            get; private set;
        }

        public void Release() => _ = _release.TrySetResult();

        protected override async Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
        {
            RunCount++;
            _ = Started.TrySetResult();
            if (RunCount == 1)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            return new Dictionary<string, object?>
            {
                ["first"] = 1,
                ["second"] = 2,
            };
        }
    }

    private sealed partial class MatchingObservationTrigger : ObservationContextWorkerTrigger
    {
        protected override bool ShouldRequestFor(Observation observation)
            => observation is TestObservation { Value: "match" };
    }

    private sealed partial class ValueObservationTrigger(params string[] values) : ObservationContextWorkerTrigger
    {
        protected override bool ShouldRequestFor(Observation observation)
            => observation is TestObservation test && values.Contains(test.Value, StringComparer.Ordinal);
    }

    private sealed partial class NamedProjectionWorker(string key, string value) : ContextWorker
    {
        public List<IReadOnlyDictionary<string, object?>> Contexts { get; } = [];

        public int RunCount
        {
            get; private set;
        }

        protected override Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
        {
            RunCount++;
            Contexts.Add(context);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { [key] = value });
        }
    }

    private sealed partial class SequencedWorker : ContextWorker
    {
        public int RunCount
        {
            get; private set;
        }

        protected override Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
        {
            RunCount++;
            return RunCount == 1
                ? Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["projection"] = "first" })
                : Task.FromException<IReadOnlyDictionary<string, object?>>(new InvalidOperationException("expected refresh failure"));
        }
    }

    private sealed partial class NullAfterProjectionWorker(IReadOnlyDictionary<string, object?> projection) : ContextWorker
    {
        public int RunCount
        {
            get; private set;
        }

        protected override Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
        {
            RunCount++;
            return Task.FromResult(RunCount == 1 ? projection : null!);
        }
    }

    private sealed partial class LateWorker : ContextWorker
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved
        {
            get; private set;
        }

        public void Release() => _ = _release.TrySetResult();

        protected override async Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => CancellationObserved = true);
            _ = Started.TrySetResult();
            await _release.Task;
            return new Dictionary<string, object?> { ["projection"] = "late" };
        }
    }

    private sealed partial class TestAgenticMind : AgenticMind
    {
        public List<IReadOnlyDictionary<string, object?>> ForegroundContexts { get; } = [];

        public bool FailNextRender
        {
            get; set;
        }

        public bool FailAfterRender
        {
            get; set;
        }

        public void ObserveForTest(Observation observation) => _ = Observe(observation);

        public void PublishCommittedForTest(Observation observation) => OnObservationIngested(observation);

        protected override Task RunAgentTurnAsync(
            IReadOnlyList<Observation> timeline,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            IReadOnlyDictionary<string, object?> context = CreateRenderContext(timeline);
            ITemplate template = FailNextRender ? ThrowingTemplate.Instance : PassthroughTemplate.Instance;
            FailNextRender = false;
            _ = RenderAndPublishSystemInstruction(template, context);
            ForegroundContexts.Add(context);
            return FailAfterRender ? throw new InvalidOperationException("expected post-render foreground failure") : Task.CompletedTask;
        }
    }

    private sealed class PassthroughTemplate : ITemplate
    {
        public static PassthroughTemplate Instance { get; } = new();

        public string Render(IReadOnlyDictionary<string, object?> context) => string.Empty;
    }

    private sealed class ThrowingTemplate : ITemplate
    {
        public static ThrowingTemplate Instance { get; } = new();

        public string Render(IReadOnlyDictionary<string, object?> context)
            => throw new InvalidOperationException("expected foreground render failure");
    }

    private sealed partial class SettlementAgenticMind : AgenticMind
    {
        public ForegroundOutcome Outcome
        {
            get; set;
        }

        public void ObserveForTest(Observation observation) => _ = Observe(observation);

        protected override Task RunAgentTurnAsync(
            IReadOnlyList<Observation> timeline,
            CancellationToken cancellationToken)
        {
            _ = timeline;
            _ = cancellationToken;
            return Outcome switch
            {
                ForegroundOutcome.Success => Task.CompletedTask,
                ForegroundOutcome.Failure => Task.FromException(new InvalidOperationException("expected foreground failure")),
                ForegroundOutcome.ContainedCancellation => Task.FromCanceled(new CancellationToken(canceled: true)),
                _ => throw new ArgumentOutOfRangeException(),
            };
        }
    }

    private enum ForegroundOutcome
    {
        Success,
        Failure,
        ContainedCancellation,
    }

    private sealed partial class TestCharacter : Node, ICharacter
    {
        public string Id { get; set; } = "context_worker_owner";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Dictionary<string, object?> Context { get; } = new() { ["name"] = "Worker Owner" };

        public int ContextRequestCount
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
        {
            ContextRequestCount++;
            return Context;
        }
    }

    private sealed record TestObservation(string Value = "") : Observation
    {
        public override string TypeKey => "test.context-worker";

        public override float CalculateImportance(ObservationContext context) => 1f;
    }

    private static TestCharacter AddFixture(SceneTree sceneTree, AgenticMind mind)
    {
        var character = new TestCharacter();
        character.AddChild(mind);
        character.AddToGroup("Actors");
        // Foreground rendering resolves the scene player, so each fixture carries a node-based player beneath the
        // owning character; it is freed together with the character.
        FixturePlayerCharacter player = new();
        character.AddChild(player);
        player.AddToGroup("Actors");
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(character);
        return character;
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames && !predicate(); frame++)
        {
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private sealed partial class TestLLMWorker : LLMContextWorker<TestResponse>
    {
        public Task<IReadOnlyDictionary<string, object?>> RunForTestAsync(IReadOnlyDictionary<string, object?> context)
            => RunAsync(context, CancellationToken.None);

        protected override bool TryMapResponse(
            TestResponse response,
            out IReadOnlyDictionary<string, object?> context)
        {
            context = new Dictionary<string, object?> { ["projection"] = response.Value };
            return true;
        }
    }

    private sealed record TestResponse(string Value);

    private sealed partial class CountingTextPromptSection : PromptSection
    {
        private readonly string _text;

        public CountingTextPromptSection(string text)
        {
            _text = text;
            Name = "Counting Prompt";
        }

        public int ContentRequestCount
        {
            get; private set;
        }

        public override Task<string> GetContentAsync(
            PromptSectionBuildContext buildContext,
            CancellationToken cancellationToken = default)
        {
            ContentRequestCount++;
            return Task.FromResult(_text);
        }
    }

    private sealed partial class ThrowingPromptSection : PromptSection
    {
        public ThrowingPromptSection()
        {
            Name = "Throwing Prompt";
        }

        public int ContentRequestCount
        {
            get; private set;
        }

        public override Task<string> GetContentAsync(
            PromptSectionBuildContext buildContext,
            CancellationToken cancellationToken = default)
        {
            ContentRequestCount++;
            throw new InvalidOperationException("expected compilation failure");
        }
    }

    private sealed partial class BlockingPromptSection : PromptSection
    {
        public BlockingPromptSection()
        {
            Name = "Blocking Prompt";
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ContentRequestCount
        {
            get; private set;
        }

        public override async Task<string> GetContentAsync(
            PromptSectionBuildContext buildContext,
            CancellationToken cancellationToken = default)
        {
            ContentRequestCount++;
            _ = Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _ = Cancelled.TrySetResult();
                }
            }
        }
    }

    private sealed class SchemaClientProvider : ClientProvider
    {
        public List<string> Instructions { get; } = [];

        public List<ChatOptions> Options { get; } = [];

        public int InvocationCount
        {
            get; private set;
        }

        public override IChatClient CreateChatClient() => new SchemaClient(this);

        private sealed class SchemaClient(SchemaClientProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _ = messages;
                cancellationToken.ThrowIfCancellationRequested();
                owner.InvocationCount++;
                owner.Instructions.Add(options?.Instructions ?? string.Empty);
                owner.Options.Add(options!);
                string response = JsonSerializer.Serialize(new TestResponse("snapshot"));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
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

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Dictionary<string, object?> properties = state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                provider._entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception, properties));
            }
        }

        public sealed record LogEntry(
            LogLevel Level,
            EventId EventId,
            string Message,
            Exception? Exception,
            IReadOnlyDictionary<string, object?> Properties);
    }
}
