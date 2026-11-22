using System.Diagnostics;
using System.Text.Json;
using AlleyCat.Mind.Attention;

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

    /// <summary>Gets the one-line model-facing evidence phrase naming what prompted this evaluation.</summary>
    public string DescribeEvidence() => Kind switch
    {
        DecisionWaitTriggerKind.Cadence => "periodic check while waiting",
        DecisionWaitTriggerKind.PlayerSpeech => "the player just said something",
        DecisionWaitTriggerKind.AttendedSpeakerStart => "a speaker this character attends to just started speaking",
        DecisionWaitTriggerKind.AttendedSpeakerEnd => "a speaker this character attends to just finished speaking",
        DecisionWaitTriggerKind.MembershipChange => "the characters present in the scene changed",
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

/// <summary>Everything one wait-decision evaluation needs, composed on the Godot thread before dispatch.</summary>
/// <param name="OwnerFullId">The waiting character's canonical full ID.</param>
/// <param name="SceneCharacters">The scene's other characters with their lore descriptions.</param>
/// <param name="RecentEvents">Bounded canonical renderings of recent observations, oldest first.</param>
/// <param name="OwnAction">What the waiting character is doing with speech.</param>
/// <param name="CurrentGazeCueValue">The character's applied gaze cue value (<c>subject#cue</c>), or null when clear.</param>
/// <param name="CurrentGazeDwellSeconds">Seconds the current gaze cue has been applied.</param>
/// <param name="Trigger">Why this evaluation is running.</param>
/// <param name="ScenePremise">The verbatim authored scenario premise, or null when none is available.</param>
internal sealed record DecisionWaitEvaluationInput(
    string OwnerFullId,
    IReadOnlyList<DecisionWaitSceneCharacter> SceneCharacters,
    IReadOnlyList<string> RecentEvents,
    DecisionWaitOwnAction OwnAction,
    string? CurrentGazeCueValue,
    double CurrentGazeDwellSeconds,
    DecisionWaitTrigger Trigger,
    string? ScenePremise = null);

/// <summary>One scene character in the wait evidence roster, summarised by lore.</summary>
/// <param name="FullId">Canonical character identity.</param>
/// <param name="Description">The character's authored lore description, or null when none is available.</param>
internal sealed record DecisionWaitSceneCharacter(string FullId, string? Description = null);

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
internal sealed class DecisionWaitPolicy
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

    private readonly IDecisionClient _client;

    public DecisionWaitPolicy(IDecisionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

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

    /// <summary>Evaluates one wait decision. Composition runs synchronously before the first await.</summary>
    public async Task<DecisionWaitEvaluationResult> EvaluateAsync(
        DecisionWaitEvaluationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        long compositionStart = Stopwatch.GetTimestamp();
        ChoiceQuestion response = ComposeResponseQuestion(input.OwnerFullId);
        ChoiceQuestion initiative = ComposeInitiativeQuestion(input.OwnerFullId);
        JsonElement evidence = ComposeEvidence(input);
        IReadOnlyList<DecisionPayloadQuestion> dispatched = [Describe(response), Describe(initiative)];
        long compositionEnd = Stopwatch.GetTimestamp();

        long decisionStart = Stopwatch.GetTimestamp();
        DecisionWaitDecision decision;
        List<DecisionPayloadAnswer> answers = [];
        try
        {
            DecisionResult result = await _client
                .EvaluateAsync(new DecisionRequest(evidence, [response, initiative]), cancellationToken)
                .ConfigureAwait(false);
            decision = ResolveAnswers(result, answers);
        }
        catch (DecisionException exception)
        {
            decision = DecisionWaitDecision.Failed($"decision failure {exception.Failure}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            decision = DecisionWaitDecision.Failed("unexpected evaluation failure");
        }

        long decisionEnd = Stopwatch.GetTimestamp();
        return new DecisionWaitEvaluationResult(
            decision,
            answers,
            ElapsedMilliseconds(compositionStart, compositionEnd),
            ElapsedMilliseconds(decisionStart, decisionEnd),
            evidence.GetRawText().Length,
            evidence,
            dispatched);
    }

    private static DecisionWaitDecision ResolveAnswers(DecisionResult result, List<DecisionPayloadAnswer> answers)
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
        DecisionResult result,
        string name,
        List<DecisionPayloadAnswer> answers,
        out string? value)
    {
        value = null;
        if (!result.ByName.TryGetValue(name, out DecisionAnswer? answer) || answer is null)
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

        answers.Add(new DecisionPayloadAnswer(name, choice.Value, choice.Probabilities, choice.Confidence));
        value = choice.Value;
        return null;
    }

    private static long ElapsedMilliseconds(long start, long end)
        => (long)Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    private static DecisionPayloadQuestion Describe(ChoiceQuestion question)
        => new(
            question.Name,
            question.Instructions,
            [.. question.Options.Select(static option => new DecisionPayloadQuestionOption(option.Value, option.Criteria))]);

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

    /// <summary>
    /// Renders the verbatim authored scene premise for the evidence's premise field, or nothing when absent: the
    /// text up to the gaze character budget, cut at the last whitespace with an ellipsis when longer. The wait
    /// questions carry no premise text — the context owns it.
    /// </summary>
    private static string? DescribeScenePremise(string? scenePremise)
    {
        if (string.IsNullOrWhiteSpace(scenePremise))
        {
            return null;
        }

        string premise = scenePremise.Trim();
        if (premise.Length <= DecisionGazePolicy.ScenePremiseCharacterBudget)
        {
            return premise;
        }

        int cut = premise.LastIndexOf(' ', DecisionGazePolicy.ScenePremiseCharacterBudget);
        if (cut <= 0)
        {
            cut = DecisionGazePolicy.ScenePremiseCharacterBudget;
        }

        return $"{premise[..cut]}…";
    }

    private static JsonElement ComposeEvidence(DecisionWaitEvaluationInput input)
    {
        Dictionary<string, object?> evidence = [];

        // The premise field appears only when a premise exists; absence is an omitted field, never a null.
        if (DescribeScenePremise(input.ScenePremise) is { } premise)
        {
            evidence.Add("premise", premise);
        }

        evidence.Add("characters", input.SceneCharacters
                .Select(static character => character.Description is null
                    ? new Dictionary<string, object?> { ["id"] = character.FullId }
                    : new Dictionary<string, object?>
                    {
                        ["id"] = character.FullId,
                        ["description"] = character.Description,
                    })
                .ToArray());
        // The history lines are first-person renderings from the owner's perspective; the label states whose
        // experiences they are so the "you" of the question and the "I" of the history can never diverge.
        evidence.Add("recent_history", new Dictionary<string, object?>
        {
            ["perspective"] = input.OwnerFullId,
            ["note"] = $"first-person lines are from `{input.OwnerFullId}`'s perspective, as they experienced them",
            ["events"] = input.RecentEvents.ToArray(),
        });
        evidence.Add("own_action", input.OwnAction.Describe());
        evidence.Add("current_gaze", new Dictionary<string, object?>
        {
            ["cue"] = input.CurrentGazeCueValue,
            ["dwell_seconds"] = Math.Round(input.CurrentGazeDwellSeconds, 1),
        });
        evidence.Add("trigger", input.Trigger.DescribeEvidence());

        return JsonSerializer.SerializeToElement(evidence);
    }
}
