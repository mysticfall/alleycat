using Godot;
using LanguageExt;
using Mutex = System.Threading.Mutex;

namespace AlleyCat.Async;

[GlobalClass]
public abstract partial class QueueNode : Node, ITaskQueue
{
    private Que<Request> _queue = Que<Request>.Empty;

    private readonly Mutex _mutex = new();

    protected struct Request
    {
        public required Func<Task<Fin<object?>>> Task { get; init; }
        public required TaskCompletionSource<object?> Completion { get; init; }
    }

    public virtual Eff<T> Enqueue<T>(Eff<T> task)
    {
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var request = new Request
        {
            Task = async () =>
            {
                var fin = await task.RunAsync();
                return fin.Map(v => (object?)v);
            },
            Completion = completion
        };

        _mutex.WaitOne();

        try
        {
            _queue = _queue.Enqueue(request);
        }
        finally
        {
            _mutex.ReleaseMutex();
        }

        return Eff.lift(() => completion.Task).Map(x => (T)x!);
    }

    protected bool Dequeue(Action<Request> callback)
    {
        _mutex.WaitOne();

        try
        {
            var (remaining, task) = _queue.TryDequeue();

            task.IfSome(callback);

            _queue = remaining;

            return task.IsSome;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    protected bool DequeueAll(Action<Seq<Request>> callback)
    {
        _mutex.WaitOne();

        try
        {
            var tasks = _queue.ToSeq();

            _queue = default;

            callback(tasks);

            return !tasks.IsEmpty;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    protected override void Dispose(bool disposing)
    {
        _queue = default;

        base.Dispose(disposing);
    }
}