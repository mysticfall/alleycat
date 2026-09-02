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
    string Content) : ObservedAction(ActorId), IHasCommitIdentity
{
    /// <summary>Fixed semantic attention contribution applied to a recognised non-self actor.</summary>
    private const float Contribution = 0.5f;

    /// <summary>The unified stable semantic key shared by every observed-speech perspective (AI-001 TR-10).</summary>
    public const string TypeKeyValue = "speech.observed";

    /// <inheritdoc />
    public override string TypeKey => TypeKeyValue;

    /// <summary>Creates observed speech with optional immutable automatic-segment transport metadata.</summary>
    /// <param name="actorId">Exact recognised actor FullId, or null when unknown.</param>
    /// <param name="voiceId">Raw source voice identifier.</param>
    /// <param name="content">Observed speech content.</param>
    /// <param name="speechGroupID">Optional automatic speech-group identity.</param>
    /// <param name="segmentIndex">Automatic segment index, defaulting to zero for ungrouped speech.</param>
    /// <param name="continued">Whether the segment follows an earlier group segment.</param>
    public ObservedSpeech(
        string? actorId,
        string? voiceId,
        string content,
        string? speechGroupID,
        int segmentIndex = 0,
        bool continued = false) : this(actorId, voiceId, content)
    {
        SpeechGroupID = speechGroupID;
        SegmentIndex = segmentIndex;
        Continued = continued;
    }

    /// <summary>Gets the optional automatic speech-group identity.</summary>
    public string? SpeechGroupID
    {
        get;
    }

    /// <summary>Gets the automatic segment index, defaulting to zero for ungrouped speech.</summary>
    public int SegmentIndex
    {
        get;
    }

    /// <summary>Gets whether this segment follows an earlier segment, defaulting to false when ungrouped.</summary>
    public bool Continued
    {
        get;
    }

    /// <summary>Gets whether this record carries a valid, complete grouped-segment identity.</summary>
    internal bool HasValidSpeechSegmentIdentity
        => !string.IsNullOrWhiteSpace(VoiceId)
            && !string.IsNullOrWhiteSpace(SpeechGroupID)
            && SegmentIndex >= 0
            && Continued == (SegmentIndex > 0);

    /// <summary>
    /// Supplies the exact-once grouped-segment commit identity — (VoiceId, SpeechGroupID, SegmentIndex) — through
    /// the generic observation commit-identity contract (AI-001 TR-45/49). Ungrouped and manual speech claim no
    /// identity and keep the ordinary allow duplicate policy, so they are never suppressed as duplicates.
    /// </summary>
    public ObservationCommitIdentity? CommitIdentity
        => HasValidSpeechSegmentIdentity ? new(VoiceId!, SpeechGroupID!, SegmentIndex) : null;

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
