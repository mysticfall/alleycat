using AlleyCat.Core.Logging;
using AlleyCat.Mind.Observation;
using Microsoft.Extensions.Logging;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Produces the model-facing view of immutable Mind observations. Grouped speech is coalesced here only; Mind's
/// timeline remains the factual, append-only segment record.
/// </summary>
internal static class ContinuationProjection
{
    internal sealed record Event(
        AgentObservation Observation,
        long Position,
        long Revision,
        SpeechGroupCorrelation? Correlation);

    /// <summary>
    /// Prompting-owned correlation identity for one projected grouped-speech event: the contributing group's
    /// source voice, speech-group identity, and contributing segment indexes. Correlation metadata only
    /// (AI-003 TR-33/35) — never model-facing wording, never rendered, and never a runner session-protocol type;
    /// the runtime consumption boundary translates it into opaque continuation identities.
    /// </summary>
    internal sealed record SpeechGroupCorrelation(
        string SourceVoiceID,
        string SpeechGroupID,
        IReadOnlySet<int> SegmentIndexes);

    public static IReadOnlyList<Event> Project(
        IReadOnlyList<AgentObservation> timeline,
        IReadOnlyList<AgentObservation>? selected = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return Project(
            [.. timeline.Select((observation, index) => new Entry(observation, index, selected is null || selected.Contains(observation), null))]);
    }

    /// <summary>Projects accepted timeline entries while retaining their ingestion-only speech transport privately.</summary>
    public static IReadOnlyList<Event> Project(
        IReadOnlyList<AcceptedObservationEntry> timeline,
        IReadOnlyList<AcceptedObservationEntry>? selected = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return Project(
            [.. timeline.Select((entry, index) => new Entry(
                entry.Payload,
                index,
                selected is null || selected.Contains(entry),
                entry.SpeechTransport))]);
    }

    private static IReadOnlyList<Event> Project(IReadOnlyList<Entry> timeline)
    {
        Dictionary<GroupIdentity, List<Entry>> groups = [];
        List<Entry> standalone = [];

        for (int index = 0; index < timeline.Count; index++)
        {
            Entry entry = timeline[index];
            AgentObservation observation = entry.Observation ?? throw new ArgumentException("Timeline contains a null observation.", nameof(timeline));
            if (observation is ObservedSpeech && TryGetGroupIdentity(entry, out GroupIdentity identity))
            {
                if (!groups.TryGetValue(identity, out List<Entry>? entries))
                {
                    entries = [];
                    groups.Add(identity, entries);
                }

                entries.Add(entry);
            }
            else
            {
                standalone.Add(entry);
            }
        }

        List<Event> projected = [];
        foreach (Entry entry in standalone)
        {
            if (entry.Selected)
            {
                projected.Add(CreateStandalone(entry));
            }
        }

        foreach ((GroupIdentity identity, List<Entry> entries) in groups)
        {
            if (!entries.Any(static entry => entry.Selected))
            {
                continue;
            }

            if (entries.Select(static entry => ((ObservedSpeech)entry.Observation).ActorId).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                LogInconsistentAttribution();
                foreach (Entry entry in entries.Where(static entry => entry.Selected))
                {
                    projected.Add(CreateStandalone(entry));
                }

                continue;
            }

            Entry latest = entries.MaxBy(static entry => entry.Position)!;
            var first = (ObservedSpeech)entries[0].Observation;
            string content = string.Join(
                " … ",
                entries
                    .OrderBy(static entry => entry.SpeechTransport!.SegmentIndex)
                    .Select(static entry => ((ObservedSpeech)entry.Observation).Content)
                    .Where(static content => !string.IsNullOrWhiteSpace(content)));
            projected.Add(new Event(
                new ObservedSpeech(first.ActorId, content) { ObservedAt = latest.Observation.ObservedAt },
                latest.Position + 1,
                latest.Position + 1,
                new SpeechGroupCorrelation(
                    identity.SourceVoiceID,
                    identity.SpeechGroupID,
                    entries.Select(static entry => entry.SpeechTransport!.SegmentIndex).ToHashSet())));
        }

        return [.. projected.OrderBy(static entry => entry.Position)];
    }

    private static Event CreateStandalone(Entry entry)
    {
        AgentObservation observation = entry.Observation is ObservedSpeech speech
            ? new ObservedSpeech(speech.ActorId, speech.Content) { ObservedAt = speech.ObservedAt }
            : entry.Observation;
        return new Event(observation, entry.Position + 1, entry.Position + 1, Correlation: null);
    }

    private static bool TryGetGroupIdentity(Entry entry, out GroupIdentity identity)
    {
        if (entry.SpeechTransport is { HasGroupedSegmentIdentity: true } transport)
        {
            identity = new GroupIdentity(transport.SourceVoiceID, transport.SpeechGroupID!);
            return true;
        }

        identity = default;
        return false;
    }

    private static void LogInconsistentAttribution()
    {
        if (GameLoggerResolver.TryResolve(out ILogger<ContinuationProjectionLog>? logger) && logger is not null)
        {
            logger.LogWarning("A grouped speech projection had inconsistent actor attribution and was kept separate.");
        }
    }

    private readonly record struct GroupIdentity(string SourceVoiceID, string SpeechGroupID);

    private sealed record Entry(
        AgentObservation Observation,
        long Position,
        bool Selected,
        SpeechObservationTransport? SpeechTransport);

    private sealed class ContinuationProjectionLog
    {
    }
}
