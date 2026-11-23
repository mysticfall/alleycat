using AlleyCat.Character;
using AlleyCat.Core.Time;
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
    /// ingestion, or null when the record was not ingested through a Mind. Used for authored relative time labels
    /// such as "(2 min 30 sec ago)".
    /// </summary>
    public double? ObservedAt
    {
        get;
        init;
    }

    /// <summary>
    /// Renders canonical model-facing text for this observation from the owning character's perspective.
    /// </summary>
    /// <param name="character">Character whose subjective timeline owns the observation.</param>
    /// <param name="nowSeconds">The composition moment's game time, so the event-time suffix states how long ago
    /// the event happened relative to when the model reads it; null omits the suffix entirely rather than
    /// rendering an absolute time the model would have to compare itself.</param>
    /// <remarks>
    /// The fallback intentionally uses only <see cref="TypeKey"/> so a future observation cannot accidentally
    /// disclose arbitrary payload data before it supplies its own rendering body.
    /// </remarks>
    public string Render(ICharacter character, double? nowSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        string body = RenderBody(character);
        return ObservedAt is { } observedAt && nowSeconds is { } now
            ? $"{body} ({RelativeGameTime.FormatAgo(now - observedAt)})"
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
    /// Determines whether this observation wakes the observing character's agent from a parked wait: when Mind
    /// commits it while a wait — a model-called <c>wait</c> or the session park — is active, or before a later wait
    /// begins while no provider request has presented it yet, that wait ends at once so the next turn runs and
    /// receives it through ordinary delivery. A running turn is never interrupted; that is
    /// <see cref="RequiresFreshTurn" />'s role. The default never wakes.
    /// </summary>
    public virtual bool WakesWaitingAgent(ObservationContext context)
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
