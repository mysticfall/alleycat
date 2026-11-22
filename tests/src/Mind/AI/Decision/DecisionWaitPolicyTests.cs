using System.Net;
using System.Text.Json;
using AlleyCat.Mind.AI.Decision;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Decision;

/// <summary>
/// Deterministic coverage for the two-question wait policy: the <c>response</c> and <c>initiative</c> choice
/// questions dispatched in one request, the lean evidence shape, the verdict mapping (including the must-act
/// override), containment per failure mode, and the own-action and trigger renderings.
/// </summary>
public sealed class DecisionWaitPolicyTests
{
    private const string Owner = "char:observer";

    /// <summary>One request carries both choice questions with their documented option sets; the response
    /// question accepts non-verbal responses and the initiative question keeps the group-scene de-biasing tail.
    /// Neither question carries the premise text, which the evidence owns.</summary>
    [Fact]
    public async Task Evaluate_ComposesBothChoiceQuestionsInOneRequest()
    {
        ScriptedDecisionClient client = new();
        _ = await EvaluateAsync(client, CreateInput(scenePremise: "A detention interview in a basement office."));

        DecisionRequest request = Assert.Single(client.Requests);
        Assert.Equal(2, request.Questions.Count);
        ChoiceQuestion response = Assert.IsType<ChoiceQuestion>(request.Questions[0]);
        ChoiceQuestion initiative = Assert.IsType<ChoiceQuestion>(request.Questions[1]);
        Assert.Equal(DecisionWaitPolicy.ResponseQuestionName, response.Name);
        Assert.Equal(DecisionWaitPolicy.InitiativeQuestionName, initiative.Name);
        Assert.Equal(
            ["expecting_pending", "expecting_arrived", "not_expecting"],
            response.Options.Select(static option => option.Value));
        Assert.Equal(["must_act", "may_act", "no_reason"], initiative.Options.Select(static option => option.Value));

        // Both questions name the subject by its backticked canonical ID.
        Assert.StartsWith($"You are playing `{Owner}` in an interactive roleplay scene.", response.Instructions, StringComparison.Ordinal);
        Assert.StartsWith($"You are playing `{Owner}` in an interactive roleplay scene.", initiative.Instructions, StringComparison.Ordinal);

        // A response may be non-verbal (user directive): the question and the arrived criterion name deeds.
        Assert.Contains("said or did", response.Instructions, StringComparison.Ordinal);
        Assert.Contains("words or deeds", response.Instructions, StringComparison.Ordinal);
        string arrived = response.Options.Single(static option => option.Value == "expecting_arrived").Criteria;
        Assert.Contains("doing what was asked", arrived, StringComparison.Ordinal);
        Assert.Contains("sitting down after being told to sit", arrived, StringComparison.Ordinal);
        Assert.Contains("pointedly", arrived, StringComparison.Ordinal);
        string pending = response.Options.Single(static option => option.Value == "expecting_pending").Criteria;
        Assert.Contains("nobody has replied, done what was asked, or refused", pending, StringComparison.Ordinal);

        Assert.EndsWith(
            "In most moments of a group scene a character has no particular reason to act.",
            initiative.Instructions,
            StringComparison.Ordinal);
        Assert.All(request.Questions, question =>
        {
            Assert.DoesNotContain("A detention interview", question.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("probability", question.Instructions, StringComparison.Ordinal);
        });

        // The evidence owns the verbatim premise.
        JsonElement evidence = Assert.IsType<JsonElement>(request.Evidence);
        Assert.Equal(
            "A detention interview in a basement office.",
            evidence.GetProperty("premise").GetString());
    }

    /// <summary>An absent premise is omitted cleanly from the evidence; no field, no label, no sentence.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Evaluate_OmitsThePremiseCleanlyWhenAbsent(string? premise)
    {
        ScriptedDecisionClient client = new();
        _ = await EvaluateAsync(client, CreateInput(scenePremise: premise));

        DecisionRequest request = Assert.Single(client.Requests);
        Assert.All(
            request.Questions,
            question => Assert.DoesNotContain("Scene premise:", question.Instructions, StringComparison.Ordinal));
        JsonElement evidence = Assert.IsType<JsonElement>(request.Evidence);
        _ = Assert.Throws<KeyNotFoundException>(() => evidence.GetProperty("premise"));
    }

    /// <summary>Evidence carries the premise, the lore roster, the owner-labelled recent history, own action,
    /// and cue-level gaze but no trigger.</summary>
    [Fact]
    public async Task Evaluate_ComposesTheLeanEvidenceShape()
    {
        ScriptedDecisionClient client = new();
        SceneDecisionSnapshot input = CreateInput(
            sceneCharacters:
            [
                new SceneDecisionCharacter("char:bravo", "A sturdily built man."),
                new SceneDecisionCharacter("char:delta"),
            ],
            recentEvents: ["`char:bravo` said: hello"],
            ownAction: new DecisionWaitOwnAction(
                DecisionWaitOwnActionKind.Hearing,
                "char:bravo",
                DecisionWaitAddresseeStatus.AddressedToMe),
            currentGazeCueValue: "char:bravo#face",
            currentGazeDwellSeconds: 3.24d);
        _ = await EvaluateAsync(client, input);

        JsonElement evidence = Assert.IsType<JsonElement>(Assert.Single(client.Requests).Evidence);
        JsonElement characters = evidence.GetProperty("characters");
        Assert.Equal(2, characters.GetArrayLength());
        Assert.Equal("char:bravo", characters[0].GetProperty("id").GetString());
        Assert.Equal("A sturdily built man.", characters[0].GetProperty("description").GetString());
        _ = Assert.Throws<KeyNotFoundException>(() => characters[1].GetProperty("description"));
        // The recent history states whose first-person perspective its lines are from (§ user issue 1).
        JsonElement recentHistory = evidence.GetProperty("recent_history");
        Assert.Equal(Owner, recentHistory.GetProperty("perspective").GetString());
        Assert.Equal(
            $"first-person lines are from `{Owner}`'s perspective, as they experienced them",
            recentHistory.GetProperty("note").GetString());
        Assert.Equal("`char:bravo` said: hello", recentHistory.GetProperty("events")[0].GetString());
        _ = Assert.Throws<KeyNotFoundException>(() => evidence.GetProperty("recent_events"));
        Assert.Equal(
            "waiting; hearing `char:bravo` (they are speaking, addressed to me)",
            evidence.GetProperty("own_action").GetString());
        Assert.Equal("char:bravo#face", evidence.GetProperty("current_gaze").GetProperty("cue").GetString());
        Assert.Equal(3.2d, evidence.GetProperty("current_gaze").GetProperty("dwell_seconds").GetDouble());
        Assert.False(evidence.TryGetProperty("trigger", out _));
    }

    /// <summary>The approved verdict mapping over every answer pair: an arrived response or must_act starts the
    /// agent now (must_act wins over a pending response); otherwise pending arms the response-pending timeout and,
    /// with nothing expected, may_act and no_reason arm their own timeouts.</summary>
    [Theory]
    [InlineData("expecting_arrived", "no_reason", null)]
    [InlineData("expecting_arrived", "may_act", null)]
    [InlineData("expecting_arrived", "must_act", null)]
    [InlineData("expecting_pending", "must_act", null)]
    [InlineData("not_expecting", "must_act", null)]
    [InlineData("expecting_pending", "may_act", "ResponsePending")]
    [InlineData("expecting_pending", "no_reason", "ResponsePending")]
    [InlineData("not_expecting", "may_act", "MayAct")]
    [InlineData("not_expecting", "no_reason", "NoReason")]
    public async Task Evaluate_MapsTheAnswerPairToTheApprovedVerdict(
        string response,
        string initiative,
        string? expectedArmName)
    {
        DecisionWaitArm? expectedArm = expectedArmName is null ? null : Enum.Parse<DecisionWaitArm>(expectedArmName);
        ScriptedDecisionClient client = new(response, initiative);
        DecisionWaitEvaluationResult result = await EvaluateAsync(client, CreateInput());

        Assert.Equal(
            expectedArm is null ? DecisionWaitDecisionKind.ActNow : DecisionWaitDecisionKind.Defer,
            result.Decision.Kind);
        Assert.Equal(expectedArm, result.Decision.Arm);
        Assert.Null(result.Decision.Failure);
    }

    /// <summary>Both returned answers are captured with their choice, distribution, and confidence for the
    /// decision-payload dump.</summary>
    [Fact]
    public async Task Evaluate_CapturesBothAnswersForDiagnostics()
    {
        ScriptedDecisionClient client = new("expecting_pending", "may_act");
        DecisionWaitEvaluationResult result = await EvaluateAsync(client, CreateInput());

        Assert.Equal(["response", "initiative"], result.Answers.Select(static answer => answer.Name));
        Assert.Equal(["expecting_pending", "may_act"], result.Answers.Select(static answer => answer.Choice));
        Assert.All(result.Answers, answer =>
        {
            Assert.Equal(0.9d, answer.Confidence);
            Assert.Equal(1d, answer.Probabilities!.Values.Sum(), 6);
        });
        Assert.Equal(["response", "initiative"], result.Questions.Select(static question => question.Name));
        Assert.All(result.Questions, question => Assert.Equal(3, question.Options!.Count));
    }

    /// <summary>A refusal, a missing answer, an unexpected answer kind, or an unknown option on either question
    /// is contained as a failure that keeps waiting.</summary>
    [Theory]
    [InlineData("response", ScriptedMode.Refusal, "response: refusal")]
    [InlineData("initiative", ScriptedMode.Refusal, "initiative: refusal")]
    [InlineData("response", ScriptedMode.Omit, "response: missing answer")]
    [InlineData("initiative", ScriptedMode.Omit, "initiative: missing answer")]
    [InlineData("initiative", ScriptedMode.WrongKind, "initiative: unexpected answer kind")]
    [InlineData("response", ScriptedMode.UnknownValue, "unknown response choice")]
    [InlineData("initiative", ScriptedMode.UnknownValue, "unknown initiative choice")]
    public async Task Evaluate_MalformedAnswersAreContainedAsFailures(
        string question,
        ScriptedMode mode,
        string expectedFailure)
    {
        ScriptedDecisionClient client = new("expecting_arrived", "must_act")
        {
            BrokenQuestion = question,
            BrokenMode = mode,
        };
        DecisionWaitEvaluationResult result = await EvaluateAsync(client, CreateInput());

        Assert.Equal(DecisionWaitDecisionKind.Failure, result.Decision.Kind);
        Assert.Equal(expectedFailure, result.Decision.Failure);
        Assert.Null(result.Decision.Arm);
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
        DecisionWaitEvaluationResult result = await EvaluateAsync(client, CreateInput());

        Assert.Equal(DecisionWaitDecisionKind.Failure, result.Decision.Kind);
        Assert.Equal($"decision failure {failure}", result.Decision.Failure);
        _ = Assert.NotNull(result.Evidence);
        Assert.Equal(2, result.Questions.Count);
        Assert.Empty(result.Answers);
    }

    /// <summary>An unexpected exception is contained as a failure with the dispatch material preserved.</summary>
    [Fact]
    public async Task Evaluate_UnexpectedExceptionIsContainedAsAFailure()
    {
        ScriptedDecisionClient client = new(throwUnexpected: true);
        DecisionWaitEvaluationResult result = await EvaluateAsync(client, CreateInput());

        Assert.Equal(DecisionWaitDecisionKind.Failure, result.Decision.Kind);
        Assert.Equal("unexpected evaluation failure", result.Decision.Failure);
    }

    /// <summary>Caller cancellation propagates instead of resolving through containment.</summary>
    [Fact]
    public async Task Evaluate_CallerCancellationPropagates()
    {
        ScriptedDecisionClient client = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => EvaluateAsync(client, CreateInput(), cancellation.Token));
    }

