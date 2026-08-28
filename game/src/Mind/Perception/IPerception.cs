using AlleyCat.Sense;

namespace AlleyCat.Mind.Perception;

/// <summary>Non-generic authoring contract for one percept family.</summary>
public interface IPerception
{
    /// <summary>Gets the percept family handled by this faculty.</summary>
    Type PerceptType
    {
        get;
    }

    /// <summary>Interprets a percept asynchronously.</summary>
    ValueTask<PerceptionResult> PerceiveAsync(
        IPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Typed perception contract used to make faculty mappings explicit.</summary>
public interface IPerception<in TPercept> : IPerception
    where TPercept : IPercept
{
    /// <summary>Interprets a typed percept asynchronously.</summary>
    ValueTask<PerceptionResult> PerceiveAsync(
        TPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken);
}
