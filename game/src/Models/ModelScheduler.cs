using Microsoft.Extensions.Logging;

namespace AlleyCat.Models;

/// <summary>
/// One backend's scheduler: bounded concurrency, policy-selected dispatch, per-consumer latest-wins supersession and
/// asynchronous completion. In-flight backend calls are never interrupted; cancelled or superseded running items
/// finish, and their results are discarded. Backend calls and disposal always run on the thread pool.
/// </summary>
/// <typeparam name="TBackend">Opaque backend type.</typeparam>
internal sealed class ModelScheduler<TBackend> : ISharedModel<TBackend> where TBackend : class, IDisposable
{
    private readonly Lock _gate = new();
    private readonly IModelSchedulingPolicy _policy;
    private readonly SharedModelDiagnostics _diagnostics;
    private readonly List<Work> _pending = [];
    private readonly List<ModelWorkItem> _pendingItems = [];
    private readonly List<Work> _running = [];
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TBackend? _backend;
    private Exception? _loadError;
    private bool _loadSettled;
    private TaskCompletionSource? _stopped;
    private bool _disposeStarted;

    internal ModelScheduler(
        ModelKey key,
        int maxConcurrency,
        IModelSchedulingPolicy policy,
        SharedModelDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Key = key;
        MaxConcurrency = maxConcurrency;
        _policy = policy;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public ModelKey Key
    {
        get;
    }

    /// <inheritdoc />
    public Task Loaded => _loaded.Task;

    /// <summary>Declared concurrency limit.</summary>
    internal int MaxConcurrency
    {
        get;
    }

    /// <inheritdoc />
    public Task<TResult> Submit<TResult>(ModelWorkItem item, Func<TBackend, TResult> execute)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(execute);
        return Enqueue(new Work<TResult>(item, backend => Task.FromResult(execute(backend))));
    }

    /// <inheritdoc />
    public Task<TResult> SubmitAsync<TResult>(ModelWorkItem item, Func<TBackend, Task<TResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(execute);
        return Enqueue(new Work<TResult>(item, execute));
    }

    internal void RaiseLoadStarted()
    {
        lock (_gate)
        {
            _diagnostics.Raise(SharedModelEventKind.LoadStarted, Key);
        }
    }

    internal void OnLoaded(TBackend backend)
    {
        lock (_gate)
        {
            _backend = backend;
            _loadSettled = true;
            _diagnostics.Raise(SharedModelEventKind.LoadSucceeded, Key);
            _ = _loaded.TrySetResult();
            if (_stopped is null)
            {
                DispatchLocked();
            }
            else
            {
                TryBeginDisposeLocked();
            }
        }
    }

    internal void OnLoadFailed(Exception error)
    {
        lock (_gate)
        {
            _loadError = error;
            _loadSettled = true;
            _diagnostics.Raise(SharedModelEventKind.LoadFailed, Key, error: error);
            _ = _loaded.TrySetException(error);
            // Consumers report the failure through their own requests; an unobserved Loaded task is not an error.
            _ = _loaded.Task.Exception;

            foreach (Work work in _pending)
            {
                FailLoadLocked(work);
            }

            _pending.Clear();
            _pendingItems.Clear();
            if (_stopped is not null)
            {
                TryBeginDisposeLocked();
            }
        }
    }

    /// <summary>
    /// Stops admission, settles queued items as cancelled, discards running results, and disposes the backend on the
    /// thread pool once the load and every in-flight call have returned. Never blocks; idempotent.
    /// </summary>
    /// <returns>Completion of backend disposal.</returns>
    internal Task Stop()
    {
        lock (_gate)
        {
            if (_stopped is not null)
            {
                return _stopped.Task;
            }

            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (Work work in _pending)
            {
                DropLocked(work, ModelWorkDropReason.Shutdown);
            }

            _pending.Clear();
            _pendingItems.Clear();
            TryBeginDisposeLocked();
            return _stopped.Task;
        }
    }

