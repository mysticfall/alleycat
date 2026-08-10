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
}