    /// <summary>Own action renders exactly the two waiting variants with their addressee states.</summary>
    [Fact]
    public void OwnAction_RendersTheWaitingVariants()
    {
        Assert.Equal("waiting", DecisionWaitOwnAction.WaitingAction.Describe());
        Assert.Equal(
            "waiting; hearing `char:speaker` (they are speaking, addressed to me)",
            new DecisionWaitOwnAction(
                DecisionWaitOwnActionKind.Hearing,
                "char:speaker",
                DecisionWaitAddresseeStatus.AddressedToMe).Describe());
        Assert.Equal(
            "waiting; hearing `char:speaker` (they are speaking, not to me)",
            new DecisionWaitOwnAction(
                DecisionWaitOwnActionKind.Hearing,
                "char:speaker",
                DecisionWaitAddresseeStatus.AddressedToAnother).Describe());
        Assert.Equal(
            "waiting; hearing `char:speaker` (they are speaking, addressee undecided)",
            new DecisionWaitOwnAction(
                DecisionWaitOwnActionKind.Hearing,
                "char:speaker",
                DecisionWaitAddresseeStatus.Undecided).Describe());
        Assert.Equal(
            "waiting",
            new DecisionWaitOwnAction(DecisionWaitOwnActionKind.Hearing).Describe());
    }

