using System.Net;
using System.Text.Json;
using AlleyCat.Mind.AI.Decision;
using AlleyCat.Mind.Attention;
using Xunit;

namespace AlleyCat.Tests.Mind.Attention;

/// <summary>
/// Deterministic coverage for the decision-driven gaze policy: subject-anchored candidate composition with
/// deterministic best-cue selection, lean question and evidence shape, answer validation, containment per failure
/// mode, and the failure backoff schedule.
/// </summary>
public sealed class DecisionGazePolicyTests
{
    private const string Owner = "char:observer";

    /// <summary>
    /// One option per cue, character-anchored: subjects attention-descending then cues prominence-descending, each
    /// criteria documenting its exact return value, with the fixed none option last.
    /// </summary>
    [Fact]
    public async Task Evaluate_ComposesCueLevelOptionsWithAFixedNoneOption()
    {
        ScriptedDecisionClient client = new();
        DecisionGazePolicy policy = new(client);
        List<GazeCueCandidate> candidates =
        [
            Candidate("char:alpha", cueKey: "face", attention: 0.42d, prominence: 1d, description: "A kind face."),
            Candidate("char:bravo", cueKey: "body", attention: 0.9d, prominence: 2d),
            Candidate("char:bravo", cueKey: "hands", attention: 0.9d, prominence: 3d),
        ];
        _ = await policy.EvaluateAsync(CreateInput(candidates));

        DecisionRequest request = Assert.Single(client.Requests);
        ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(Assert.Single(request.Questions), exactMatch: false);
        Assert.Equal(DecisionGazePolicy.QuestionName, question.Name);

        Assert.Equal(
            ["char:bravo#hands", "char:bravo#body", "char:alpha#face", "none"],
            question.Options.Select(option => option.Value));
        Assert.All(question.Options, option => Assert.False(string.IsNullOrWhiteSpace(option.Criteria)));
        Assert.Equal(
            "`char:bravo#hands` — hands: hands (attention 0.9, prominence 3.00)",
            question.Options[0].Criteria);
        Assert.Equal(
            "`char:alpha#face` — face: A kind face. (attention 0.42, prominence 1.00)",
            question.Options[2].Criteria);
    }

    /// <summary>Instructions carry the identity and framing sentences; evidence carries own action, current gaze dwell, and recent events.</summary>
    [Fact]
    public async Task Evaluate_ComposesLeanInstructionsAndBoundedEvidence()
    {
        ScriptedDecisionClient client = new();
        DecisionGazePolicy policy = new(client);
        GazeEvaluationInput input = CreateInput(
            [],
            ownAction: new GazeOwnAction(GazeOwnActionKind.SpeakingTo, ["char:tola"]),
            currentGazeCueValue: "char:tola#face",
            currentGazeDwellSeconds: 3.24d,
            recentEvents: ["`char:tola` said: hello"]);
        _ = await policy.EvaluateAsync(input);

        DecisionRequest request = Assert.Single(client.Requests);
        ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(Assert.Single(request.Questions), exactMatch: false);

        Assert.StartsWith("You are deciding where `char:observer` should look. ", question.Instructions);
        Assert.Contains("You are currently speaking to `char:tola`. ", question.Instructions);
        Assert.Contains("most likely place `char:observer` would put their eyes right now", question.Instructions);
        Assert.Contains("Gazes naturally wander", question.Instructions);
        Assert.Contains("weak situational hints, never a ranking", question.Instructions);
        Assert.Contains("or `none`", question.Instructions);
        Assert.Contains("valid, common choice; do not stare.", question.Instructions);

        JsonElement evidence = Assert.IsType<JsonElement>(request.Evidence);
        Assert.Equal(Owner, evidence.GetProperty("owner").GetString());
        Assert.Equal("speaking_to: `char:tola`", evidence.GetProperty("own_action").GetString());
        Assert.Equal("char:tola#face", evidence.GetProperty("current_gaze").GetProperty("cue").GetString());
        Assert.Equal(3.2d, evidence.GetProperty("current_gaze").GetProperty("dwell_seconds").GetDouble());
        Assert.Equal("`char:tola` said: hello", evidence.GetProperty("recent_events")[0].GetString());
        _ = Assert.Throws<KeyNotFoundException>(() => evidence.GetProperty("candidates"));
    }

