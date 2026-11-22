using System.Text.Json;
using System.Text.Json.Serialization;

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
/// <param name="Verdict">Resolved verdict token: for gaze the chosen option value (a cue value or <c>none</c>); for
/// the wait gate <c>now</c>, <c>arm-5.2s</c>, <c>keep-3.1s</c> or <c>keep</c>; <c>failed</c> when the evaluation
/// resolved through containment.</param>
/// <param name="Confidence">Provider confidence when present; null when omitted.</param>
/// <param name="CompositionMs">Evidence composition time in milliseconds.</param>
/// <param name="DecisionMs">Decision round-trip time in milliseconds.</param>
/// <param name="EvidenceChars">Serialised evidence payload length in characters.</param>
/// <param name="CoalescedTriggers">Triggers subsumed into this evaluation instead of evaluating separately.</param>
/// <param name="Failure">Safe failure detail when the evaluation resolved through containment; null otherwise.</param>
/// <param name="Evidence">The evidence JSON exactly as dispatched to the decision model; null when composition
/// failed before a payload existed, which is itself diagnostic truth.</param>
/// <param name="Questions">Every dispatched question's name and verbatim instructions.</param>
/// <param name="Answers">Every returned answer with its choice and probabilities, for producers that record them;
/// omitted from the file when null.</param>
/// <param name="WaitResolution">The wait gate's resolved verdict and deadline; omitted from the file when null.</param>
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
    IReadOnlyList<DecisionPayloadQuestion> Questions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<DecisionPayloadAnswer>? Answers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DecisionPayloadWaitResolution? WaitResolution = null);

/// <summary>One returned choice answer inside a decision-payload dump.</summary>
/// <param name="Name">The question's name.</param>
/// <param name="Choice">The selected option value.</param>
/// <param name="Probabilities">The provider's option distribution, or null when omitted.</param>
/// <param name="Confidence">Provider confidence, or null when omitted.</param>
internal sealed record DecisionPayloadAnswer(
    string Name,
    string Choice,
    IReadOnlyDictionary<string, double>? Probabilities,
    double? Confidence);

/// <summary>
/// The wait gate's resolution of one evaluation: how the verdict changed the schedule and the deadline in effect
/// afterwards.
/// </summary>
/// <param name="Step">How the verdict changed the schedule: <c>now</c>, <c>armed</c>, <c>kept</c>, or <c>failed</c>.</param>
/// <param name="Arm">The deferred verdict's arm (<c>response_pending</c>, <c>may_act</c>, <c>no_reason</c>), or
/// null when the verdict did not defer.</param>
/// <param name="RequestedSeconds">The deferred verdict's jittered timeout, or null when it did not defer.</param>
/// <param name="DeadlineInSeconds">Seconds until the deadline in effect afterwards, or null when none is armed or
/// the agent starts now.</param>
/// <param name="DeadlineSource">Who armed that deadline (<c>explicit</c> or <c>decision</c>), or null.</param>
internal sealed record DecisionPayloadWaitResolution(
    string Step,
    string? Arm,
    double? RequestedSeconds,
    double? DeadlineInSeconds,
    string? DeadlineSource);
