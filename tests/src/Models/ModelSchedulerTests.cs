using AlleyCat.Models;
using Xunit;

namespace AlleyCat.Tests.Models;

/// <summary>
/// CORE-011 scheduler contract: bounded concurrency, fair policy-driven dispatch, supersession, cancellation,
/// failure isolation and asynchronous completion. Dispatch decisions are synchronous under the scheduler lock, so
/// observer events read immediately after a submit or release are deterministic.
/// </summary>
public sealed class ModelSchedulerTests
{
    private static readonly TimeSpan _bound = SharedModelTestTimeouts.Bound;
    private static readonly TestModelKey _key = new("/models/a.onnx");

    private static ModelWorkItem Item(ModelConsumerId consumer, object? key = null, CancellationToken token = default) =>
        new(consumer)
        {
            SupersessionKey = key,
            CancellationToken = token,
        };

    /// <summary>Resolves a model whose load completes only when the returned source is released.</summary>
    private static (ISharedModel<TestBackend> Model, TaskCompletionSource<TestBackend> LoadGate) GatedModel(
        RecordingObserver? observer = null,
        int maxConcurrency = 1,
        Func<IModelSchedulingPolicy>? policy = null)
    {
        var loadGate = new TaskCompletionSource<TestBackend>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new SharedModelRegistry(observer: observer);
        var definition = new TestDefinition(_key, _ => loadGate.Task, maxConcurrency, policy);
        return (registry.Resolve(definition), loadGate);
    }

    private static async Task<ISharedModel<TestBackend>> LoadedModel(
        RecordingObserver? observer = null,
        int maxConcurrency = 1,
        TestBackend? backend = null)
    {
        var registry = new SharedModelRegistry(observer: observer);
        ISharedModel<TestBackend> model = registry.Resolve(
            TestDefinition.Immediate(_key, backend ?? new TestBackend(), maxConcurrency));
        await model.Loaded.WaitAsync(_bound);
        return model;
    }

    /// <summary>CORE-011-TAC-004 (TR11): the default limit is 1 and running items never exceed it.</summary>
    [Fact]
    public async Task DefaultConcurrency_RunsOneItemAtATime()
    {
        var observer = new RecordingObserver();
        ISharedModel<TestBackend> model = await LoadedModel(observer);
        GatedCall[] calls = [.. Enumerable.Range(0, 4).Select(i => new GatedCall(i))];
        Task<int>[] results = [.. calls.Select((call, i) => model.SubmitAsync(Item(new ModelConsumerId($"c{i}")), call.Execute))];

        for (int i = 0; i < calls.Length; i++)
        {
            await calls[i].Started.WaitAsync(_bound);
            Assert.Equal(i + 1, observer.Count(SharedModelEventKind.ItemStarted));
            Assert.All(calls.Skip(i + 1), call => Assert.False(call.HasStarted));
            calls[i].Release();
            _ = await results[i].WaitAsync(_bound);
        }

        int[] values = await Task.WhenAll(results);
        Assert.Equal([0, 1, 2, 3], values);
    }

    /// <summary>CORE-011-TAC-004 (TR11): a declared higher limit is honoured and never exceeded.</summary>
    [Fact]
    public async Task DeclaredConcurrency_RunsUpToTheLimit()
    {
        var observer = new RecordingObserver();
        ISharedModel<TestBackend> model = await LoadedModel(observer, maxConcurrency: 2);
        GatedCall[] calls = [.. Enumerable.Range(0, 4).Select(_ => new GatedCall())];
        Task<int>[] results = [.. calls.Select((call, i) => model.SubmitAsync(Item(new ModelConsumerId($"c{i}")), call.Execute))];

        await Task.WhenAll(calls[0].Started, calls[1].Started).WaitAsync(_bound);
        Assert.Equal(2, observer.Count(SharedModelEventKind.ItemStarted));

        calls[0].Release();
        _ = await results[0].WaitAsync(_bound);
        await calls[2].Started.WaitAsync(_bound);
        Assert.Equal(3, observer.Count(SharedModelEventKind.ItemStarted));
        Assert.False(calls[3].HasStarted);

        foreach (GatedCall call in calls)
        {
            call.Release();
        }

        _ = await Task.WhenAll(results).WaitAsync(_bound);
    }

