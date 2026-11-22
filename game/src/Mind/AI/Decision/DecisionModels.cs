using System.Net;
using System.Text.Json;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>Explicit, asynchronous typed decisions over shared evidence.</summary>
public interface IDecisionClient
{
    /// <summary>Evaluates and validates the entire batch; cancellation throws OperationCanceledException.</summary>
    Task<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Distinct non-cancellation failure categories.</summary>
public enum DecisionFailure
{
    /// <summary>Missing or invalid independent settings.</summary>
    Configuration,
    /// <summary>Invalid caller questions.</summary>
    Input,
    /// <summary>Unsupported evidence or question feature.</summary>
    Unsupported,
    /// <summary>Configured deadline elapsed.</summary>
    Timeout,
    /// <summary>Network or stream failure.</summary>
    Transport,
    /// <summary>Non-success HTTP status.</summary>
    HTTP,
    /// <summary>Malformed, oversized or incompatible provider output.</summary>
    InvalidResponse
}

/// <summary>Safe diagnostics only; provider bodies and underlying transport exceptions are deliberately not attached.</summary>
public sealed class DecisionException(DecisionFailure failure, string message, HttpStatusCode? statusCode = null)
    : Exception(message)
{
    /// <summary>Failure category.</summary>
    public DecisionFailure Failure { get; } = failure;
    /// <summary>HTTP status when applicable.</summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>A portable string-valued choice.</summary>
/// <param name="Value">Caller identity, preserved verbatim.</param>
/// <param name="Criteria">Plain-text selection guidance.</param>
public sealed record DecisionOption(string Value, string Criteria);

/// <summary>Named question with portable instructions.</summary>
/// <param name="Name">Unique caller identity.</param>
/// <param name="Instructions">Plain-text question.</param>
public abstract record DecisionQuestion(string Name, string Instructions);
/// <summary>
/// Instructions-plus-optional-pole-guidance condition returning a probability, not a bool. The pole guidance —
/// when supplied, both poles together — renders as the dialect's documented criteria shape where one exists
/// (the <c>noul</c> criteria map) and appends to the instructions as plain text otherwise, so nothing is
/// silently discarded.
/// </summary>
/// <param name="Name">Unique caller identity.</param>
/// <param name="Instructions">Plain-text question.</param>
/// <param name="CriteriaTrue">Textual guidance for the condition-true pole, or null when none is supplied.</param>
/// <param name="CriteriaFalse">Textual guidance for the condition-false pole, or null when none is supplied.</param>
public sealed record BooleanQuestion(
    string Name,
    string Instructions,
    string? CriteriaTrue = null,
    string? CriteriaFalse = null) : DecisionQuestion(Name, Instructions);
/// <summary>Unordered categorical decision.</summary>
/// <param name="Name">Unique caller identity.</param>
/// <param name="Instructions">Plain-text question.</param>
/// <param name="Options">Distinct options in caller order.</param>
public sealed record ChoiceQuestion(string Name, string Instructions, IReadOnlyList<DecisionOption> Options)
    : DecisionQuestion(Name, Instructions);
/// <summary>Probability-weighted zero-based score, with 2–10 levels.</summary>
/// <param name="Name">Unique caller identity.</param>
/// <param name="Instructions">Plain-text question.</param>
/// <param name="Levels">Ordered textual criteria, lowest to highest.</param>
public sealed record ScoreQuestion(string Name, string Instructions, IReadOnlyList<string> Levels)
    : DecisionQuestion(Name, Instructions);

/// <summary>Evidence must be a string or a JsonElement containing an object/array. Other types are rejected locally.</summary>
/// <param name="Evidence">Shared evidence, snapshotted before dispatch.</param>
/// <param name="Questions">Ordered, uniquely named questions.</param>
public sealed record DecisionRequest(object Evidence, IReadOnlyList<DecisionQuestion> Questions);

/// <summary>Validated answer or explicit refusal.</summary>
public abstract class DecisionAnswer(DecisionQuestion question, double? confidence)
{
    /// <summary>Snapshot of the caller's question and ordered criteria.</summary>
    public DecisionQuestion Question { get; } = question;
    /// <summary>Caller question identity.</summary>
    public string Name => Question.Name;
    /// <summary>Provider confidence, absent if omitted.</summary>
    public double? Confidence { get; } = confidence;
}

/// <summary>Probability that the condition is true.</summary>
public sealed class BooleanAnswer(BooleanQuestion question, double probability, double? confidence)
    : DecisionAnswer(question, confidence)
{
    /// <summary>Finite probability in [0, 1].</summary>
    public double Probability { get; } = probability;
}

/// <summary>Selected caller option and optional distribution in caller order.</summary>
public sealed class ChoiceAnswer(ChoiceQuestion question, string value, double? confidence,
    IReadOnlyDictionary<string, double>? probabilities) : DecisionAnswer(question, confidence)
{
    /// <summary>Selected caller option identity.</summary>
    public string Value { get; } = value;
    /// <summary>Complete normalised provider distribution, or null.</summary>
    public IReadOnlyDictionary<string, double>? Probabilities { get; } = probabilities;
}

/// <summary>Fractional weighted level index.</summary>
public sealed class ScoreAnswer(ScoreQuestion question, double score, double? confidence,
    IReadOnlyList<double>? probabilities) : DecisionAnswer(question, confidence)
{
    /// <summary>Finite score in [0, level count - 1].</summary>
    public double Score { get; } = score;
    /// <summary>Provider probabilities in zero-based level order, or null.</summary>
    public IReadOnlyList<double>? Probabilities { get; } = probabilities;
}

/// <summary>Explicit refusal, never a default answer. Detail is data, not a diagnostic message.</summary>
public sealed class DecisionRefusal(DecisionQuestion question, JsonElement detail) : DecisionAnswer(question, null)
{
    /// <summary>Provider refusal payload. Do not log this sensitive data.</summary>
    public JsonElement Detail { get; } = detail;
    /// <inheritdoc />
    public override string ToString() => nameof(DecisionRefusal);
}

/// <summary>Provider usage without invented missing fields.</summary>
/// <param name="InputTokens">Optional input count.</param>
/// <param name="OutputTokens">Optional output count.</param>
/// <param name="Cost">Optional provider cost.</param>
/// <param name="Raw">Complete usage metadata.</param>
public sealed record DecisionUsage(long? InputTokens, long? OutputTokens, double? Cost, JsonElement Raw);

/// <summary>Atomic, validated decision set.</summary>
/// <param name="Model">Required resolved model, never the configured fallback.</param>
/// <param name="ID">Optional provider request identity.</param>
/// <param name="Provider">Optional resolved provider.</param>
/// <param name="Usage">Optional usage.</param>
/// <param name="Cost">Optional cost.</param>
/// <param name="Answers">Answers/refusals in caller question order.</param>
/// <param name="ByName">Answers/refusals indexed by caller identity.</param>
public sealed record DecisionResult(string Model, string? ID, string? Provider, DecisionUsage? Usage,
    double? Cost, IReadOnlyList<DecisionAnswer> Answers, IReadOnlyDictionary<string, DecisionAnswer> ByName);
