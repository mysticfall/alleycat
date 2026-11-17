namespace AlleyCat.Motion;

/// <summary>Application-owned workers, retained beyond scene removal until native disposal completes.</summary>
internal sealed class MotionBricksWorkers
{
    private readonly Lock _gate = new();
    private readonly List<SomaInferenceWorker> _workers = [];
    private Task? _completion;

    internal bool IsStopping
    {
        get; private set;
    }

    internal SomaInferenceWorker Create(Func<SomaInferenceWorker.ISession> factory, Action<SomaInferenceWorker.Result>? returned = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsStopping, this);
            _ = _workers.RemoveAll(worker => worker.Completion.IsCompletedSuccessfully);
            var worker = new SomaInferenceWorker(factory, returned);
            _workers.Add(worker);
            return worker;
        }
    }

    internal Task Stop()
    {
        lock (_gate)
        {
            if (_completion is not null)
            {
                return _completion;
            }
            IsStopping = true;
            foreach (SomaInferenceWorker worker in _workers)
            {
                worker.Dispose();
            }
            _completion = Task.WhenAll(_workers.Select(worker => worker.Completion));
            return _completion;
        }
    }
}
