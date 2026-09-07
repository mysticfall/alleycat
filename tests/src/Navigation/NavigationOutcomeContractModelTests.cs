using AlleyCat.Navigation;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>Production lifecycle coverage for NAV-TAC-008 and the outcome foundation of NAV-TAC-009.</summary>
public sealed class NavigationOutcomeContractModelTests
{
    /// <inheritdoc/>
    [Fact]
    public void RejectedAttempts_HaveNoIdentityAndPreserveActiveAndReservedState()
    {
        var lifecycle = new NavigationRequestLifecycle(2);
        NavigationDestinationAttempt accepted = Request(lifecycle, Requested(1));
        NavigationCurrentStatus before = lifecycle.CurrentStatus;
        int reservedBefore = lifecycle.ReservedTerminalCapacity;

        Assert.Equal(NavigationDestinationResult.Invalid, Request(lifecycle, Requested(2), _invalid).Result);
        Assert.Equal(NavigationDestinationResult.NotReady, Request(lifecycle, Requested(2), _notReady).Result);
        Assert.Equal(NavigationDestinationResult.Unreachable, Request(lifecycle, Requested(2), _unreachable).Result);

        Assert.Equal(before, lifecycle.CurrentStatus);
        Assert.Equal(reservedBefore, lifecycle.ReservedTerminalCapacity);
        Assert.Empty(lifecycle.TerminalOutcomes);
        NavigationDestinationAttempt replacement = Request(lifecycle, Requested(3), replacementContext: Context(1));
        Assert.Equal(new NavigationRequestIdentity(1, 1), accepted.Request);
        Assert.Equal(new NavigationRequestIdentity(2, 1), replacement.Request);
    }

    /// <inheritdoc/>
    [Fact]
    public void Completion_PersistsEveryMandatoryRecordFieldAndUsesExactSignedMargins()
    {
        var lifecycle = new NavigationRequestLifecycle(1);
        NavigationDestinationAttempt accepted = Request(lifecycle, Requested(1), acceptanceTick: 10);
        var occurrence = new NPCReleaseOccurrence(7, 2, 0.75f, NPCAnimationPhaseLane.Right, "release_r");
        NavigationTerminalContext context = new(
            Final(1.1f),
            new NavigationPlanarYawError(0.02f, 0.03f),
            _tolerances,
            NavigationTerminalReason.DestinationReached,
            boundaryPhysicsTick: 12,
            occurrence: occurrence);

        Assert.True(lifecycle.TryComplete(context, 14));

        Assert.Equal(NavigationCurrentStatus.Inactive, lifecycle.CurrentStatus);
        NavigationTerminalOutcomeRecord record = Assert.Single(lifecycle.TerminalOutcomes);
        AssertMandatoryFields(record, accepted.Request!.Value, NavigationTerminalOutcome.Completed);
        Assert.Equal(accepted.Request, record.Request);
        Assert.Equal(1, record.Revision);
        Assert.Equal(7, record.ClockGeneration);
        Assert.Equal(1, record.Sequence);
        Assert.Equal(NavigationTerminalOutcome.Completed, record.Outcome);
        Assert.Equal(NavigationTerminalReason.DestinationReached, record.Reason);
        Assert.Equal(Requested(1), record.RequestedTransform);
        Assert.Equal(Final(1.1f), record.FinalTransform);
        Assert.Equal(new NavigationPlanarYawError(0.02f, 0.03f), record.Error);
        Assert.Equal(_tolerances, record.Tolerances);
        Assert.Equal(_tolerances.CalculateMargins(new NavigationPlanarYawError(0.02f, 0.03f)), record.Margins);
        Assert.Equal(10UL, record.AcceptancePhysicsTick);
        Assert.Equal(12UL, record.BoundaryPhysicsTick);
        Assert.Equal(14UL, record.TerminalPhysicsTick);
        Assert.Equal(occurrence, record.Occurrence);
        Assert.Null(record.Obstruction);
        Assert.Null(record.CancellationSource);
        Assert.Null(record.SuccessorRequest);
    }

