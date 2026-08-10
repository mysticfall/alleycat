namespace AlleyCat.Core.Threading;

/// <summary>
/// Starts deferred work on Godot's main thread and exposes its eventual outcome.
/// </summary>
public interface IMainThreadDispatcher
{
    /// <summary>
    /// Queues a synchronous invocation for deferred execution on the main thread.
    /// </summary>
    /// <param name="action">The invocation to run.</param>
    /// <param name="cancellationToken">Cancellation that may prevent a queued invocation from starting.</param>
    /// <returns>An awaitable representing the invocation.</returns>
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues an asynchronous invocation to start on the main thread.
    /// </summary>
    /// <param name="action">The invocation to start with caller and dispatcher lifetime cancellation.</param>
    /// <param name="cancellationToken">Cancellation that may prevent a queued invocation from starting.</param>
    /// <returns>An awaitable representing the invocation.</returns>
    ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default);
}