    private Task<TResult> Enqueue<TResult>(Work<TResult> work)
    {
        ModelWorkItem item = work.Item;
        lock (_gate)
        {
            if (_stopped is not null)
            {
                DropLocked(work, ModelWorkDropReason.Shutdown);
                return work.Completion;
            }

            if (_loadError is not null)
            {
                FailLoadLocked(work);
                return work.Completion;
            }

            if (item.CancellationToken.IsCancellationRequested)
            {
                DropLocked(work, ModelWorkDropReason.Cancelled);
                return work.Completion;
            }

            if (item.SupersessionKey is { } supersessionKey)
            {
                SupersedeLocked(item.Consumer, supersessionKey);
            }

            work.State = WorkState.Queued;
            _pending.Add(work);
            _pendingItems.Add(item);
            _diagnostics.Raise(SharedModelEventKind.ItemQueued, Key, item);
            DispatchLocked();
        }

        if (item.CancellationToken.CanBeCanceled)
        {
            // Registered outside the lock: an already-cancelled token runs the callback inline.
            CancellationTokenRegistration registration = item.CancellationToken.UnsafeRegister(
                static state =>
                {
                    (ModelScheduler<TBackend> scheduler, Work cancelled) = ((ModelScheduler<TBackend>, Work))state!;
                    scheduler.OnCancelled(cancelled);
                },
                (this, (Work)work));
            lock (_gate)
            {
                if (work.State == WorkState.Queued)
                {
                    work.Registration = registration;
                }
                else
                {
                    _ = registration.Unregister();
                }
            }
        }

        return work.Completion;
    }

    private void OnCancelled(Work work)
    {
        lock (_gate)
        {
            // Running items are discarded on completion; settled items need nothing.
            int index = work.State == WorkState.Queued ? _pending.IndexOf(work) : -1;
            if (index >= 0)
            {
                RemovePendingAt(index);
                DropLocked(work, ModelWorkDropReason.Cancelled);
            }
        }
    }

