using System.Diagnostics;
using System.Text.Json;
using AlleyCat.Mind.AI.Decision;

namespace AlleyCat.Mind.Attention;

/// <summary>
/// One adapter-resolved cue candidate. <see cref="IsValid"/> represents resolution and authored enablement; the
/// policy applies no semantic, visibility, or geometry filters.
/// </summary>
/// <typeparam name="TTarget">Opaque target value that a caller can map to its concrete gaze target.</typeparam>
public sealed record AttentionGazeTargetCandidate<TTarget>(
    string SubjectFullId,
    string CueKey,
    int CueOrder,
    TTarget Target,
    double Attention,
    double Prominence,
    bool IsValid)
    where TTarget : notnull;

/// <summary>Describes the one optional target-assignment action required after a policy evaluation.</summary>
public enum AttentionGazeTargetAction
{
    /// <summary>No target assignment is required.</summary>
    None,

    /// <summary>Assign the accompanying target.</summary>
    SetLookTarget,

    /// <summary>Clear the current target and allow the presentation fallback to take effect.</summary>
    ClearLookTarget,
}

/// <summary>One unambiguous target-assignment decision for an adapter.</summary>
/// <typeparam name="TTarget">Opaque target value supplied by the corresponding candidate.</typeparam>
public readonly record struct AttentionGazeTargetDecision<TTarget>
    where TTarget : notnull
{
    private AttentionGazeTargetDecision(AttentionGazeTargetAction action, TTarget? target)
    {
        Action = action;
        Target = target;
    }

    /// <summary>Gets the required adapter action.</summary>
    public AttentionGazeTargetAction Action
    {
        get;
    }

    /// <summary>Gets the target to assign when <see cref="Action"/> is <see cref="AttentionGazeTargetAction.SetLookTarget"/>.</summary>
    public TTarget? Target
    {
        get;
    }

    internal static AttentionGazeTargetDecision<TTarget> None() => new(AttentionGazeTargetAction.None, default);

    internal static AttentionGazeTargetDecision<TTarget> Set(TTarget target)
        => new(AttentionGazeTargetAction.SetLookTarget, target);

    internal static AttentionGazeTargetDecision<TTarget> Clear()
        => new(AttentionGazeTargetAction.ClearLookTarget, default);
}

/// <summary>Shared model-facing text helpers for decision-gaze composition.</summary>
internal static class GazePromptText
{
    /// <summary>Renders canonical IDs as a comma-separated backticked list (AI-004 prose identity).</summary>
    public static string JoinBackticked(IReadOnlyList<string>? fullIds)
        => fullIds is null || fullIds.Count == 0
            ? string.Empty
            : string.Join(", ", fullIds.Select(fullId => $"`{fullId}`"));
}

/// <summary>Why one gaze evaluation ran; every non-cadence kind may carry character context.</summary>
internal enum GazeEvaluationTriggerKind
{
    /// <summary>Periodic <c>_Process</c> cadence boundary.</summary>
    Cadence,

    /// <summary>The owner's speak tool resolved valid declared recipients (pre-admission intention).</summary>
    SpeakInvoked,

    /// <summary>The owner's own speaking window closed.</summary>
    OwnSpeechEnded,

    /// <summary>An attended speaker's speech segment started or resumed.</summary>
    AttendedSpeakerStarted,

    /// <summary>An attended speaker's speaking window closed.</summary>
    AttendedSpeakerEnded,
}

/// <summary>One evaluation trigger with optional canonical character context, latest nudge wins.</summary>
/// <param name="Kind">Why the evaluation was requested.</param>
/// <param name="ContextCharacterFullIds">Canonical character IDs the trigger is about, when it carries any.</param>
internal sealed record GazeEvaluationTrigger(
    GazeEvaluationTriggerKind Kind,
    IReadOnlyList<string>? ContextCharacterFullIds = null)
{
    /// <summary>The trigger kind that carries no nudge context.</summary>
    public static GazeEvaluationTrigger Cadence { get; } = new(GazeEvaluationTriggerKind.Cadence);

    /// <summary>Gets the free-form diagnostics token for this trigger kind.</summary>
    public string Token => Kind switch
    {
        GazeEvaluationTriggerKind.Cadence => "Cadence",
        GazeEvaluationTriggerKind.SpeakInvoked => "SpeakInvoked",
        GazeEvaluationTriggerKind.OwnSpeechEnded => "OwnSpeechEnded",
        GazeEvaluationTriggerKind.AttendedSpeakerStarted => "AttendedSpeakerStarted",
        GazeEvaluationTriggerKind.AttendedSpeakerEnded => "AttendedSpeakerEnded",
        _ => Kind.ToString(),
    };
}

