namespace AlleyCat.Core.Threading;

/// <summary>
/// Queues main-thread work through the Godot host's deferred event dispatch.
/// </summary>
public sealed class GodotMainThreadDispatcher : IMainThreadDispatcher
{
    private readonly Action<Action> _scheduleDeferred;
    private readonly Action? _beforeWorkItemStart;
    private readonly Action<Action>? _onSynchronousWorkItemAccepted;
    private readonly Lock _lock = new();
    private readonly Lock _startCloseLock = new();
    private readonly Queue<DispatcherWorkItem> _queue = [];
    private readonly HashSet<DispatcherWorkItem> _acceptedOperations = [];
    private readonly CancellationTokenSource _lifetime = new();

    private bool _admissionOpen = true;
    private bool _flushScheduled;

    /// <summary>
    /// Creates a dispatcher using the supplied Godot deferred scheduling boundary.
    /// </summary>
    /// <param name="scheduleDeferred">Schedules an action through Godot's deferred main event dispatch.</param>
    public GodotMainThreadDispatcher(Action<Action> scheduleDeferred)
        : this(scheduleDeferred, null)
    {
    }

    internal GodotMainThreadDispatcher(Action<Action> scheduleDeferred, Action? beforeWorkItemStart)
        : this(scheduleDeferred, beforeWorkItemStart, null)
    {
    }

    /// <summary>
    /// Creates a dispatcher with test-only observation of synchronous work accepted at the queue linearisation point.
    /// The callback runs while admission is serialised. This internal seam is available only to friend test assemblies
    /// and does not affect the production API.
    /// </summary>
    internal GodotMainThreadDispatcher(
        Action<Action> scheduleDeferred,
        Action? beforeWorkItemStart,
        Action<Action>? onSynchronousWorkItemAccepted)
    {
        _scheduleDeferred = scheduleDeferred ?? throw new ArgumentNullException(nameof(scheduleDeferred));
        _beforeWorkItemStart = beforeWorkItemStart;
        _onSynchronousWorkItemAccepted = onSynchronousWorkItemAccepted;
    }