    /// <summary>
    /// The evidence roster carries the scene's characters with their lore descriptions — bare identities when
    /// absent — and a clear current gaze reports the none token with the cue-level form otherwise.
    /// </summary>
    [Fact]
    public async Task Evaluate_ComposesTheSceneCharacterRosterAndCueLevelCurrentGaze()
    {
        ScriptedDecisionClient client = new();
        DecisionGazePolicy policy = new(client);
        GazeEvaluationInput input = CreateInput(
            [],
            sceneCharacters:
            [
                new GazeSceneCharacter("char:bravo", "A sturdily built man."),
                new GazeSceneCharacter("char:delta"),
            ]);
        _ = await policy.EvaluateAsync(input);

        JsonElement evidence = Assert.IsType<JsonElement>(Assert.Single(client.Requests).Evidence);
        JsonElement characters = evidence.GetProperty("characters");
        Assert.Equal(2, characters.GetArrayLength());
        Assert.Equal("char:bravo", characters[0].GetProperty("id").GetString());
        Assert.Equal("A sturdily built man.", characters[0].GetProperty("description").GetString());
        Assert.Equal("char:delta", characters[1].GetProperty("id").GetString());
        _ = Assert.Throws<KeyNotFoundException>(() => characters[1].GetProperty("description"));
        Assert.Equal(
            DecisionGazePolicy.NoneOptionValue,
            evidence.GetProperty("current_gaze").GetProperty("cue").GetString());
    }

    /// <summary>A trigger-specific context sentence precedes the standing own-action sentence.</summary>
    [Fact]
    public async Task Evaluate_AppliesTriggerContextSentenceBeforeStandingOwnAction()
    {
        ScriptedDecisionClient client = new();
        DecisionGazePolicy policy = new(client);
        GazeEvaluationInput input = CreateInput(
            [],
            ownAction: GazeOwnAction.IdleAction,
            trigger: new GazeEvaluationTrigger(GazeEvaluationTriggerKind.SpeakInvoked, ["char:tola"]));
        _ = await policy.EvaluateAsync(input);

        ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(
            Assert.Single(Assert.Single(client.Requests).Questions),
            exactMatch: false);
        Assert.Contains("You are about to speak to `char:tola`. ", question.Instructions);
    }

    /// <summary>A valid chosen cue value resolves to a cue decision carrying the provider confidence.</summary>
    [Fact]
    public async Task Evaluate_ValidChosenCueValueResolvesToChooseCue()
    {
        ScriptedDecisionClient client = new(answerValue: "char:alpha#body");
        DecisionGazePolicy policy = new(client);
        GazeEvaluationResult result = await policy.EvaluateAsync(
            CreateInput([Candidate("char:alpha", cueKey: "body", attention: 0.5d, prominence: 1d)]));

        Assert.Equal(GazeDecisionKind.ChooseCue, result.Decision.Kind);
        Assert.Equal("char:alpha#body", result.Decision.CueValue);
        Assert.Null(result.Decision.Failure);
        Assert.Equal(0.9d, result.Confidence);
    }

    /// <summary>The none answer resolves to a clear decision.</summary>
    [Fact]
    public async Task Evaluate_NoneAnswerResolvesToNone()
    {
        ScriptedDecisionClient client = new(answerValue: DecisionGazePolicy.NoneOptionValue);
        DecisionGazePolicy policy = new(client);
        GazeEvaluationResult result = await policy.EvaluateAsync(
            CreateInput([Candidate("char:alpha", cueKey: "body", attention: 0.5d, prominence: 1d)]));

        Assert.Equal(GazeDecisionKind.None, result.Decision.Kind);
    }

    /// <summary>An explicit refusal is contained as a failure that keeps the current target.</summary>
    [Fact]
    public async Task Evaluate_RefusalIsContainedAsAFailure()
    {
        ScriptedDecisionClient client = new(refusal: true);
        DecisionGazePolicy policy = new(client);
        GazeEvaluationResult result = await policy.EvaluateAsync(CreateInput([]));

        Assert.Equal(GazeDecisionKind.Failure, result.Decision.Kind);
        Assert.Equal("refusal", result.Decision.Failure);
        Assert.Null(result.Confidence);
    }

    /// <summary>A missing answer for the gaze question is contained as a failure.</summary>
    [Fact]
    public async Task Evaluate_MissingAnswerIsContainedAsAFailure()
    {
        ScriptedDecisionClient client = new(omitAnswer: true);
        DecisionGazePolicy policy = new(client);
        GazeEvaluationResult result = await policy.EvaluateAsync(CreateInput([]));

        Assert.Equal(GazeDecisionKind.Failure, result.Decision.Kind);
        Assert.Equal("missing answer", result.Decision.Failure);
    }

