using AlleyCat.Models;

namespace AlleyCat.Motion;

/// <summary>
/// Application-owned MotionBricks handles, retained beyond scene removal until their per-consumer sessions are
/// released. The shared native sessions are owned by the shared model registry, which disposes them at the same
/// application-shutdown boundary; this owner never disposes a backend.
/// </summary>
internal sealed class MotionBricksWorkers
{
    private readonly Lock _gate = new();
    private readonly List<SomaInferenceWorker> _workers = [];
    private Task? _completion;

    internal bool IsStopping
    {
        get; private set;
    }

    /// <summary>Creates a handle over a shared native session.</summary>
    internal SomaInferenceWorker Create(
        ISharedModel<SomaNativeSession> model,
        Func<SomaNativeSession, SomaInferenceWorker.ISession> factory,
        Action<SomaInferenceWorker.Result>? returned = null,
        Action<string>? nativeObservation = null,
        string consumerName = nameof(SomaInferenceWorker)) =>
        Add(() => new SomaInferenceWorker(model, factory, returned, nativeObservation, consumerName));

    /// <summary>Creates a standalone handle whose session needs no native model, such as a deterministic test session.</summary>
    internal SomaInferenceWorker Create(Func<SomaInferenceWorker.ISession> factory, Action<SomaInferenceWorker.Result>? returned = null) =>
        Add(() => new SomaInferenceWorker(factory, returned));

    /// <summary>
    /// Stops admission and releases every retained handle without blocking. The returned task completes once every
    /// handle's last call has returned and its per-consumer session is released.
    /// </summary>
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

    private SomaInferenceWorker Add(Func<SomaInferenceWorker> create)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsStopping, this);
            _ = _workers.RemoveAll(worker => worker.Completion.IsCompletedSuccessfully);
            SomaInferenceWorker worker = create();
            _workers.Add(worker);
            return worker;
        }
    }
}
