using AlleyCat.Body.Eyes;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Deterministic barrier coverage for high-importance active-turn interruption.
/// </summary>
[Headless]
public sealed partial class MindInterruptionIntegrationTests
{
    /// <summary>
    /// One high observation interrupts below the normal threshold and replacement waits for settlement without overlap.
    /// </summary>
    [Fact]
    public async Task HighObservation_InterruptsAndReplacementWaitsForSettlementWithoutOverlap()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.Interruptible();
        var replacement = TurnGate.Completed();
        InterruptionMind mind = CreateMind(firstTurn, replacement);
        mind.ObservationImportanceThreshold = 100f;
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        AddTestNode(sceneTree, mind);

        try
        {
            mind.ObserveForTest(new TestObservation(100f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            mind.ObserveForTest(new TestObservation(5f, "high"));
            await firstTurn.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(replacement.Started.Task.IsCompleted);
            Assert.Equal(1, mind.MaximumConcurrentTurns);

            firstTurn.ReleaseSettlement();
            await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, mind.MaximumConcurrentTurns);
            Assert.Equal(["high"], mind.Batches[1].Cast<TestObservation>().Select(x => x.Value));
            Assert.DoesNotContain(loggerProvider.Entries, entry => entry.Level >= LogLevel.Error);
        }
        finally
        {
            firstTurn.ReleaseSettlement();
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Individually low observations never interrupt merely because their cumulative normal importance crosses threshold.
    /// </summary>
    [Fact]
    public async Task CumulativeLowObservations_DoNotInterruptActiveTurn()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.NaturallyCompleting();
        var nextTurn = TurnGate.Completed();
        InterruptionMind mind = CreateMind(firstTurn, nextTurn);
        mind.ObservationImportanceThreshold = 4f;
        AddTestNode(sceneTree, mind);

        try
        {
            mind.ObserveForTest(new TestObservation(4f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            mind.ObserveForTest(new TestObservation(2f, "low one"));
            mind.ObserveForTest(new TestObservation(2f, "low two"));

            Assert.False(firstTurn.CancellationObserved.Task.IsCompleted);
            firstTurn.CompleteNaturally();
            await nextTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(firstTurn.CancellationObserved.Task.IsCompleted);
            Assert.Equal(["low one", "low two"], mind.Batches[1].Cast<TestObservation>().Select(x => x.Value));
        }
        finally
        {
            firstTurn.CompleteNaturally();
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// High observations remain pending without cancellation when interruption is explicitly disabled.
    /// </summary>
    [Fact]
    public async Task HighObservation_WhenInterruptionDisabled_DoesNotCancelActiveTurn()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.NaturallyCompleting();
        InterruptionMind mind = CreateMind(firstTurn);
        mind.HighImportanceInterruptionEnabled = false;
        mind.ObservationImportanceThreshold = 100f;
        AddTestNode(sceneTree, mind);

        try
        {
            mind.ObserveForTest(new TestObservation(100f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            mind.ObserveForTest(new TestObservation(5f, "high but disabled"));
            Assert.False(firstTurn.CancellationObserved.Task.IsCompleted);
            firstTurn.CompleteNaturally();
            await firstTurn.Settled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            Assert.False(firstTurn.CancellationObserved.Task.IsCompleted);
            Assert.True(mind.HasPendingForTest);
            _ = Assert.Single(mind.Batches);
        }
        finally
        {
            firstTurn.CompleteNaturally();
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Replacement bypasses a non-zero minimum interval exactly once and claims pending FIFO with the full timeline.
    /// </summary>
    [Fact]
    public async Task Replacement_BypassesMinimumIntervalAndPreservesFIFOAndTimelineSnapshot()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.Interruptible();
        var replacement = TurnGate.Completed();
        InterruptionMind mind = CreateMind(firstTurn, replacement);
        mind.MinimumTurnIntervalSeconds = 5f;
        AddTestNode(sceneTree, mind);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            mind.ObserveForTest(new TestObservation(5f, "high"));
            await firstTurn.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            mind.ObserveForTest(new TestObservation(1f, "ordinary during settlement"));
            firstTurn.ReleaseSettlement();

            await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(
                ["high", "ordinary during settlement"],
                mind.Batches[1].Cast<TestObservation>().Select(x => x.Value));
            Assert.Equal(
                ["initial", "high", "ordinary during settlement"],
                mind.TimelineSnapshots[1].Cast<TestObservation>().Select(x => x.Value));
            Assert.Equal(3, mind.GetTimelineForTest().Count);
        }
        finally
        {
            firstTurn.ReleaseSettlement();
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Multiple high arrivals coalesce into one cancellation request and one replacement containing all events.
    /// </summary>
    [Fact]
    public async Task MultipleHighObservations_CoalesceIntoOneCancellationAndReplacement()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.Interruptible();
        var replacement = TurnGate.Completed();
        InterruptionMind mind = CreateMind(firstTurn, replacement);
        AddTestNode(sceneTree, mind);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            mind.ObserveForTest(new TestObservation(5f, "high one"));
            mind.ObserveForTest(new TestObservation(6f, "high two"));
            mind.ObserveForTest(new TestObservation(7f, "high three"));
            await firstTurn.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            firstTurn.ReleaseSettlement();
            await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(1, firstTurn.CancellationCount);
            Assert.Equal(2, mind.Batches.Count);
            Assert.Equal(
                ["high one", "high two", "high three"],
                mind.Batches[1].Cast<TestObservation>().Select(x => x.Value));
        }
        finally
        {
            firstTurn.ReleaseSettlement();
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Natural completion winning the cancellation race still produces exactly one immediate replacement.
    /// </summary>
    [Fact]
    public async Task NaturalCompletionRacingCancellation_StartsOneImmediateReplacement()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.NaturallyCompleting();
        var replacement = TurnGate.Completed();
        InterruptionMind mind = CreateMind(firstTurn, replacement);
        mind.MinimumTurnIntervalSeconds = 5f;
        AddTestNode(sceneTree, mind);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            mind.ObserveForTest(new TestObservation(5f, "racing high"));
            firstTurn.CompleteNaturally();
            await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(2, mind.Batches.Count);
            Assert.Equal("racing high", Assert.IsType<TestObservation>(Assert.Single(mind.Batches[1])).Value);
            Assert.Equal(1, mind.MaximumConcurrentTurns);
        }
        finally
        {
            firstTurn.CompleteNaturally();
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Disabling during settlement preserves pending replacement work until re-enabled.
    /// </summary>
    [Fact]
    public async Task DisableDuringSettlement_DefersReplacementUntilReenabled()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.Interruptible();
        var replacement = TurnGate.Completed();
        InterruptionMind mind = CreateMind(firstTurn, replacement);
        AddTestNode(sceneTree, mind);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            mind.ObserveForTest(new TestObservation(5f, "high"));
            await firstTurn.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            mind.Enabled = false;
            firstTurn.ReleaseSettlement();
            await firstTurn.Settled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(replacement.Started.Task.IsCompleted);
            Assert.True(mind.HasPendingForTest);

            mind.Enabled = true;
            await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("high", Assert.IsType<TestObservation>(Assert.Single(mind.Batches[1])).Value);
        }
        finally
        {
            firstTurn.ReleaseSettlement();
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Node lifetime cancellation wins during pre-emption and starts no replacement or expected-error diagnostic.
    /// </summary>
    [Fact]
    public async Task NodeExitDuringPreemption_StartsNoReplacementAndLogsNoError()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        var firstTurn = TurnGate.Interruptible();
        var replacement = TurnGate.Completed();
        InterruptionMind mind = CreateMind(firstTurn, replacement);
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        parent.AddChild(mind);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            mind.ObserveForTest(new TestObservation(5f, "high"));
            await firstTurn.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            parent.RemoveChild(mind);
            firstTurn.ReleaseSettlement();
            await firstTurn.Settled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            Assert.False(replacement.Started.Task.IsCompleted);
            Assert.DoesNotContain(loggerProvider.Entries, entry => entry.Level >= LogLevel.Error);
        }
        finally
        {
            firstTurn.ReleaseSettlement();
            if (mind.GetParent() is { } currentParent)
            {
                currentParent.RemoveChild(mind);
            }

            mind.Free();
        }
    }

    /// <summary>
    /// A queued production tool submission observes its turn and node-lifetime cancellation before the shared dispatcher starts it.
    /// </summary>
    [Fact]
    [Headless(false)]
    public async Task NodeExitBeforeQueuedToolDispatch_CancelsWithoutDelegateObservationOrExitedServiceAccess()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var dispatcher = new StartBarrierDispatcher();
        var toolHost = new QueuedToolHost();
        AgentToolInvocationMind mind = new(dispatcher, toolHost);
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        parent.AddChild(mind);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "begin queued tool"));
            CancellationToken queuedCancellation = await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(dispatcher.DelegateStarted);
            Assert.False(queuedCancellation.IsCancellationRequested);

            parent.RemoveChild(mind);
            Assert.True(queuedCancellation.IsCancellationRequested);

            dispatcher.AllowStart();
            await mind.TurnSettled.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, toolHost.DelegateCalls);
            Assert.Equal(0, toolHost.WorldEffects);
            Assert.Collection(
                mind.GetTimelineForTest(),
                item => Assert.Equal("begin queued tool", Assert.IsType<TestObservation>(item).Value));
            Assert.DoesNotContain(mind.GetTimelineForTest(), item => item is TestAction);
            Assert.False(mind.HasPendingForTest);
        }
        finally
        {
            dispatcher.AllowStart();
            if (mind.GetParent() is { } currentParent)
            {
                currentParent.RemoveChild(mind);
            }

            mind.Free();
        }
    }

    /// <summary>
    /// Tool observations committed during cancellation settlement remain ordered in replacement memory.
    /// </summary>
    [Fact]
    public async Task ToolObservationDuringSettlement_RemainsOrderedInReplacement()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var firstTurn = TurnGate.Interruptible();
        var replacement = TurnGate.Completed();
        InterruptionMind mind = CreateMind(firstTurn, replacement);
        AddTestNode(sceneTree, mind);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "initial"));
            await firstTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            mind.ObserveForTest(new TestObservation(5f, "high"));
            await firstTurn.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var context = new AgentToolContext(mind.Owner, new TestSceneContext([mind.Owner]));
            AIFunction function = AgentTool.CreateFunction(
                ToolHost.CommitActionAsync,
                context,
                mind,
                Game.Instance.GetRequiredService<IMainThreadDispatcher>());
            Assert.Equal("committed", await function.InvokeAsync([], CancellationToken.None));
            firstTurn.ReleaseSettlement();
            await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Collection(
                mind.Batches[1],
                item => Assert.Equal("high", Assert.IsType<TestObservation>(item).Value),
                item => Assert.Equal(((IIdentifiable)mind.Owner).FullId, Assert.IsType<TestAction>(item).ActorId));
            Assert.Equal(3, mind.TimelineSnapshots[1].Count);
        }
        finally
        {
            firstTurn.ReleaseSettlement();
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Genuine failures remain logged and do not retry the failed claimed batch.
    /// </summary>
    [Fact]
    public async Task GenuineFailure_RemainsLoggedWithoutRetry()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        var failure = TurnGate.Failing();
        var nextTurn = TurnGate.Completed();
        InterruptionMind mind = CreateMind(failure, nextTurn);
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        AddTestNode(sceneTree, mind);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "failed batch"));
            await failure.Settled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(sceneTree, () => loggerProvider.Entries.Any(entry => entry.Level == LogLevel.Error));

            mind.ObserveForTest(new TestObservation(1f, "later batch"));
            await nextTurn.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, mind.Batches.Count);
            Assert.Equal("failed batch", Assert.IsType<TestObservation>(Assert.Single(mind.Batches[0])).Value);
            Assert.Equal("later batch", Assert.IsType<TestObservation>(Assert.Single(mind.Batches[1])).Value);
            Assert.Contains(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error
                    && entry.Exception is InvalidOperationException
                    && entry.Message.Contains("observation processing failed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Interruption authoring defaults disabled and provides bounded positive threshold metadata.
    /// </summary>
    [Fact]
    public void InterruptionSettings_HaveExplicitDisabledDefaultAndRange()
    {
        InterruptionMind mind = new();
        ExportAttribute thresholdExport = Assert.Single(
            typeof(MindBase).GetProperty(nameof(MindBase.HighImportanceInterruptionThreshold))!
                .GetCustomAttributes(typeof(ExportAttribute), inherit: true)
                .Cast<ExportAttribute>());

        Assert.False(mind.HighImportanceInterruptionEnabled);
        Assert.Equal(1f, mind.HighImportanceInterruptionThreshold);
        Assert.Equal(PropertyHint.Range, thresholdExport.Hint);
        Assert.Equal("0.01,100,0.01", thresholdExport.HintString);
        mind.Free();
    }

    private static InterruptionMind CreateMind(params TurnGate[] turns)
        => new(turns)
        {
            HighImportanceInterruptionEnabled = true,
            HighImportanceInterruptionThreshold = 5f,
            ObservationImportanceThreshold = 1f,
            MaxObservationWaitSeconds = 10f,
        };

    private static void AddTestNode(SceneTree sceneTree, Node node)
    {
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        parent.AddChild(node);
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, Node node)
    {
        node.QueueFree();
        await TestUtils.WaitForFramesAsync(sceneTree, 2);
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate)
    {
        for (int frame = 0; frame < 120; frame++)
        {
            if (predicate())
            {
                return;
            }

            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), "Condition was not met within 120 frames.");
    }

    private sealed partial class InterruptionMind(params TurnGate[] turns) : MindBase
    {
        private readonly Queue<TurnGate> _turns = new(turns);
        private int _activeTurns;

        public new TestCharacter Owner { get; } = new();

        public List<IReadOnlyList<AgentObservation>> Batches { get; } = [];

        public List<IReadOnlyList<AgentObservation>> TimelineSnapshots { get; } = [];

        public int MaximumConcurrentTurns
        {
            get; private set;
        }

        public bool HasPendingForTest => HasPendingObservations;

        public void ObserveForTest(AgentObservation observation) => _ = Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => Owner;

        protected override async Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<AgentObservation> timelineSnapshot,
            CancellationToken cancellationToken)
        {
            TurnGate turn = _turns.Count > 0 ? _turns.Dequeue() : TurnGate.Completed();
            _activeTurns++;
            MaximumConcurrentTurns = Math.Max(MaximumConcurrentTurns, _activeTurns);
            Batches.Add([.. observations]);
            TimelineSnapshots.Add([.. timelineSnapshot]);

            try
            {
                await turn.RunAsync(cancellationToken);
            }
            finally
            {
                _activeTurns--;
            }
        }
    }

    private sealed partial class AgentToolInvocationMind(
        IMainThreadDispatcher dispatcher,
        QueuedToolHost toolHost) : MindBase
    {
        private readonly TestCharacter _owner = new();

        public TaskCompletionSource TurnSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasPendingForTest => HasPendingObservations;

        public bool IsQueuedToolTestLifetimeEnded => IsNodeLifetimeEnded;

        public void ObserveForTest(AgentObservation observation) => _ = Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => _owner;

        protected override async Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<AgentObservation> timelineSnapshot,
            CancellationToken cancellationToken)
        {
            _ = observations;
            _ = timelineSnapshot;
            var context = new AgentToolContext(_owner, new TestSceneContext([_owner]));
            AIFunction function = AgentTool.CreateFunction(
                toolHost.InvokeAsync,
                context,
                this,
                dispatcher,
                "queued_production_tool");
            try
            {
                _ = await function.InvokeAsync([], cancellationToken);
            }
            finally
            {
                _ = TurnSettled.TrySetResult();
            }
        }
    }

    private sealed class StartBarrierDispatcher : IMainThreadDispatcher
    {
        private readonly TaskCompletionSource _startAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CancellationToken> Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DelegateStarted
        {
            get; private set;
        }

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            return InvokeAsync(_ =>
            {
                action();
                return ValueTask.CompletedTask;
            }, cancellationToken);
        }

        public ValueTask InvokeAsync(
            Func<CancellationToken, ValueTask> action,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            _ = Queued.TrySetResult(cancellationToken);
            return new ValueTask(WaitForStartAsync(action, cancellationToken));
        }

        public void AllowStart() => _startAllowed.TrySetResult();

        private async Task WaitForStartAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken)
        {
            await _startAllowed.Task;
            cancellationToken.ThrowIfCancellationRequested();
            DelegateStarted = true;
            await action(cancellationToken);
        }
    }

    private sealed class QueuedToolHost
    {
        public int DelegateCalls
        {
            get; private set;
        }

        public int WorldEffects
        {
            get; private set;
        }

        public ValueTask<AgentToolResult> InvokeAsync(CancellationToken cancellationToken)
        {
            DelegateCalls++;
            WorldEffects++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AgentToolResult(
                "unexpected dispatch",
                [new TestAction("spoofed", "unexpected world effect")]));
        }
    }

    private sealed class TurnGate
    {
        private readonly TurnMode _mode;
        private readonly TaskCompletionSource _completion = CreateCompletion();
        private readonly TaskCompletionSource _settlement = CreateCompletion();

        private TurnGate(TurnMode mode)
        {
            _mode = mode;
        }

        public TaskCompletionSource Started { get; } = CreateCompletion();

        public TaskCompletionSource CancellationObserved { get; } = CreateCompletion();

        public TaskCompletionSource Settled { get; } = CreateCompletion();

        public int CancellationCount
        {
            get; private set;
        }

        public static TurnGate Interruptible() => new(TurnMode.Interruptible);

        public static TurnGate NaturallyCompleting() => new(TurnMode.Natural);

        public static TurnGate Completed() => new(TurnMode.Completed);

        public static TurnGate Failing() => new(TurnMode.Failing);

        public void ReleaseSettlement() => _settlement.TrySetResult();

        public void CompleteNaturally() => _completion.TrySetResult();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _ = Started.TrySetResult();
            try
            {
                switch (_mode)
                {
                    case TurnMode.Interruptible:
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            CancellationCount++;
                            _ = CancellationObserved.TrySetResult();
                            await _settlement.Task;
                            throw;
                        }

                        break;
                    case TurnMode.Natural:
                        using (cancellationToken.Register(() =>
                        {
                            CancellationCount++;
                            _ = CancellationObserved.TrySetResult();
                        }))
                        {
                            await _completion.Task;
                        }

                        break;
                    case TurnMode.Failing:
                        throw new InvalidOperationException("Genuine test turn failure.");
                    case TurnMode.Completed:
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown turn mode '{_mode}'.");
                }
            }
            finally
            {
                _ = Settled.TrySetResult();
            }
        }

        private static TaskCompletionSource CreateCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum TurnMode
    {
        Interruptible,
        Natural,
        Completed,
        Failing,
    }

    private sealed record TestObservation(float Importance, string Value) : AgentObservation
    {
        public override string TypeKey => "test.interruption";

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed record TestAction(string? ActorId, string Value) : ObservedAction(ActorId)
    {
        public override string TypeKey => "test.action";

        public override float CalculateImportance(ObservationContext context) => 1f;
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "interruption_owner";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }

    private static class ToolHost
    {
        public static ValueTask<AgentToolResult> CommitActionAsync()
            => ValueTask.FromResult(new AgentToolResult(
                "committed",
                [new TestAction("spoofed", "tool action")]));
    }

    private sealed record TestSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException();
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
