using System.Text.Json;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>Why one wait-decision evaluation ran.</summary>
internal enum DecisionWaitTriggerKind
{
    /// <summary>Periodic cadence boundary while waiting.</summary>
    Cadence,

    /// <summary>The player's speech was delivered to this character.</summary>
    PlayerSpeech,

    /// <summary>An attended speaker's speech segment started or resumed.</summary>
    AttendedSpeakerStart,

    /// <summary>An attended speaker's speaking window closed.</summary>
    AttendedSpeakerEnd,

    /// <summary>The scene's character membership changed since the last evaluation.</summary>
    MembershipChange,
}

/// <summary>One wait-decision trigger with its free-form diagnostics token.</summary>
internal sealed record DecisionWaitTrigger(DecisionWaitTriggerKind Kind)
{
    /// <summary>The trigger with no event context.</summary>
    public static DecisionWaitTrigger Cadence { get; } = new(DecisionWaitTriggerKind.Cadence);

    /// <summary>Gets the free-form diagnostics token for this trigger kind.</summary>
    public string Token => Kind switch
    {
        DecisionWaitTriggerKind.Cadence => "Cadence",
        DecisionWaitTriggerKind.PlayerSpeech => "PlayerSpeech",
        DecisionWaitTriggerKind.AttendedSpeakerStart => "AttendedSpeakerStart",
        DecisionWaitTriggerKind.AttendedSpeakerEnd => "AttendedSpeakerEnd",
        DecisionWaitTriggerKind.MembershipChange => "MembershipChange",
        _ => Kind.ToString(),
    };


}

/// <summary>What the waiting character is currently doing, for the own-action evidence.</summary>
internal enum DecisionWaitOwnActionKind
{
    /// <summary>Waiting with no attended speaker.</summary>
    Waiting,

    /// <summary>Waiting while hearing an attended speaker.</summary>
    Hearing,
}

/// <summary>How the attended speaker's current speech is addressed, when hearing.</summary>
internal enum DecisionWaitAddresseeStatus
{
    /// <summary>The speech declares this character as a recipient.</summary>
    AddressedToMe,

    /// <summary>The speech declares recipients and this character is not among them.</summary>
    AddressedToAnother,

    /// <summary>The speech declares no recipients.</summary>
    Undecided,
}

/// <summary>One structured own-action observation attached to a wait evaluation.</summary>
/// <param name="Kind">What the waiting character is doing.</param>
/// <param name="SpeakerFullId">The attended speaker's canonical full ID when hearing.</param>
/// <param name="AddresseeStatus">How the speaker's current speech is addressed, when known.</param>
internal sealed record DecisionWaitOwnAction(
    DecisionWaitOwnActionKind Kind,
    string? SpeakerFullId = null,
    DecisionWaitAddresseeStatus AddresseeStatus = DecisionWaitAddresseeStatus.Undecided)
{
    /// <summary>The own action that carries no speech context.</summary>
    public static DecisionWaitOwnAction WaitingAction { get; } = new(DecisionWaitOwnActionKind.Waiting);

    /// <summary>Renders the model-facing own-action phrase.</summary>
    public string Describe() => Kind switch
    {
        DecisionWaitOwnActionKind.Waiting => "waiting",
        DecisionWaitOwnActionKind.Hearing when SpeakerFullId is { } speaker =>
            $"waiting; hearing `{speaker}` (they are speaking, {DescribeAddressee()})",
        DecisionWaitOwnActionKind.Hearing => "waiting",
        _ => "waiting",
    };

    private string DescribeAddressee() => AddresseeStatus switch
    {
        DecisionWaitAddresseeStatus.AddressedToMe => "addressed to me",
        DecisionWaitAddresseeStatus.AddressedToAnother => "not to me",
        DecisionWaitAddresseeStatus.Undecided => "addressee undecided",
        _ => "addressee undecided",
    };
}

/// <summary>What one completed wait evaluation decided.</summary>
internal enum DecisionWaitDecisionKind
{
    /// <summary>Start the agent now, whatever deadline is armed.</summary>
    ActNow,

    /// <summary>Keep waiting with the verdict's deadline armed under the shorten-only timer rules.</summary>
    Defer,

    /// <summary>
    /// Every failure mode: keep waiting with backoff; for timing a failure arms the no-reason timeout under the
    /// shorten-only rules, so the agent always wakes eventually.
    /// </summary>
    Failure,
}

/// <summary>One resolved wait decision with its deferred arm or safe failure detail.</summary>
internal sealed record DecisionWaitDecision(DecisionWaitDecisionKind Kind, DecisionWaitArm? Arm, string? Failure)
{
    /// <summary>Creates an immediate decision.</summary>
    public static DecisionWaitDecision ActNowDecision() => new(DecisionWaitDecisionKind.ActNow, null, null);

