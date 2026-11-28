using AlleyCat.Models;
using Xunit;

namespace AlleyCat.Tests.Models;

/// <summary>
/// CORE-011 shutdown and handle-release contract: admission stops, queued work is cancelled, and backends are
/// disposed only after their load and in-flight calls return, without blocking the caller.
/// </summary>
public sealed class SharedModelShutdownTests
{
    private static readonly TimeSpan _bound = SharedModelTestTimeouts.Bound;

    private static ModelWorkItem Item(string consumer) => new(new ModelConsumerId(consumer));

    /// <summary>
    /// CORE-011-TAC-008 (TR24): shutdown cancels queued items, rejects new ones, returns before the in-flight call
    /// finishes, and disposes the backend only after that call returns.
    /// </summary>
    [Fact]
    public async Task Stop_CancelsQueued_WaitsForInFlight_ThenDisposesBackend()
    {
        var registry = new SharedModelRegistry();
        var backend = new TestBackend();
        ISharedModel<TestBackend> model = registry.Resolve(TestDefinition.Immediate(new TestModelKey("/models/a.onnx"), backend));
        await model.Loaded.WaitAsync(_bound);
        var inFlight = new GatedCall(1);
        Task<int> running = model.SubmitAsync(Item("a"), inFlight.Execute);
        await inFlight.Started.WaitAsync(_bound);
        Task<int> queued = model.Submit(Item("b"), _ => 2);
        backend.ObserveOnDispose = () => inFlight.Finished;

        Task stopped = registry.Stop();

        Assert.False(stopped.IsCompleted);
        Assert.True(queued.IsCanceled);
        Assert.True(model.Submit(Item("c"), _ => 3).IsCanceled);
        Assert.Same(stopped, registry.Stop());
        Assert.Equal(0, backend.DisposeCount);

        inFlight.Release();
        await stopped.WaitAsync(_bound);

        Assert.Equal(1, backend.DisposeCount);
        Assert.True(backend.ObservedOnDispose);
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(_bound));
    }

    /// <summary>
    /// CORE-011-TAC-008 (TR24): shutdown during a load signals the loader and disposes the backend only after the load
    /// returns, never concurrently with it.
    /// </summary>
    [Fact]
    public async Task Stop_DuringLoad_DisposesOnlyAfterLoadReturns()
    {
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new TestBackend();
        bool loadReturned = false;
        var definition = new TestDefinition(new TestModelKey("/models/a.onnx"), async token =>
        {
            using CancellationTokenRegistration _ = token.Register(() => cancellationSeen.TrySetResult());
            // A native load that cannot be interrupted ignores the token and returns when it finishes.
            await loadGate.Task.ConfigureAwait(false);
            loadReturned = true;
            return backend;
        });
        var registry = new SharedModelRegistry();
        ISharedModel<TestBackend> model = registry.Resolve(definition);
        Task<int> queued = model.Submit(Item("a"), _ => 1);
        backend.ObserveOnDispose = () => Volatile.Read(ref loadReturned);

        Task stopped = registry.Stop();
        await cancellationSeen.Task.WaitAsync(_bound);

        Assert.True(queued.IsCanceled);
        Assert.False(stopped.IsCompleted);

        loadGate.SetResult();
        await stopped.WaitAsync(_bound);

        Assert.Equal(1, backend.DisposeCount);
        Assert.True(backend.ObservedOnDispose);
    }

    /// <summary>
    /// CORE-011-TAC-008 (TR24): a second stop after the first has completed, such as the owning <c>Game</c>'s tree
    /// exit after the shutdown boundary, returns the same completion and never disposes the backend again.
    /// </summary>
    [Fact]
    public async Task Stop_CalledAgainAfterCompletion_ReturnsSameTask_AndDisposesOnce()
    {
        var registry = new SharedModelRegistry();
        var backend = new TestBackend();
        ISharedModel<TestBackend> model = registry.Resolve(TestDefinition.Immediate(new TestModelKey("/models/a.onnx"), backend));
        await model.Loaded.WaitAsync(_bound);

        Task first = registry.Stop();
        await first.WaitAsync(_bound);
        Task second = registry.Stop();

        Assert.Same(first, second);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(1, backend.DisposeCount);
    }

    /// <summary>CORE-011-TAC-008 (TR24): stop completes for failed loads and aggregates every backend.</summary>
    [Fact]
    public async Task Stop_AggregatesEveryBackend_IncludingFailedLoads()
    {
        var registry = new SharedModelRegistry();
        var loaded = new TestBackend();
        ISharedModel<TestBackend> good = registry.Resolve(TestDefinition.Immediate(new TestModelKey("/models/good.onnx"), loaded));
        ISharedModel<TestBackend> bad = registry.Resolve(new TestDefinition(
            new TestModelKey("/models/bad.onnx"), _ => Task.FromException<TestBackend>(new InvalidOperationException("bad"))));
        await good.Loaded.WaitAsync(_bound);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => bad.Loaded.WaitAsync(_bound));

        await registry.Stop().WaitAsync(_bound);

        Assert.Equal(1, loaded.DisposeCount);
    }

    /// <summary>
    /// CORE-011 TR22: disposing a handle cancels its queued items and discards its in-flight result without waiting,
    /// leaves other consumers untouched, and never disposes the shared backend.
    /// </summary>
    [Fact]
    public async Task HandleDispose_CancelsOwnWorkWithoutBlocking_AndLeavesOthersRunning()
    {
        var registry = new SharedModelRegistry();
        var backend = new TestBackend();
        ISharedModel<TestBackend> model = registry.Resolve(TestDefinition.Immediate(new TestModelKey("/models/a.onnx"), backend));
        await model.Loaded.WaitAsync(_bound);
        var leaving = new TestHandle(model, "leaving");
        var staying = new TestHandle(model, "staying");
        var inFlight = new GatedCall(1);
        Task<int> leavingRunning = leaving.Run(inFlight.Execute);
        await inFlight.Started.WaitAsync(_bound);
        Task<int> leavingQueued = leaving.RunSync(_ => 2);
        Task<int> stayingQueued = staying.RunSync(_ => 3);

        leaving.Dispose();

        Assert.True(leavingQueued.IsCanceled);
        Assert.False(inFlight.Finished);
        Assert.True(leaving.IsDisposed);
        _ = Assert.Throws<ObjectDisposedException>(() => { _ = leaving.RunSync(_ => 4); });

        inFlight.Release();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leavingRunning.WaitAsync(_bound));
        Assert.Equal(3, await stayingQueued.WaitAsync(_bound));
        Assert.Equal(0, backend.DisposeCount);
    }

    /// <summary>CORE-011 TR12: a per-call token passed through a handle cancels only that call.</summary>
    [Fact]
    public async Task HandlePerCallToken_CancelsOnlyThatCall()
    {
        var loadGate = new TaskCompletionSource<TestBackend>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new SharedModelRegistry();
        var handle = new TestHandle(registry.Resolve(new TestDefinition(new TestModelKey("/models/a.onnx"), _ => loadGate.Task)), "npc");
        using var source = new CancellationTokenSource();
        Task<int> cancelled = handle.Run(_ => Task.FromResult(1), token: source.Token);
        Task<int> kept = handle.Run(_ => Task.FromResult(2));

        source.Cancel();
        loadGate.SetResult(new TestBackend());

        Assert.True(cancelled.IsCanceled);
        Assert.Equal(2, await kept.WaitAsync(_bound));
    }
}