    /// <summary>
    /// CORE-011-TAC-004 (TR13): round-robin serves every consumer with pending items before any consumer gets a
    /// second turn, FIFO within one consumer.
    /// </summary>
    [Fact]
    public async Task RoundRobin_ServesEveryConsumerBeforeSecondTurns()
    {
        (ISharedModel<TestBackend> model, TaskCompletionSource<TestBackend> loadGate) = GatedModel();
        var order = new List<string>();
        var a = new ModelConsumerId("a");
        var b = new ModelConsumerId("b");
        var c = new ModelConsumerId("c");
        (ModelConsumerId Consumer, string Label)[] submissions =
            [(a, "a1"), (a, "a2"), (a, "a3"), (b, "b1"), (b, "b2"), (c, "c1")];
        Task<int>[] results = [.. submissions.Select(s => model.Submit(Item(s.Consumer), _ =>
        {
            lock (order)
            {
                order.Add(s.Label);
            }

            return 0;
        }))];

        loadGate.SetResult(new TestBackend());
        _ = await Task.WhenAll(results).WaitAsync(_bound);

        Assert.Equal(["a1", "b1", "c1", "a2", "b2", "a3"], order);
    }

    /// <summary>
    /// CORE-011-TAC-004 (TR13, UR2): a consumer that resubmits as soon as its item starts cannot delay others.
    /// </summary>
    [Fact]
    public async Task RoundRobin_ResubmittingConsumerDoesNotStarveOthers()
    {
        ISharedModel<TestBackend> model = await LoadedModel();
        var busy = new ModelConsumerId("busy");
        var order = new List<string>();
        var first = new GatedCall();
        Task<int> firstResult = model.SubmitAsync(Item(busy), first.Execute);
        await first.Started.WaitAsync(_bound);

        Task<int> Record(ModelConsumerId consumer, string label)
        {
            return model.Submit(Item(consumer), _ =>
            {
                lock (order)
                {
                    order.Add(label);
                }

                return 0;
            });
        }

        Task<int> other1 = Record(new ModelConsumerId("x"), "x");
        Task<int> other2 = Record(new ModelConsumerId("y"), "y");
        Task<int> busyAgain = Record(busy, "busy-2");
        first.Release();
        _ = await Task.WhenAll(firstResult, other1, other2, busyAgain).WaitAsync(_bound);

        Assert.Equal(["x", "y", "busy-2"], order);
    }

    /// <summary>
    /// CORE-011-TAC-004/005 (TR12–13): a substituted policy is honoured and receives the metadata bag unchanged.
    /// </summary>
    [Fact]
    public async Task SubstitutedPolicy_IsHonoured_AndSeesMetadataUnchanged()
    {
        var policy = new LastInFirstOutPolicy();
        (ISharedModel<TestBackend> model, TaskCompletionSource<TestBackend> loadGate) = GatedModel(policy: () => policy);
        var order = new List<int>();
        var metadata = new Dictionary<string, object?> { ["priority"] = "high", ["budget"] = 3 };
        Task<int>[] results = [.. Enumerable.Range(0, 3).Select(i => model.Submit(
            new ModelWorkItem(new ModelConsumerId($"c{i}")) { Metadata = metadata },
            _ =>
            {
                lock (order)
                {
                    order.Add(i);
                }

                return i;
            }))];

        loadGate.SetResult(new TestBackend());
        _ = await Task.WhenAll(results).WaitAsync(_bound);

        Assert.Equal([2, 1, 0], order);
        Assert.All(policy.SeenMetadata, seen => Assert.Same(metadata, seen));
        Assert.NotEmpty(policy.SeenMetadata);
    }

