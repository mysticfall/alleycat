using AlleyCat.Mind.Attention;

namespace AlleyCat.Mind.Observation;

/// <summary>
/// Speech observed from the owning character, a recognised other character, or an unknown speaker.
/// </summary>
/// <param name="ActorId">Exact recognised actor FullId, or <see langword="null"/> when unknown.</param>
/// <param name="VoiceId">Optional raw voice ID used for configured attribution, but not authenticated provenance.</param>
/// <param name="Content">Observed speech content.</param>
public sealed record ObservedSpeech(
    string? ActorId,
    string? VoiceId,
    string Content) : ObservedAction(ActorId)
{
    /// <summary>Fixed semantic attention contribution applied to a recognised non-self actor.</summary>
    private const float Contribution = 0.5f;

    /// <summary>The unified stable semantic key shared by every observed-speech perspective (AI-001 TR-10).</summary>
    public const string TypeKeyValue = "speech.observed";

    /// <inheritdoc />
    public override string TypeKey => TypeKeyValue;

    /// <inheritdoc />
    public override float CalculateImportance(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return string.Equals(ActorId, context.Character.FullId, StringComparison.Ordinal) ? 0f : 1f;
    }

    /// <summary>
    /// Requires a fresh turn for every accepted non-self speaker: recognised other characters, unknown speakers,
    /// and any actor identity that is not the observing character's exact full ID. Exact self speech never
    /// requires one.
    /// </summary>
    public override bool RequiresFreshTurn(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !string.Equals(ActorId, context.Character.FullId, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override IReadOnlyList<AttentionEffect> GetAttentionEffects(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ActorId is not null
            && !string.Equals(ActorId, context.Character.FullId, StringComparison.Ordinal)
            ? [new AttentionEffect(ActorId, Contribution)]
            : [];
    }
}
