namespace AlleyCat.Navigation;

/// <summary>
/// Bounded, allocation-free-at-rest lifecycle store for accepted navigation requests and terminal outcomes.
/// </summary>
internal sealed class NavigationRequestLifecycle : INavigationOutcomeSource
{
    private readonly List<NavigationTerminalOutcomeRecord> _terminalOutcomes;
    private ActiveRequest? _activeRequest;
    private long _generation = 1;
    private long _nextRequestID;
    private long _nextTerminalSequence;
    private bool IsNavigationEnabled { get; set; } = true;

    public NavigationRequestLifecycle(int terminalCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(terminalCapacity, 1);
        TerminalCapacity = terminalCapacity;
        _terminalOutcomes = new List<NavigationTerminalOutcomeRecord>(terminalCapacity);
        TerminalOutcomes = _terminalOutcomes.AsReadOnly();
    }

    public int TerminalCapacity
    {
        get;
    }

    public bool IsLocalHistoryEnded
    {
        get; private set;
    }

    /// <summary>Raised after a terminal record is retained and before the old active request is deactivated.</summary>
    public event Action<NavigationTerminalOutcomeRecord>? TerminalRecordPersisted;

    public IReadOnlyList<NavigationTerminalOutcomeRecord> TerminalOutcomes
    {
        get;
    }

    public int ReservedTerminalCapacity => _terminalOutcomes.Count + (_activeRequest is null ? 0 : 1);

    public NavigationCurrentStatus CurrentStatus => _activeRequest is { } active
        ? new NavigationCurrentStatus(
            NavigationRequestStatus.Active,
            active.Phase,
            active.Identity,
            active.RequestedTransform,
            active.Revision,
            active.ClockGeneration,
            active.AcceptancePhysicsTick)
        : NavigationCurrentStatus.Inactive;

    public NavigationDestinationAttempt RequestDestination(
        NavigationOutcomeTransform requestedTransform,
        NavigationRequestValidation validation,
        long revision,
        long clockGeneration,
        ulong acceptancePhysicsTick,
        NavigationTerminalContext? replacementContext = null,
        ulong replacementTerminalPhysicsTick = 0)
    {
        if (!validation.IsValid)
        {
            return Rejected(NavigationDestinationResult.Invalid);
        }

        if (!IsNavigationEnabled || IsLocalHistoryEnded || !validation.IsReady)
        {
            return Rejected(NavigationDestinationResult.NotReady);
        }

        if (!validation.IsReachable)
        {
            return Rejected(NavigationDestinationResult.Unreachable);
        }

        if (_activeRequest is not null && replacementContext is null)
        {
            return Rejected(NavigationDestinationResult.Invalid);
        }

        if (ReservedTerminalCapacity >= TerminalCapacity)
        {
            return Rejected(NavigationDestinationResult.OutcomeBackpressure);
        }

        var successorIdentity = new NavigationRequestIdentity(++_nextRequestID, _generation);
        var successor = new ActiveRequest(
            successorIdentity,
            requestedTransform,
            revision,
            clockGeneration,
            acceptancePhysicsTick,
            NavigationActivityPhase.Following);

        if (_activeRequest is not null)
        {
            CommitTerminal(
                NavigationTerminalOutcome.Superseded,
                replacementContext!.Value.WithReason(NavigationTerminalReason.Replacement),
                replacementTerminalPhysicsTick,
                null,
                successorIdentity);
        }

        _activeRequest = successor;
        return new NavigationDestinationAttempt(NavigationDestinationResult.Accepted, successorIdentity);
    }

    public long AdvanceRevision()
    {
        ActiveRequest active = RequireActiveRequest();
        active.Revision++;
        _activeRequest = active;
        return active.Revision;
    }

    public void SetClockGeneration(long clockGeneration)
    {
        if (_activeRequest is { } active)
        {
            active.ClockGeneration = clockGeneration;
            _activeRequest = active;
        }
    }

    public void EnterTransientNeutralisation() => SetActivityPhase(NavigationActivityPhase.Neutralised);

    public void EnterTransientWaiting() => SetActivityPhase(NavigationActivityPhase.Waiting);

    public void ResumeFollowing() => SetActivityPhase(NavigationActivityPhase.Following);

    public bool TryComplete(NavigationTerminalContext context, ulong terminalPhysicsTick)
        => TryCommitTerminal(
            NavigationTerminalOutcome.Completed,
            context.WithReason(NavigationTerminalReason.DestinationReached),
            terminalPhysicsTick,
            null,
            null);

    public bool TryFail(NavigationTerminalContext context, ulong terminalPhysicsTick)
        => TryCommitTerminal(NavigationTerminalOutcome.Failed, context, terminalPhysicsTick, null, null);

