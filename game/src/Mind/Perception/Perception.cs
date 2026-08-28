using AlleyCat.Sense;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>Node base that checks assignable percept dispatch before calling a typed faculty.</summary>
public abstract partial class PerceptionNode : Node, IPerception
{
    /// <inheritdoc/>
    public abstract Type PerceptType
    {
        get;
    }

    /// <inheritdoc/>
    public abstract ValueTask<PerceptionResult> PerceiveAsync(
        IPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Typed node base that checks assignable percept dispatch before calling a faculty.</summary>
public abstract partial class Perception<TPercept> : PerceptionNode, IPerception<TPercept>
    where TPercept : IPercept
{
    /// <inheritdoc />
    public override Type PerceptType => typeof(TPercept);

    /// <inheritdoc />
    public override ValueTask<PerceptionResult> PerceiveAsync(
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
    public abstract ValueTask<PerceptionResult> PerceiveAsync(
        TPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken);
}
