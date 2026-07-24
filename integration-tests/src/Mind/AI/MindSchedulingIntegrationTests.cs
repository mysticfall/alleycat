using System.Collections;
using System.Diagnostics;
using AlleyCat.Body.Eyes;
using AlleyCat.Body.Voice;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Isolated real-time scheduling and observation timeline coverage for the base Mind.
/// </summary>
[Headless]
public sealed partial class MindSchedulingIntegrationTests
{
    /// <summary>
    /// Every observation atomically enters history and the pending queue.
    /// </summary>
    [Fact]
    public async Task ObservationIntake_QueuesEveryObservationWithTimelineOrder()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            Enabled = false,
            ObservationImportanceThreshold = 1f,
        };
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            TestObservation self = new(1f, "self");
            TestObservation external = new(1f, "external");
            mind.ObserveForTest(self);
            mind.ObserveForTest(external);

            Assert.Equal([self, external], mind.GetTimelineForTest());
            Assert.True(mind.HasPendingObservationsForTest);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Timeline snapshots preserve insertion order and cannot mutate either the snapshot or its private source.
    /// </summary>
    [Fact]
    public void TimelineSnapshot_IsOrderedStableAndImmutable()
    {
        TestMind mind = new()
        {
            Enabled = false
        };
        TestObservation first = new(0f, "first");
        TestObservation second = new(0f, "second");
        TestObservation later = new(0f, "later");

        mind.ObserveForTest(first);
        mind.ObserveForTest(second);
        IReadOnlyList<AgentObservation> snapshot = mind.GetTimelineForTest();

        Assert.Equal([first, second], snapshot);
        IList mutableView = Assert.IsAssignableFrom<IList>(snapshot);
        _ = Assert.Throws<NotSupportedException>(() => mutableView.Add(later));

        mind.ObserveForTest(later);
        Assert.Equal([first, second], snapshot);
        Assert.Equal([first, second, later], mind.GetTimelineForTest());
        mind.Free();
    }

    /// <summary>
    /// Observations arriving during a turn remain queued and never create concurrent processing.
    /// </summary>
    [Fact]
    public async Task Observe_DuringActiveTurn_RunsQueuedFIFOAfterCompletionWithoutConcurrency()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            BlockProcessing = true,
            ObservationImportanceThreshold = 1f,
        };
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "first"));
            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, 120);

            mind.ObserveForTest(new TestObservation(1f, "second"));
            mind.ObserveForTest(new TestObservation(1f, "third"));
            await TestUtils.WaitForFramesAsync(sceneTree, 5);

            _ = Assert.Single(mind.ProcessedBatches);
            Assert.Equal(1, mind.MaximumConcurrentProcessing);

            mind.ReleaseProcessing();
            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 2, 120);

            Assert.Equal(["second", "third"], mind.ProcessedBatches[1].Cast<TestObservation>().Select(x => x.Value));
            Assert.Equal(1, mind.MaximumConcurrentProcessing);
            mind.ReleaseProcessing();
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Threshold eligibility reached during the post-turn interval waits until that lower bound has elapsed.
    /// </summary>
    [Fact]
    public async Task Observe_WhenThresholdReachedDuringMinimumInterval_WaitsForInterval()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            MinimumTurnIntervalSeconds = 0.25f,
            ObservationImportanceThreshold = 1f,
        };
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "first"));
            await WaitUntilAsync(sceneTree, () => mind.CompletionTimestamps.Count == 1, 120);
            double firstCompletion = mind.CompletionTimestamps[0];

            mind.ObserveForTest(new TestObservation(1f, "second"));
            await TestUtils.WaitForFramesAsync(sceneTree, 5);
            _ = Assert.Single(mind.ProcessedBatches);

            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 2, 120);
            Assert.True(
                mind.StartTimestamps[1] - firstCompletion >= 0.20d,
                $"Second turn began only {mind.StartTimestamps[1] - firstCompletion:F3}s after completion.");
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Maximum-wait expiry does not bypass the lower bound established by the previous turn's completion.
    /// </summary>
    [Fact]
    public async Task Observe_WhenMaximumWaitExpiresDuringMinimumInterval_WaitsForInterval()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            MaxObservationWaitSeconds = 0.05f,
            MinimumTurnIntervalSeconds = 0.25f,
            ObservationImportanceThreshold = 1f,
        };
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "first"));
            await WaitUntilAsync(sceneTree, () => mind.CompletionTimestamps.Count == 1, 120);
            double firstCompletion = mind.CompletionTimestamps[0];

            mind.ObservationImportanceThreshold = 10f;
            mind.ObserveForTest(new TestObservation(1f, "below threshold"));
            await TestUtils.WaitForFramesAsync(sceneTree, 7);
            _ = Assert.Single(mind.ProcessedBatches);

            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 2, 120);
            Assert.True(
                mind.StartTimestamps[1] - firstCompletion >= 0.20d,
                $"Second turn began only {mind.StartTimestamps[1] - firstCompletion:F3}s after completion.");
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Maximum-wait eligibility reached during an active turn is retained and evaluated when that turn completes.
    /// </summary>
    [Fact]
    public async Task Observe_WhenMaximumWaitExpiresDuringTurn_RunsAfterCompletion()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            BlockProcessing = true,
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 1f,
        };
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "active"));
            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, 120);

            mind.ObservationImportanceThreshold = 10f;
            mind.ObserveForTest(new TestObservation(1f, "waiting"));
            await TestUtils.WaitForFramesAsync(sceneTree, 8);
            _ = Assert.Single(mind.ProcessedBatches);

            mind.ReleaseProcessing();
            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 2, 120);
            Assert.Equal("waiting", Assert.IsType<TestObservation>(Assert.Single(mind.ProcessedBatches[1])).Value);
            Assert.Equal(1, mind.MaximumConcurrentProcessing);
            mind.ReleaseProcessing();
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Disabling scheduling preserves the pending FIFO and re-enable processes one atomic batch.
    /// </summary>
    [Fact]
    public async Task Enabled_WhenRestored_ProcessesPreservedPendingFIFOAtomically()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            Enabled = false,
            ObservationImportanceThreshold = 2f,
        };
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "first"));
            mind.ObserveForTest(new TestObservation(1f, "second"));
            await TestUtils.WaitForFramesAsync(sceneTree, 5);
            Assert.Empty(mind.ProcessedBatches);

            mind.Enabled = true;
            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, 120);

            Assert.Equal(["first", "second"], mind.ProcessedBatches[0].Cast<TestObservation>().Select(x => x.Value));
            Assert.False(mind.HasPendingObservationsForTest);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Importance is calculated once at intake and later observation state cannot change stored eligibility.
    /// </summary>
    [Fact]
    public async Task Observe_CalculatesImportanceOnceAndStoresItForScheduling()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            Enabled = false,
            MaxObservationWaitSeconds = 1f,
            ObservationImportanceThreshold = 1f,
        };
        CountingObservation observation = new(1f, "stored");
        mind.ObserveForTest(observation);
        observation.Importance = 0f;

        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.Enabled = true;
            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, 60);

            Assert.Equal(1, observation.CalculationCount);
            Assert.Equal("stored", Assert.IsType<CountingObservation>(Assert.Single(mind.ProcessedBatches[0])).Value);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Invalid importance rejects intake before either timeline or pending state changes.
    /// </summary>
    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Observe_WithInvalidImportance_RejectsWithoutMutation(float importance)
    {
        TestMind mind = new()
        {
            Enabled = false
        };

        _ = Assert.Throws<InvalidOperationException>(
            () => mind.ObserveForTest(new TestObservation(importance, "invalid")));

        Assert.Empty(mind.GetTimelineForTest());
        Assert.False(mind.HasPendingObservationsForTest);
        mind.Free();
    }

    /// <summary>
    /// Zero-importance observations remain pending and process when maximum wait expires.
    /// </summary>
    [Fact]
    public async Task Observe_WithZeroImportance_ProcessesThroughMaximumWait()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 1f,
        };
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(0f, "zero"));
            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, 120);

            Assert.Equal("zero", Assert.IsType<TestObservation>(Assert.Single(mind.ProcessedBatches[0])).Value);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Intake before Ready is evaluated once the scheduling timer is initialised.
    /// </summary>
    [Fact]
    public async Task Observe_BeforeReady_BecomesSchedulableAfterReady()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f
        };
        mind.ObserveForTest(new TestObservation(1f, "pre-ready"));

        AddTestNode(sceneTree, mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, 120);
            Assert.Equal("pre-ready", Assert.IsType<TestObservation>(Assert.Single(mind.ProcessedBatches[0])).Value);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Tool wrappers await envelopes, project only messages, stamp actors, and preserve ordered atomic ingestion.
    /// </summary>
    [Fact]
    public async Task AgentToolWrapper_AwaitsProjectsAndAtomicallyIngestsStampedOrderedObservations()
    {
        TestMind mind = new()
        {
            Enabled = false
        };
        ToolServiceProvider services = new(mind);
        ToolHost.Reset();
        AIFunction function = AgentTool.CreateFunction(ToolHost.WaitForResultAsync, services, "test_tool");
        ValueTask<object?> invocation = function.InvokeAsync([], CancellationToken.None);

        Assert.Empty(mind.GetTimelineForTest());
        ToolHost.Complete(new AgentToolResult(
            "model acknowledgement",
            [
                new ObservedSpeech("spoofed-actor", "raw-self-device", "first"),
                new TestObservation(1f, "second"),
            ]));
        object? projected = await invocation;

        Assert.Equal("model acknowledgement", projected);
        Assert.Collection(
            mind.GetTimelineForTest(),
            item =>
            {
                ObservedSpeech speech = Assert.IsType<ObservedSpeech>(item);
                Assert.Equal("scheduling-owner", speech.ActorId);
                Assert.Equal("raw-self-device", speech.VoiceId);
            },
            item => Assert.Equal("second", Assert.IsType<TestObservation>(item).Value));
        Assert.True(mind.HasPendingObservationsForTest);
        mind.Free();
    }

    /// <summary>
    /// Empty/null-message envelopes are valid while invalid batches, exceptions, cancellation, and null results ingest nothing.
    /// </summary>
    [Fact]
    public async Task AgentToolWrapper_HandlesEmptyAndFailureResultsWithoutPartialIngestion()
    {
        TestMind mind = new()
        {
            Enabled = false
        };
        ToolServiceProvider services = new(mind);
        AIFunction emptyFunction = AgentTool.CreateFunction(ToolHost.EmptyAsync, services);
        AIFunction invalidBatchFunction = AgentTool.CreateFunction(ToolHost.InvalidBatchAsync, services);
        AIFunction throwingFunction = AgentTool.CreateFunction(ToolHost.ThrowAsync, services);
        AIFunction nullFunction = AgentTool.CreateFunction(ToolHost.NullAsync, services);
        AIFunction cancellingFunction = AgentTool.CreateFunction(ToolHost.CancelAsync, services);

        Assert.Null(await emptyFunction.InvokeAsync([], CancellationToken.None));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(invalidBatchFunction.InvokeAsync([], CancellationToken.None).AsTask);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(throwingFunction.InvokeAsync([], CancellationToken.None).AsTask);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(nullFunction.InvokeAsync([], CancellationToken.None).AsTask);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            cancellingFunction.InvokeAsync([], cancellation.Token).AsTask);

        Assert.Empty(mind.GetTimelineForTest());
        Assert.False(mind.HasPendingObservationsForTest);
        mind.Free();
    }

    /// <summary>
    /// Minimum-turn interval authoring is constrained to the approved 0–5 second editor range.
    /// </summary>
    [Fact]
    public void MinimumTurnIntervalSeconds_UsesZeroToFiveEditorRange()
    {
        ExportAttribute attribute = Assert.Single(
            typeof(MindBase).GetProperty(nameof(MindBase.MinimumTurnIntervalSeconds))!
                .GetCustomAttributes(typeof(ExportAttribute), inherit: true)
                .Cast<ExportAttribute>());

        Assert.Equal(PropertyHint.Range, attribute.Hint);
        Assert.Equal("0,5,0.05", attribute.HintString);
    }

    private static void AddTestNode(SceneTree sceneTree, Node node)
    {
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        parent.AddChild(node);
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (predicate())
            {
                return;
            }

            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, Node node)
    {
        node.QueueFree();
        await TestUtils.WaitForFramesAsync(sceneTree, 2);
    }

    private sealed partial class TestMind : MindBase
    {
        private readonly TestCharacter _character = new();
        private TaskCompletionSource? _processingRelease;
        private int _activeProcessing;

        public bool BlockProcessing
        {
            get; init;
        }

        public List<IReadOnlyList<AgentObservation>> ProcessedBatches { get; } = [];

        public List<double> StartTimestamps { get; } = [];

        public List<double> CompletionTimestamps { get; } = [];

        public int MaximumConcurrentProcessing
        {
            get; private set;
        }

        public bool HasPendingObservationsForTest => HasPendingObservations;

        public new ICharacter Owner => _character;

        public void ObserveForTest(AgentObservation observation) => _ = Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        public void ReleaseProcessing() => _processingRelease?.TrySetResult();

        public override void ReceiveVoice(string speech, IVoice source)
        {
        }

        protected override async Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<AgentObservation> timelineSnapshot,
            CancellationToken cancellationToken)
        {
            _activeProcessing++;
            MaximumConcurrentProcessing = Math.Max(MaximumConcurrentProcessing, _activeProcessing);
            ProcessedBatches.Add([.. observations]);
            StartTimestamps.Add(GetTimestamp());

            try
            {
                if (BlockProcessing)
                {
                    _processingRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await _processingRelease.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                CompletionTimestamps.Add(GetTimestamp());
                _activeProcessing--;
            }
        }

        protected override ICharacter ResolveOwningCharacter() => _character;

        private static double GetTimestamp() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }

    private sealed class ToolServiceProvider(TestMind mind) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ICharacter) ? mind.Owner
                : serviceType.IsInstanceOfType(mind) ? mind
                : null;
    }

    private static class ToolHost
    {
        private static TaskCompletionSource<AgentToolResult> _completion = CreateCompletion();

        public static Task<AgentToolResult> WaitForResultAsync() => _completion.Task;

        public static ValueTask<AgentToolResult> EmptyAsync()
            => ValueTask.FromResult(new AgentToolResult());

        public static Task<AgentToolResult> InvalidBatchAsync()
            => Task.FromResult(new AgentToolResult(
                observations:
                [
                    new TestObservation(1f, "valid"),
                    new TestObservation(float.NaN, "invalid"),
                ]));

        public static Task<AgentToolResult> ThrowAsync()
            => Task.FromException<AgentToolResult>(new InvalidOperationException("tool failed"));

        public static ValueTask<AgentToolResult> NullAsync()
            => ValueTask.FromResult<AgentToolResult>(null!);

        public static async ValueTask<AgentToolResult> CancelAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AgentToolResult();
        }

        public static void Reset() => _completion = CreateCompletion();

        public static void Complete(AgentToolResult result) => _completion.SetResult(result);

        private static TaskCompletionSource<AgentToolResult> CreateCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record TestObservation(float Importance, string Value) : AgentObservation
    {
        public override string TypeKey => "test.scheduling";

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed record CountingObservation(float InitialImportance, string Value) : AgentObservation
    {
        public float Importance { get; set; } = InitialImportance;

        public int CalculationCount
        {
            get; private set;
        }

        public override string TypeKey => "test.counting";

        public override float CalculateImportance(ObservationContext context)
        {
            CalculationCount++;
            return Importance;
        }
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "scheduling-owner";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }
}
