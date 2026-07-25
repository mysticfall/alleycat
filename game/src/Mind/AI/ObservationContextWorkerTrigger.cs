using Godot;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI;

/// <summary>Base for cheap, synchronous, side-effect-free observation policies.</summary>
[GlobalClass]
public abstract partial class ObservationContextWorkerTrigger : ContextWorkerTrigger
{
    /// <inheritdoc />
    public override void _Ready() => RequireOwningMind().ObservationCommitted += OnObservationCommitted;

    /// <inheritdoc />
    public override void _ExitTree() => RequireOwningMind().ObservationCommitted -= OnObservationCommitted;

    private void OnObservationCommitted(AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (ShouldRequestFor(observation))
        {
            RequestRun();
        }
    }

    /// <summary>Evaluates a cheap synchronous observation predicate without side effects.</summary>
    protected abstract bool ShouldRequestFor(AgentObservation observation);
}
