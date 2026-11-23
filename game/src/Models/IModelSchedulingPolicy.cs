namespace AlleyCat.Models;

/// <summary>
/// Chooses the next queued work item whenever a concurrency slot of one scheduler is free. Each scheduler owns its
/// policy instance and calls it serially under the scheduler's lock, so implementations need no synchronisation but
/// must not call back into the scheduler.
/// </summary>
public interface IModelSchedulingPolicy
{
    /// <summary>Selects the next item to start.</summary>
    /// <param name="pending">Queued, non-empty items in submission order. The list is only valid during the call.</param>
    /// <returns>Index into <paramref name="pending" /> of the item to start.</returns>
    int SelectNext(IReadOnlyList<ModelWorkItem> pending);
}
