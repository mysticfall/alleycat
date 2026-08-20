using System.Collections.Concurrent;
using AlleyCat.Core.Threading;
using Xunit;

namespace AlleyCat.Tests.Core.Threading;

/// <summary>
/// Deterministic unit coverage for dispatcher synchronisation boundaries.
/// </summary>
public sealed class GodotMainThreadDispatcherTests
{
    /// <summary>
    /// Verifies closure wins a flush-to-start hand-off without invoking either delegate form.
    /// </summary>
    [Fact]
    public async Task Close_BetweenFlushAndStart_CancelsSyncAndAsyncWorkWithoutInvokingEither()
    {
        Action? deferredFlush = null;
        using ManualResetEventSlim startGateReached = new();
        using ManualResetEventSlim releaseStartGate = new();
        GodotMainThreadDispatcher dispatcher = new(
            action => deferredFlush = action,
            () =>
            {
                startGateReached.Set();
                releaseStartGate.Wait();
            });

        bool synchronousInvoked = false;
        bool asynchronousInvoked = false;
        Task synchronous = dispatcher.InvokeAsync(() => synchronousInvoked = true).AsTask();
        Task asynchronous = dispatcher.InvokeAsync(_ =>
        {
            asynchronousInvoked = true;
            return ValueTask.CompletedTask;
        }).AsTask();

        var flushing = Task.Run(deferredFlush!);
        Assert.True(startGateReached.Wait(TimeSpan.FromSeconds(2)));
        dispatcher.Close();
        releaseStartGate.Set();

        await flushing.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronous);
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asynchronous);
        Assert.False(synchronousInvoked);
        Assert.False(asynchronousInvoked);
        Assert.True(synchronous.IsCanceled);
        Assert.True(asynchronous.IsCanceled);
    }

    /// <summary>
    /// Verifies the scheduler boundary can synchronously re-enter dispatcher shutdown.
    /// </summary>
    [Fact]
    public async Task Scheduler_ReentryOccursOutsideDispatcherLock_AndSettlesAcceptedWork()
    {
        GodotMainThreadDispatcher? dispatcher = null;
        bool schedulerCalled = false;
        dispatcher = new GodotMainThreadDispatcher(_ =>
        {
            schedulerCalled = true;
            dispatcher!.Close();
        });

        Task invocation = dispatcher.InvokeAsync(() => throw new InvalidOperationException("must not run")).AsTask();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.True(schedulerCalled);
        Assert.True(invocation.IsCanceled);
    }

    /// <summary>
    /// Verifies a scheduler failure settles work already accepted for that callback.
    /// </summary>
    [Fact]
    public async Task SchedulerFailure_FaultsAcceptedWorkWithoutLeavingItUnsettled()
    {
        GodotMainThreadDispatcher dispatcher = new(_ => throw new InvalidOperationException("scheduler failed"));

        Task invocation = dispatcher.InvokeAsync(() => { }).AsTask();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => invocation);
        Assert.Equal("scheduler failed", exception.Message);
        Assert.True(invocation.IsFaulted);
    }

    /// <summary>
    /// Verifies dedicated workers released together execute in actual FIFO acceptance order recorded at admission.
    /// </summary>
    [Fact]
    public async Task ConcurrentWorkerAdmissions_RunInTheirRecordedAcceptanceOrder()
    {
        Queue<Action> deferredFlushes = [];
        const int workerCount = 8;
        ConcurrentQueue<int> acceptedOrder = [];
        List<int> invocationOrder = [];
        var actions = new Action[workerCount];
        Dictionary<Action, int> actionValues = [];

        for (int value = 0; value < workerCount; value++)
        {
            int capturedValue = value;
            actions[value] = () => invocationOrder.Add(capturedValue);
            actionValues.Add(actions[value], capturedValue);
        }

        GodotMainThreadDispatcher dispatcher = new(
            deferredFlushes.Enqueue,
            null,
            action => acceptedOrder.Enqueue(actionValues[action]));
        using CountdownEvent workersReady = new(workerCount);
        using ManualResetEventSlim releaseWorkers = new();
        var submissions = new Task<Task>[workerCount];

        for (int worker = 0; worker < workerCount; worker++)
        {
            int capturedWorker = worker;
            submissions[worker] = Task.Factory.StartNew(
                () =>
                {
                    _ = workersReady.Signal();
                    releaseWorkers.Wait();
                    return dispatcher.InvokeAsync(actions[capturedWorker]).AsTask();
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Assert.True(workersReady.Wait(TimeSpan.FromSeconds(2)));
        releaseWorkers.Set();
        Task[] invocations = await Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds(2));
        _ = Assert.Single(deferredFlushes);
        deferredFlushes.Dequeue()();
        await Task.WhenAll(invocations);
        Assert.Equal(acceptedOrder, invocationOrder);
    }

    /// <summary>
    /// Verifies a main-thread caller runs synchronously inline without touching the deferred scheduling boundary.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_OnMainThread_RunsInlineWithoutDeferredScheduling()
    {
        bool schedulerCalled = false;
        GodotMainThreadDispatcher dispatcher = new(
            _ => schedulerCalled = true,
            null,
            null,
            () => true);

        bool invoked = false;
        ValueTask invocation = dispatcher.InvokeAsync(() => invoked = true);

        Assert.True(invoked);
        Assert.True(invocation.IsCompletedSuccessfully);
        await invocation;
        Assert.False(schedulerCalled);
    }

    /// <summary>
    /// Verifies a non-main-thread caller still queues through the deferred scheduling boundary.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_OffMainThread_StillDefersThroughScheduler()
    {
        Action? deferredFlush = null;
        GodotMainThreadDispatcher dispatcher = new(
            action => deferredFlush = action,
            null,
            null,
            () => false);

        bool invoked = false;
        ValueTask invocation = dispatcher.InvokeAsync(() => invoked = true);

        Assert.False(invocation.IsCompleted);
        Assert.False(invoked);
        Assert.NotNull(deferredFlush);

        deferredFlush!();
        await invocation;
        Assert.True(invoked);
    }

    /// <summary>
    /// Verifies a pre-cancelled submission on the main thread never invokes its delegate, mirroring the queued path.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_OnMainThread_WithPreCancelledToken_DoesNotRunInline()
    {
        GodotMainThreadDispatcher dispatcher = new(
            _ => { },
            null,
            null,
            () => true);
        using CancellationTokenSource cancelledSource = new();
        cancelledSource.Cancel();

        bool invoked = false;
        Task invocation = dispatcher
            .InvokeAsync(() => invoked = true, cancelledSource.Token)
            .AsTask();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.True(invocation.IsCanceled);
        Assert.False(invoked);
    }

    /// <summary>
    /// Verifies a closed dispatcher refuses main-thread submissions with the same cancelled outcome as the queued path.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_OnMainThread_AfterClose_IsRejectedAsCancelled()
    {
        GodotMainThreadDispatcher dispatcher = new(
            _ => { },
            null,
            null,
            () => true);
        dispatcher.Close();

        bool invoked = false;
        Task invocation = dispatcher.InvokeAsync(() => invoked = true).AsTask();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.True(invocation.IsCanceled);
        Assert.False(invoked);
    }

    /// <summary>
    /// Verifies the before-work-item-start hook fires immediately before an inline invocation.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_OnMainThread_FiresBeforeWorkItemStartBeforeInlineInvocation()
    {
        List<string> events = [];
        GodotMainThreadDispatcher dispatcher = new(
            _ => { },
            () => events.Add("before"),
            null,
            () => true);

        await dispatcher.InvokeAsync(() => events.Add("invoked"));

        Assert.Equal(["before", "invoked"], events);
    }

    /// <summary>
    /// Verifies inline synchronous failures surface through the returned awaitable rather than throwing on submission.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_OnMainThread_PropagatesInlineFaultsThroughAwaitable()
    {
        GodotMainThreadDispatcher dispatcher = new(
            _ => { },
            null,
            null,
            () => true);

        Task invocation = dispatcher
            .InvokeAsync(() => throw new InvalidOperationException("inline failure"))
            .AsTask();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => invocation);
        Assert.Equal("inline failure", exception.Message);
        Assert.True(invocation.IsFaulted);
    }

    /// <summary>
    /// Verifies an inline asynchronous invocation starts synchronously and completes its awaitable only when the
    /// delegate completes.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AsynchronousDelegate_OnMainThread_AwaitsInlineInvocationCompletion()
    {
        GodotMainThreadDispatcher dispatcher = new(
            _ => { },
            null,
            null,
            () => true);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool started = false;
        Task invocation = dispatcher
            .InvokeAsync(async _ =>
            {
                started = true;
                await completion.Task;
            })
            .AsTask();

        Assert.True(started);
        Assert.False(invocation.IsCompleted);

        completion.SetResult();
        await invocation;
    }

    /// <summary>
    /// Verifies shutdown cancels an inline-started asynchronous item, keeping accepted-operation bookkeeping coherent.
    /// </summary>
    [Fact]
    public async Task Close_WhileInlineAsynchronousItemIsRunning_CancelsIt()
    {
        GodotMainThreadDispatcher dispatcher = new(
            _ => { },
            null,
            null,
            () => true);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task invocation = dispatcher
            .InvokeAsync(async cancellationToken =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            })
            .AsTask();

        await started.Task;
        dispatcher.Close();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.True(invocation.IsCanceled);
    }
}