    /// <summary>Trigger tokens match the dump contract.</summary>
    [Fact]
    public void Triggers_RenderContractTokens()
    {
        Assert.Equal(
            ["Cadence", "PlayerSpeech", "AttendedSpeakerStart", "AttendedSpeakerEnd", "MembershipChange"],
            Enum.GetValues<DecisionWaitTriggerKind>()
                .Select(kind => new DecisionWaitTrigger(kind).Token));

    }

    private static SceneDecisionSnapshot CreateInput(
        IReadOnlyList<SceneDecisionCharacter>? sceneCharacters = null,
        IReadOnlyList<string>? recentEvents = null,
        DecisionWaitOwnAction? ownAction = null,
        string? currentGazeCueValue = null,
        double currentGazeDwellSeconds = 0d,
        string? scenePremise = null)
        => new(Owner, scenePremise, sceneCharacters ?? [], recentEvents ?? [],
            (ownAction ?? DecisionWaitOwnAction.WaitingAction).Describe(), currentGazeCueValue, currentGazeDwellSeconds);

    private static async Task<DecisionWaitEvaluationResult> EvaluateAsync(IDecisionClient client, SceneDecisionSnapshot input,
        CancellationToken token = default)
    {
        DecisionWaitEvaluationResult? result = null;
        SceneDecisionRunner runner = new(client, () => input, [() => DecisionWaitPolicy.PrepareGroup(r => result = r)]);
        await runner.EvaluateAsync(token);
        return result!;
    }

