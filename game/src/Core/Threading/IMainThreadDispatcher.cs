namespace AlleyCat.Core.Threading;

/// <summary>
/// Starts work on Godot's main thread and exposes its eventual outcome.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must defer work submitted away from the main thread through the host's main event dispatch.
/// When the caller is already on the main thread, implementations may instead execute the invocation inline
/// (synchronously) with submission and return an already-completed awaitable, bypassing only the deferred queue
/// hop.
/// </para>
/// <para>
/// The inline fast path must not weaken any other guarantee: admission, cancellation, and shutdown semantics are
/// identical on both paths, <see cref="InvokeAsync(Action, CancellationToken)" /> exceptions still surface through
/// the returned awaitable rather than throwing synchronously, and submissions that must be queued keep their
/// relative FIFO order.
/// </para>
/// </remarks>
public interface IMainThreadDispatcher
{
    /// <summary>
    /// Starts a synchronous invocation on the main thread, inline when the caller is already there and deferred
    /// otherwise.
    /// </summary>
    /// <param name="action">The invocation to run.</param>
    /// <param name="cancellationToken">Cancellation that may prevent an accepted invocation from starting.</param>
    /// <returns>
    /// An awaitable representing the invocation, already completed when the invocation ran inline.
    /// </returns>
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an asynchronous invocation on the main thread, inline when the caller is already there and deferred
    /// otherwise.
    /// </summary>
    /// <param name="action">The invocation to start with caller and dispatcher lifetime cancellation.</param>
    /// <param name="cancellationToken">Cancellation that may prevent an accepted invocation from starting.</param>
    /// <returns>
    /// An awaitable representing the invocation. Inline invocations start synchronously, but the awaitable still
    /// completes only when the asynchronous invocation itself completes.
    /// </returns>
    ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default);
}
