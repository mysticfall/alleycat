namespace AlleyCat.Models;

/// <summary>
/// Reusable base for a per-consumer handle over a shared backend. A model-specific handle derives from it, implements
/// the consumer's existing interface, keeps per-consumer state, and routes backend calls through <see cref="Submit{TResult}" />.
/// Disposing the handle cancels its queued items and discards its in-flight results without waiting for backend
/// work; it never disposes or unloads the shared backend.
/// </summary>
/// <typeparam name="TBackend">Opaque backend type.</typeparam>
public abstract class SharedModelHandle<TBackend> : IDisposable where TBackend : class, IDisposable
{
    // Deliberately never disposed: scheduler registrations may still reference its token after handle disposal,
    // and a timer-free source holds no unmanaged resources.
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    /// <summary>Creates a handle for one consumer.</summary>
    /// <param name="model">Shared backend resolved from a registry.</param>
    /// <param name="consumerName">Diagnostic consumer name.</param>
    protected SharedModelHandle(ISharedModel<TBackend> model, string consumerName)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        Consumer = new ModelConsumerId(consumerName);
    }

    /// <summary>Identity under which this handle's items are scheduled.</summary>
    public ModelConsumerId Consumer
    {
        get;
    }

    /// <summary>Whether the handle has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Shared backend this handle routes through.</summary>
    protected ISharedModel<TBackend> Model
    {
        get;
    }

    /// <summary>Signalled when the handle is disposed.</summary>
    protected CancellationToken Lifetime => _lifetime.Token;

    /// <summary>Submits a synchronous backend call for this consumer. See <see cref="ISharedModel{TBackend}.Submit{TResult}" />.</summary>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="execute">Backend call, given only per-call inputs; it must not touch Godot or scene nodes.</param>
    /// <param name="supersessionKey">Optional latest-wins key within this consumer.</param>
    /// <param name="cancellationToken">Per-call cancellation, combined with the handle's lifetime.</param>
    /// <param name="metadata">Optional metadata for scheduling policies.</param>
    /// <returns>The item's result.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the handle is disposed.</exception>
    protected Task<TResult> Submit<TResult>(
        Func<TBackend, TResult> execute,
        object? supersessionKey = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return Route(item => Model.Submit(item, execute), supersessionKey, cancellationToken, metadata);
    }

    /// <summary>Submits an asynchronous backend call for this consumer. See <see cref="ISharedModel{TBackend}.SubmitAsync{TResult}" />.</summary>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="execute">Backend call, given only per-call inputs; it must not touch Godot or scene nodes.</param>
    /// <param name="supersessionKey">Optional latest-wins key within this consumer.</param>
    /// <param name="cancellationToken">Per-call cancellation, combined with the handle's lifetime.</param>
    /// <param name="metadata">Optional metadata for scheduling policies.</param>
    /// <returns>The item's result.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the handle is disposed.</exception>
    protected Task<TResult> SubmitAsync<TResult>(
        Func<TBackend, Task<TResult>> execute,
        object? supersessionKey = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return Route(item => Model.SubmitAsync(item, execute), supersessionKey, cancellationToken, metadata);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the handle. The base cancels this consumer's queued items and discards its in-flight results without
    /// blocking. Overrides release per-consumer state and must call the base implementation.
    /// </summary>
    /// <param name="disposing">Whether the call comes from <see cref="Dispose()" />.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
    }

    private Task<TResult> Route<TResult>(
        Func<ModelWorkItem, Task<TResult>> submit,
        object? supersessionKey,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (!cancellationToken.CanBeCanceled)
        {
            return submit(CreateItem(_lifetime.Token, supersessionKey, metadata));
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        Task<TResult> result = submit(CreateItem(linked.Token, supersessionKey, metadata));
        // The scheduler releases its registration before settling, so the linked source can go with the item.
        _ = result.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            linked,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return result;
    }

    private ModelWorkItem CreateItem(
        CancellationToken token,
        object? supersessionKey,
        IReadOnlyDictionary<string, object?>? metadata) =>
        new(Consumer)
        {
            SupersessionKey = supersessionKey,
            CancellationToken = token,
            Metadata = metadata ?? ModelWorkItem.EmptyMetadata,
        };
}