    /// <summary>A choice value outside the offered options is contained as a failure.</summary>
    [Fact]
    public async Task Evaluate_UnknownChoiceValueIsContainedAsAFailure()
    {
        ScriptedDecisionClient client = new(answerValue: "char:hallucinated#face");
        DecisionGazePolicy policy = new(client);
        GazeEvaluationResult result = await policy.EvaluateAsync(
            CreateInput([Candidate("char:alpha", cueKey: "body", attention: 0.5d, prominence: 1d)]));

        Assert.Equal(GazeDecisionKind.Failure, result.Decision.Kind);
        Assert.Equal("unknown choice value", result.Decision.Failure);
    }

    /// <summary>Every typed decision failure kind is contained with its failure kind in the safe detail.</summary>
    [Theory]
    [InlineData(DecisionFailure.Configuration)]
    [InlineData(DecisionFailure.Input)]
    [InlineData(DecisionFailure.Unsupported)]
    [InlineData(DecisionFailure.Timeout)]
    [InlineData(DecisionFailure.Transport)]
    [InlineData(DecisionFailure.HTTP)]
    [InlineData(DecisionFailure.InvalidResponse)]
    public async Task Evaluate_TypedDecisionFailuresAreContainedWithTheFailureKind(DecisionFailure failure)
    {
        ScriptedDecisionClient client = new(decisionFailure: failure);
        DecisionGazePolicy policy = new(client);
        GazeEvaluationResult result = await policy.EvaluateAsync(CreateInput([]));

        Assert.Equal(GazeDecisionKind.Failure, result.Decision.Kind);
        Assert.Equal($"decision failure {failure}", result.Decision.Failure);
        Assert.Equal(DecisionGazePolicy.QuestionName, Assert.Single(result.Questions).Name);
        _ = Assert.NotNull(result.Evidence);
    }

    /// <summary>An unexpected exception is contained as a failure with the verbatim dispatch material preserved.</summary>
    [Fact]
    public async Task Evaluate_UnexpectedExceptionIsContainedAsAFailure()
    {
        ScriptedDecisionClient client = new(throwUnexpected: true);
        DecisionGazePolicy policy = new(client);
        GazeEvaluationResult result = await policy.EvaluateAsync(CreateInput([]));

        Assert.Equal(GazeDecisionKind.Failure, result.Decision.Kind);
        Assert.Equal("unexpected evaluation failure", result.Decision.Failure);
    }

