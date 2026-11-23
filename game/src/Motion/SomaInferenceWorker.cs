using AlleyCat.Models;
using Godot;

namespace AlleyCat.Motion;

/// <summary>
/// Per-consumer MotionBricks handle over a shared <see cref="SomaNativeSession" /> (CORE-011). It owns this consumer's
/// <see cref="ISession" /> (codec state), request ownership and latest-wins supersession; the registry owns the native
/// session's loading and disposal. Supersession discards work; it never interrupts a native CUDA call. Dispose signals
/// release without waiting. Completion includes per-consumer session disposal after this consumer's last call returns.
/// </summary>
public sealed class SomaInferenceWorker : SharedModelHandle<SomaNativeSession>
{
    /// <summary>
    /// Per-consumer generation boundary, also used for deterministic ownership tests. Production sessions are codecs
    /// over the shared native session; they never own or dispose it.
    /// </summary>
    public interface ISession : IDisposable
    {
        /// <summary>Generates poses without accessing scene nodes.</summary>
        Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target);
        /// <summary>Sessions with decoded-pose endpoint correction may use a distinct presentation start.
        /// The model's conditioning poses are unchanged.</summary>
        Transform3D[][] GeneratePresented(Transform3D[][] context, Transform3D[][] target, Transform3D[] start)
            => Generate(context, target);
    }

    /// <summary>Only the current request may be consumed; errors are delivered, never silently retried.</summary>
    public sealed record Result(long Request, Transform3D[][]? Poses, Exception? Error)
    {
        /// <summary>Monotonic publication clock, diagnostic only; not native GPU completion.</summary>
        public long PublishedAt
        {
            get; init;
        }
    }

    private sealed class Request(long id, Transform3D[][] context, Transform3D[][] target, Transform3D[]? presentationStart)
    {
        internal long ID { get; } = id;
        internal Transform3D[][] Context { get; } = context;
        internal Transform3D[][] Target { get; } = target;
        internal Transform3D[]? PresentationStart { get; } = presentationStart;
        internal int Started;
    }

    private const int NotStarted = 0, Running = 1, StaleTraced = 2;
    // One constant key: every submission replaces this consumer's queued request and discards its running result.
    private static readonly object _generationSlot = new();
    private readonly Lock _gate = new();
    private readonly Func<SomaNativeSession, ISession> _factory;
    private readonly Action<Result>? _returned;
    private readonly Action<string>? _nativeObservation;
    private readonly SharedModelRegistry? _ownedRegistry;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;
    private bool _stopped;
    private bool _hasRequest;
    private bool _terminal;
    private int _outstanding;
    private Result? _result;
    private Exception? _loadError;
    private ISession? _session;
    private CancellationTokenSource? _requestCancellation;
    private int _discardedResults;
    private int _loadReported;
    internal Action<long, string>? TraceWorkerEvent
    {
        private get; set;
    }
    /// <summary>Completed native results discarded after cancellation, replacement or shutdown.</summary>
    public int DiscardedResults => Volatile.Read(ref _discardedResults);

    /// <summary>
    /// Completes after this consumer's session is released, once its last call has returned, or after a terminal
    /// load failure. Never synchronously wait for this on the scene thread.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Creates a handle over a shared backend. The session factory runs once, off the caller's thread, after the shared
    /// load; its failure is this handle's failure and is never cached under the model key.
    /// </summary>
    /// <param name="model">Shared backend resolved from a registry.</param>
    /// <param name="factory">Builds this consumer's session over the shared backend; must not access scene nodes.</param>
    /// <param name="returned">Optional value-only observer of every completed result, including discarded ones.</param>
    /// <param name="nativeObservation">Optional passive observer of the shared load phases seen by this consumer.</param>
    /// <param name="consumerName">Diagnostic consumer name.</param>
    public SomaInferenceWorker(
        ISharedModel<SomaNativeSession> model,
        Func<SomaNativeSession, ISession> factory,
        Action<Result>? returned = null,
        Action<string>? nativeObservation = null,
        string consumerName = nameof(SomaInferenceWorker))
        : this(model, factory, returned, nativeObservation, consumerName, null)
    {
    }

    /// <summary>
    /// Creates a standalone handle whose session needs no native model (deterministic tests and fixtures). The factory,
    /// generation and disposal all execute off the caller's thread, with the same ownership contract.
    /// </summary>
    /// <param name="factory">Builds the session; must not access scene nodes.</param>
    /// <param name="returned">Optional value-only observer of every completed result, including discarded ones.</param>
    public SomaInferenceWorker(Func<ISession> factory, Action<Result>? returned = null)
        : this(new DetachedModel(), factory ?? throw new ArgumentNullException(nameof(factory)), returned)
    {
    }

    private SomaInferenceWorker(DetachedModel detached, Func<ISession> factory, Action<Result>? returned)
        : this(detached.Model, _ => factory(), returned, null, "detached-" + nameof(SomaInferenceWorker), detached.Registry)
    {
    }

    private SomaInferenceWorker(
        ISharedModel<SomaNativeSession> model,
        Func<SomaNativeSession, ISession> factory,
        Action<Result>? returned,
        Action<string>? nativeObservation,
        string consumerName,
        SharedModelRegistry? ownedRegistry)
        : base(model, consumerName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _returned = returned;
        _ownedRegistry = ownedRegistry;
        _nativeObservation = nativeObservation;
        if (nativeObservation is not null)
        {
            // Successful loads report load-exit from this consumer's first item, ahead of its first generation.
            nativeObservation("load-enter");
            _ = model.Loaded.ContinueWith(
                _ => nativeObservation("load-exit"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        lock (_gate)
        {
            _outstanding++;
            Track(Submit(CreateSession), null);
        }
    }

    /// <summary>Copies input ownership and supersedes queued/in-flight results. Returns the new request identity.</summary>
    public long Submit(Transform3D[][] context, Transform3D[][] target, Transform3D[]? presentationStart = null)
    {
        Transform3D[][] ownedContext = Copy(context), ownedTarget = Copy(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            long id = ++_version;
            _hasRequest = true;
            _result = _loadError is null ? null : new Result(id, null, _loadError);
            if (_loadError is null)
            {
                var request = new Request(id, ownedContext, ownedTarget,
                    presentationStart is null ? null : (Transform3D[])presentationStart.Clone());
                _requestCancellation?.Dispose();
                _requestCancellation = new CancellationTokenSource();
                _outstanding++;
                Track(Submit(backend => Generate(backend, request), _generationSlot, _requestCancellation.Token), request);
            }
            return id;
        }
    }

    /// <summary>Invalidates pending results without stopping the session or resetting displayed motion.</summary>
    public void CancelRequest()
    {
        lock (_gate)
        {
            ++_version;
            _hasRequest = false;
            _result = null;
            _requestCancellation?.Cancel();
        }
    }

    /// <summary>Returns at most one current result. Poll on the same scene thread that submits/cancels requests.</summary>
    public bool TryTake(out Result? result)
    {
        lock (_gate)
        {
            result = _result;
            _result = null;
            return result is not null;
        }
    }

    /// <summary>
    /// Nonblocking release. Queued work is dropped; per-consumer resources remain owned until this consumer's in-flight
    /// call returns. The shared native session is never disposed here.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _stopped = true;
                _hasRequest = false;
                ++_version;
                _result = null;
            }
        }
        base.Dispose(disposing);
        if (disposing)
        {
            lock (_gate)
            {
                TryFinishLocked();
            }
        }
    }

    private bool CreateSession(SomaNativeSession backend)
    {
        if (Interlocked.Exchange(ref _loadReported, 1) == 0)
        {
            _nativeObservation?.Invoke("load-exit");
        }
        lock (_gate)
        {
            if (_stopped || _session is not null || _loadError is not null)
            {
                return false;
            }
        }
        ISession session;
        try
        {
            session = _factory(backend);
        }
        catch (Exception error)
        {
            Fail(error);
            return false;
        }
        lock (_gate)
        {
            // Retained even after shutdown so release happens once, after the last call returns.
            _session = session;
        }
        return true;
    }

    private bool Generate(SomaNativeSession backend, Request request)
    {
        ISession? session;
        lock (_gate)
        {
            if (_stopped)
            {
                return false;
            }
            if (request.ID != _version)
            {
                Volatile.Write(ref request.Started, StaleTraced);
                TraceWorkerEvent?.Invoke(request.ID, "stale-before-start");
                return false;
            }
            session = _session;
        }
        if (session is null && CreateSession(backend))
        {
            lock (_gate)
            {
                session = _session;
            }
        }
        if (session is null)
        {
            lock (_gate)
            {
                PublishFailureLocked(request.ID);
            }
            return false;
        }
        _ = Interlocked.Exchange(ref request.Started, Running);
        Result result;
        try
        {
            TraceWorkerEvent?.Invoke(request.ID, "worker-start");
            result = new Result(request.ID, request.PresentationStart is { } start
                ? session.GeneratePresented(request.Context, request.Target, start)
                : session.Generate(request.Context, request.Target), null);
        }
        catch (Exception error)
        {
            result = new Result(request.ID, null, error);
        }
        TraceWorkerEvent?.Invoke(request.ID, "worker-end");
        // Value-only diagnostics copy before publication/reuse, including outputs later discarded.
        // Observer failure must never become a provider failure or change ownership.
        try
        {
            _returned?.Invoke(result);
        }
        catch { /* Diagnostic sinks expose their own failure status. */ }
        lock (_gate)
        {
            if (!_stopped && request.ID == _version)
            {
                _result = result with
                {
                    PublishedAt = System.Diagnostics.Stopwatch.GetTimestamp()
                };
                return true;
            }
            _ = Interlocked.Increment(ref _discardedResults);
            TraceWorkerEvent?.Invoke(request.ID, "late-reject");
            return false;
        }
    }

    // Shared load failures arrive here from the registry's cached failure; session failures from the factory.
    private void Fail(Exception error)
    {
        lock (_gate)
        {
            _loadError ??= error;
            if (!_stopped && _hasRequest)
            {
                _result = new Result(_version, null, _loadError);
            }
        }
    }

    private void PublishFailureLocked(long request)
    {
        if (_loadError is { } error && !_stopped && request == _version)
        {
            _result = new Result(request, null, error);
        }
    }

    private void Track(Task<bool> item, Request? request) =>
        _ = item.ContinueWith(
            task => OnSettled(task, request),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

    private void OnSettled(Task<bool> item, Request? request)
    {
        if (item.IsFaulted)
        {
            // Item bodies capture their own errors, so a fault is the shared backend's cached load failure.
            Fail(item.Exception!.InnerException ?? item.Exception);
        }
        else if (item.IsCanceled && request is not null && Volatile.Read(ref request.Started) == NotStarted)
        {
            TraceWorkerEvent?.Invoke(request.ID, "stale-before-start");
        }
        lock (_gate)
        {
            _outstanding--;
            TryFinishLocked();
        }
    }

    private void TryFinishLocked()
    {
        if (_terminal || _outstanding > 0 || (!_stopped && _loadError is null))
        {
            return;
        }
        _terminal = true;
        ISession? session = _session;
        _session = null;
        _requestCancellation?.Dispose();
        _requestCancellation = null;
        _ = Task.Run(() => FinishAsync(session));
    }

    private async Task FinishAsync(ISession? session)
    {
        try
        {
            session?.Dispose();
            if (_ownedRegistry is not null)
            {
                await _ownedRegistry.Stop().ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            _ = _completion.TrySetException(error);
            return;
        }
        _ = _completion.TrySetResult();
    }

    private static Transform3D[][] Copy(Transform3D[][] poses)
    {
        ArgumentNullException.ThrowIfNull(poses);
        return poses.Length != 4 || poses.Any(p => p is null || p.Length == 0)
            ? throw new ArgumentException("Four non-empty poses are required.", nameof(poses))
            : [.. poses.Select(p => (Transform3D[])p.Clone())];
    }

    // A private registry and native-free backend so standalone sessions keep the same scheduling contract without
    // loading a model or sharing a slot with any other consumer.
    private sealed class DetachedModel
    {
        internal DetachedModel()
        {
            Registry = new SharedModelRegistry();
            Model = Registry.Resolve(new DetachedDefinition());
        }

        internal SharedModelRegistry Registry
        {
            get;
        }

        internal ISharedModel<SomaNativeSession> Model
        {
            get;
        }
    }

    private sealed record DetachedKey : ModelKey;

    private sealed class DetachedDefinition : ModelDefinition<SomaNativeSession>
    {
        public override ModelKey Key { get; } = new DetachedKey();

        public override Task<SomaNativeSession> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SomaNativeSession.CreateDetached());
    }
}
