using AlleyCat.Character;

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
    /// Exact, case-sensitive semantic key used for authored prompt dispatch.
    /// </summary>
    public abstract string TypeKey
    {
        get;
    }

    /// <summary>
    /// Calculates significance relative to the observing character at ingestion time.
    /// </summary>
    public abstract float CalculateImportance(ObservationContext context);
}

/// <summary>
/// Base contract for a naturally observed action with optional recognised actor identity.
/// </summary>
/// <param name="ActorId">Exact stable actor ID, or <see langword="null"/> when the actor is unknown.</param>
public abstract record ObservedAction(string? ActorId) : Observation;

/// <summary>
/// Speech observed from the owning character, a recognised other character, or an unknown speaker.
/// </summary>
/// <param name="ActorId">Exact recognised actor ID, or <see langword="null"/> when unknown.</param>
/// <param name="VoiceId">Optional raw voice provenance. This value does not establish actor identity.</param>
/// <param name="Content">Observed speech content.</param>
public sealed record ObservedSpeech(
    string? ActorId,
    string? VoiceId,
    string Content) : ObservedAction(ActorId)
{
    /// <inheritdoc />
    public override string TypeKey => "speech.observed";

    /// <inheritdoc />
    public override float CalculateImportance(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return string.Equals(ActorId, context.Character.Id, StringComparison.Ordinal) ? 0f : 1f;
    }
}