    public bool TryBlock(NavigationTerminalContext context, ulong terminalPhysicsTick)
    {
        ArgumentNullException.ThrowIfNull(context.Obstruction);

        return TryCommitTerminal(
            NavigationTerminalOutcome.Blocked,
            context.WithReason(NavigationTerminalReason.Obstruction),
            terminalPhysicsTick,
            null,
            null);
    }

    public bool TryCancel(
        NavigationCancellationSource source,
        NavigationTerminalContext context,
        ulong terminalPhysicsTick)
        => TryCommitTerminal(
            NavigationTerminalOutcome.Cancelled,
            context.WithReason(NavigationTerminalReason.Cancellation),
            terminalPhysicsTick,
            source,
            null);

    public bool Disable(NavigationTerminalContext context, ulong terminalPhysicsTick)
    {
        IsNavigationEnabled = false;
        return TryCancel(NavigationCancellationSource.NavigationDisabled, context, terminalPhysicsTick);
    }

    public void Enable() => IsNavigationEnabled = true;

    public bool Reset(NavigationTerminalContext context, ulong terminalPhysicsTick)
    {
        bool committed = TryCancel(NavigationCancellationSource.Reset, context, terminalPhysicsTick);
        _generation++;
        return committed;
    }

    public void RemoveOwner(
        INavigationTerminalOutcomeSink survivingSink,
        NavigationTerminalContext context,
        ulong terminalPhysicsTick)
    {
        ArgumentNullException.ThrowIfNull(survivingSink);
        if (IsLocalHistoryEnded)
        {
            return;
        }

        _ = TryCancel(NavigationCancellationSource.OwnerRemoved, context, terminalPhysicsTick);
        foreach (NavigationTerminalOutcomeRecord outcome in _terminalOutcomes)
        {
            survivingSink.RecordTerminalOutcome(outcome);
        }

        IsLocalHistoryEnded = true;
        _activeRequest = null;
        _terminalOutcomes.Clear();
    }

    public bool TryAcknowledgeOutcome(NavigationRequestIdentity request, long terminalSequence)
    {
        for (int index = 0; index < _terminalOutcomes.Count; index++)
        {
            NavigationTerminalOutcomeRecord outcome = _terminalOutcomes[index];
            if (outcome.Request == request && outcome.Sequence == terminalSequence)
            {
                _terminalOutcomes.RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    private static NavigationDestinationAttempt Rejected(NavigationDestinationResult result) => new(result, null);

    private bool TryCommitTerminal(
        NavigationTerminalOutcome outcome,
        NavigationTerminalContext context,
        ulong terminalPhysicsTick,
        NavigationCancellationSource? cancellationSource,
        NavigationRequestIdentity? successorRequest)
    {
        if (_activeRequest is null)
        {
            return false;
        }

        CommitTerminal(outcome, context, terminalPhysicsTick, cancellationSource, successorRequest);
        return true;
    }

    private void CommitTerminal(
        NavigationTerminalOutcome outcome,
        NavigationTerminalContext context,
        ulong terminalPhysicsTick,
        NavigationCancellationSource? cancellationSource,
        NavigationRequestIdentity? successorRequest)
    {
        ActiveRequest active = RequireActiveRequest();
        var record = new NavigationTerminalOutcomeRecord(
            ++_nextTerminalSequence,
            active.Identity,
            active.Revision,
            active.ClockGeneration,
            outcome,
            context.Reason,
            active.RequestedTransform,
            context.FinalTransform,
            context.Error,
            context.Tolerances,
            context.Margins,
            active.AcceptancePhysicsTick,
            context.BoundaryPhysicsTick,
            terminalPhysicsTick,
            context.Occurrence,
            context.Obstruction,
            cancellationSource,
            successorRequest);

        _terminalOutcomes.Add(record);
        TerminalRecordPersisted?.Invoke(record);
        _activeRequest = null;
    }

    private ActiveRequest RequireActiveRequest()
        => _activeRequest ?? throw new InvalidOperationException("The operation requires an active navigation request.");

    private void SetActivityPhase(NavigationActivityPhase phase)
    {
        if (_activeRequest is { } active)
        {
            active.Phase = phase;
            _activeRequest = active;
        }
    }

    private struct ActiveRequest(
        NavigationRequestIdentity identity,
        NavigationOutcomeTransform requestedTransform,
        long revision,
        long clockGeneration,
        ulong acceptancePhysicsTick,
        NavigationActivityPhase phase)
    {
        public NavigationRequestIdentity Identity = identity;
        public NavigationOutcomeTransform RequestedTransform = requestedTransform;
        public long Revision = revision;
        public long ClockGeneration = clockGeneration;
        public ulong AcceptancePhysicsTick = acceptancePhysicsTick;
        public NavigationActivityPhase Phase = phase;
    }
}