/// <summary>What the owning character is currently doing with speech.</summary>
internal enum GazeOwnActionKind
{
    /// <summary>No speaking and no attended speaker.</summary>
    Idle,

    /// <summary>Speaking (or about to speak) to declared recipients.</summary>
    SpeakingTo,

    /// <summary>Listening to an attended speaker.</summary>
    Hearing,
}

/// <summary>One own-action observation attached to a gaze evaluation.</summary>
/// <param name="Kind">What the owner is doing.</param>
/// <param name="CharacterFullIds">Canonical character IDs the action involves, when it carries any.</param>
internal sealed record GazeOwnAction(GazeOwnActionKind Kind, IReadOnlyList<string>? CharacterFullIds = null)
{
    /// <summary>The action that carries no character context.</summary>
    public static GazeOwnAction IdleAction { get; } = new(GazeOwnActionKind.Idle);

    /// <summary>Renders the model-facing own-action phrase.</summary>
    public string Describe()
    {
        return Kind switch
        {
            GazeOwnActionKind.Idle => "idle",
            GazeOwnActionKind.SpeakingTo => $"speaking_to: {GazePromptText.JoinBackticked(CharacterFullIds)}",
            GazeOwnActionKind.Hearing => $"hearing: {GazePromptText.JoinBackticked(CharacterFullIds)} (attended speaker)",
            _ => Kind.ToString(),
        };
    }
}

/// <summary>One character-anchored gaze candidate: the subject, not any single cue.</summary>
/// <param name="FullId">Canonical subject identity, used verbatim as the option value.</param>
/// <param name="Attention">Decayed attention value for the subject.</param>
/// <param name="BestCueKind">Kind word of the subject's most salient valid cue.</param>
/// <param name="BestCueProminence">Prominence of that best cue.</param>
internal sealed record GazeSubjectCandidate(
    string FullId,
    double Attention,
    string BestCueKind,
    double BestCueProminence);

/// <summary>Everything one decision-gaze evaluation needs, gathered on the Godot thread before dispatch.</summary>
/// <param name="OwnerFullId">The deciding character's canonical full ID.</param>
/// <param name="Candidates">Subject-anchored candidates, ordered attention descending then ID ordinal.</param>
/// <param name="OwnAction">What the owner is doing with speech at gathering time.</param>
/// <param name="CurrentGazeSubjectFullId">Currently applied gaze subject, or null when the gaze is clear.</param>
/// <param name="CurrentGazeDwellSeconds">Seconds the current gaze subject has been applied.</param>
/// <param name="Trigger">Why this evaluation is running.</param>
/// <param name="RecentEvents">Bounded canonical renderings of recent observations, oldest first.</param>
/// <param name="ScenePremise">The verbatim authored scenario premise resolved once per selector lifetime, or
/// null when none is available; instructions compose it after an explicit framing label.</param>
internal sealed record GazeEvaluationInput(
    string OwnerFullId,
    IReadOnlyList<GazeSubjectCandidate> Candidates,
    GazeOwnAction OwnAction,
    string? CurrentGazeSubjectFullId,
    double CurrentGazeDwellSeconds,
    GazeEvaluationTrigger Trigger,
    IReadOnlyList<string> RecentEvents,
    string? ScenePremise = null);

/// <summary>What one completed evaluation decided.</summary>
internal enum GazeDecisionKind
{
    /// <summary>Look at the chosen subject; the adapter resolves the subject's best cue.</summary>
    ChooseSubject,

    /// <summary>Nothing in particular; clear the gaze target.</summary>
    None,

    /// <summary>Every failure mode: keep the current target unchanged.</summary>
    Failure,

    /// <summary>The evaluation was cancelled (node exit); apply nothing.</summary>
    Cancelled,
}

