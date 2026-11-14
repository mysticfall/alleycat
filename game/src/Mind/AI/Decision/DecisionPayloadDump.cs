using System.Text.Json;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>
/// One offered option inside a decision-payload dump question: the option value and its selection criteria,
/// exactly as sent to the decision model.
/// </summary>
internal sealed record DecisionPayloadQuestionOption(string Value, string Criteria);

/// <summary>
/// One question's verbatim dispatch material inside a decision-payload dump: the caller question identity, its
/// full instruction text, and — for choice questions — every offered option, exactly as sent to the decision
/// model.
/// </summary>
internal sealed record DecisionPayloadQuestion(
    string Name,
    string Instructions,
    IReadOnlyList<DecisionPayloadQuestionOption>? Options = null);

/// <summary>
/// One decision evaluation's complete diagnostic dump (decision-payload logging): the verbatim
/// <c>DecisionRequest</c> material — the evidence JSON exactly as sent plus every question's name and
/// instructions — together with the evaluation's metadata and outcome, written to one JSON file per evaluation
/// when <c>Diagnostics:AI:EnableDecisionPayloadLogging</c> is enabled.
/// </summary>
/// <param name="Timestamp">When the evaluation settled (local time).</param>
/// <param name="CharacterId">Owning character's canonical full ID.</param>
/// <param name="TriggerKind">Free-form trigger token; gaze evaluations use Cadence, SpeakInvoked,
/// OwnSpeechEnded, or AttendedSpeakerEnded.</param>
/// <param name="Verdict">Resolved verdict token: the chosen option value (a canonical full ID or <c>none</c> for
/// gaze) or <c>failed</c> when the evaluation resolved through containment.</param>
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
    string Verdict,
    double? Confidence,
    long CompositionMs,
    long DecisionMs,
    int EvidenceChars,
    int CoalescedTriggers,
    string? Failure,
    JsonElement? Evidence,
    IReadOnlyList<DecisionPayloadQuestion> Questions);
