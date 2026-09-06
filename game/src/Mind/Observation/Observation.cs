using System.Globalization;
using AlleyCat.Character;
using AlleyCat.Mind.Attention;

namespace AlleyCat.Mind.Observation;

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
    /// <summary>
    /// Gets whether this observation is attention-only and therefore bypasses acceptance, retention, scheduling, and
    /// event history. Lifetime policies govern every accepted observation; this flag exists solely for the initial
    /// visual-presence path.
    /// </summary>
    public virtual bool IsAttentionOnly => false;

    /// <summary>
    /// Exact, case-sensitive semantic key identifying this observation's semantic type.
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
    /// Renders canonical model-facing text for this observation from the owning character's perspective.
    /// </summary>
    /// <remarks>
    /// The fallback intentionally uses only <see cref="TypeKey"/> so a future observation cannot accidentally
    /// disclose arbitrary payload data before it supplies its own rendering body.
    /// </remarks>
    public string Render(ICharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        string body = RenderBody(character);
        return ObservedAt is { } observedAt
            ? $"{body} (at {observedAt.ToString("F1", CultureInfo.InvariantCulture)}s game time)"
            : body;
    }

    /// <summary>
    /// Renders this observation's canonical text without the common event-time suffix.
    /// </summary>
    protected virtual string RenderBody(ICharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return $"((Received {TypeKey} event.))";
    }

    /// <summary>
    /// Calculates significance relative to the observing character at ingestion time.
    /// </summary>
    public abstract float CalculateImportance(ObservationContext context);

    /// <summary>
    /// Determines whether this observation invalidates the observing character's current reasoning context and
    /// therefore forces a fresh model turn. The default never requires a fresh turn; overrides evaluate
    /// owner-relative identity against the supplied context.
    /// </summary>
    public virtual bool RequiresFreshTurn(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return false;
    }

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