/// <summary>One resolved gaze decision with its safe failure detail when contained.</summary>
internal sealed record GazeDecision(GazeDecisionKind Kind, string? SubjectFullId, string? Failure)
{
    /// <summary>Creates a subject decision.</summary>
    public static GazeDecision ChooseSubject(string subjectFullId)
        => new(GazeDecisionKind.ChooseSubject, subjectFullId, null);

    /// <summary>Creates a nothing-in-particular decision.</summary>
    public static GazeDecision NoneDecision() => new(GazeDecisionKind.None, null, null);

    /// <summary>Creates a contained failure decision that keeps the current target.</summary>
    public static GazeDecision Failed(string failure) => new(GazeDecisionKind.Failure, null, failure);

    /// <summary>Creates a cancelled decision that applies nothing.</summary>
    public static GazeDecision CancelledDecision() => new(GazeDecisionKind.Cancelled, null, null);
}

/// <summary>One completed evaluation's decision plus the verbatim dispatch material and timings.</summary>
/// <param name="Decision">The resolved decision.</param>
/// <param name="Confidence">Provider confidence when present; null when omitted or on failure.</param>
/// <param name="CompositionMilliseconds">Evidence and question composition time.</param>
/// <param name="DecisionMilliseconds">Decision round-trip time.</param>
/// <param name="EvidenceCharacters">Serialised evidence payload length.</param>
/// <param name="Evidence">The evidence JSON exactly as dispatched, or null when composition failed.</param>
/// <param name="Questions">Every dispatched question's name and verbatim instructions.</param>
internal sealed record GazeEvaluationResult(
    GazeDecision Decision,
    double? Confidence,
    long CompositionMilliseconds,
    long DecisionMilliseconds,
    int EvidenceCharacters,
    JsonElement? Evidence,
    IReadOnlyList<DecisionPayloadQuestion> Questions);

/// <summary>
/// Composes subject-anchored candidates from adapter-resolved cue candidates: eligibility filtering, per-subject
/// anchoring, deterministic best-cue selection, and a stable subject order.
/// </summary>
internal static class GazeSubjectCandidates
{
    /// <summary>
    /// Groups eligible cue candidates by subject and picks each subject's best cue by
    /// (prominence descending, cue order ascending). Subjects are returned attention descending, then full ID
    /// ordinal ascending.
    /// </summary>
    public static IReadOnlyList<GazeSubjectCandidate> Compose<TTarget>(
        IReadOnlyList<AttentionGazeTargetCandidate<TTarget>> cueCandidates)
        where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(cueCandidates);

        Dictionary<string, AttentionGazeTargetCandidate<TTarget>> best = new(StringComparer.Ordinal);
        for (int index = 0; index < cueCandidates.Count; index++)
        {
            AttentionGazeTargetCandidate<TTarget> candidate = cueCandidates[index]
                ?? throw new ArgumentException($"Candidate at index {index} cannot be null.", nameof(cueCandidates));
            if (!IsEligible(candidate))
            {
                continue;
            }

            if (best.TryGetValue(candidate.SubjectFullId, out AttentionGazeTargetCandidate<TTarget>? current)
                && !IsBetterCue(candidate, current))
            {
                continue;
            }

            best[candidate.SubjectFullId] = candidate;
        }

        List<GazeSubjectCandidate> subjects = new(best.Count);
        foreach (KeyValuePair<string, AttentionGazeTargetCandidate<TTarget>> entry in best)
        {
            subjects.Add(new GazeSubjectCandidate(
                entry.Key,
                entry.Value.Attention,
                entry.Value.CueKey,
                entry.Value.Prominence));
        }

