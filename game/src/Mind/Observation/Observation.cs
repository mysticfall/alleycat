using AlleyCat.Character;
using AlleyCat.Mind.Attention;

namespace AlleyCat.Mind.Observation;

/// <summary>Controls whether equivalent retained observations are accepted.</summary>
public enum ObservationDuplicatePolicy
{
    /// <summary>Retains every observation.</summary>
    Allow,

    /// <summary>Suppresses an observation equivalent to the latest retained observation in its scope.</summary>
    IgnoreEquivalent,
}

/// <summary>Controls how Mind commits an observation.</summary>
public enum ObservationRetention
{
    /// <summary>The observation ingests through the ordinary timeline and prompt-history path.</summary>
    Durable,

    /// <summary>
    /// The observation applies its attention effects only and leaves no durable record, history entry, or prompt
    /// participation.
    /// </summary>
    Transient,
}

/// <summary>
/// Context available when an observation calculates its scheduling importance.
/// </summary>
/// <param name="Character">Character whose subjective timeline owns the observation.</param>
public sealed record ObservationContext(ICharacter Character);

/// <summary>
/// Base contract for sensory data perceived by an agent.
/// </summary>
public abstract record Observation
{
    /// <summary>Gets duplicate handling for this observation. Retention is the default.</summary>
    public virtual ObservationDuplicatePolicy DuplicatePolicy => ObservationDuplicatePolicy.Allow;

    /// <summary>Gets the stable ordinal scope used by opt-in duplicate handling.</summary>
    public virtual string? DuplicateScope => null;

    /// <summary>
    /// Gets how Mind commits this observation; durable ingestion is the default.
    /// </summary>
    public virtual ObservationRetention Retention => ObservationRetention.Durable;

    /// <summary>
    /// Exact, case-sensitive semantic key used for authored prompt dispatch.
    /// </summary>
    public abstract string TypeKey
    {
        get;
    }

    /// <summary>
    /// Game-time timestamp in seconds elapsed since the game began, stamped exactly once by the owning Mind at
    /// ingestion, or null when the record was not ingested through a Mind. Used for authored time labels such as
    /// "(at 30.5s game time)".
    /// </summary>
    public double? ObservedAt
    {
        get;
        init;
    }

    /// <summary>
    /// Calculates significance relative to the observing character at ingestion time.
    /// </summary>
    public abstract float CalculateImportance(ObservationContext context);

    /// <summary>
    /// Calculates the ordered attention effects this observation applies when Mind commits it.
    /// </summary>
    public virtual IReadOnlyList<AttentionEffect> GetAttentionEffects(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [];
    }

    /// <summary>Determines semantic equivalence, excluding ingestion metadata such as <see cref="ObservedAt"/>.</summary>
    public virtual bool IsSemanticallyEquivalentTo(Observation other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return false;
    }
}

/// <summary>
/// Base contract for a naturally observed action with optional recognised actor identity.
/// </summary>
/// <param name="ActorId">Exact stable actor FullId, or <see langword="null"/> when the actor is unknown.</param>
public abstract record ObservedAction(string? ActorId) : Observation;
