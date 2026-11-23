namespace AlleyCat.Models;

/// <summary>
/// Model-specific description of a shared backend: its key, how to load it, and its declared scheduling limits.
/// The base layer treats the backend as an opaque disposable resource and never assumes in-process execution.
/// </summary>
/// <typeparam name="TBackend">Opaque backend type that work items run against.</typeparam>
public abstract class ModelDefinition<TBackend> where TBackend : class, IDisposable
{
    /// <summary>Key that decides which consumers share the backend.</summary>
    public abstract ModelKey Key
    {
        get;
    }

    /// <summary>
    /// Maximum number of work items that may run against the backend at once. The default is 1; declare a higher
    /// limit only where the backend's thread-safety for concurrent calls has been verified.
    /// </summary>
    public virtual int MaxConcurrency => 1;

    /// <summary>
    /// Loads the backend. The registry calls this at most once per key and always off the caller's thread, so a
    /// synchronous native load is permitted. The token is cancelled at registry shutdown.
    /// </summary>
    /// <param name="cancellationToken">Signalled when the registry shuts down.</param>
    /// <returns>The loaded backend.</returns>
    public abstract Task<TBackend> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the scheduling policy for this backend's scheduler. The default is fair round-robin across consumers
    /// with FIFO order within one consumer.
    /// </summary>
    /// <returns>A new policy instance owned by one scheduler.</returns>
    public virtual IModelSchedulingPolicy CreateSchedulingPolicy() => new RoundRobinSchedulingPolicy();
}