    /// <summary>Creates a deferred decision arming the given verdict's timeout.</summary>
    public static DecisionWaitDecision DeferDecision(DecisionWaitArm arm) => new(DecisionWaitDecisionKind.Defer, arm, null);

    /// <summary>Creates a contained failure decision that keeps waiting.</summary>
    public static DecisionWaitDecision Failed(string failure) => new(DecisionWaitDecisionKind.Failure, null, failure);
}

/// <summary>One completed wait evaluation's decision plus the verbatim dispatch material and timings.</summary>
/// <param name="Decision">The resolved decision.</param>
/// <param name="Answers">Every valid choice answer returned, in question order; empty on transport failures.</param>
/// <param name="CompositionMilliseconds">Evidence and question composition time.</param>
/// <param name="DecisionMilliseconds">Decision round-trip time.</param>
/// <param name="EvidenceCharacters">Serialised evidence payload length.</param>
/// <param name="Evidence">The evidence JSON exactly as dispatched, or null when composition failed.</param>
/// <param name="Questions">The dispatched questions' names, verbatim instructions and options.</param>
internal sealed record DecisionWaitEvaluationResult(
    DecisionWaitDecision Decision,
    IReadOnlyList<DecisionPayloadAnswer> Answers,
    long CompositionMilliseconds,
    long DecisionMilliseconds,
    int EvidenceCharacters,
    JsonElement? Evidence,
    IReadOnlyList<DecisionPayloadQuestion> Questions);

/// <summary>
/// Two-question decision wait policy: code owns the timing, the decision model answers two narrow classification
/// questions in one request — <c>response</c> (did the subject's latest speech or action call for a response, and
/// has it arrived, verbal or not) and <c>initiative</c> (how strongly the circumstances call for the subject to
/// act) — and <see cref="MapVerdict" /> resolves the pair into an immediate start or a deferred deadline arm.
/// </summary>
/// <remarks>
/// Every failure mode — typed decision failures, refusals, malformed, missing, or unknown answers on either
/// question — resolves to a <see cref="DecisionWaitDecisionKind.Failure" /> decision that keeps waiting; only
/// caller cancellation propagates. The policy never throws for provider-side causes and never touches Godot state.
/// </remarks>
internal static class DecisionWaitPolicy
{
    /// <summary>Model-facing identity of the response-status choice question.</summary>
    internal const string ResponseQuestionName = "response";

    /// <summary>Model-facing identity of the initiative choice question.</summary>
    internal const string InitiativeQuestionName = "initiative";

    /// <summary>The subject's latest speech or action awaits a response that has not arrived.</summary>
    internal const string ExpectingPendingValue = "expecting_pending";

    /// <summary>The subject's latest speech or action called for a response, and it arrived.</summary>
    internal const string ExpectingArrivedValue = "expecting_arrived";

    /// <summary>The subject's latest speech or action called for no response, or there is none.</summary>
    internal const string NotExpectingValue = "not_expecting";

    /// <summary>The circumstances call for the subject to react.</summary>
    internal const string MustActValue = "must_act";

    /// <summary>The scene offers the subject an opening to act.</summary>
    internal const string MayActValue = "may_act";

    /// <summary>The subject has no particular reason to act.</summary>
    internal const string NoReasonValue = "no_reason";

    /// <summary>
    /// Maps one validated answer pair to a decision: an arrived response or <c>must_act</c> starts the agent now
    /// (<c>must_act</c> wins over a pending response, so the initiative answer is always read); otherwise a pending
    /// response arms the response-pending timeout, and with no response expected <c>may_act</c> and
    /// <c>no_reason</c> arm their own timeouts.
    /// </summary>
    internal static DecisionWaitDecision MapVerdict(string response, string initiative)
    {
        if (response is not (ExpectingPendingValue or ExpectingArrivedValue or NotExpectingValue))
        {
            return DecisionWaitDecision.Failed("unknown response choice");
        }

        if (initiative is not (MustActValue or MayActValue or NoReasonValue))
        {
            return DecisionWaitDecision.Failed("unknown initiative choice");
        }

        // An arrived response or a must-act situation starts the agent now; must_act wins over a pending response.
        return response == ExpectingArrivedValue || initiative == MustActValue
            ? DecisionWaitDecision.ActNowDecision()
            : response == ExpectingPendingValue
                ? DecisionWaitDecision.DeferDecision(DecisionWaitArm.ResponsePending)
                : initiative == MayActValue
                    ? DecisionWaitDecision.DeferDecision(DecisionWaitArm.MayAct)
                    : DecisionWaitDecision.DeferDecision(DecisionWaitArm.NoReason);
    }

