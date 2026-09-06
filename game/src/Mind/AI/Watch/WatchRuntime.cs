using AlleyCat.Mind.Observation;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Watch;

/// <summary>Condition-owned mutable runtime for one armed watch.</summary>
internal abstract class WatchRuntime(string conditionId, string subjectId)
{
    public string ConditionId { get; } = conditionId;

    public string SubjectId { get; } = subjectId;

    public abstract void Initialise(IReadOnlyList<AcceptedObservationEntry> retained);

    public abstract AgentObservation? Evaluate(
        RetainedObservationChange change,
        IReadOnlyList<AcceptedObservationEntry> retained);

    public abstract WatchStatusSnapshot Snapshot(string watchId);

    public abstract string DescribeArmResult(WatchStatusSnapshot snapshot);
}

/// <summary>Condition-specific immutable details included in an active watch status.</summary>
public interface IWatchStatusDetails;

/// <summary>Immutable active-watch state exposed by the current-scene projector.</summary>
public sealed record WatchStatusSnapshot(
    string WatchId,
    string ConditionId,
    string SubjectId,
    IWatchStatusDetails Status);