    /// <summary>Caller cancellation propagates instead of resolving through containment.</summary>
    [Fact]
    public async Task Evaluate_CallerCancellationPropagates()
    {
        ScriptedDecisionClient client = new();
        DecisionGazePolicy policy = new(client);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => policy.EvaluateAsync(CreateInput([]), cancellation.Token));
    }

    /// <summary>
    /// Composition offers every subject's cues — prominence descending, cue order ascending — with subjects
    /// attention descending then full ID ordinal.
    /// </summary>
    [Fact]
    public void Compose_OffersPerSubjectCuesInDeterministicOrder()
    {
        List<AttentionGazeTargetCandidate<string>> cueCandidates =
        [
            Cue("char:alpha", cueKey: "body", cueOrder: 0, attention: 0.4d, prominence: 1d),
            Cue("char:alpha", cueKey: "face", cueOrder: 1, attention: 0.4d, prominence: 3d),
            Cue("char:alpha", cueKey: "hands", cueOrder: 2, attention: 0.4d, prominence: 3d),
            Cue("char:bravo", cueKey: "face", cueOrder: 1, attention: 0.9d, prominence: 2d),
            Cue("char:bravo", cueKey: "body", cueOrder: 0, attention: 0.9d, prominence: 2d),
        ];

        IReadOnlyList<GazeCueCandidate> composed = GazeCueCandidates.Compose(cueCandidates);

        Assert.Equal(
            ["char:bravo#body", "char:bravo#face", "char:alpha#face", "char:alpha#hands", "char:alpha#body"],
            composed.Select(candidate => candidate.Value));
    }

    /// <summary>Ineligible cue candidates are skipped, and separator-bearing cue keys are never offered.</summary>
    [Fact]
    public void Compose_SkipsIneligibleCandidatesAndSeparatorBearingCueKeys()
    {
        List<AttentionGazeTargetCandidate<string>> cueCandidates =
        [
            Cue("char:alpha", cueKey: "body", cueOrder: 0, attention: 0.4d, prominence: 1d),
            Cue("char:alpha", cueKey: "disabled", cueOrder: 1, attention: 0.4d, prominence: 0d),
            Cue("char:alpha", cueKey: "am#biguous", cueOrder: 2, attention: 0.4d, prominence: 5d),
            Cue("char:bravo", cueKey: "nan", cueOrder: 0, attention: double.NaN, prominence: 1d),
            Cue("char:bravo", cueKey: "inf", cueOrder: 1, attention: 0.5d, prominence: double.PositiveInfinity),
            Cue("char:invalid", cueKey: "any", cueOrder: 0, attention: 0.5d, prominence: 1d, isValid: false),
        ];

        IReadOnlyList<GazeCueCandidate> composed = GazeCueCandidates.Compose(cueCandidates);

        GazeCueCandidate candidate = Assert.Single(composed);
        Assert.Equal("char:alpha", candidate.SubjectFullId);
        Assert.Equal("body", candidate.CueKey);
    }

    /// <summary>Attention ties order subjects by full ID ordinal for determinism.</summary>
    [Fact]
    public void Compose_AttentionTiesBreakByFullIdOrdinal()
    {
        List<AttentionGazeTargetCandidate<string>> cueCandidates =
        [
            Cue("char:zulu", cueKey: "body", cueOrder: 0, attention: 0.5d, prominence: 1d),
            Cue("char:alfa", cueKey: "body", cueOrder: 0, attention: 0.5d, prominence: 1d),
        ];

        IReadOnlyList<GazeCueCandidate> composed = GazeCueCandidates.Compose(cueCandidates);

        Assert.Equal(["char:alfa", "char:zulu"], composed.Select(candidate => candidate.SubjectFullId));
    }

    /// <summary>Cues per subject cap at three by prominence then cue order, and the total caps at twelve.</summary>
    [Fact]
    public void Compose_CapsCuesPerSubjectAndTheTotalCandidateCount()
    {
        List<AttentionGazeTargetCandidate<string>> manyCues = [];
        for (int cueIndex = 0; cueIndex < 6; cueIndex++)
        {
            manyCues.Add(Cue("char:solo", cueKey: $"cue{cueIndex}", cueOrder: cueIndex, attention: 1d, prominence: 6d - cueIndex));
        }

        IReadOnlyList<GazeCueCandidate> cappedPerSubject = GazeCueCandidates.Compose(manyCues);
        Assert.Equal(["char:solo#cue0", "char:solo#cue1", "char:solo#cue2"], cappedPerSubject.Select(c => c.Value));

        List<AttentionGazeTargetCandidate<string>> manySubjects = [];
        for (int subjectIndex = 0; subjectIndex < 6; subjectIndex++)
        {
            for (int cueIndex = 0; cueIndex < 3; cueIndex++)
            {
                manySubjects.Add(Cue(
                    $"char:s{subjectIndex}",
                    cueKey: $"cue{cueIndex}",
                    cueOrder: cueIndex,
                    attention: 1d - (subjectIndex * 0.01d),
                    prominence: 3d - cueIndex));
            }
        }

        IReadOnlyList<GazeCueCandidate> cappedTotal = GazeCueCandidates.Compose(manySubjects);
        Assert.Equal(GazeCueCandidates.MaximumCueCandidates, cappedTotal.Count);
        Assert.Equal("char:s3#cue2", cappedTotal[^1].Value);
    }

    /// <summary>Cue option values encode subject and cue unambiguously and parse back exactly.</summary>
    [Theory]
    [InlineData("char:x#face", "char:x", "face")]
    [InlineData("char:player#left hand", "char:player", "left hand")]
    public void CueValueEncoding_RoundTripsUnambiguously(string value, string subject, string cue)
    {
        Assert.Equal(value, GazePromptText.EncodeCueValue(subject, cue));
        Assert.True(GazePromptText.TryParseCueValue(value, out string parsedSubject, out string parsedCue));
        Assert.Equal(subject, parsedSubject);
        Assert.Equal(cue, parsedCue);
    }

    /// <summary>Malformed cue values never parse: no separator, or an empty side.</summary>
    [Theory]
    [InlineData("char:x")]
    [InlineData("#face")]
    [InlineData("char:x#")]
    [InlineData("")]
    public void CueValueEncoding_RejectsMalformedValues(string value)
        => Assert.False(GazePromptText.TryParseCueValue(value, out _, out _));

    /// <summary>The first failure doubles the base interval; consecutive failures double up to the cap.</summary>
    [Fact]
    public void Backoff_FirstFailureDoublesTheIntervalThenDoublesUpToTheCap()
    {
        Assert.Equal(2d, GazeFailureBackoff.NextSeconds(0d, 1d));
        Assert.Equal(4d, GazeFailureBackoff.NextSeconds(2d, 1d));
        Assert.Equal(8d, GazeFailureBackoff.NextSeconds(4d, 1d));
        Assert.Equal(GazeFailureBackoff.MaximumSeconds, GazeFailureBackoff.NextSeconds(16d, 1d));
        Assert.Equal(GazeFailureBackoff.MaximumSeconds, GazeFailureBackoff.NextSeconds(30d, 1d));
        Assert.Equal(1d, GazeFailureBackoff.NextSeconds(0d, 0.5d));
    }

    /// <summary>Invalid backoff arguments are rejected.</summary>
    [Fact]
    public void Backoff_RejectsInvalidArguments()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => GazeFailureBackoff.NextSeconds(0d, 0d));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => GazeFailureBackoff.NextSeconds(0d, double.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => GazeFailureBackoff.NextSeconds(-1d, 1d));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => GazeFailureBackoff.NextSeconds(double.PositiveInfinity, 1d));
    }

    /// <summary>The own-action phrase renders exactly the three model-facing forms.</summary>
    [Fact]
    public void OwnAction_RendersExactlyTheThreeModelFacingPhrases()
    {
        Assert.Equal("idle", GazeOwnAction.IdleAction.Describe());
        Assert.Equal(
            "speaking_to: `char:a`, `char:b`",
            new GazeOwnAction(GazeOwnActionKind.SpeakingTo, ["char:a", "char:b"]).Describe());
        Assert.Equal(
            "hearing: `char:a` (attended speaker)",
            new GazeOwnAction(GazeOwnActionKind.Hearing, ["char:a"]).Describe());
    }

    /// <summary>An available scene premise rides the instructions verbatim under its framing label.</summary>
    [Fact]
    public async Task Evaluate_ComposesTheScenePremiseUnderItsFramingLabel()
    {
        ScriptedDecisionClient client = new();
        DecisionGazePolicy policy = new(client);
        const string premise = "A detention interview in the basement of the Charter Office of Compliance.";
        _ = await policy.EvaluateAsync(CreateInput([], scenePremise: premise));

        ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(
            Assert.Single(Assert.Single(client.Requests).Questions),
            exactMatch: false);
        Assert.Contains($"Scene premise: {premise} ", question.Instructions);
        int labelIndex = question.Instructions.IndexOf(DecisionGazePolicy.ScenePremiseLabel, StringComparison.Ordinal);
        Assert.True(labelIndex > 0, "The premise must follow the identity sentence, not open the instructions.");
    }

    /// <summary>An absent or blank premise is omitted cleanly: no label, no sentence.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Evaluate_OmitsThePremiseCleanlyWhenAbsentOrBlank(string? premise)
    {
        ScriptedDecisionClient client = new();
        DecisionGazePolicy policy = new(client);
        _ = await policy.EvaluateAsync(CreateInput([], scenePremise: premise));

        ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(
            Assert.Single(Assert.Single(client.Requests).Questions),
            exactMatch: false);
        Assert.DoesNotContain(DecisionGazePolicy.ScenePremiseLabel, question.Instructions, StringComparison.Ordinal);
    }

    /// <summary>An over-long premise is cut at the budget's last whitespace and marked with an ellipsis.</summary>
    [Fact]
    public async Task Evaluate_TruncatesAnOverLongPremiseAtTheLastWhitespace()
    {
        ScriptedDecisionClient client = new();
        DecisionGazePolicy policy = new(client);
        string word = "premise";
        string longPremise = string.Join(' ', Enumerable.Repeat(word, 200));

        _ = await policy.EvaluateAsync(CreateInput([], scenePremise: longPremise));

        ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(
            Assert.Single(Assert.Single(client.Requests).Questions),
            exactMatch: false);
        string instructions = question.Instructions;
        int labelIndex = instructions.IndexOf(DecisionGazePolicy.ScenePremiseLabel, StringComparison.Ordinal);
        int ellipsisIndex = instructions.IndexOf('…', labelIndex);
        Assert.True(ellipsisIndex > labelIndex, "An over-long premise must be marked with an ellipsis.");
        string truncated = instructions[(labelIndex + DecisionGazePolicy.ScenePremiseLabel.Length + 1)..ellipsisIndex];
        Assert.True(
            truncated.Length <= DecisionGazePolicy.ScenePremiseCharacterBudget,
            $"The composed premise must respect the character budget; found {truncated.Length}.");
        Assert.EndsWith("premise", truncated, StringComparison.Ordinal);
    }

    /// <summary>The dispatch dump material carries every offered cue option's value and criteria.</summary>
    [Fact]
    public async Task Evaluate_CarriesTheOfferedOptionsInTheDispatchMaterial()
    {
        ScriptedDecisionClient client = new();
        DecisionGazePolicy policy = new(client);
        GazeEvaluationResult result = await policy.EvaluateAsync(CreateInput(
        [
            Candidate("char:bravo", cueKey: "body", attention: 0.9d, prominence: 2d),
            Candidate("char:alpha", cueKey: "face", attention: 0.42d, prominence: 1d),
        ]));

        DecisionPayloadQuestion question = Assert.Single(result.Questions);
        Assert.Equal(
            ["char:bravo#body", "char:alpha#face", "none"],
            question.Options!.Select(option => option.Value));
        Assert.Equal(
            "`char:bravo#body` — body: body (attention 0.9, prominence 2.00)",
            question.Options![0].Criteria);
        Assert.All(question.Options!, option => Assert.False(string.IsNullOrWhiteSpace(option.Criteria)));
    }

    private static GazeEvaluationInput CreateInput(
        IReadOnlyList<GazeCueCandidate> candidates,
        GazeOwnAction? ownAction = null,
        string? currentGazeCueValue = null,
        double currentGazeDwellSeconds = 0d,
        GazeEvaluationTrigger? trigger = null,
        IReadOnlyList<string>? recentEvents = null,
        string? scenePremise = null,
        IReadOnlyList<GazeSceneCharacter>? sceneCharacters = null)
        => new(
            Owner,
            candidates,
            ownAction ?? GazeOwnAction.IdleAction,
            currentGazeCueValue,
            currentGazeDwellSeconds,
            trigger ?? GazeEvaluationTrigger.Cadence,
            recentEvents ?? [],
            scenePremise,
            sceneCharacters);

    private static GazeCueCandidate Candidate(
        string subjectFullId,
        string cueKey,
        double attention,
        double prominence,
        string? description = null)
        => new(subjectFullId, cueKey, attention, prominence, description);

    private static AttentionGazeTargetCandidate<string> Cue(
        string subjectFullId,
        string cueKey,
        int cueOrder,
        double attention,
        double prominence,
        bool isValid = true)
        => new(subjectFullId, cueKey, cueOrder, cueKey, attention, prominence, isValid);

    /// <summary>Scripted decision client capturing every request and answering through one configured mode.</summary>
    private sealed class ScriptedDecisionClient(
        string? answerValue = null,
        bool refusal = false,
        bool omitAnswer = false,
        DecisionFailure? decisionFailure = null,
        bool throwUnexpected = false) : IDecisionClient
    {
        public List<DecisionRequest> Requests { get; } = [];

        public Task<DecisionResult> EvaluateAsync(
            DecisionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (throwUnexpected)
            {
                throw new InvalidOperationException("The scripted transport exploded.");
            }

            if (decisionFailure is { } failure)
            {
                throw new DecisionException(failure, "Scripted failure.", HttpStatusCode.ServiceUnavailable);
            }

            ChoiceQuestion question = request.Questions.OfType<ChoiceQuestion>().Single();
            DecisionAnswer? answer = refusal
                ? new DecisionRefusal(question, JsonDocument.Parse("{}").RootElement)
                : omitAnswer
                    ? null
                    : new ChoiceAnswer(question, answerValue ?? DecisionGazePolicy.NoneOptionValue, 0.9d, null);
            Dictionary<string, DecisionAnswer> byName = [];
            if (answer is not null)
            {
                byName[question.Name] = answer;
            }

            return Task.FromResult(new DecisionResult(
                "scripted",
                ID: null,
                Provider: null,
                Usage: null,
                Cost: null,
                [.. byName.Values],
                byName));
        }
    }
}
