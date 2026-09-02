using AlleyCat.Mind.AI.Prompting;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using ObservedSpeechRecord = AlleyCat.Mind.Observation.ObservedSpeech;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Session-scoped speech-turn and continuation coordinator for one AgenticMind session (AI-001 TR-50, AI-002
/// TR-62): owns the speech lifecycle identity and keyed expectation maps, manual synthetic-token correlation,
/// grouped and ungrouped projected-event matching, leaked-hold watchdog decisions, and the translation from
/// Prompting-owned projection identity into generic runner continuation and injection operations.
/// </summary>
/// <remarks>
/// This coordinator is the Mind.AI integration component that interprets concrete speech observation records and
/// their feature payloads — correlation is its purpose, so AgenticMind itself never does. Effective
/// serialisation matches AgenticMind's delivery chain and the Godot-thread lifecycle-cue path: every
/// expectation-map access is guarded by the coordinator's own lock.
/// </remarks>
internal sealed class SpeechTurnContinuationCoordinator(AgenticMind mind)
{
    private readonly Lock _continuationExpectationLock = new();
    private readonly Dictionary<ContinuationIdentity, List<FreshInjectionExpectation>> _continuationExpectations = [];

    private readonly record struct ContinuationIdentity(string SourceVoiceID, string SpeechGroupID, int SegmentIndex);

    /// <summary>
    /// Routes one textless external speech lifecycle cue (AI-001 TR-47) into expectation management: onset and
    /// resume cues register a keyed continuation expectation for the cued identity, while textless terminals
    /// abandon that identity's pending expectations.
    /// </summary>
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
                RegisterContinuationExpectation(identity);
                break;
            case SpeechSegmentLifecycleTransition.Blank:
            case SpeechSegmentLifecycleTransition.Failed:
            case SpeechSegmentLifecycleTransition.Abandoned:
                AbandonContinuationExpectations(identity);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(notification),
                    notification.Transition,
                    "Unsupported speech lifecycle transition.");
        }
    }

    /// <summary>Indicates whether any keyed continuation expectation is currently pending.</summary>
    internal bool HasContinuationExpectations()
    {
        lock (_continuationExpectationLock)
        {
            return _continuationExpectations.Count > 0;
        }
    }

    internal IReadOnlyList<FreshInjectionExpectation> GetMatchingContinuationExpectations(
        ContinuationProjection.Event @event)
    {
        if (@event.Correlation is not { } correlation)
        {
            return [];
        }

        List<FreshInjectionExpectation> matches = [];
        lock (_continuationExpectationLock)
        {
            foreach (int index in correlation.SegmentIndexes)
            {
                ContinuationIdentity identity = new(correlation.SourceVoiceID, correlation.SpeechGroupID, index);
                if (_continuationExpectations.TryGetValue(identity, out List<FreshInjectionExpectation>? expectations))
                {
                    matches.AddRange(expectations);
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Translates a Prompting-owned projected-event correlation into the runner's session injection key: the
    /// single runtime boundary where projection identity becomes runner session protocol (AI-003 TR-35).
    /// Prompting never exposes the runner key itself.
    /// </summary>
    internal static AgentSessionInjectionKey CreateSessionInjectionKey(ContinuationProjection.Event @event)
        => @event.Correlation is { } correlation
            ? new AgentSessionInjectionKey(correlation.SourceVoiceID, correlation.SpeechGroupID)
            : throw new InvalidOperationException(
                "A projected event without speech-group correlation cannot own a keyed session injection.");

    /// <summary>
    /// Collects pending continuation expectations whose source voice owns the supplied observation.
    /// </summary>
    /// <remarks>
    /// Manual synthetic tokens are never projection groups, so their holds complete when the source voice's
    /// ungrouped speech observation is delivered. A pending automatic expectation for the same voice completes the
    /// same way: pre-emption abandons it before a manual session can publish, so only unreachable states could
    /// observe that fallback.
    /// </remarks>
    internal IReadOnlyList<FreshInjectionExpectation> GetVoiceCompletionExpectations(AgentObservation observation)
    {
        if (observation is not ObservedSpeechRecord speech || string.IsNullOrWhiteSpace(speech.VoiceId))
        {
            return [];
        }

        lock (_continuationExpectationLock)
        {
            List<FreshInjectionExpectation>? matches = null;
            foreach (KeyValuePair<ContinuationIdentity, List<FreshInjectionExpectation>> entry in _continuationExpectations)
            {
                if (!string.Equals(entry.Key.SourceVoiceID, speech.VoiceId, StringComparison.Ordinal))
                {
                    continue;
                }

                matches ??= [];
                matches.AddRange(entry.Value);
            }

            return matches ?? [];
        }
    }

    internal void RemoveContinuationExpectations(IEnumerable<FreshInjectionExpectation> expectations)
    {
        HashSet<FreshInjectionExpectation> removed = [.. expectations];
        if (removed.Count == 0)
        {
            return;
        }

        lock (_continuationExpectationLock)
        {
            foreach (ContinuationIdentity identity in _continuationExpectations.Keys.ToArray())
            {
                List<FreshInjectionExpectation> values = _continuationExpectations[identity];
                _ = values.RemoveAll(removed.Contains);
                if (values.Count == 0)
                {
                    _ = _continuationExpectations.Remove(identity);
                }
            }
        }
    }

    private void RegisterContinuationExpectation(ContinuationIdentity identity)
    {
        AgentSessionRunner? runner = mind.ActiveRunner;
        if (runner is null)
        {
            return;
        }

        lock (_continuationExpectationLock)
        {
            if (_continuationExpectations.ContainsKey(identity))
            {
                // A duplicate start or resume cue for an identity with a pending lease is idempotent: no second
                // lease is stacked and the active phase is not cancelled again.
                return;
            }
        }

        // Segment identity is retained by the coordinator's mapping. The runner's revision is deliberately the
        // lowest valid value because model-facing revisions are timeline positions rather than segment indexes.
        FreshInjectionExpectation expectation = runner.RegisterSpeechContinuation(
            new AgentSessionContinuationKey(new AgentSessionInjectionKey(identity.SourceVoiceID, identity.SpeechGroupID), 0));
        lock (_continuationExpectationLock)
        {
            if (mind.HasNodeLifetimeEnded || !_continuationExpectations.TryAdd(identity, [expectation]))
            {
                runner.AbandonFreshExpectation(expectation);
            }
        }

        // A cue for speech a delivery channel already consumed without correlating cannot settle through
        // delivery: the sweep releases that lease now instead of holding the next phase until session end
        // (AI-002 TR-57).
        ReleaseSettledContinuationHolds();
    }

    private void AbandonContinuationExpectations(ContinuationIdentity identity)
    {
        AgentSessionRunner? runner = mind.ActiveRunner;
        List<FreshInjectionExpectation>? expectations;
        lock (_continuationExpectationLock)
        {
            if (!_continuationExpectations.Remove(identity, out expectations))
            {
                return;
            }
        }

        if (runner is not null)
        {
            foreach (FreshInjectionExpectation expectation in expectations)
            {
                runner.AbandonFreshExpectation(expectation);
            }
        }
    }

    internal void ClearContinuationExpectations()
    {
        lock (_continuationExpectationLock)
        {
            _continuationExpectations.Clear();
        }
    }

    /// <summary>
    /// Settles every pending cue hold whose awaited speech can never reach the model through delivery (AI-002
    /// TR-57, no leaked holds): a committed speech record whose hold a delivery channel already consumed without
    /// correlating — or which left the pending deliverable accumulation with an importance that can never cross
    /// the configured threshold — settles its exact lease textlessly instead of leaving the fresh gate closed
    /// until session end. Records still pending delivery keep their holds: the ordinary delivery path owns them.
    /// </summary>
    internal void ReleaseSettledContinuationHolds()
    {
        AgentSessionRunner? runner = mind.ActiveRunner;
        if (runner is null)
        {
            return;
        }

        List<FreshInjectionExpectation> undeliverable = [];
        lock (_continuationExpectationLock)
        {
            if (_continuationExpectations.Count == 0)
            {
                return;
            }

            IReadOnlyList<AgentObservation> timeline = mind.GetObservationTimelineSnapshot();
            foreach (KeyValuePair<ContinuationIdentity, List<FreshInjectionExpectation>> entry in _continuationExpectations)
            {
                if (AwaitedSpeechLeftDelivery(entry.Key, timeline))
                {
                    undeliverable.AddRange(entry.Value);
                }
            }
        }

        foreach (FreshInjectionExpectation expectation in undeliverable)
        {
            runner.AbandonFreshExpectation(expectation);
        }

        RemoveContinuationExpectations(undeliverable);

        // Determines whether the identity's awaited speech committed and left the deliverable window.
        bool AwaitedSpeechLeftDelivery(ContinuationIdentity identity, IReadOnlyList<AgentObservation> timeline)
        {
            bool committed = false;
            bool stillPendingDelivery = false;
            foreach (AgentObservation observation in timeline)
            {
                if (observation is not ObservedSpeechRecord speech
                    || !string.Equals(speech.VoiceId, identity.SourceVoiceID, StringComparison.Ordinal))
                {
                    continue;
                }

                // A real automatic group matches its exact grouped record; a synthetic manual token is never a
                // group ID, so its voice's ungrouped records carry the same voice-level correlation the delivery
                // path applies (Run 1 semantics).
                bool isAwaitedRecord = speech.SpeechGroupID is null
                    || (string.Equals(speech.SpeechGroupID, identity.SpeechGroupID, StringComparison.Ordinal)
                        && speech.SegmentIndex == identity.SegmentIndex);
                if (!isAwaitedRecord)
                {
                    continue;
                }

                committed = true;
                stillPendingDelivery |= mind.ContainsPendingDeliveryObservation(observation);
            }

            // Nothing committed cannot leak: the ordinary settlement paths still own the hold. Committed records
            // still inside the deliverable accumulation will be claimed, rendered, and correlated normally.
            return committed && !stillPendingDelivery;
        }
    }
}
