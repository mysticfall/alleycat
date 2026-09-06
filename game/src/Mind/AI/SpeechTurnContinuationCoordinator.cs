using AlleyCat.Mind.Observation;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Holds attended speech continuations until their committed event is present in canonical request context.
/// </summary>
/// <remarks>
/// The coordinator deliberately has no runner-side gate. A request which presents the committed
/// speech must be admitted immediately; only a locally valid response confirms that presentation.
/// </remarks>
internal sealed class SpeechTurnContinuationCoordinator(AgenticMind mind)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ContinuationIdentity, ContinuationState> _continuations = [];
    private TaskCompletionSource _changed = CreateChangeSignal();

    private readonly record struct ContinuationIdentity(string SourceVoiceID, string SpeechGroupID, int SegmentIndex);

    private enum ContinuationStage
    {
        Pending,
        PresentedAwaitingConfirmation,
    }

    private readonly record struct ContinuationState(ContinuationStage Stage, long PresentationWatermark = 0);

    /// <summary>Routes textless lifecycle transitions into the continuation state machine.</summary>
    internal void HandleLifecycleNotification(SpeechSegmentLifecycleNotification notification)
    {
        ContinuationIdentity identity = new(
            notification.SourceVoiceID,
            notification.Metadata.SpeechGroupID,
            notification.Metadata.SegmentIndex);

        switch (notification.Transition)
        {
            case SpeechSegmentLifecycleTransition.Started:
            case SpeechSegmentLifecycleTransition.Resumed:
                lock (_lock)
                {
                    if (_continuations.TryAdd(identity, new ContinuationState(ContinuationStage.Pending)))
                    {
                        SignalChangeLocked();
                    }
                }

                // Any work based on the provisional utterance is stale. The following request waits here, at the
                // Mind-owned context boundary, rather than receiving rendered event text through the generic runner.
                SpeechContinuationKey runnerKey = ToRunnerKey(identity);
                AgentSessionRunner? runner = mind.ActiveRunner;
                runner?.RegisterSpeechContinuation(runnerKey);
                runner?.InvalidateForFreshTurn(new HashSet<SpeechContinuationKey> { runnerKey });
                break;

            case SpeechSegmentLifecycleTransition.Blank:
            case SpeechSegmentLifecycleTransition.Failed:
            case SpeechSegmentLifecycleTransition.Abandoned:
                lock (_lock)
                {
                    if (_continuations.Remove(identity))
                    {
                        SignalChangeLocked();
                    }
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(notification),
                    notification.Transition,
                    "Unsupported speech lifecycle transition.");
        }
    }

    /// <summary>Wakes a materialising context when a newly accepted event could satisfy a pending continuation.</summary>
    internal void NotifyObservationAccepted(AgentObservation observation)
    {
        if (observation is not ObservedSpeech)
        {
            return;
        }

        lock (_lock)
        {
            SignalChangeLocked();
        }
    }

    /// <summary>Gets pending continuations whose exact speech event is now present in the persistent timeline.</summary>
    internal IReadOnlySet<SpeechContinuationKey> GetReadyContinuationKeys()
    {
        IReadOnlyList<AcceptedObservationEntry> timeline = mind.GetPersistentEventTimelineSnapshot();
        HashSet<SpeechContinuationKey> ready = [];
        lock (_lock)
        {
            foreach ((ContinuationIdentity identity, ContinuationState state) in _continuations)
            {
                if (state.Stage == ContinuationStage.Pending
                    && timeline.Any(entry => Matches(identity, entry)))
                {
                    _ = ready.Add(ToRunnerKey(identity));
                }
            }
        }

        return ready;
    }

    /// <summary>
    /// Waits until every pending continuation has a matching committed event in the supplied persistent timeline.
    /// </summary>
    internal async Task WaitForPresentationEligibilityAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            IReadOnlyList<AcceptedObservationEntry> timeline = mind.GetPersistentEventTimelineSnapshot();
            lock (_lock)
            {
                if (_continuations.All(entry => entry.Value.Stage != ContinuationStage.Pending
                    || timeline.Any(eventEntry => Matches(entry.Key, eventEntry))))
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Marks each matching pending continuation as presented by a request with this timeline watermark.</summary>
    internal bool TryPresentThrough(long watermark, IReadOnlyList<AcceptedObservationEntry> timeline)
    {
        lock (_lock)
        {
            if (_continuations.Any(entry => entry.Value.Stage == ContinuationStage.Pending
                && !timeline.Any(timelineEntry => timelineEntry.SequenceID <= watermark && Matches(entry.Key, timelineEntry))))
            {
                return false;
            }

            foreach (ContinuationIdentity identity in _continuations
                          .Where(static entry => entry.Value.Stage == ContinuationStage.Pending)
                         .Select(static entry => entry.Key)
                         .ToArray())
            {
                if (timeline.Any(entry => entry.SequenceID <= watermark && Matches(identity, entry)))
                {
                    _continuations[identity] = new ContinuationState(
                        ContinuationStage.PresentedAwaitingConfirmation,
                        watermark);
                }
            }

            return true;
        }
    }

    /// <summary>Settles only continuations which the valid accepted response actually presented.</summary>
    internal void ConfirmThrough(long watermark)
    {
        lock (_lock)
        {
            bool removed = false;
            foreach (ContinuationIdentity identity in _continuations
                          .Where(entry => entry.Value.Stage == ContinuationStage.PresentedAwaitingConfirmation
                              && entry.Value.PresentationWatermark <= watermark)
                         .Select(static entry => entry.Key)
                         .ToArray())
            {
                removed |= _continuations.Remove(identity);
            }
            if (removed)
            {
                SignalChangeLocked();
            }
        }
    }

    /// <summary>Returns a non-confirmed presentation to pending state so the next logical request rematerialises it.</summary>
    internal void DiscardPresentation(long watermark)
    {
        lock (_lock)
        {
            bool changed = false;
            foreach (ContinuationIdentity identity in _continuations
                          .Where(entry => entry.Value.Stage == ContinuationStage.PresentedAwaitingConfirmation
                              && entry.Value.PresentationWatermark <= watermark)
                         .Select(static entry => entry.Key)
                         .ToArray())
            {
                _continuations[identity] = new ContinuationState(ContinuationStage.Pending);
                changed = true;
            }

            if (changed)
            {
                SignalChangeLocked();
            }
        }
    }

    /// <summary>Releases all holds at the terminal node-lifetime boundary.</summary>
    internal void ClearContinuationExpectations()
    {
        lock (_lock)
        {
            if (_continuations.Count > 0)
            {
                _continuations.Clear();
                SignalChangeLocked();
            }
        }
    }

    private static bool Matches(ContinuationIdentity identity, AcceptedObservationEntry entry)
        => entry.Payload is ObservedSpeech
            && entry.SpeechTransport is { } transport
            && string.Equals(transport.SourceVoiceID, identity.SourceVoiceID, StringComparison.Ordinal)
            && (!transport.HasGroupedSegmentIdentity
                || (string.Equals(transport.SpeechGroupID, identity.SpeechGroupID, StringComparison.Ordinal)
                     && transport.SegmentIndex == identity.SegmentIndex));

    private static SpeechContinuationKey ToRunnerKey(ContinuationIdentity identity)
        => new(identity.SourceVoiceID, identity.SpeechGroupID, identity.SegmentIndex);

    private void SignalChangeLocked()
    {
        TaskCompletionSource changed = _changed;
        _changed = CreateChangeSignal();
        _ = changed.TrySetResult();
    }

    private static TaskCompletionSource CreateChangeSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