    /// <summary>
    /// CORE-011-TAC-005 (TR14–15): latest-wins per consumer and key; replaced queued items settle as cancelled
    /// without running, and other consumers and other keys are untouched.
    /// </summary>
    [Fact]
    public async Task Supersession_ReplacesQueuedItemsPerConsumerAndKey()
    {
        var observer = new RecordingObserver();
        (ISharedModel<TestBackend> model, TaskCompletionSource<TestBackend> loadGate) = GatedModel(observer);
        var a = new ModelConsumerId("a");
        var b = new ModelConsumerId("b");
        var ran = new List<string>();
        Task<int> Submit(ModelConsumerId consumer, string key, string label)
        {
            return model.Submit(Item(consumer, key), _ =>
            {
                lock (ran)
                {
                    ran.Add(label);
                }

                return 0;
            });
        }

        Task<int> a1 = Submit(a, "motion", "a1");
        Task<int> aOther = Submit(a, "other", "a-other");
        Task<int> b1 = Submit(b, "motion", "b1");
        Task<int> a2 = Submit(a, "motion", "a2");
        Task<int> a3 = Submit(a, "motion", "a3");

        Assert.True(a1.IsCanceled);
        Assert.True(a2.IsCanceled);
        loadGate.SetResult(new TestBackend());
        _ = await Task.WhenAll(aOther, b1, a3).WaitAsync(_bound);

        Assert.Equal(["a-other", "b1", "a3"], ran);
        Assert.Equal(2, observer.Events.Count(e => e.DropReason == ModelWorkDropReason.Superseded));
    }

    /// <summary>
    /// CORE-011-TAC-005 (TR14, TR16): a running item superseded by its consumer finishes uninterrupted, but its
    /// result is discarded and the consumer observes cancellation.
    /// </summary>
    [Fact]
    public async Task Supersession_OfRunningItem_LetsItFinishAndDiscardsResult()
    {
        var observer = new RecordingObserver();
        ISharedModel<TestBackend> model = await LoadedModel(observer);
        var a = new ModelConsumerId("a");
        var running = new GatedCall(1);
        var replacement = new GatedCall(2);
        Task<int> stale = model.SubmitAsync(Item(a, "motion"), running.Execute);
        await running.Started.WaitAsync(_bound);

        Task<int> fresh = model.SubmitAsync(Item(a, "motion"), replacement.Execute);
        Assert.False(stale.IsCompleted);
        Assert.False(replacement.HasStarted);

        running.Release();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale.WaitAsync(_bound));
        Assert.True(running.Finished);
        await replacement.Started.WaitAsync(_bound);
        replacement.Release();