    /// <summary>How the scripted client breaks one question's answer.</summary>
    public enum ScriptedMode
    {
        /// <summary>Answer normally.</summary>
        Valid,

        /// <summary>Return an explicit refusal.</summary>
        Refusal,

        /// <summary>Omit the answer.</summary>
        Omit,

        /// <summary>Return a boolean answer kind.</summary>
        WrongKind,

        /// <summary>Return an option value the question never offered.</summary>
        UnknownValue,
    }

    /// <summary>Scripted decision client capturing every request and answering both choice questions.</summary>
    private sealed class ScriptedDecisionClient(
        string response = "not_expecting",
        string initiative = "no_reason",
        DecisionFailure? decisionFailure = null,
        bool throwUnexpected = false) : IDecisionClient
    {
        public List<DecisionRequest> Requests { get; } = [];

        public string? BrokenQuestion
        {
            get; init;
        }

        public ScriptedMode BrokenMode
        {
            get; init;
        }

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

            Dictionary<string, DecisionAnswer> byName = [];
            foreach (ChoiceQuestion question in request.Questions.OfType<ChoiceQuestion>())
            {
                string value = question.Name == DecisionWaitPolicy.ResponseQuestionName ? response : initiative;
                ScriptedMode mode = question.Name == BrokenQuestion ? BrokenMode : ScriptedMode.Valid;
                DecisionAnswer? answer = mode switch
                {
                    ScriptedMode.Refusal => new DecisionRefusal(question, JsonDocument.Parse("{}").RootElement),
                    ScriptedMode.Omit => null,
                    ScriptedMode.WrongKind => new BooleanAnswer(new BooleanQuestion(question.Name, "x"), 0.5d, null),
                    ScriptedMode.UnknownValue => new ChoiceAnswer(question, "bogus", 0.9d, null),
                    ScriptedMode.Valid => new ChoiceAnswer(
                        question,
                        value,
                        0.9d,
                        question.Options.ToDictionary(
                            static option => option.Value,
                            option => option.Value == value ? 0.8d : 0.1d)),
                    _ => null,
                };
                if (answer is not null)
                {
                    byName[question.Name] = answer;
                }
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