    /// <inheritdoc/>
    [Fact]
    public void Failure_IsTerminalAndFirstTerminalCommitWins()
    {
        var lifecycle = new NavigationRequestLifecycle(1);
        _ = Request(lifecycle, Requested(1));
        NavigationTerminalContext failure = Context(
            1,
            planarError: 0.11f,
            yawError: 0.08f,
            reason: NavigationTerminalReason.ConvergenceFailed);

        Assert.True(lifecycle.TryFail(failure, 20));
        Assert.False(lifecycle.TryComplete(Context(1), 21));
        Assert.False(lifecycle.TryCancel(NavigationCancellationSource.ExplicitClear, Context(1), 22));

        NavigationTerminalOutcomeRecord record = Assert.Single(lifecycle.TerminalOutcomes);
        AssertMandatoryFields(record, new NavigationRequestIdentity(1, 1), NavigationTerminalOutcome.Failed);
        Assert.Equal(NavigationTerminalOutcome.Failed, record.Outcome);
        Assert.Equal(_tolerances.CalculateMargins(new NavigationPlanarYawError(0.11f, 0.08f)), record.Margins);
    }

    /// <inheritdoc/>
    [Fact]
    public void Clear_RecordsCancellationBeforeDeactivationAndIsIdempotent()
    {
        var lifecycle = new NavigationRequestLifecycle(1);
        _ = Request(lifecycle, Requested(1));
        bool wasActiveWhenPersisted = false;
        lifecycle.TerminalRecordPersisted += _ => wasActiveWhenPersisted = lifecycle.CurrentStatus.HasActiveRequest;

        Assert.True(lifecycle.TryCancel(NavigationCancellationSource.ExplicitClear, Context(1), 12));
        Assert.True(wasActiveWhenPersisted);
        Assert.False(lifecycle.TryCancel(NavigationCancellationSource.ExplicitClear, Context(1), 13));
        NavigationTerminalOutcomeRecord record = Assert.Single(lifecycle.TerminalOutcomes);
        AssertMandatoryFields(record, new NavigationRequestIdentity(1, 1), NavigationTerminalOutcome.Cancelled);
        Assert.Equal(NavigationTerminalOutcome.Cancelled, record.Outcome);
        Assert.Equal(NavigationCancellationSource.ExplicitClear, record.CancellationSource);
    }

    /// <inheritdoc/>
    [Fact]
    public void BlockedOutcome_RequiresAndRetainsStructuredObstructionContext()
    {
        var lifecycle = new NavigationRequestLifecycle(1);
        _ = Request(lifecycle, Requested(1));
        NavigationObstructionDetails details = new("path-lost", "replan", 2);

        Assert.True(lifecycle.TryBlock(Context(1, obstruction: details, reason: NavigationTerminalReason.Obstruction), 15));

        NavigationTerminalOutcomeRecord record = Assert.Single(lifecycle.TerminalOutcomes);
        AssertMandatoryFields(record, new NavigationRequestIdentity(1, 1), NavigationTerminalOutcome.Blocked);
        Assert.Equal(NavigationTerminalOutcome.Blocked, record.Outcome);
        Assert.Equal(details, record.Obstruction);
    }

    /// <inheritdoc/>
    [Fact]
    public void Replacement_AtomicallySupersedesOldRequestAndLinksFullSuccessorIdentity()
    {
        var lifecycle = new NavigationRequestLifecycle(2);
        NavigationDestinationAttempt first = Request(lifecycle, Requested(1));
        NavigationCurrentStatus observed = default;
        lifecycle.TerminalRecordPersisted += _ => observed = lifecycle.CurrentStatus;

        NavigationDestinationAttempt replacement = Request(lifecycle, Requested(2), replacementContext: Context(1.5f));

        NavigationTerminalOutcomeRecord superseded = Assert.Single(lifecycle.TerminalOutcomes);
        AssertMandatoryFields(superseded, first.Request!.Value, NavigationTerminalOutcome.Superseded);
        Assert.Equal(first.Request, superseded.Request);
        Assert.Equal(NavigationTerminalOutcome.Superseded, superseded.Outcome);
        Assert.Equal(NavigationTerminalReason.Replacement, superseded.Reason);
        Assert.Equal(replacement.Request, superseded.SuccessorRequest);
        Assert.Equal(first.Request, observed.ActiveRequest);
        Assert.Equal(replacement.Request, lifecycle.CurrentStatus.ActiveRequest);
        Assert.Equal(2, lifecycle.ReservedTerminalCapacity);
    }