        Assert.Equal(2, await fresh.WaitAsync(_bound));
        Assert.Equal(ModelWorkOutcome.Discarded, observer.Events.First(e => e.Kind == SharedModelEventKind.ItemCompleted).Outcome);
    }

    /// <summary>CORE-011-TAC-005 (TR15): a cancelled queued item, or one cancelled before submission, never runs.</summary>
    [Fact]
    public async Task Cancellation_BeforeStart_DropsItemWithoutRunning()
    {
        (ISharedModel<TestBackend> model, TaskCompletionSource<TestBackend> loadGate) = GatedModel();
        int runs = 0;
        using var queuedSource = new CancellationTokenSource();
        Task<int> queued = model.Submit(Item(new ModelConsumerId("a"), token: queuedSource.Token), _ => Interlocked.Increment(ref runs));
        Task<int> preCancelled = model.Submit(
            Item(new ModelConsumerId("b"), token: new CancellationToken(true)), _ => Interlocked.Increment(ref runs));

        queuedSource.Cancel();

        Assert.True(queued.IsCanceled);
        Assert.True(preCancelled.IsCanceled);
        loadGate.SetResult(new TestBackend());
        int survivor = await model.Submit(Item(new ModelConsumerId("c")), _ => 42).WaitAsync(_bound);
        Assert.Equal(42, survivor);
        Assert.Equal(0, Volatile.Read(ref runs));
    }

    /// <summary>
    /// CORE-011-TAC-005 (TR16): cancelling a running item does not interrupt it; its result is discarded on
    /// completion.
    /// </summary>
    [Fact]
    public async Task Cancellation_OfRunningItem_LetsItFinishAndDiscardsResult()
    {
        ISharedModel<TestBackend> model = await LoadedModel();
        using var source = new CancellationTokenSource();
        var call = new GatedCall(5);
        Task<int> result = model.SubmitAsync(Item(new ModelConsumerId("a"), token: source.Token), call.Execute);
        await call.Started.WaitAsync(_bound);

        source.Cancel();
        Assert.False(result.IsCompleted);

        call.Release();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.WaitAsync(_bound));
        Assert.True(call.Finished);
    }

    /// <summary>CORE-011-TAC-006 (TR17): an execution failure faults only its item and the scheduler continues.</summary>
    [Fact]
    public async Task ExecutionFailure_FaultsOnlyItsItem()
    {
        (ISharedModel<TestBackend> model, TaskCompletionSource<TestBackend> loadGate) = GatedModel();
        var failure = new InvalidOperationException("inference failed");
        Task<int> before = model.Submit(Item(new ModelConsumerId("a")), _ => 1);
        Task<int> failing = model.Submit<int>(Item(new ModelConsumerId("b")), _ => throw failure);
        Task<int> after = model.SubmitAsync(Item(new ModelConsumerId("c")), _ => Task.FromException<int>(failure));
        Task<int> last = model.Submit(Item(new ModelConsumerId("d")), _ => 4);

        loadGate.SetResult(new TestBackend());

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(_bound)));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => after.WaitAsync(_bound)));
        Assert.Equal(1, await before.WaitAsync(_bound));
        Assert.Equal(4, await last.WaitAsync(_bound));
    }

    /// <summary>
    /// CORE-011-TAC-006 (TR18): a consumer continuation that requests synchronous execution still does not run on
    /// the dispatch path; were it inline, the next item could not start while it blocks.
    /// </summary>
    [Fact]
    public async Task Completions_AreNotRunInlineOnDispatchPath()
    {
        (ISharedModel<TestBackend> model, TaskCompletionSource<TestBackend> loadGate) = GatedModel();
        using var nextCompleted = new ManualResetEventSlim();
        Task<int> first = model.Submit(Item(new ModelConsumerId("a")), _ => 1);
        Task<bool> blockingContinuation = first.ContinueWith(
            _ => nextCompleted.Wait(_bound),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Task<int> second = model.Submit(Item(new ModelConsumerId("b")), _ => 2);
        _ = second.ContinueWith(_ => nextCompleted.Set(), TaskScheduler.Default);

        loadGate.SetResult(new TestBackend());

        Assert.Equal(2, await second.WaitAsync(_bound));
        Assert.True(await blockingContinuation.WaitAsync(_bound));
    }

    /// <summary>CORE-011 TR18: submission is thread-safe; concurrent submitters all settle exactly once.</summary>
    [Fact]
    public async Task ConcurrentSubmissions_AllSettle()
    {
        ISharedModel<TestBackend> model = await LoadedModel();
        int runs = 0;

        int[] results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            model.Submit(Item(new ModelConsumerId($"c{i % 8}")), backend =>
            {
                _ = Interlocked.Increment(ref runs);
                return i;
            })))).WaitAsync(_bound);

        Assert.Equal(Enumerable.Range(0, 64), results);
        Assert.Equal(64, Volatile.Read(ref runs));
    }

    private sealed class LastInFirstOutPolicy : IModelSchedulingPolicy
    {
        public List<IReadOnlyDictionary<string, object?>> SeenMetadata { get; } = [];

        public int SelectNext(IReadOnlyList<ModelWorkItem> pending)
        {
            SeenMetadata.AddRange(pending.Select(item => item.Metadata));
            return pending.Count - 1;
        }
    }
}