    private void SupersedeLocked(ModelConsumerId consumer, object supersessionKey)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            Work queued = _pending[i];
            if (IsSameSlot(queued.Item, consumer, supersessionKey))
            {
                RemovePendingAt(i);
                DropLocked(queued, ModelWorkDropReason.Superseded);
            }
        }

        foreach (Work running in _running)
        {
            if (IsSameSlot(running.Item, consumer, supersessionKey))
            {
                running.Superseded = true;
            }
        }
    }

    private static bool IsSameSlot(ModelWorkItem item, ModelConsumerId consumer, object supersessionKey) =>
        ReferenceEquals(item.Consumer, consumer) && Equals(item.SupersessionKey, supersessionKey);

    private void DispatchLocked()
    {
        if (_backend is not { } backend || _stopped is not null)
        {
            return;
        }

        while (_running.Count < MaxConcurrency && _pending.Count > 0)
        {
            // A cancellation that raced its registration must not spend a consumer's turn.
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Work queued = _pending[i];
                if (queued.Item.CancellationToken.IsCancellationRequested)
                {
                    RemovePendingAt(i);
                    DropLocked(queued, ModelWorkDropReason.Cancelled);
                }
            }

            if (_pending.Count == 0)
            {
                return;
            }

            int index = SelectLocked();
            Work work = _pending[index];
            RemovePendingAt(index);
            _ = work.Registration.Unregister();
            work.State = WorkState.Running;
            _running.Add(work);
            _diagnostics.Raise(SharedModelEventKind.ItemStarted, Key, work.Item);
            _ = Task.Run(() => RunAsync(work, backend));
        }
    }

    private int SelectLocked()
    {
        int index;
        try
        {
            index = _policy.SelectNext(_pendingItems);
        }
        catch (Exception error)
        {
            _diagnostics.GetLogger().LogError(
                error, "Scheduling policy failed for {ModelKey}; serving the oldest queued item", Key);
            return 0;
        }

        if (index >= 0 && index < _pending.Count)
        {
            return index;
        }

        _diagnostics.GetLogger().LogError(
            "Scheduling policy returned invalid index {Index} for {ModelKey}; serving the oldest queued item", index, Key);
        return 0;
    }

    private async Task RunAsync(Work work, TBackend backend)
    {
        Exception? error = null;
        try
        {
            await work.ExecuteAsync(backend).ConfigureAwait(false);
        }
        catch (Exception executionError)
        {
            error = executionError;
        }

        lock (_gate)
        {
            _ = _running.Remove(work);
            if (work.Superseded || _stopped is not null || work.Item.CancellationToken.IsCancellationRequested)
            {
                work.Cancel();
                _diagnostics.Raise(
                    SharedModelEventKind.ItemCompleted, Key, work.Item, error, outcome: ModelWorkOutcome.Discarded);
            }
            else if (error is not null)
            {
                work.Fault(error);
                _diagnostics.Raise(
                    SharedModelEventKind.ItemCompleted, Key, work.Item, error, outcome: ModelWorkOutcome.Faulted);
            }
            else
            {
                work.Publish();
                _diagnostics.Raise(
                    SharedModelEventKind.ItemCompleted, Key, work.Item, outcome: ModelWorkOutcome.Succeeded);
            }

            if (_stopped is null)
            {
                DispatchLocked();
            }
            else
            {
                TryBeginDisposeLocked();
            }
        }
    }

    private void TryBeginDisposeLocked()
    {
        if (_disposeStarted || !_loadSettled || _running.Count > 0)
        {
            return;
        }

        _disposeStarted = true;
        TaskCompletionSource stopped = _stopped!;
        TBackend? backend = _backend;
        _backend = null;
        if (backend is null)
        {
            _ = stopped.TrySetResult();
            return;
        }

        _ = Task.Run(() => DisposeBackend(backend, stopped));
    }

    private void DisposeBackend(TBackend backend, TaskCompletionSource stopped)
    {
        try
        {
            backend.Dispose();
        }
        catch (Exception error)
        {
            _diagnostics.GetLogger().LogError(error, "Shared model backend disposal failed for {ModelKey}", Key);
            _ = stopped.TrySetException(error);
            return;
        }

        lock (_gate)
        {
            _diagnostics.Raise(SharedModelEventKind.BackendDisposed, Key);
        }

        _ = stopped.TrySetResult();
    }

    private void RemovePendingAt(int index)
    {
        _pending.RemoveAt(index);
        _pendingItems.RemoveAt(index);
    }

    private void DropLocked(Work work, ModelWorkDropReason reason)
    {
        _ = work.Registration.Unregister();
        work.State = WorkState.Settled;
        work.Cancel();
        _diagnostics.Raise(SharedModelEventKind.ItemDropped, Key, work.Item, dropReason: reason);
    }

    private void FailLoadLocked(Work work)
    {
        _ = work.Registration.Unregister();
        work.State = WorkState.Settled;
        work.Fault(_loadError!);
        _diagnostics.Raise(
            SharedModelEventKind.ItemDropped, Key, work.Item, _loadError, ModelWorkDropReason.LoadFailed);
    }

    private enum WorkState
    {
        New,
        Queued,
        Running,
        Settled
    }

    private abstract class Work(ModelWorkItem item)
    {
        public ModelWorkItem Item { get; } = item;

        public WorkState State
        {
            get; set;
        }

        public bool Superseded
        {
            get; set;
        }

        public CancellationTokenRegistration Registration
        {
            get; set;
        }

        public abstract Task ExecuteAsync(TBackend backend);

        public abstract void Publish();

        public abstract void Fault(Exception error);

        public abstract void Cancel();
    }

    private sealed class Work<TResult>(ModelWorkItem item, Func<TBackend, Task<TResult>> execute) : Work(item)
    {
        // Continuations never run inline on the scheduler's dispatch path or under its lock.
        private readonly TaskCompletionSource<TResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TResult? _result;

        public Task<TResult> Completion => _completion.Task;

        public override async Task ExecuteAsync(TBackend backend) =>
            _result = await execute(backend).ConfigureAwait(false);

        public override void Publish()
        {
            State = WorkState.Settled;
            _ = _completion.TrySetResult(_result!);
        }

        public override void Fault(Exception error)
        {
            State = WorkState.Settled;
            _ = _completion.TrySetException(error);
        }

        public override void Cancel()
        {
            State = WorkState.Settled;
            _result = default;
            CancellationToken token = Item.CancellationToken;
            _ = token.IsCancellationRequested
                ? _completion.TrySetCanceled(token)
                : _completion.TrySetCanceled();
        }
    }
}