    internal static SceneDecisionGroup PrepareGroup(Action<DecisionWaitEvaluationResult> apply, CancellationToken lifetime = default) => new(
        subject => [ComposeResponseQuestion(subject), ComposeInitiativeQuestion(subject)],
        receipt =>
        {
            List<DecisionPayloadAnswer> answers = [];
            DecisionWaitDecision decision = receipt.Failure is { } failure ? DecisionWaitDecision.Failed(failure)
                : ResolveAnswers(receipt.Answers, answers);
            apply(new(decision, answers, receipt.CompositionMs, receipt.DecisionMs,
                receipt.Evidence?.GetRawText().Length ?? 0, receipt.Evidence, receipt.Questions));
        }, lifetime);

    private static DecisionWaitDecision ResolveAnswers(IReadOnlyDictionary<string, DecisionAnswer> result, List<DecisionPayloadAnswer>? answers)
    {
        string? responseFailure = TryReadChoice(result, ResponseQuestionName, answers, out string? response);
        string? initiativeFailure = TryReadChoice(result, InitiativeQuestionName, answers, out string? initiative);
        return responseFailure is not null
            ? DecisionWaitDecision.Failed($"{ResponseQuestionName}: {responseFailure}")
            : initiativeFailure is not null
                ? DecisionWaitDecision.Failed($"{InitiativeQuestionName}: {initiativeFailure}")
                : MapVerdict(response!, initiative!);
    }

    private static string? TryReadChoice(
        IReadOnlyDictionary<string, DecisionAnswer> result,
        string name,
        List<DecisionPayloadAnswer>? answers,
        out string? value)
    {
        value = null;
        if (!result.TryGetValue(name, out DecisionAnswer? answer) || answer is null)
        {
            return "missing answer";
        }

        if (answer is DecisionRefusal)
        {
            return "refusal";
        }

        if (answer is not ChoiceAnswer choice)
        {
            return "unexpected answer kind";
        }

        answers?.Add(new DecisionPayloadAnswer(name, choice.Value, choice.Probabilities, choice.Confidence));
        value = choice.Value;
        return null;
    }

    /// <summary>
    /// Composes the response-status question. A response may be spoken or acted — after an order to sit down, the
    /// other person sitting, or pointedly refusing, counts as the response arriving.
    /// </summary>
    internal static ChoiceQuestion ComposeResponseQuestion(string subject) => new(
        ResponseQuestionName,
        $"You are playing `{subject}` in an interactive roleplay scene. Look at the most recent thing `{subject}` "
            + "said or did in recent_history. Did it call for a response from someone else (for example a question, "
            + "a request or an order), and if so, has someone responded to it yet? A response can be words or "
            + "deeds: a reply, doing what was asked, or pointedly refusing all count.",
        [
            new DecisionOption(
                ExpectingPendingValue,
                $"`{subject}`'s most recent speech or action called for a response, and nothing after it in "
                    + "recent_history responds to it yet: nobody has replied, done what was asked, or refused."),
            new DecisionOption(
                ExpectingArrivedValue,
                $"`{subject}`'s most recent speech or action called for a response, and someone responded to it "
                    + "after it in recent_history, in words or in deeds: a reply, doing what was asked (for example "
                    + "sitting down after being told to sit, or handing over what was asked for), or pointedly "
                    + "refusing (for example staying put with arms crossed, or turning away)."),
            new DecisionOption(
                NotExpectingValue,
                $"`{subject}`'s most recent speech or action did not call for a response (for example a statement, "
                    + $"a remark, a farewell), or `{subject}` has not said or done anything yet."),
        ]);

    /// <summary>Composes the initiative question, keeping the group-scene de-biasing tail.</summary>
    internal static ChoiceQuestion ComposeInitiativeQuestion(string subject) => new(
        InitiativeQuestionName,
        $"You are playing `{subject}` in an interactive roleplay scene. Considering recent_history, the premise "
            + $"and `{subject}`'s character, how strongly does the current situation call for `{subject}` to speak "
            + "or act? In most moments of a group scene a character has no particular reason to act.",
        [
            new DecisionOption(
                MustActValue,
                $"The situation calls for `{subject}` to react: someone has just asked `{subject}` a question, made "
                    + "a request of them, addressed them directly, or done something to them."),
            new DecisionOption(
                MayActValue,
                $"Nobody is waiting on `{subject}`, but the latest events touch something `{subject}` cares about or "
                    + "is involved in, giving them a clear opening to interject, continue what they were doing, or "
                    + "start something."),
            new DecisionOption(
                NoReasonValue,
                $"`{subject}` has no particular reason to speak or act now: the latest events do not involve them, "
                    + "an exchange with them has just been wrapped up, others are talking among themselves, or "
                    + "staying quiet suits them."),
        ]);

}
