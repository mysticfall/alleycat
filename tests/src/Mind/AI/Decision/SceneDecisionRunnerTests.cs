using System.Text.Json;
using AlleyCat.Mind.AI.Decision;
using AlleyCat.Mind.Attention;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Decision;

/// <summary>Exercises the policy-to-client boundary without either Godot scheduler.</summary>
public sealed class SceneDecisionRunnerTests
{
    /// <summary>Both, either or neither gate can be due without changing the subject's evidence.</summary>
    [Fact]
    public async Task SubjectPass_DispatchesOnlyDueGroupsAgainstTheSamePerspective()
    {
        const string subject = "char:observer";
        const string cue = "char:witness#face";
        bool waitDue = true;
        bool gazeDue = true;
        int captures = 0;
        SceneDecisionSnapshot Capture()
        {
            captures++;
            return new(subject, "A hearing.", [new("char:witness", "The witness.")],
                ["I asked the witness to sit."], "waiting", null, 2);
        }

        DecisionWaitSchedule schedule = new(new(5, 3, 10, 0), () => 0);
        schedule.SeedExplicit(100, 20);
        List<DecisionWaitScheduleStep> waitSteps = [];
        List<GazeDecision> gazeDecisions = [];
        ScriptedClient client = new();
        SceneDecisionRunner runner = new(client, Capture,
        [
            () => waitDue ? DecisionWaitPolicy.PrepareGroup(answer => waitSteps.Add(schedule.Apply(answer.Decision, 100))) : null,
            () => gazeDue ? DecisionGazePolicy.PrepareGroup([new("char:witness", "face", 1, 1)],
                GazeOwnAction.IdleAction, GazeEvaluationTrigger.Cadence, r => gazeDecisions.Add(r.Decision)) : null,
        ]);

        await runner.EvaluateAsync();
        DecisionRequest combined = Assert.Single(client.Requests);
        Assert.Equal(["response", "initiative", "gaze_target"], combined.Questions.Select(q => q.Name));
        Assert.Equal([cue, "none"], Assert.IsType<ChoiceQuestion>(combined.Questions[2]).Options.Select(o => o.Value));
        JsonElement evidence = Assert.IsType<JsonElement>(combined.Evidence);
        Assert.Equal(subject, evidence.GetProperty("recent_history").GetProperty("perspective").GetString());
        Assert.False(evidence.TryGetProperty("trigger", out _));
        Assert.DoesNotContain(cue, evidence.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(DecisionWaitArm.ResponsePending, Assert.Single(waitSteps).Arm);
        Assert.Equal(105d, schedule.DeadlineAt);
        Assert.Equal(GazeDecision.ChooseCue(cue), Assert.Single(gazeDecisions));

        gazeDue = false;
        await runner.EvaluateAsync();
        Assert.Equal(["response", "initiative"], client.Requests[1].Questions.Select(q => q.Name));
        Assert.Equal(DecisionWaitScheduleStepKind.Kept, waitSteps[1].Kind);
        _ = Assert.Single(gazeDecisions);

        waitDue = false;
        gazeDue = true;
        await runner.EvaluateAsync();
        Assert.Equal("gaze_target", Assert.Single(client.Requests[2].Questions).Name);
        Assert.Equal(GazeDecision.ChooseCue(cue), gazeDecisions[1]);
        gazeDue = false;
        await runner.EvaluateAsync();
        Assert.Equal(2, waitSteps.Count);
        Assert.Equal(2, gazeDecisions.Count);
        Assert.Equal(3, captures);
        Assert.Equal(3, client.Requests.Count);
        Assert.All(client.Requests, r => Assert.Equal(evidence.GetRawText(), ((JsonElement)r.Evidence).GetRawText()));
    }

    /// <summary>A third registered question and a second subject share transport, not evidence or answers.</summary>
    [Fact]
    public async Task ThirdGate_ReceivesOnlyItsAnswerFromItsOwnSubject()
    {
        ScriptedClient client = new();
        SceneDecisionRunner MakeRunner(string subject, Action<string> record)
        {
            SceneDecisionGroup third = new(id => [new ChoiceQuestion("posture", $"How should {id} stand?",
                [new("sitting", "sit"), new("standing", "stand")])], receipt =>
            {
                ChoiceAnswer answer = Assert.IsType<ChoiceAnswer>(Assert.Single(receipt.Answers).Value);
                Assert.Equal("posture", answer.Name);
                record(answer.Value);
            });
            return new(client, () => new(subject, null, [], [$"Private history of {subject}"], "waiting", null, 0),
                [() => DecisionWaitPolicy.PrepareGroup(_ => { }), () => third]);
        }

        string? first = null;
        string? second = null;
        await MakeRunner("char:alice", value => first = value).EvaluateAsync();
        await MakeRunner("char:bob", value => second = value).EvaluateAsync();
        Assert.Equal("sitting", first);
        Assert.Equal("standing", second);
        Assert.Equal(2, client.Requests.Count);
        Assert.DoesNotContain("char:bob", ((JsonElement)client.Requests[0].Evidence).GetRawText());
        Assert.DoesNotContain("char:alice", ((JsonElement)client.Requests[1].Evidence).GetRawText());
    }

    private sealed class ScriptedClient : IDecisionClient
    {
        public List<DecisionRequest> Requests { get; } = [];

        public Task<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Dictionary<string, string> script = new()
            {
                ["response"] = "expecting_pending",
                ["initiative"] = "no_reason",
                ["gaze_target"] = "char:witness#face",
                ["posture"] = ((JsonElement)request.Evidence).GetProperty("owner").GetString() == "char:alice" ? "sitting" : "standing",
            };
            DecisionAnswer[] answers = [.. request.Questions.Cast<ChoiceQuestion>()
                .Select(q => new ChoiceAnswer(q, script[q.Name], null, null))];
            return Task.FromResult(new DecisionResult("script", null, null, null, null,
                answers, answers.ToDictionary(a => a.Name)));
        }
    }
}
