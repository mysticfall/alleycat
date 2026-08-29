using AlleyCat.Sense;

namespace AlleyCat.Mind.Perception;

/// <summary>Non-generic authoring contract for one percept family.</summary>
public interface IPerception
{
    /// <summary>Raised for each observation this faculty emits, including emissions outside percept dispatch.</summary>
    event Action<Observation.Observation>? Observed;

    /// <summary>Gets the percept family handled by this faculty.</summary>
    Type PerceptType
    {
        get;
    }

    /// <summary>Interprets a percept asynchronously without returning a result.</summary>
    ValueTask PerceiveAsync(
        IPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Typed perception contract used to make faculty mappings explicit.</summary>
public interface IPerception<in TPercept> : IPerception
    where TPercept : IPercept
{
    /// <summary>Interprets a typed percept asynchronously without returning a result.</summary>
    ValueTask PerceiveAsync(
        TPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken);
}