    /// <inheritdoc/>
    [Fact]
    public void MissingReplacementContext_IsInvalidWithoutReservingIdentity()
    {
        var lifecycle = new NavigationRequestLifecycle(2);
        NavigationDestinationAttempt first = Request(lifecycle, Requested(1));
        NavigationCurrentStatus before = lifecycle.CurrentStatus;

        NavigationDestinationAttempt malformed = Request(lifecycle, Requested(2));
        NavigationDestinationAttempt replacement = Request(lifecycle, Requested(3), replacementContext: Context(1));

        Assert.Equal(NavigationDestinationResult.Invalid, malformed.Result);
        Assert.Null(malformed.Request);
        Assert.Equal(first.Request, before.ActiveRequest);
        Assert.Equal(new NavigationRequestIdentity(2, 1), replacement.Request);
    }

    /// <inheritdoc/>
    [Fact]
    public void CapacityBlockedReplacement_LeavesOldRequestUntouchedAndDoesNotReserveIdentity()
    {
        var lifecycle = new NavigationRequestLifecycle(1);
        NavigationDestinationAttempt first = Request(lifecycle, Requested(1));
        NavigationCurrentStatus before = lifecycle.CurrentStatus;

        NavigationDestinationAttempt blocked = Request(lifecycle, Requested(2), replacementContext: Context(1));

        Assert.Equal(NavigationDestinationResult.OutcomeBackpressure, blocked.Result);
        Assert.Null(blocked.Request);
        Assert.Equal(before, lifecycle.CurrentStatus);
        Assert.Equal(first.Request, lifecycle.CurrentStatus.ActiveRequest);
        Assert.Empty(lifecycle.TerminalOutcomes);
    }

    /// <inheritdoc/>
    [Fact]
    public void Acknowledgement_RemovesOnlyMatchingIdentityAndSequenceAndReleasesCapacity()
    {
        var lifecycle = new NavigationRequestLifecycle(2);
        NavigationDestinationAttempt first = Request(lifecycle, Requested(1));
        Assert.True(lifecycle.TryComplete(Context(1), 12));
        NavigationDestinationAttempt second = Request(lifecycle, Requested(2));
        Assert.True(lifecycle.TryComplete(Context(2), 13));
        NavigationTerminalOutcomeRecord firstRecord = lifecycle.TerminalOutcomes[0];

        Assert.False(lifecycle.TryAcknowledgeOutcome(second.Request!.Value, firstRecord.Sequence));
        Assert.Equal(2, lifecycle.TerminalOutcomes.Count);
        Assert.True(lifecycle.TryAcknowledgeOutcome(first.Request!.Value, firstRecord.Sequence));
        Assert.False(lifecycle.TryAcknowledgeOutcome(first.Request.Value, firstRecord.Sequence));
        NavigationTerminalOutcomeRecord retained = Assert.Single(lifecycle.TerminalOutcomes);
        Assert.Equal(second.Request, retained.Request);
        Assert.Equal(1, lifecycle.ReservedTerminalCapacity);
    }

    /// <inheritdoc/>
    [Fact]
    public void Reset_CancelsActiveWorkRetainsOutcomeAndAdvancesIdentityGeneration()
    {
        var lifecycle = new NavigationRequestLifecycle(2);
        NavigationDestinationAttempt first = Request(lifecycle, Requested(1));

        Assert.True(lifecycle.Reset(Context(1), 20));
        NavigationDestinationAttempt second = Request(lifecycle, Requested(2));

        Assert.Equal(new NavigationRequestIdentity(1, 1), first.Request);
        Assert.Equal(new NavigationRequestIdentity(2, 2), second.Request);
        NavigationTerminalOutcomeRecord cancelled = Assert.Single(lifecycle.TerminalOutcomes);
        Assert.Equal(NavigationCancellationSource.Reset, cancelled.CancellationSource);
    }

