using AlleyCat.Core;

namespace AlleyCat.Sense;

/// <summary>Component that synchronously publishes exact percept runtime types.</summary>
public interface ISense : IComponent
{
    /// <summary>Occurs synchronously when this sense publishes a percept.</summary>
    event Action<IPercept>? Perceived;

    /// <summary>Gets the immutable, deterministic exact runtime types this sense can publish.</summary>
    IReadOnlyList<Type> PerceptTypes
    {
        get;
    }
}