        subjects.Sort(static (left, right) =>
        {
            int attentionComparison = right.Attention.CompareTo(left.Attention);
            return attentionComparison != 0
                ? attentionComparison
                : string.CompareOrdinal(left.FullId, right.FullId);
        });
        return subjects;
    }

    private static bool IsEligible<TTarget>(AttentionGazeTargetCandidate<TTarget> candidate)
        where TTarget : notnull
        => candidate.IsValid
            && candidate.Target is not null
            && double.IsFinite(candidate.Attention)
            && candidate.Attention >= 0d
            && double.IsFinite(candidate.Prominence)
            && candidate.Prominence > 0d;

    private static bool IsBetterCue<TTarget>(
        AttentionGazeTargetCandidate<TTarget> candidate,
        AttentionGazeTargetCandidate<TTarget> current)
        where TTarget : notnull
    {
        int prominenceComparison = candidate.Prominence.CompareTo(current.Prominence);
        return prominenceComparison != 0 ? prominenceComparison > 0 : candidate.CueOrder < current.CueOrder;
    }
}

/// <summary>Failure backoff schedule: 2× the base interval on first failure, doubling thereafter, capped.</summary>
internal static class GazeFailureBackoff
{
    /// <summary>The maximum backoff delay in seconds.</summary>
    public const double MaximumSeconds = 30d;

    /// <summary>Computes the next backoff delay after one more consecutive failure.</summary>
    /// <param name="currentSeconds">The backoff in effect for the failed evaluation; zero when none.</param>
    /// <param name="baseIntervalSeconds">The evaluation cadence interval.</param>
    public static double NextSeconds(double currentSeconds, double baseIntervalSeconds)
    {
        if (!double.IsFinite(baseIntervalSeconds) || baseIntervalSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseIntervalSeconds),
                baseIntervalSeconds,
                "Base interval must be finite and positive.");
        }

        if (!double.IsFinite(currentSeconds) || currentSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentSeconds),
                currentSeconds,
                "Current backoff must be finite and non-negative.");
        }

        double next = currentSeconds <= 0d ? baseIntervalSeconds * 2d : currentSeconds * 2d;
        return Math.Min(next, MaximumSeconds);
    }
}

/// <summary>
/// Decision-model-driven gaze policy: composes one lean choice question over character-anchored candidates,
/// dispatches it through the shared decision client, and validates the answer into a contained decision. The model
/// picks a subject or <c>none</c>; cue resolution stays deterministic and adapter-side.
/// </summary>
/// <remarks>
/// Every failure mode — typed decision failures, refusals, malformed, missing, or unknown answers — resolves to a
/// <see cref="GazeDecisionKind.Failure"/> decision that keeps the current target; only caller cancellation
/// propagates. The policy never throws for provider-side causes and never touches Godot state, so callers may drive
/// it from a Godot node's process callback.
/// </remarks>
internal sealed class DecisionGazePolicy
{
    /// <summary>Model-facing identity of the gaze choice question.</summary>
    internal const string QuestionName = "gaze_target";

    /// <summary>The fixed option value for looking at nothing in particular.</summary>
    internal const string NoneOptionValue = "none";

    /// <summary>
    /// The character budget for the composed scene premise (playtest R1): the verbatim premise text up to this
    /// many characters, cut at the last whitespace and marked with an ellipsis when it is longer, keeping the
    /// question lean while carrying the authored opening that states the setup.
    /// </summary>
    internal const int ScenePremiseCharacterBudget = 600;

    /// <summary>The framing label introducing the scene premise inside the question instructions.</summary>
    internal const string ScenePremiseLabel = "Scene premise:";

    private readonly IDecisionClient _client;

