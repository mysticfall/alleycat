using System.Collections.ObjectModel;
using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI;

/// <summary>Background, projection-owning contextual worker attached directly to an <see cref="AgenticMind"/>.</summary>
[GlobalClass]
public abstract partial class ContextWorker : Node
{
    private static readonly EventId _runStartedEvent = new(1, "ContextWorkerRunStarted");
    private static readonly EventId _requestCoalescedEvent = new(2, "ContextWorkerRequestCoalesced");
    private static readonly EventId _projectionPublishedEvent = new(3, "ContextWorkerProjectionPublished");
    private static readonly EventId _followUpScheduledEvent = new(4, "ContextWorkerFollowUpScheduled");
    private static readonly IReadOnlyDictionary<string, object?> _emptyProjection =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    private readonly Lock _runLock = new();
    private IReadOnlyDictionary<string, object?> _projection = _emptyProjection;
    private CancellationTokenSource? _lifetimeCancellation;
    private AgenticMind? _mind;
    private ContextWorkerTrigger? _trigger;
    private bool _runActive;
    private bool _followUpPending;
    private bool _runQueued;
    private int _unavailable;
    private int _exited;

    private ILogger<ContextWorker>? Logger
    {
        get; set;
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        if (GetParent() is AgenticMind mind)
        {
            Attach(mind);
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        ContextWorkerTrigger? trigger = _trigger;
        if (trigger is not null)
        {
            trigger.RunRequested -= RequestRun;
            _trigger = null;
        }

        bool cancelLifetime;
        lock (_runLock)
        {
            cancelLifetime = Interlocked.Exchange(ref _exited, 1) == 0;
        }

        if (cancelLifetime)
        {
            _lifetimeCancellation?.Cancel();
        }
    }

    /// <summary>Gets this worker's most recently published projection for foreground context composition.</summary>
    internal IReadOnlyDictionary<string, object?> GetProjection() => Volatile.Read(ref _projection);

    /// <summary>Builds a projection from the shared published render dictionary.</summary>
    protected abstract Task<IReadOnlyDictionary<string, object?>> RunAsync(
        IReadOnlyDictionary<string, object?> context,
        CancellationToken cancellationToken);

    /// <summary>Gets the owning Mind while this node is attached.</summary>
    protected AgenticMind OwningMind
        => _mind ?? throw new InvalidOperationException("ContextWorker has not been attached to an AgenticMind.");

    /// <summary>Gets cancellation for this worker's Mind and node lifetime.</summary>
    protected CancellationToken LifetimeCancellationToken
        => _lifetimeCancellation?.Token
            ?? throw new InvalidOperationException("ContextWorker has not been attached to an AgenticMind.");

    internal void Attach(AgenticMind mind)
    {
        ArgumentNullException.ThrowIfNull(mind);
        if (_mind is not null && !ReferenceEquals(_mind, mind))
        {
            throw new InvalidOperationException("A ContextWorker can belong to only one AgenticMind.");
        }

        if (_mind is not null)
        {
            return;
        }

        ContextWorkerTrigger[] triggers = [.. GetChildren().OfType<ContextWorkerTrigger>()];
        if (triggers.Length != 1)
        {
            throw new InvalidOperationException("ContextWorker requires exactly one direct ContextWorkerTrigger child.");
        }

        _mind = mind;
        _trigger = triggers[0];
        Logger = GameLoggerResolver.ResolveRequired<ContextWorker>();
        _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(mind.LifetimeCancellationToken);
        _trigger.RunRequested += RequestRun;
        OnAttached(mind);
    }

    internal void RequestRun()
    {
        lock (_runLock)
        {
            if (IsExited || IsUnavailable)
            {
                return;
            }

            if (_runActive)
            {
                if (!_followUpPending)
                {
                    GetLogger().LogDebug(
                        _requestCoalescedEvent,
                        "Context worker {WorkerName} coalesced a refresh request while a run was active.",
                        Name);
                }

                _followUpPending = true;
                return;
            }

            if (_runQueued)
            {
                return;
            }

            _runQueued = true;
        }

        _ = CallDeferred(nameof(BeginRunDeferred));
    }

    private bool IsExited => Volatile.Read(ref _exited) != 0 || _lifetimeCancellation?.IsCancellationRequested == true;

    /// <summary>Prevents all future trigger-driven execution while retaining the last published snapshot.</summary>
    protected void MarkUnavailable() => Interlocked.Exchange(ref _unavailable, 1);

    /// <summary>Indicates whether this worker can accept another execution request.</summary>
    protected bool IsUnavailable => Volatile.Read(ref _unavailable) != 0;

    private void BeginRunDeferred()
    {
        IReadOnlyDictionary<string, object?> context;
        lock (_runLock)
        {
            _runQueued = false;
            if (IsExited || IsUnavailable || _runActive || _mind is null)
            {
                return;
            }

            _runActive = true;
            context = _mind.GetLatestRenderContext();
        }

        GetLogger().LogDebug(
            _runStartedEvent,
            "Context worker {WorkerName} started an accepted refresh run.",
            Name);

        try
        {
            _ = RunAndPublishAsync(context);
        }
        catch (Exception ex)
        {
            CompleteRun(ex);
        }
    }

    /// <summary>Initialises implementation resources once after valid Mind attachment.</summary>
    protected virtual void OnAttached(AgenticMind mind)
    {
    }

    private async Task RunAndPublishAsync(IReadOnlyDictionary<string, object?> context)
    {
        try
        {
            CancellationToken cancellationToken = LifetimeCancellationToken;
            IReadOnlyDictionary<string, object?> projection = await RunAsync(context, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(projection);
            bool published = false;
            lock (_runLock)
            {
                if (!IsExited)
                {
                    _ = Interlocked.Exchange(ref _projection, projection);
                    published = true;
                }
            }

            if (published)
            {
                GetLogger().LogDebug(
                    _projectionPublishedEvent,
                    "Context worker {WorkerName} published a projection containing {KeyCount} keys.",
                    Name,
                    projection.Count);
            }

            CompleteRun(null);
        }
        catch (OperationCanceledException) when (IsExited)
        {
            CompleteRun(null);
        }
        catch (ContextWorkerUnavailableException)
        {
            CompleteRun(null);
        }
        catch (Exception ex)
        {
            CompleteRun(ex);
        }
    }

    private void CompleteRun(Exception? failure)
    {
        bool runFollowUp;
        lock (_runLock)
        {
            _runActive = false;
            runFollowUp = _followUpPending && !IsExited;
            _followUpPending = false;
        }

        if (failure is not null)
        {
            GetLogger().LogError(failure, "Context worker {WorkerName} refresh failed; retaining its prior projection.", Name);
        }

        if (runFollowUp)
        {
            GetLogger().LogDebug(
                _followUpScheduledEvent,
                "Context worker {WorkerName} scheduled its coalesced follow-up refresh.",
                Name);
            RequestRun();
        }
    }

    private ILogger<ContextWorker> GetLogger()
        => Logger ?? throw new InvalidOperationException("ContextWorker logging is unavailable before attachment.");

    /// <summary>Signals that a permanently unavailable worker should settle silently.</summary>
    protected sealed class ContextWorkerUnavailableException : Exception;
}
