using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlleyCat.Models;

/// <summary>
/// Log-only diagnostics plus the observer seam. Neither path can change scheduling: failures are contained here.
/// </summary>
/// <param name="loggerResolver">Late logger resolution, retried until it yields a logger.</param>
/// <param name="observer">Optional event observer.</param>
internal sealed class SharedModelDiagnostics(Func<ILogger?>? loggerResolver, ISharedModelObserver? observer)
{
    private ILogger? _logger;

    internal ILogger GetLogger()
    {
        if (_logger is not null || loggerResolver is null)
        {
            return _logger ?? NullLogger.Instance;
        }

        try
        {
            _logger = loggerResolver();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            // The owner's logging infrastructure is not available yet or any more; stay silent for this event.
        }

        return _logger ?? NullLogger.Instance;
    }

    internal void Raise(
        SharedModelEventKind kind,
        ModelKey key,
        ModelWorkItem? item = null,
        Exception? error = null,
        ModelWorkDropReason dropReason = ModelWorkDropReason.None,
        ModelWorkOutcome outcome = ModelWorkOutcome.None)
    {
        Log(kind, key, item, error, dropReason, outcome);

        if (observer is null)
        {
            return;
        }

        try
        {
            observer.OnEvent(new SharedModelEvent(kind, key, item, error, dropReason, outcome));
        }
        catch (Exception observerError)
        {
            GetLogger().LogError(observerError, "Shared model observer failed on {EventKind} for {ModelKey}", kind, key);
        }
    }

    private void Log(
        SharedModelEventKind kind,
        ModelKey key,
        ModelWorkItem? item,
        Exception? error,
        ModelWorkDropReason dropReason,
        ModelWorkOutcome outcome)
    {
        ILogger logger = GetLogger();
        switch (kind)
        {
            case SharedModelEventKind.LoadStarted:
                logger.LogInformation("Shared model load started for {ModelKey}", key);
                break;
            case SharedModelEventKind.LoadSucceeded:
                logger.LogInformation("Shared model loaded for {ModelKey}", key);
                break;
            case SharedModelEventKind.LoadFailed:
                logger.LogError(error, "Shared model load failed for {ModelKey}; the failure is cached for this session", key);
                break;
            case SharedModelEventKind.BackendDisposed:
                logger.LogInformation("Shared model backend disposed for {ModelKey}", key);
                break;
            case SharedModelEventKind.ItemCompleted when outcome == ModelWorkOutcome.Faulted:
                logger.LogDebug(error, "Shared model item {WorkItem} faulted on {ModelKey}", item, key);
                break;
            case SharedModelEventKind.ItemDropped:
                logger.LogDebug("Shared model item {WorkItem} dropped on {ModelKey}: {DropReason}", item, key, dropReason);
                break;
            case SharedModelEventKind.ItemQueued:
            case SharedModelEventKind.ItemStarted:
            case SharedModelEventKind.ItemCompleted:
            default:
                logger.LogTrace("Shared model item {WorkItem} {EventKind} on {ModelKey} ({Outcome})", item, kind, key, outcome);
                break;
        }
    }
}
