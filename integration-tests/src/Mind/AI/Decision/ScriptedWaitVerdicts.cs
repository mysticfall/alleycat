using AlleyCat.Mind.AI.Decision;

namespace AlleyCat.IntegrationTests.Mind.AI.Decision;

/// <summary>One scripted answer pair for the two-question wait gate.</summary>
/// <param name="Response">The <c>response</c> question's chosen option.</param>
/// <param name="Initiative">The <c>initiative</c> question's chosen option.</param>
internal readonly record struct WaitVerdict(string Response, string Initiative)
{
    /// <summary>An immediate verdict: a response arrived and the circumstances call for acting.</summary>
    public static WaitVerdict ActNow
    {
        get;
    } =
        new(DecisionWaitPolicy.ExpectingArrivedValue, DecisionWaitPolicy.MustActValue);

    /// <summary>A deferred verdict arming the no-reason timeout.</summary>
    public static WaitVerdict NoReason
    {
        get;
    } =
        new(DecisionWaitPolicy.NotExpectingValue, DecisionWaitPolicy.NoReasonValue);

    /// <summary>A deferred verdict arming the may-act timeout.</summary>
    public static WaitVerdict MayAct
    {
        get;
    } =
        new(DecisionWaitPolicy.NotExpectingValue, DecisionWaitPolicy.MayActValue);
}

/// <summary>Shared scripted answers and timing for integration fixtures that drive the wait gate.</summary>
internal static class ScriptedWaitVerdicts
{
    /// <summary>
    /// Timing whose deferred deadlines never expire within any test window, so a deferred verdict keeps the wait
    /// parked until the fixture scripts an immediate one.
    /// </summary>
    public static DecisionWaitTimingSettings NoDeadlines { get; } = new(3600d, 3600d, 3600d, 0d);

    /// <summary>Answers both wait-gate choice questions of one request with the given verdict.</summary>
    public static DecisionResult Answer(DecisionRequest request, WaitVerdict verdict, string model = "scripted-wait")
    {
        Dictionary<string, DecisionAnswer> byName = [];
        foreach (ChoiceQuestion question in request.Questions.OfType<ChoiceQuestion>())
        {
            string value = question.Name switch
            {
                DecisionWaitPolicy.ResponseQuestionName => verdict.Response,
                DecisionWaitPolicy.InitiativeQuestionName => verdict.Initiative,
                _ => throw new InvalidOperationException($"Unexpected wait question '{question.Name}'."),
            };
            byName[question.Name] = new ChoiceAnswer(question, value, null, null);
        }

        return new DecisionResult(model, ID: null, Provider: null, Usage: null, Cost: null, [.. byName.Values], byName);
    }
}
