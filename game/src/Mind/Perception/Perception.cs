using AlleyCat.Sense;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>Node base that checks assignable percept dispatch before calling a typed faculty.</summary>
public abstract partial class PerceptionNode : Node, IPerception
{
    /// <inheritdoc cref="IPerception.Observed" />
    public event Action<Observation.Observation>? Observed;

    /// <inheritdoc/>
    public abstract Type PerceptType
    {
        get;
    }

    /// <inheritdoc/>
    public abstract ValueTask PerceiveAsync(
        IPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken);

    /// <summary>Raises <see cref="Observed" /> for one observation emitted by this faculty.</summary>
    protected void Emit(Observation.Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Action<Observation.Observation>? handlers = Observed;
        handlers?.Invoke(observation);
    }
}

/// <summary>Typed node base that checks assignable percept dispatch before calling a faculty.</summary>
public abstract partial class Perception<TPercept> : PerceptionNode, IPerception<TPercept>
    where TPercept : IPercept
{
    /// <inheritdoc />
    public override Type PerceptType => typeof(TPercept);

    /// <inheritdoc />
    public override ValueTask PerceiveAsync(
        IPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(percept);
        return percept is not TPercept typedPercept
            ? throw new ArgumentException($"{GetType().Name} handles only percepts assignable to '{typeof(TPercept).FullName}'.", nameof(percept))
            : PerceiveAsync(typedPercept, context, cancellationToken);
    }

    /// <inheritdoc />
    public abstract ValueTask PerceiveAsync(
        TPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken);
}
