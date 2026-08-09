using AlleyCat.Sense;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>Resource base that checks exact percept dispatch before calling a typed faculty.</summary>
public abstract partial class PerceptionResource : Resource, IPerception
{
    /// <inheritdoc/>
    public abstract Type PerceptType
    {
        get;
    }

    /// <inheritdoc/>
    public abstract PerceptionResult Perceive(IPercept percept, PerceptionContext context);
}

/// <summary>Typed resource base that checks exact percept dispatch before calling a faculty.</summary>
public abstract partial class Perception<TPercept> : PerceptionResource, IPerception<TPercept>
    where TPercept : IPercept
{
    /// <inheritdoc />
    public override Type PerceptType => typeof(TPercept);

    /// <inheritdoc />
    public override PerceptionResult Perceive(IPercept percept, PerceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(percept);
        return percept.GetType() != typeof(TPercept)
            ? throw new ArgumentException($"{GetType().Name} handles only exact percept type '{typeof(TPercept).FullName}'.", nameof(percept))
            : Perceive((TPercept)percept, context);
    }

    /// <inheritdoc />
    public abstract PerceptionResult Perceive(TPercept percept, PerceptionContext context);
}
