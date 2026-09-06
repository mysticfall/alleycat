using AlleyCat.Core;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using Godot;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Watch;

/// <summary>Authorable tool that arms a focus-limited relative-distance watch for one character.</summary>
[Tool]
[GlobalClass]
public sealed partial class ProximityWatchTool : WatchConditionTool
{
    /// <summary>Stable condition identity for proximity watches.</summary>
    public const string ConditionIdValue = "proximity";

    /// <summary>Creates the standard proximity tool with its authored defaults.</summary>
    public ProximityWatchTool()
    {
        ToolName = "watch_proximity";
        ToolDescription = "Watch one exact character and report when they enter or leave the chosen inclusive distance.";
    }

    /// <inheritdoc />
    public override string ConditionId => ConditionIdValue;

    /// <summary>Authored scheduling importance copied to each emitted outcome.</summary>
    [Export(PropertyHint.Range, "0,100,0.01,or_greater")]
    public float OutcomeImportance { get; set; } = 1f;

    /// <summary>Whether a transition requests immediate fresh reconsideration.</summary>
    [Export]
    public bool ImmediateReconsideration { get; set; } = true;

    /// <inheritdoc />
    protected override Delegate CreateWatchDelegate(WatchRegistry registry)
        => (string subject_id, float maximum_distance) => ArmAsync(registry, subject_id, maximum_distance);

    internal Task<AgentToolResult> ArmAsync(WatchRegistry registry, string subjectId, float maximumDistance)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ValidateSubjectId(subjectId);
        if (!float.IsFinite(maximumDistance) || maximumDistance < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDistance),
                maximumDistance,
                "Maximum distance must be finite and non-negative.");
        }

        _ = float.IsFinite(OutcomeImportance) && OutcomeImportance >= 0f
            ? true
            : throw new InvalidOperationException("Proximity watch outcome importance must be finite and non-negative.");

        return Task.FromResult(registry.Arm(new ProximityWatchRuntime(
            subjectId,
            maximumDistance,
            OutcomeImportance,
            ImmediateReconsideration)));
    }

    private static void ValidateSubjectId(string subjectId)
    {
        IdentityValidator.ValidateFullId(subjectId, nameof(subjectId));
        if (!subjectId.StartsWith("char:", StringComparison.Ordinal))
        {
            throw new ArgumentException("Proximity watches require an exact character FullId.", nameof(subjectId));
        }
    }

    private sealed class ProximityWatchRuntime(
        string subjectId,
        float maximumDistance,
        float importance,
        bool immediateReconsideration) : WatchRuntime(ConditionIdValue, subjectId)
    {
        private enum ProximityState
        {
            Unknown,
            Outside,
            Inside,
        }

        private ProximityState _state;
        private ProximityWatchEvidence? _evidence;

        public override void Initialise(IReadOnlyList<AcceptedObservationEntry> retained)
        {
            _evidence = FindEvidence(retained);
            _state = GetState(_evidence);
        }

        public override AgentObservation? Evaluate(
            RetainedObservationChange change,
            IReadOnlyList<AcceptedObservationEntry> retained)
        {
            _ = change;
            ProximityState previous = _state;
            _evidence = FindEvidence(retained);
            ProximityState current = GetState(_evidence);
            _state = current;
            ProximityTransition? transition = (previous, current) switch
            {
                (ProximityState.Unknown, ProximityState.Inside) => ProximityTransition.Entered,
                (ProximityState.Outside, ProximityState.Inside) => ProximityTransition.Entered,
                (ProximityState.Inside, ProximityState.Outside) => ProximityTransition.Left,
                _ => null,
            };
            return transition is null || _evidence is null
                ? null
                : new ObservedProximityTransition(
                    SubjectId,
                    transition.Value,
                    _evidence.Distance,
                    importance,
                    immediateReconsideration);
        }

        public override WatchStatusSnapshot Snapshot(string watchId)
            => new(watchId, ConditionId, SubjectId, new ProximityWatchStatus(_state.ToString(), _evidence));

        public override string DescribeArmResult(WatchStatusSnapshot snapshot)
        {
            var status = (ProximityWatchStatus)snapshot.Status;
            string relation = status.Evidence is null
                ? "Current retained proximity evidence is unavailable."
                : $"Current retained distance is {status.Evidence.Distance:0.###}.";
            return $"Armed watch {snapshot.WatchId} for {snapshot.SubjectId}. State: {status.State}. {relation}";
        }

        private ProximityState GetState(ProximityWatchEvidence? evidence)
            => evidence is null ? ProximityState.Unknown
                : evidence.Distance <= maximumDistance ? ProximityState.Inside
                : ProximityState.Outside;

        private ProximityWatchEvidence? FindEvidence(IReadOnlyList<AcceptedObservationEntry> retained)
        {
            for (int index = retained.Count - 1; index >= 0; index--)
            {
                AcceptedObservationEntry entry = retained[index];
                if (entry.IsRetained
                    && entry.Payload is ObservedRelativePosition position
                    && string.Equals(position.SubjectId, SubjectId, StringComparison.Ordinal))
                {
                    return new ProximityWatchEvidence(
                        position.Distance,
                        position.SubjectDirection,
                        position.ObserverDirection,
                        entry.ObservedAt);
                }
            }

            return null;
        }
    }
}

/// <summary>Condition-specific proximity evidence kept in active-watch status.</summary>
public sealed record ProximityWatchEvidence(
    float Distance,
    RelativeDirection SubjectDirection,
    RelativeDirection ObserverDirection,
    double ObservedAt);

/// <summary>Condition-specific active status for one proximity watch.</summary>
public sealed record ProximityWatchStatus(string State, ProximityWatchEvidence? Evidence) : IWatchStatusDetails;
