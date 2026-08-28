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

/// <summary>Sense marker exposing the family of percepts published through the non-generic event bridge.</summary>
/// <typeparam name="TPercept">Covariant percept family published by the sense.</typeparam>
public interface ISense<out TPercept> : ISense
    where TPercept : IPercept
{
}
