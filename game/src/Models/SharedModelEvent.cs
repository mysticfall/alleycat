namespace AlleyCat.Models;

/// <summary>Kinds of load and execution events raised by the shared model layer.</summary>
public enum SharedModelEventKind
{
    /// <summary>The single load of a key started.</summary>
    LoadStarted,

    /// <summary>The load of a key succeeded.</summary>
    LoadSucceeded,

    /// <summary>The load of a key failed; the failure is cached for the session.</summary>
    LoadFailed,

    /// <summary>A work item was accepted into the queue.</summary>
    ItemQueued,

    /// <summary>A work item started running against the backend.</summary>
    ItemStarted,

    /// <summary>A work item settled without running; see <see cref="SharedModelEvent.DropReason" />.</summary>
    ItemDropped,

    /// <summary>A started work item finished; see <see cref="SharedModelEvent.Outcome" />.</summary>
    ItemCompleted,

    /// <summary>The backend was disposed at shutdown.</summary>
    BackendDisposed
}

/// <summary>Why a work item settled without running.</summary>
public enum ModelWorkDropReason
{
    /// <summary>Not applicable.</summary>
    None,

    /// <summary>The item's cancellation token was signalled before it started.</summary>
    Cancelled,

    /// <summary>A newer item from the same consumer with an equal supersession key replaced it.</summary>
    Superseded,

    /// <summary>The scheduler stopped admission at shutdown.</summary>
    Shutdown,

    /// <summary>The backend failed to load; the item faults with the cached load failure.</summary>
    LoadFailed
}

/// <summary>How a started work item finished.</summary>
public enum ModelWorkOutcome
{
    /// <summary>Not applicable.</summary>
    None,

    /// <summary>The result was delivered to the consumer.</summary>
    Succeeded,

    /// <summary>The execution failed and only this item faulted.</summary>
    Faulted,

    /// <summary>The item was cancelled, superseded or shut down while running; its result was discarded.</summary>
    Discarded
}

/// <summary>One load or execution event, observed through <see cref="ISharedModelObserver" />.</summary>
/// <param name="Kind">Event kind.</param>
/// <param name="Key">Model key of the backend.</param>
/// <param name="Item">Work item for item events; otherwise <see langword="null" />.</param>
/// <param name="Error">Load or execution failure, when any.</param>
/// <param name="DropReason">Reason for <see cref="SharedModelEventKind.ItemDropped" />.</param>
/// <param name="Outcome">Outcome for <see cref="SharedModelEventKind.ItemCompleted" />.</param>
public sealed record SharedModelEvent(
    SharedModelEventKind Kind,
    ModelKey Key,
    ModelWorkItem? Item = null,
    Exception? Error = null,
    ModelWorkDropReason DropReason = ModelWorkDropReason.None,
    ModelWorkOutcome Outcome = ModelWorkOutcome.None);

/// <summary>
/// Diagnostic seam for load and execution events. Observers are invoked synchronously under the owning scheduler's
/// lock, so they must be quick and must not call back into the shared model layer. Observer failures are logged and
/// never change scheduling.
/// </summary>
public interface ISharedModelObserver
{
    /// <summary>Receives one event.</summary>
    /// <param name="modelEvent">The event.</param>
    void OnEvent(SharedModelEvent modelEvent);
}
