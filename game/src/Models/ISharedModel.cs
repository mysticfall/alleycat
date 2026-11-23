namespace AlleyCat.Models;

/// <summary>
/// One shared backend with its scheduler. Consumers normally reach it through a <see cref="SharedModelHandle{TBackend}" />.
/// </summary>
/// <typeparam name="TBackend">Opaque backend type.</typeparam>
public interface ISharedModel<TBackend> where TBackend : class, IDisposable
{
    /// <summary>Key of the backend.</summary>
    ModelKey Key
    {
        get;
    }

    /// <summary>
    /// Completes when the backend has loaded, or faults with the cached load failure. Never wait for this
    /// synchronously on the scene thread.
    /// </summary>
    Task Loaded
    {
        get;
    }

    /// <summary>
    /// Queues a synchronous backend call. Thread-safe. The call runs off the submitting thread; the returned task
    /// settles asynchronously with the result, the execution failure, the cached load failure, or cancellation when
    /// the item is cancelled, superseded, or stopped by shutdown.
    /// </summary>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="item">Scheduling envelope.</param>
    /// <param name="execute">Backend call. It must not access Godot APIs or scene nodes.</param>
    /// <returns>The item's result.</returns>
    Task<TResult> Submit<TResult>(ModelWorkItem item, Func<TBackend, TResult> execute);

    /// <summary>
    /// Queues an asynchronous backend call, for backends that are not in-process. Semantics match
    /// <see cref="Submit{TResult}" />; the call holds its concurrency slot until the returned task completes.
    /// </summary>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="item">Scheduling envelope.</param>
    /// <param name="execute">Backend call. It must not access Godot APIs or scene nodes.</param>
    /// <returns>The item's result.</returns>
    Task<TResult> SubmitAsync<TResult>(ModelWorkItem item, Func<TBackend, Task<TResult>> execute);
}