    /// <inheritdoc/>
    [Fact]
    public void RevisionAndClockGenerationMutationsUpdateActiveRecordWithoutChangingIdentity()
    {
        var lifecycle = new NavigationRequestLifecycle(1);
        NavigationDestinationAttempt accepted = Request(lifecycle, Requested(1));

        Assert.Equal(2, lifecycle.AdvanceRevision());
        lifecycle.SetClockGeneration(9);
        Assert.True(lifecycle.TryFail(Context(1, reason: NavigationTerminalReason.ClockGenerationChanged), 20));

        NavigationTerminalOutcomeRecord record = Assert.Single(lifecycle.TerminalOutcomes);
        Assert.Equal(accepted.Request, record.Request);
        Assert.Equal(2, record.Revision);
        Assert.Equal(9, record.ClockGeneration);
    }

    /// <inheritdoc/>
    [Fact]
    public void DisableRejectsNewWorkUntilEnabledAndRetainsCancellation()
    {
        var lifecycle = new NavigationRequestLifecycle(2);
        _ = Request(lifecycle, Requested(1));

        Assert.True(lifecycle.Disable(Context(1), 20));
        Assert.Equal(NavigationDestinationResult.NotReady, Request(lifecycle, Requested(2)).Result);
        lifecycle.Enable();
        Assert.Equal(NavigationDestinationResult.Accepted, Request(lifecycle, Requested(2)).Result);
        Assert.Equal(NavigationCancellationSource.NavigationDisabled, lifecycle.TerminalOutcomes[0].CancellationSource);
    }

    /// <inheritdoc/>
    [Fact]
    public void TransientNeutralisationAndWaitingRetainActiveIdentityWithoutTerminalOutcome()
    {
        var lifecycle = new NavigationRequestLifecycle(1);
        NavigationDestinationAttempt accepted = Request(lifecycle, Requested(1));

        lifecycle.EnterTransientNeutralisation();
        Assert.Equal(NavigationActivityPhase.Neutralised, lifecycle.CurrentStatus.Phase);
        lifecycle.EnterTransientWaiting();
        Assert.Equal(NavigationActivityPhase.Waiting, lifecycle.CurrentStatus.Phase);
        lifecycle.ResumeFollowing();

        Assert.Equal(NavigationActivityPhase.Following, lifecycle.CurrentStatus.Phase);
        Assert.Equal(accepted.Request, lifecycle.CurrentStatus.ActiveRequest);
        Assert.Empty(lifecycle.TerminalOutcomes);
    }

    /// <inheritdoc/>
    [Fact]
    public void OwnerRemoval_DeliversExactlyOnceBeforeEndingLocalHistory()
    {
        var lifecycle = new NavigationRequestLifecycle(2);
        var sink = new RecordingTerminalSink(() => lifecycle.IsLocalHistoryEnded);
        _ = Request(lifecycle, Requested(1));
        Assert.True(lifecycle.TryComplete(Context(1), 12));
        _ = Request(lifecycle, Requested(2));

        lifecycle.RemoveOwner(sink, Context(2), 20);
        lifecycle.RemoveOwner(sink, Context(2), 21);

        Assert.Collection(
            sink.Records,
            completed => Assert.Equal(NavigationTerminalOutcome.Completed, completed.Outcome),
            cancelled => Assert.Equal(NavigationCancellationSource.OwnerRemoved, cancelled.CancellationSource));
        Assert.Equal([false, false], sink.HistoryEndedAtDelivery);
        Assert.True(lifecycle.IsLocalHistoryEnded);
        Assert.Empty(lifecycle.TerminalOutcomes);
    }