    /// <inheritdoc />
    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Enqueue(new DispatcherWorkItem(this, action, cancellationToken), cancellationToken, action);
    }

    /// <inheritdoc />
    public ValueTask InvokeAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Enqueue(new DispatcherWorkItem(this, action, cancellationToken), cancellationToken, null);
    }

    /// <summary>
    /// Stops admission and settles queued and active work as cancelled.
    /// </summary>
    internal void Close()
    {
        DispatcherWorkItem[] acceptedWork;
        lock (_startCloseLock)
        {
            lock (_lock)
            {
                if (!_admissionOpen)
                {
                    return;
                }

                _admissionOpen = false;
                _flushScheduled = false;
                acceptedWork = [.. _acceptedOperations];
                _queue.Clear();
                _acceptedOperations.Clear();
            }

            // A start either completed its initial delegate invocation before this point, or observes
            // closed admission while holding this same hand-off lock. No dispatcher lock is held here.
            _lifetime.Cancel();
        }

        foreach (DispatcherWorkItem workItem in acceptedWork)
        {
            workItem.CancelForShutdown(_lifetime.Token);
        }
    }

    private ValueTask Enqueue(DispatcherWorkItem workItem, CancellationToken callerToken, Action? synchronousAction)
    {
        if (callerToken.IsCancellationRequested)
        {
            workItem.CancelBeforeStart(callerToken);
            return workItem.Completion;
        }

        bool scheduleFlush = false;
        bool rejectedAfterShutdown;
        lock (_lock)
        {
            rejectedAfterShutdown = !_admissionOpen;
            if (!rejectedAfterShutdown && !workItem.IsSettled)
            {
                _ = _acceptedOperations.Add(workItem);
                _queue.Enqueue(workItem);
                if (synchronousAction is not null)
                {
                    _onSynchronousWorkItemAccepted?.Invoke(synchronousAction);
                }

                if (!_flushScheduled)
                {
                    _flushScheduled = true;
                    scheduleFlush = true;
                }
            }
        }

        if (rejectedAfterShutdown)
        {
            workItem.CancelBeforeStart(_lifetime.Token);
        }
        else if (scheduleFlush)
        {
            ScheduleFlush();
        }

        return workItem.Completion;
    }

    private void ScheduleFlush()
    {
        try
        {
            _scheduleDeferred(Flush);
        }
        catch (Exception exception)
        {
            DispatcherWorkItem[] failedWork = [];
            lock (_lock)
            {
                if (_admissionOpen && _flushScheduled)
                {
                    _flushScheduled = false;
                    failedWork = [.. _queue];
                    _queue.Clear();
                    foreach (DispatcherWorkItem workItem in failedWork)
                    {
                        _ = _acceptedOperations.Remove(workItem);
                    }
                }
            }

            foreach (DispatcherWorkItem workItem in failedWork)
            {
                workItem.FailBeforeStart(exception);
            }
        }
    }

    private void Flush()
    {
        DispatcherWorkItem[] batch;
        lock (_lock)
        {
            _flushScheduled = false;
            if (!_admissionOpen)
            {
                return;
            }

            batch = [.. _queue];
            _queue.Clear();
        }

        foreach (DispatcherWorkItem workItem in batch)
        {
            _beforeWorkItemStart?.Invoke();
            StartWorkItem(workItem);
        }
    }

    private void StartWorkItem(DispatcherWorkItem workItem)
    {
        lock (_startCloseLock)
        {
            bool admissionOpen;
            lock (_lock)
            {
                admissionOpen = _admissionOpen;
            }

            if (!admissionOpen)
            {
                workItem.CancelBeforeStart(_lifetime.Token);
                return;
            }

            workItem.Start();
        }
    }

    private void StopTrackingAcceptedOperation(DispatcherWorkItem workItem)
    {
        lock (_lock)
        {
            _ = _acceptedOperations.Remove(workItem);
        }
    }

    private sealed class DispatcherWorkItem
    {
        private const int QueuedState = 0;
        private const int StartedState = 1;
        private const int SettledState = 2;

        private readonly GodotMainThreadDispatcher _owner;
        private readonly Action? _syncAction;
        private readonly Func<CancellationToken, ValueTask>? _asyncAction;
        private readonly CancellationToken _callerToken;
        private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();

        private readonly CancellationTokenRegistration _callerCancellationRegistration;
        private CancellationTokenSource? _linkedCancellationSource;
        private int _state;

        public DispatcherWorkItem(GodotMainThreadDispatcher owner, Action action, CancellationToken callerToken)
        {
            _owner = owner;
            _syncAction = action;
            _callerToken = callerToken;
            _callerCancellationRegistration = callerToken.UnsafeRegister(
                static state => ((DispatcherWorkItem)state!).CancelBeforeStart(((DispatcherWorkItem)state!)._callerToken), this);
            if (IsSettled)
            {
                _callerCancellationRegistration.Dispose();
            }
        }

        public DispatcherWorkItem(
            GodotMainThreadDispatcher owner,
            Func<CancellationToken, ValueTask> action,
            CancellationToken callerToken)
        {
            _owner = owner;
            _asyncAction = action;
            _callerToken = callerToken;
            _callerCancellationRegistration = callerToken.UnsafeRegister(
                static state => ((DispatcherWorkItem)state!).CancelBeforeStart(((DispatcherWorkItem)state!)._callerToken), this);
            if (IsSettled)
            {
                _callerCancellationRegistration.Dispose();
            }
        }

        public ValueTask Completion => new(_completionSource.Task);

        public bool IsSettled => Volatile.Read(ref _state) == SettledState;

        public void Start()
        {
            ValueTask invocation = default;
            CancellationToken invocationToken = default;
            Exception? exception = null;
            bool asynchronous = false;
            Action? synchronousAction = null;
            Func<CancellationToken, ValueTask>? asynchronousAction = null;

            lock (_lock)
            {
                if (Interlocked.CompareExchange(ref _state, StartedState, QueuedState) != QueuedState)
                {
                    return;
                }

                _callerCancellationRegistration.Dispose();
                if (_syncAction is not null)
                {
                    synchronousAction = _syncAction;
                }
                else
                {
                    _linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                        _callerToken,
                        _owner._lifetime.Token);
                    invocationToken = _linkedCancellationSource.Token;
                    asynchronousAction = _asyncAction;
                    asynchronous = true;
                }
            }

            try
            {
                if (synchronousAction is not null)
                {
                    synchronousAction();
                }
                else
                {
                    invocation = asynchronousAction!(invocationToken);
                }
            }
            catch (Exception caughtException)
            {
                exception = caughtException;
            }

            if (exception is not null)
            {
                CompleteWithException(exception, invocationToken);
            }
            else if (!asynchronous)
            {
                CompleteSuccessfully();
            }
            else if (invocation.IsCompleted)
            {
                CompleteCompletedInvocation(invocation, invocationToken);
            }
            else
            {
                _ = AwaitAsynchronousAction(invocation, invocationToken);
            }
        }

        public void CancelBeforeStart(CancellationToken cancellationToken) => SettleBeforeStart(cancellationToken, null);

        public void FailBeforeStart(Exception exception) => SettleBeforeStart(default, exception);

        public void CancelForShutdown(CancellationToken cancellationToken)
        {
            CancellationTokenSource? linkedCancellationSource;
            lock (_lock)
            {
                if (Interlocked.Exchange(ref _state, SettledState) == SettledState)
                {
                    return;
                }

                linkedCancellationSource = _linkedCancellationSource;
                _linkedCancellationSource = null;
                _callerCancellationRegistration.Dispose();
            }

            linkedCancellationSource?.Cancel();
            linkedCancellationSource?.Dispose();
            _ = _completionSource.TrySetCanceled(cancellationToken);
        }

        private void SettleBeforeStart(CancellationToken cancellationToken, Exception? exception)
        {
            lock (_lock)
            {
                if (Interlocked.CompareExchange(ref _state, SettledState, QueuedState) != QueuedState)
                {
                    return;
                }

                _callerCancellationRegistration.Dispose();
            }

            _owner.StopTrackingAcceptedOperation(this);
            _ = exception is null
                ? _completionSource.TrySetCanceled(cancellationToken)
                : _completionSource.TrySetException(exception);
        }

        private void CompleteCompletedInvocation(ValueTask invocation, CancellationToken invocationToken)
        {
            try
            {
                invocation.GetAwaiter().GetResult();
                CompleteSuccessfully();
            }
            catch (Exception exception)
            {
                CompleteWithException(exception, invocationToken);
            }
        }

        private async Task AwaitAsynchronousAction(ValueTask invocation, CancellationToken invocationToken)
        {
            try
            {
                await invocation.ConfigureAwait(false);
                CompleteSuccessfully();
            }
            catch (Exception exception)
            {
                CompleteWithException(exception, invocationToken);
            }
        }

        private void CompleteSuccessfully()
        {
            if (Interlocked.CompareExchange(ref _state, SettledState, StartedState) != StartedState)
            {
                return;
            }

            ReleaseAfterInvocation();
            _ = _completionSource.TrySetResult();
        }

        private void CompleteWithException(Exception exception, CancellationToken invocationToken)
        {
            if (Interlocked.CompareExchange(ref _state, SettledState, StartedState) != StartedState)
            {
                return;
            }

            ReleaseAfterInvocation();
            _ = exception is OperationCanceledException && invocationToken.IsCancellationRequested
                ? _completionSource.TrySetCanceled(invocationToken)
                : _completionSource.TrySetException(exception);
        }

        private void ReleaseAfterInvocation()
        {
            _owner.StopTrackingAcceptedOperation(this);
            lock (_lock)
            {
                Interlocked.Exchange(ref _linkedCancellationSource, null)?.Dispose();
            }
        }
    }
}
