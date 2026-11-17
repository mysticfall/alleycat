using System.Threading.Channels;
using Godot;

namespace AlleyCat.Motion;

/// <summary>
/// Owns a session off the scene thread. Supersession discards work; it does not interrupt a native CUDA call.
/// Dispose signals shutdown without waiting. Completion includes disposal after the last native call returns.
/// </summary>
public sealed class SomaInferenceWorker : IDisposable
{
    /// <summary>Worker-exclusive native session boundary, also used for deterministic ownership tests.</summary>
    public interface ISession : IDisposable
    {
        /// <summary>Generates poses without accessing scene nodes.</summary>
        Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target);
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

    private sealed record Request(long ID, Transform3D[][] Context, Transform3D[][] Target);
    private readonly Lock _gate = new();
    private readonly Channel<Request> _requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false
    });
    private long _version;
    private bool _stopped;
    private bool _hasRequest;
    private Result? _result;
    private Exception? _loadError;
    private int _discardedResults;
    private readonly Action<Result>? _returned;
    /// <summary>Completed native results discarded after cancellation, replacement or shutdown.</summary>
    public int DiscardedResults => Volatile.Read(ref _discardedResults);

    /// <summary>Completes after native session disposal; never synchronously wait for this on the scene thread.</summary>
    public Task Completion
    {
        get;
    }

    /// <summary>The factory, generation and disposal all execute on the worker, never the caller.</summary>
    public SomaInferenceWorker(Func<ISession> factory, Action<Result>? returned = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _returned = returned;
        Completion = Task.Run(() => RunAsync(factory));
    }

    /// <summary>Copies input ownership and supersedes queued/in-flight results. Returns the new request identity.</summary>
    public long Submit(Transform3D[][] context, Transform3D[][] target)
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
                _ = _requests.Writer.TryWrite(new Request(id, ownedContext, ownedTarget));
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

    /// <summary>Nonblocking shutdown. Native resources remain owned until native work finishes.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            _hasRequest = false;
            ++_version;
            _result = null;
            _ = _requests.Writer.TryComplete();
        }
    }

    private async Task RunAsync(Func<ISession> factory)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
        }
        ISession session;
        try
        {
            session = factory();
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _loadError = error;
                if (!_stopped && _hasRequest)
                {
                    _result = new Result(_version, null, error);
                }
                _ = _requests.Writer.TryComplete();
            }
            while (_requests.Reader.TryRead(out _))
            {
            }
            return;
        }

        using (session)
        {
            await foreach (Request request in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_stopped)
                    {
                        return;
                    }
                    if (request.ID != _version)
                    {
                        continue;
                    }
                }
                Result result;
                try
                {
                    result = new Result(request.ID, session.Generate(request.Context, request.Target), null);
                }
                catch (Exception error)
                {
                    result = new Result(request.ID, null, error);
                }
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
                    }
                    else
                    {
                        _ = Interlocked.Increment(ref _discardedResults);
                    }
                }
            }
        }
    }

    private static Transform3D[][] Copy(Transform3D[][] poses)
    {
        ArgumentNullException.ThrowIfNull(poses);
        return poses.Length != 4 || poses.Any(p => p is null || p.Length == 0)
            ? throw new ArgumentException("Four non-empty poses are required.", nameof(poses))
            : [.. poses.Select(p => (Transform3D[])p.Clone())];
    }
}