    /// <inheritdoc/>
    [Fact]
    public void SteadyStateReadsAndAcknowledgementMissesAllocateNoMemory()
    {
        var lifecycle = new NavigationRequestLifecycle(1);
        _ = Request(lifecycle, Requested(1));
        NavigationRequestIdentity missing = new(999, 999);
        _ = lifecycle.CurrentStatus;
        _ = lifecycle.TerminalOutcomes;
        _ = lifecycle.TryAcknowledgeOutcome(missing, 999);
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 10_000; index++)
        {
            _ = lifecycle.CurrentStatus;
            _ = lifecycle.TerminalOutcomes;
            _ = lifecycle.TryAcknowledgeOutcome(missing, 999);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <inheritdoc/>
    [Fact]
    public void ClearAndReplacementRetainAtomicTransitionOrder()
    {
        var lifecycle = new NavigationRequestLifecycle(3);
        NavigationDestinationAttempt first = Request(lifecycle, Requested(1));
        Assert.True(lifecycle.TryCancel(NavigationCancellationSource.ExplicitClear, Context(1), 12));
        NavigationDestinationAttempt second = Request(lifecycle, Requested(2));
        NavigationDestinationAttempt third = Request(lifecycle, Requested(3), replacementContext: Context(2));

        Assert.Collection(
            lifecycle.TerminalOutcomes,
            cancellation =>
            {
                Assert.Equal(1, cancellation.Sequence);
                Assert.Equal(first.Request, cancellation.Request);
                Assert.Equal(NavigationTerminalOutcome.Cancelled, cancellation.Outcome);
            },
            replacement =>
            {
                Assert.Equal(2, replacement.Sequence);
                Assert.Equal(second.Request, replacement.Request);
                Assert.Equal(third.Request, replacement.SuccessorRequest);
            });
    }

    /// <inheritdoc/>
    [Fact]
    public void ManualOverrideCancellationCommitsBeforeOverrideAction()
    {
        var lifecycle = new NavigationRequestLifecycle(1);
        _ = Request(lifecycle, Requested(1));

        Assert.True(lifecycle.TryCancel(NavigationCancellationSource.ManualOverride, Context(1), 12));
        NavigationTerminalOutcomeRecord observed = Assert.Single(lifecycle.TerminalOutcomes);
        Assert.Equal(NavigationCancellationSource.ManualOverride, observed.CancellationSource);
        Assert.False(lifecycle.CurrentStatus.HasActiveRequest);
    }

    private static readonly NavigationRequestValidation _invalid = new(false, true, true);
    private static readonly NavigationRequestValidation _notReady = new(true, false, true);
    private static readonly NavigationRequestValidation _unreachable = new(true, true, false);
    private static readonly NavigationTerminalTolerances _tolerances = new(0.1f, 0.05f);

    private static NavigationDestinationAttempt Request(
        NavigationRequestLifecycle lifecycle,
        NavigationOutcomeTransform transform,
        NavigationRequestValidation? validation = null,
        NavigationTerminalContext? replacementContext = null,
        ulong acceptanceTick = 10)
        => lifecycle.RequestDestination(
            transform,
            validation ?? NavigationRequestValidation.Accepted,
            revision: 1,
            clockGeneration: 7,
            acceptanceTick,
            replacementContext,
            replacementTerminalPhysicsTick: 11);

    private static NavigationOutcomeTransform Requested(float x) => new(x, 0, 0, x / 10);

    private static NavigationOutcomeTransform Final(float x) => new(x, 0, 1, x / 10);

    private static NavigationTerminalContext Context(
        float finalX,
        float planarError = 0,
        float yawError = 0,
        NavigationObstructionDetails? obstruction = null,
        NavigationTerminalReason reason = NavigationTerminalReason.DestinationReached)
        => new(Final(finalX), new NavigationPlanarYawError(planarError, yawError), _tolerances, reason, obstruction);

    private static void AssertMandatoryFields(
        NavigationTerminalOutcomeRecord record,
        NavigationRequestIdentity request,
        NavigationTerminalOutcome outcome)
    {
        Assert.Equal(request, record.Request);
        Assert.True(record.Revision > 0);
        Assert.True(record.ClockGeneration > 0);
        Assert.True(record.Sequence > 0);
        Assert.Equal(outcome, record.Outcome);
        Assert.NotEqual(default, record.RequestedTransform);
        Assert.NotEqual(default, record.FinalTransform);
        Assert.Equal(record.Tolerances.CalculateMargins(record.Error), record.Margins);
        Assert.True(record.AcceptancePhysicsTick > 0);
        Assert.True(record.TerminalPhysicsTick >= record.AcceptancePhysicsTick);
    }

    private sealed class RecordingTerminalSink(Func<bool> historyEnded) : INavigationTerminalOutcomeSink
    {
        public List<NavigationTerminalOutcomeRecord> Records { get; } = [];

        public List<bool> HistoryEndedAtDelivery { get; } = [];

        public void RecordTerminalOutcome(NavigationTerminalOutcomeRecord outcome)
        {
            Records.Add(outcome);
            HistoryEndedAtDelivery.Add(historyEnded());
        }
    }
}
