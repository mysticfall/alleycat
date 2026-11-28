using Microsoft.Extensions.Logging;

namespace AlleyCat.Models;

/// <summary>
/// Owner-scoped registry of shared backends, one scheduler per key. <c>Game</c> owns one instance for its autoload
/// lifetime; tests and standalone fixtures may construct their own and inject it into consumers.
/// </summary>
/// <param name="loggerResolver">
/// Resolves the diagnostics logger on first use, so an owner can create the registry before its logging
/// infrastructure is built. Resolution is retried until it yields a logger.
/// </param>
/// <param name="observer">Optional load and execution event observer.</param>
public sealed class SharedModelRegistry(Func<ILogger?>? loggerResolver = null, ISharedModelObserver? observer = null)
    : ISharedModelRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ModelKey, object> _models = [];
    private readonly List<Func<Task>> _stops = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SharedModelDiagnostics _diagnostics = new(loggerResolver, observer);
    private Task? _completion;

    /// <summary>Whether shutdown has started.</summary>
    public bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _completion is not null;
            }
        }
    }

    /// <inheritdoc />
    public ISharedModel<TBackend> Resolve<TBackend>(ModelDefinition<TBackend> definition)
        where TBackend : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(definition);
        ModelKey key = definition.Key ?? throw new ArgumentException("A model definition must provide a key.", nameof(definition));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_completion is not null, this);
            if (_models.TryGetValue(key, out object? existing))
            {
                return existing as ModelScheduler<TBackend>
                    ?? throw new InvalidOperationException(
                        $"Model key '{key}' is already bound to a different backend type than '{typeof(TBackend).FullName}'.");
            }

            var scheduler = new ModelScheduler<TBackend>(
                key,
                definition.MaxConcurrency,
                definition.CreateSchedulingPolicy(),
                _diagnostics);
            _models.Add(key, scheduler);
            _stops.Add(scheduler.Stop);
            scheduler.RaiseLoadStarted();
            CancellationToken shutdownToken = _shutdown.Token;
            _ = Task.Run(() => LoadAsync(definition, scheduler, shutdownToken));
            return scheduler;
        }
    }

    /// <summary>
    /// Stops the registry at the end of its owner's lifetime (for <c>Game</c>, the application-shutdown boundary or tree
    /// exit, whichever comes first): stops admission on every scheduler, settles queued items as cancelled, signals
    /// in-progress loads, and disposes each backend only after its load and in-flight calls return. Never blocks;
    /// await the returned aggregate completion asynchronously. Idempotent: later calls return the same completion.
    /// </summary>
    /// <returns>Aggregate completion of every backend's disposal.</returns>
    public Task Stop()
    {
        lock (_gate)
        {
            if (_completion is not null)
            {
                return _completion;
            }

            var stops = new Task[_stops.Count];
            for (int i = 0; i < stops.Length; i++)
            {
                stops[i] = _stops[i]();
            }

            _completion = Task.WhenAll(stops);
        }

        // Cancellation callbacks run on the thread pool, never on the caller's (scene) thread.
        _ = _shutdown.CancelAsync();
        return _completion;
    }

    private static async Task LoadAsync<TBackend>(
        ModelDefinition<TBackend> definition,
        ModelScheduler<TBackend> scheduler,
        CancellationToken shutdownToken)
        where TBackend : class, IDisposable
    {
        TBackend backend;
        try
        {
            backend = await definition.LoadAsync(shutdownToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Loading model '{definition.Key}' produced no backend.");
        }
        catch (Exception error)
        {
            scheduler.OnLoadFailed(error);
            return;
        }

        scheduler.OnLoaded(backend);
    }
}
