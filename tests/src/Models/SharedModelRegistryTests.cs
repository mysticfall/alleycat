using AlleyCat.Models;
using Xunit;

namespace AlleyCat.Tests.Models;

/// <summary>
/// CORE-011 registry contract: lazy exactly-once loading per key, coalescing, cached load failure, and lifetime.
/// </summary>
public sealed class SharedModelRegistryTests
{
    private static readonly TimeSpan _bound = SharedModelTestTimeouts.Bound;

    /// <summary>CORE-011-TAC-002 (TR6–7): concurrent first requests for one key coalesce into one load.</summary>
    [Fact]
    public async Task Resolve_ConcurrentFirstRequests_CoalesceIntoOneLoad()
    {
        var loadGate = new TaskCompletionSource<TestBackend>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new TestDefinition(new TestModelKey("/models/a.onnx"), _ => loadGate.Task);
        var registry = new SharedModelRegistry();
        using var startLine = new Barrier(8);

        ISharedModel<TestBackend>[] models = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            _ = startLine.SignalAndWait(_bound);
            return registry.Resolve(definition);
        }))).WaitAsync(_bound);

        Assert.All(models, model => Assert.Same(models[0], model));
        Assert.False(models[0].Loaded.IsCompleted);

        loadGate.SetResult(new TestBackend());
        await models[0].Loaded.WaitAsync(_bound);
        Assert.Equal(1, definition.LoadCount);
    }

    /// <summary>CORE-011-TAC-002 (TR6–7): loads per key stay at one regardless of consumer count.</summary>
    [Fact]
    public async Task Resolve_ManyConsumersOfEqualKeys_ShareOneBackend()
    {
        var registry = new SharedModelRegistry();
        var backend = new TestBackend();
        var first = TestDefinition.Immediate(new TestModelKey("/models/a.onnx", "cuda"), backend);
        // Distinct definition objects with equal keys model independent consumers constructing their own keys.
        TestDefinition[] others = [.. Enumerable.Range(0, 5)
            .Select(_ => TestDefinition.Immediate(new TestModelKey("/models/a.onnx", "cuda"), new TestBackend()))];

        ISharedModel<TestBackend> model = registry.Resolve(first);
        var handles = others.Select((definition, i) => new TestHandle(registry.Resolve(definition), $"npc-{i}")).ToList();
        int[] seen = await Task.WhenAll(handles.Select(handle =>
            handle.RunSync(b => ReferenceEquals(b, backend) ? 1 : 0))).WaitAsync(_bound);

        Assert.Equal(1, first.LoadCount);
        Assert.All(others, definition => Assert.Equal(0, definition.LoadCount));
        Assert.All(seen, value => Assert.Equal(1, value));
        Assert.Same(model, registry.Resolve(others[0]));
    }

    /// <summary>CORE-011-TAC-002 (TR6): unequal keys, including a different provider, load separately.</summary>
    [Fact]
    public async Task Resolve_UnequalKeys_LoadSeparately()
    {
        var registry = new SharedModelRegistry();
        var cuda = TestDefinition.Immediate(new TestModelKey("/models/a.onnx", "cuda"), new TestBackend());
        var cpu = TestDefinition.Immediate(new TestModelKey("/models/a.onnx", "cpu"), new TestBackend());
        var other = TestDefinition.Immediate(new TestModelKey("/models/b.onnx", "cuda"), new TestBackend());

        ISharedModel<TestBackend>[] models = [registry.Resolve(cuda), registry.Resolve(cpu), registry.Resolve(other)];
        await Task.WhenAll(models.Select(m => m.Loaded)).WaitAsync(_bound);

        Assert.Equal(3, models.Distinct().Count());
        Assert.Equal([1, 1, 1], [cuda.LoadCount, cpu.LoadCount, other.LoadCount]);
    }

    /// <summary>CORE-011 TR7: the first request never runs the load on the caller's thread or waits for it.</summary>
    [Fact]
    public async Task Resolve_BlockingLoad_DoesNotBlockCaller()
    {
        using var loadGate = new ManualResetEventSlim();
        int callerThread = Environment.CurrentManagedThreadId;
        int loadThread = -1;
        var definition = new TestDefinition(new TestModelKey("/models/a.onnx"), token =>
        {
            loadThread = Environment.CurrentManagedThreadId;
            // A synchronous native load: blocking here must not block Resolve.
            _ = loadGate.Wait(_bound);
            return Task.FromResult(new TestBackend());
        });
        var registry = new SharedModelRegistry();

        ISharedModel<TestBackend> model = registry.Resolve(definition);

        Assert.False(model.Loaded.IsCompleted);
        loadGate.Set();
        await model.Loaded.WaitAsync(_bound);
        Assert.NotEqual(callerThread, loadThread);
    }

    /// <summary>
    /// CORE-011-TAC-003 (TR8): a load failure reaches queued, later and newly resolving requesters, and no retry
    /// happens.
    /// </summary>
    [Fact]
    public async Task LoadFailure_IsCachedAndDeliveredToEveryRequester_WithoutRetry()
    {
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("model file missing");
        var definition = new TestDefinition(new TestModelKey("/models/missing.onnx"), async _ =>
        {
            await loadGate.Task.ConfigureAwait(false);
            throw failure;
        });
        var registry = new SharedModelRegistry();
        var early = new TestHandle(registry.Resolve(definition), "early");
        Task<int> queued = early.RunSync(_ => 1);

        loadGate.SetResult();
        InvalidOperationException queuedError =
            await Assert.ThrowsAsync<InvalidOperationException>(() => queued.WaitAsync(_bound));

        var later = new TestHandle(registry.Resolve(definition), "later");
        var retryDefinition = new TestDefinition(new TestModelKey("/models/missing.onnx"), _ => Task.FromResult(new TestBackend()));
        ISharedModel<TestBackend> reResolved = registry.Resolve(retryDefinition);
        InvalidOperationException laterError =
            await Assert.ThrowsAsync<InvalidOperationException>(() => later.RunSync(_ => 2).WaitAsync(_bound));
        InvalidOperationException loadedError =
            await Assert.ThrowsAsync<InvalidOperationException>(() => reResolved.Loaded.WaitAsync(_bound));

        Assert.Same(failure, queuedError);
        Assert.Same(failure, laterError);
        Assert.Same(failure, loadedError);
        Assert.Equal(1, definition.LoadCount);
        Assert.Equal(0, retryDefinition.LoadCount);
    }

    /// <summary>
    /// CORE-011-TAC-003 (TR8): a failure in one consumer's own state faults only that consumer's request and is not
    /// cached under the model key.
    /// </summary>
    [Fact]
    public async Task PerConsumerFailure_AffectsOnlyThatHandle_AndIsNotCachedUnderKey()
    {
        var registry = new SharedModelRegistry();
        var definition = TestDefinition.Immediate(new TestModelKey("/models/a.onnx"), new TestBackend());
        var broken = new TestHandle(registry.Resolve(definition), "broken");
        var healthy = new TestHandle(registry.Resolve(definition), "healthy");

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            broken.RunSync<int>(_ => throw new KeyNotFoundException("joint missing")).WaitAsync(_bound));
        int result = await healthy.RunSync(_ => 7).WaitAsync(_bound);
        await registry.Resolve(definition).Loaded.WaitAsync(_bound);

        Assert.Equal(7, result);
        Assert.Equal(1, definition.LoadCount);
    }

    /// <summary>CORE-011-TAC-002 (TR10): releasing consumers never disposes or unloads the shared backend.</summary>
    [Fact]
    public async Task ReleasingConsumers_DoesNotDisposeBackend()
    {
        var registry = new SharedModelRegistry();
        var backend = new TestBackend();
        var definition = TestDefinition.Immediate(new TestModelKey("/models/a.onnx"), backend);
        var first = new TestHandle(registry.Resolve(definition), "first");
        _ = await first.RunSync(_ => 1).WaitAsync(_bound);

        first.Dispose();
        var second = new TestHandle(registry.Resolve(definition), "second");
        int result = await second.RunSync(b => b.DisposeCount).WaitAsync(_bound);
        second.Dispose();

        Assert.Equal(0, result);
        Assert.Equal(0, backend.DisposeCount);
        Assert.Equal(1, definition.LoadCount);
    }

    /// <summary>A key bound to one backend type is never shared with another backend type.</summary>
    [Fact]
    public void Resolve_SameKeyWithDifferentBackendType_Throws()
    {
        var registry = new SharedModelRegistry();
        var key = new TestModelKey("/models/a.onnx");
        _ = registry.Resolve(TestDefinition.Immediate(key, new TestBackend()));

        _ = Assert.Throws<InvalidOperationException>(() => registry.Resolve(new OtherDefinition(key)));
    }

    /// <summary>CORE-011 TR24: the registry stops admitting new models at shutdown.</summary>
    [Fact]
    public async Task Resolve_AfterStop_Throws()
    {
        var registry = new SharedModelRegistry();
        await registry.Stop().WaitAsync(_bound);

        Assert.True(registry.IsStopping);
        _ = Assert.Throws<ObjectDisposedException>(() =>
            registry.Resolve(TestDefinition.Immediate(new TestModelKey("/models/a.onnx"), new TestBackend())));
    }

    /// <summary>CORE-011-TAC-009 (TR25): load events are observable, and a failing observer changes nothing.</summary>
    [Fact]
    public async Task Observer_SeesLoadEvents_AndObserverFailureDoesNotAlterScheduling()
    {
        var recorder = new RecordingObserver();
        var registry = new SharedModelRegistry(observer: new ThrowingAfterRecordingObserver(recorder));
        var definition = TestDefinition.Immediate(new TestModelKey("/models/a.onnx"), new TestBackend());
        ISharedModel<TestBackend> model = registry.Resolve(definition);
        await model.Loaded.WaitAsync(_bound);
        var handle = new TestHandle(model, "npc");

        int result = await handle.RunSync(_ => 3).WaitAsync(_bound);
        await registry.Stop().WaitAsync(_bound);

        Assert.Equal(3, result);
        SharedModelEventKind[] kinds = [.. recorder.Events.Select(e => e.Kind)];
        Assert.Equal(
            [
                SharedModelEventKind.LoadStarted,
                SharedModelEventKind.LoadSucceeded,
                SharedModelEventKind.ItemQueued,
                SharedModelEventKind.ItemStarted,
                SharedModelEventKind.ItemCompleted,
                SharedModelEventKind.BackendDisposed,
            ],
            kinds);
        Assert.All(recorder.Events, e => Assert.Equal(definition.Key, e.Key));
        Assert.Equal(ModelWorkOutcome.Succeeded, recorder.Events.Single(e => e.Kind == SharedModelEventKind.ItemCompleted).Outcome);
    }

    private sealed class OtherBackend : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class OtherDefinition(ModelKey key) : ModelDefinition<OtherBackend>
    {
        public override ModelKey Key => key;

        public override Task<OtherBackend> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new OtherBackend());
    }

    private sealed class ThrowingAfterRecordingObserver(RecordingObserver inner) : ISharedModelObserver
    {
        public void OnEvent(SharedModelEvent modelEvent)
        {
            inner.OnEvent(modelEvent);
            throw new InvalidOperationException("observer failure");
        }
    }
}
