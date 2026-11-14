using System.Text.Json;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>
/// One question's verbatim dispatch material inside a decision-payload dump: the caller question identity and its
/// full instruction text, exactly as sent to the decision model.
/// </summary>
internal sealed record DecisionPayloadQuestion(string Name, string Instructions);

/// <summary>
/// One decision-gate evaluation's complete diagnostic dump (decision-payload logging): the verbatim
/// <c>DecisionRequest</c> material — the evidence JSON exactly as sent plus every question's name and
/// instructions — together with the evaluation's metadata and outcome, written to one JSON file per evaluation
/// when <c>Diagnostics:AI:EnableDecisionPayloadLogging</c> is enabled.
/// </summary>
/// <param name="Timestamp">When the evaluation settled (local time).</param>
/// <param name="CharacterId">Owning character's canonical full ID.</param>
/// <param name="TriggerKind">The gate's trigger kind: Event, Idle, AttendedSpeakerEnd, or Interruption.</param>
/// <param name="TriggerSequenceID">Triggering timeline entry's sequence ID; 0 when the trigger carries no entry.</param>
/// <param name="Applied">Whether the act verdict took effect: authorised one cycle or applied the invalidation.</param>
/// <param name="Stale">Whether a newer trigger, cycle generation, or timeline watermark superseded the snapshot.</param>
/// <param name="Threshold">The act threshold in effect for this evaluation.</param>
/// <param name="Verdict">Resolved verdict token: <c>act</c>, <c>idle</c>, or <c>failed</c>.</param>
/// <param name="Act">Whether the decision probability reached the threshold.</param>
/// <param name="Probability">Returned act probability; null when no boolean answer was produced.</param>
/// <param name="Confidence">Provider confidence when present; null when omitted.</param>
/// <param name="CompositionMs">Evidence composition time in milliseconds.</param>
/// <param name="DecisionMs">Decision round-trip time in milliseconds.</param>
/// <param name="EvidenceChars">Serialised evidence payload length in characters.</param>
/// <param name="CoalescedTriggers">Triggers subsumed into this evaluation instead of evaluating separately.</param>
/// <param name="Failure">Safe failure detail when the evaluation resolved through containment; null otherwise.</param>
/// <param name="Evidence">The evidence JSON exactly as dispatched to the decision model; null when composition
/// failed before a payload existed, which is itself diagnostic truth.</param>
/// <param name="Questions">Every dispatched question's name and verbatim instructions.</param>
internal sealed record DecisionPayloadDump(
    DateTimeOffset Timestamp,
    string CharacterId,
    string TriggerKind,
    long TriggerSequenceID,
    bool Applied,
    bool Stale,
    double Threshold,
    string Verdict,
    bool Act,
    double? Probability,
    double? Confidence,
    long CompositionMs,
    long DecisionMs,
    int EvidenceChars,
    int CoalescedTriggers,
    string? Failure,
    JsonElement? Evidence,
    IReadOnlyList<DecisionPayloadQuestion> Questions);
