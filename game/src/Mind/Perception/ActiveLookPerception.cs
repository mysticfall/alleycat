using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>Reusable, independently owned perception base tracking one active visual cue and subject.</summary>
public abstract partial class ActiveLookPerception : Perception<LookTargetChangedPercept>
{
    private CancellationTokenSource? _activationCts;

    /// <summary>
    /// Gets the cue most recently supplied by a target transition. Published during percept dispatch on Mind's
    /// serial perception drain worker and cleared on the main thread by tree exit. Cross-thread reference reads are
    /// atomic, but the value is not coherent with <see cref="ActiveSubject"/> or <see cref="LatestContext"/>: bound
    /// staleness with <see cref="ActivationToken"/> or <see cref="ActivationGeneration"/> before acting on it.
    /// </summary>
    public VisualCue? ActiveCue
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the nearest subject resolved from the most recent live active cue. Published during percept dispatch on
    /// Mind's serial perception drain worker and cleared on the main thread by tree exit. Cross-thread reference
    /// reads are atomic, but the value is not coherent with <see cref="ActiveCue"/> or <see cref="LatestContext"/>:
    /// bound staleness with <see cref="ActivationToken"/> or <see cref="ActivationGeneration"/> before acting on it.
    /// </summary>
    public IVisualSubject? ActiveSubject
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the most recent percept-dispatch context. Polling and subject-event faculties need a context for
    /// out-of-band work performed outside percept dispatch. Cached during percept dispatch on Mind's serial
    /// perception drain worker and cleared on the main thread by tree exit. Cross-thread reference reads are
    /// atomic, but the value is not coherent with <see cref="ActiveCue"/> or <see cref="ActiveSubject"/>: bound
    /// staleness with <see cref="ActivationToken"/> or <see cref="ActivationGeneration"/> before acting on it.
    /// </summary>
    protected PerceptionContext? LatestContext
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the identity of the current activation; changes whenever a new activation begins or tree exit ends one,
    /// letting derived faculties detect stale asynchronous work.
    /// </summary>
    protected int ActivationGeneration
    {
        get; private set;
    }

    /// <summary>Gets the cancellation governing the current activation, or none when no activation is active.</summary>
    protected CancellationToken ActivationToken => _activationCts?.Token ?? CancellationToken.None;

    /// <inheritdoc />
    public sealed override async ValueTask PerceiveAsync(
        LookTargetChangedPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(percept);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        VisualCue? previousCue = ActiveCue;
        IVisualSubject? previousSubject = ActiveSubject;

        VisualCue? currentCue = percept.Current;
        IVisualSubject? currentSubject = currentCue is not null && IsLiveCue(currentCue)
            ? FindNearestSubject(currentCue)
            : null;

        CancellationToken activationToken = BeginActivation(cancellationToken);

        if (previousSubject is not null)
        {
            OnActiveSubjectDetached(previousSubject);
        }

        ActiveCue = currentCue;
        ActiveSubject = currentSubject;
        LatestContext = context;

        if (currentSubject is not null)
        {
            OnActiveSubjectAttached(currentSubject);
        }

        if (currentCue is null)
        {
            return;
        }

        await OnActiveLookChangedAsync(
            previousCue,
            previousSubject,
            currentCue,
            currentSubject,
            context,
            activationToken);
    }

    /// <summary>Detaches any active subject, cancels the current activation, and clears published state.</summary>
    public override void _ExitTree()
    {
        base._ExitTree();

        if (ActiveSubject is IVisualSubject subject)
        {
            OnActiveSubjectDetached(subject);
        }

        ActiveCue = null;
        ActiveSubject = null;
        LatestContext = null;
        EndActivation();
    }

    /// <summary>Determines whether the supplied cue is a live instance still present in the scene tree.</summary>
    protected static bool IsLiveCue(VisualCue cue)
        => IsInstanceValid(cue) && cue.IsInsideTree() && !cue.IsQueuedForDeletion();

    /// <summary>Finds the nearest visual-subject ancestor of the supplied cue.</summary>
    protected static IVisualSubject? FindNearestSubject(VisualCue cue)
    {
        for (Node? ancestor = cue.GetParent(); ancestor is not null; ancestor = ancestor.GetParent())
        {
            if (ancestor is IVisualSubject subject)
            {
                return subject;
            }
        }

        return null;
    }

    /// <summary>Called when the previous subject stops being active, before replacement state is published.</summary>
    protected virtual void OnActiveSubjectDetached(IVisualSubject subject)
    {
    }

    /// <summary>Called when a newly resolved subject becomes active, after the new state is published.</summary>
    protected virtual void OnActiveSubjectAttached(IVisualSubject subject)
    {
    }

    /// <summary>
    /// Called after active-look state changes with a non-null current cue; the default implementation is a no-op.
    /// </summary>
    protected virtual ValueTask OnActiveLookChangedAsync(
        VisualCue? previousCue,
        IVisualSubject? previousSubject,
        VisualCue? currentCue,
        IVisualSubject? currentSubject,
        PerceptionContext context,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Begins a new activation, cancelling and replacing any previous activation.</summary>
    private CancellationToken BeginActivation(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous = _activationCts;
        _activationCts = cts;
        ActivationGeneration++;
        // Cancelled but deliberately not disposed: in-flight derived-faculty work may still hold the token, and
        // access to a disposed source's wait handle would throw ObjectDisposedException. The dropped source owns
        // no timers, and its link registration roots it only until the upstream percept token cancels, so dropping
        // the reference and letting the collector reclaim it is safe.
        previous?.Cancel();
        return cts.Token;
    }

    /// <summary>Cancels the current activation, if any, without disposing it; see <see cref="BeginActivation"/>.</summary>
    private void EndActivation()
    {
        CancellationTokenSource? cts = _activationCts;
        _activationCts = null;
        ActivationGeneration++;
        // Deliberately not disposed; see the note in BeginActivation.
        cts?.Cancel();
    }
}