    public DecisionGazePolicy(IDecisionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Evaluates one gaze decision. Composition runs synchronously before the first await.</summary>
    public async Task<GazeEvaluationResult> EvaluateAsync(
        GazeEvaluationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        long compositionStart = Stopwatch.GetTimestamp();
        ChoiceQuestion question = ComposeQuestion(input);
        JsonElement evidence = ComposeEvidence(input);
        IReadOnlyList<DecisionPayloadQuestion> questions =
            [DescribeDispatchedQuestion(question)];
        long compositionEnd = Stopwatch.GetTimestamp();

        long decisionStart = Stopwatch.GetTimestamp();
        try
        {
            DecisionResult result = await _client
                .EvaluateAsync(new DecisionRequest(evidence, [question]), cancellationToken)
                .ConfigureAwait(false);
            long decisionEnd = Stopwatch.GetTimestamp();
            return ResolveAnswer(input, result, evidence, questions, compositionStart, compositionEnd, decisionStart, decisionEnd);
        }
        catch (DecisionException exception)
        {
            long decisionEnd = Stopwatch.GetTimestamp();
            return CreateFailure(
                $"decision failure {exception.Failure}",
                evidence,
                questions,
                compositionStart,
                compositionEnd,
                decisionStart,
                decisionEnd);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            long decisionEnd = Stopwatch.GetTimestamp();
            return CreateFailure(
                "unexpected evaluation failure",
                evidence,
                questions,
                compositionStart,
                compositionEnd,
                decisionStart,
                decisionEnd);
        }
    }

    private static GazeEvaluationResult ResolveAnswer(
        GazeEvaluationInput input,
        DecisionResult result,
        JsonElement evidence,
        IReadOnlyList<DecisionPayloadQuestion> questions,
        long compositionStart,
        long compositionEnd,
        long decisionStart,
        long decisionEnd)
    {
        GazeDecision decision;
        double? confidence = null;
        if (!result.ByName.TryGetValue(QuestionName, out DecisionAnswer? answer) || answer is null)
        {
            decision = GazeDecision.Failed("missing answer");
        }
        else if (answer is DecisionRefusal)
        {
            decision = GazeDecision.Failed("refusal");
        }
        else if (answer is not ChoiceAnswer choice)
        {
            decision = GazeDecision.Failed("unexpected answer kind");
        }
        else
        {
            confidence = choice.Confidence;
            decision = choice.Value switch
            {
                NoneOptionValue => GazeDecision.NoneDecision(),
                var value when input.Candidates.Any(candidate => candidate.FullId == value)
                    => GazeDecision.ChooseSubject(choice.Value),
                _ => GazeDecision.Failed("unknown choice value"),
            };
        }

        return new GazeEvaluationResult(
            decision,
            confidence,
            ElapsedMilliseconds(compositionStart, compositionEnd),
            ElapsedMilliseconds(decisionStart, decisionEnd),
            evidence.GetRawText().Length,
            evidence,
            questions);
    }

    private static GazeEvaluationResult CreateFailure(
        string failure,
        JsonElement evidence,
        IReadOnlyList<DecisionPayloadQuestion> questions,
        long compositionStart,
        long compositionEnd,
        long decisionStart,
        long decisionEnd)
        => new(
            GazeDecision.Failed(failure),
            null,
            ElapsedMilliseconds(compositionStart, compositionEnd),
            ElapsedMilliseconds(decisionStart, decisionEnd),
            evidence.GetRawText().Length,
            evidence,
            questions);

    private static long ElapsedMilliseconds(long start, long end)
        => (long)Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    private static ChoiceQuestion ComposeQuestion(GazeEvaluationInput input)
    {
        List<DecisionOption> options = new(input.Candidates.Count + 1);
        foreach (GazeSubjectCandidate candidate in OrderCandidates(input.Candidates))
        {
            options.Add(new DecisionOption(candidate.FullId, DescribeCandidate(candidate)));
        }

        options.Add(new DecisionOption(
            NoneOptionValue,
            "nothing in particular — no one deserves attention right now."));
        return new ChoiceQuestion(QuestionName, ComposeInstructions(input), options.AsReadOnly());
    }

    /// <summary>
    /// Orders candidates attention descending then full ID ordinal ascending, enforcing the deterministic
    /// model-facing option order regardless of the caller's composition order.
    /// </summary>
    private static IEnumerable<GazeSubjectCandidate> OrderCandidates(IReadOnlyList<GazeSubjectCandidate> candidates)
        => candidates.OrderByDescending(static candidate => candidate.Attention)
            .ThenBy(static candidate => candidate.FullId, StringComparer.Ordinal);

    private static string DescribeCandidate(GazeSubjectCandidate candidate)
        => $"`{candidate.FullId}`'s face/appearance; attention {candidate.Attention:0.##}; "
            + $"most salient cue: {candidate.BestCueKind}.";

    /// <summary>
    /// Captures one dispatched question's verbatim dump material: name, instructions, and — for a choice
    /// question — every offered option's value and criteria, so dumps show the full candidate list.
    /// </summary>
    private static DecisionPayloadQuestion DescribeDispatchedQuestion(ChoiceQuestion question)
        => new(
            question.Name,
            question.Instructions,
            [.. question.Options.Select(
                static option => new DecisionPayloadQuestionOption(option.Value, option.Criteria))]);

    private static string ComposeInstructions(GazeEvaluationInput input)
    {
        string context = DescribeContextSentence(input) ?? string.Empty;
        string premise = DescribeScenePremise(input.ScenePremise);
        return $"You are deciding where `{input.OwnerFullId}` should look. "
            + context
            + premise
            + $"Choose the character present in the scene who most deserves `{input.OwnerFullId}`'s visual "
            + $"attention right now, or `{NoneOptionValue}`. Looking at the person being addressed or the "
            + $"person speaking is usually right. `{NoneOptionValue}` — nothing in particular — is a "
            + "valid, common choice; do not stare.";
    }

    /// <summary>
    /// Renders the scene premise under its framing label, or nothing when absent: the verbatim authored text up
    /// to <see cref="ScenePremiseCharacterBudget"/> characters, cut at the last whitespace with an ellipsis when
    /// longer.
    /// </summary>
    private static string DescribeScenePremise(string? scenePremise)
    {
        if (string.IsNullOrWhiteSpace(scenePremise))
        {
            return string.Empty;
        }

        string premise = scenePremise.Trim();
        if (premise.Length <= ScenePremiseCharacterBudget)
        {
            return $"{ScenePremiseLabel} {premise} ";
        }

        int cut = premise.LastIndexOf(' ', ScenePremiseCharacterBudget);
        if (cut <= 0)
        {
            cut = ScenePremiseCharacterBudget;
        }

        return $"{ScenePremiseLabel} {premise[..cut]}… ";
    }

    /// <summary>
    /// The one lean context sentence: trigger-specific first (freshest signal), otherwise the standing own action.
    /// </summary>
    private static string? DescribeContextSentence(GazeEvaluationInput input)
    {
        if (input.Trigger.Kind == GazeEvaluationTriggerKind.SpeakInvoked
            && input.Trigger.ContextCharacterFullIds is { Count: > 0 } recipients)
        {
            return $"You are about to speak to {GazePromptText.JoinBackticked(recipients)}. ";
        }

        if (input.Trigger.Kind == GazeEvaluationTriggerKind.AttendedSpeakerEnded
            && input.Trigger.ContextCharacterFullIds is { Count: > 0 } heard)
        {
            return $"You have just heard {GazePromptText.JoinBackticked(heard)} finish speaking. ";
        }

        if (input.Trigger.Kind == GazeEvaluationTriggerKind.OwnSpeechEnded)
        {
            return "You have just finished speaking. ";
        }

        GazeOwnAction ownAction = input.OwnAction;
        return ownAction switch
        {
            { Kind: GazeOwnActionKind.SpeakingTo, CharacterFullIds: { Count: > 0 } addressed }
                => $"You are currently speaking to {GazePromptText.JoinBackticked(addressed)}. ",
            { Kind: GazeOwnActionKind.Hearing, CharacterFullIds: { Count: > 0 } speaker }
                => $"You are currently listening to {GazePromptText.JoinBackticked(speaker)}, who is speaking. ",
            _ => null,
        };
    }

    private static JsonElement ComposeEvidence(GazeEvaluationInput input)
    {
        Dictionary<string, object?> evidence = new()
        {
            ["owner"] = input.OwnerFullId,
            ["own_action"] = input.OwnAction.Describe(),
            ["current_gaze"] = input.CurrentGazeSubjectFullId is { } subject
                ? new Dictionary<string, object?>
                {
                    ["subject"] = subject,
                    ["dwell_seconds"] = Math.Round(input.CurrentGazeDwellSeconds, 1),
                }
                : new Dictionary<string, object?> { ["subject"] = NoneOptionValue },
            ["candidates"] = OrderCandidates(input.Candidates)
                .Select(candidate => new Dictionary<string, object?>
                {
                    ["id"] = candidate.FullId,
                    ["attention"] = Math.Round(candidate.Attention, 3),
                    ["best_cue"] = candidate.BestCueKind,
                })
                .ToArray(),
            ["recent_events"] = input.RecentEvents.ToArray(),
        };

        return JsonSerializer.SerializeToElement(evidence);
    }

}
