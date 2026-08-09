using AlleyCat.Sense;

namespace AlleyCat.Mind.Perception;

/// <summary>Non-generic authoring contract for one exact percept runtime type.</summary>
public interface IPerception
{
    /// <summary>Gets the sole exact percept runtime type handled by this faculty.</summary>
    Type PerceptType
    {
        get;
    }

    /// <summary>Interprets a percept synchronously.</summary>
    PerceptionResult Perceive(IPercept percept, PerceptionContext context);
}

/// <summary>Typed perception contract used to make faculty mappings explicit.</summary>
public interface IPerception<in TPercept> : IPerception
    where TPercept : IPercept
{
    /// <summary>Interprets a typed percept synchronously.</summary>
    PerceptionResult Perceive(TPercept percept, PerceptionContext context);
}
