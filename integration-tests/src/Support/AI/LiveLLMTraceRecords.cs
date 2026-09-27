using System.Text.Json.Nodes;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>Kind of a trace entry.</summary>
public enum LiveLLMTraceEntryKind
{
    /// <summary>A provider request/response exchange (target or continuation).</summary>
    Exchange,

    /// <summary>A continuation message supplied by the experiment (scripted, no provider call).</summary>
    SuppliedContinuation,

    /// <summary>A simulated tool result supplied by the experiment (never executed gameplay).</summary>
    SuppliedToolResult,
}

/// <summary>
/// Immutable snapshot of one captured message: its role plus its ordered content records.
/// </summary>
public sealed record LiveLLMMessageRecord(string Role, IReadOnlyList<LiveLLMContentRecord> Contents)
{
    /// <summary>Concatenation of the message's text contents; empty when the message carries none.</summary>
    public string Text => string.Concat(Contents.Where(static content => content.Text is not null).Select(static content => content.Text));
}

/// <summary>
/// Immutable snapshot of one content item. Text contents carry <see cref="Text" />; function calls carry
/// <see cref="CallId" />, <see cref="Name" />, and the sanitised argument object in <see cref="Data" />;
/// function results carry <see cref="CallId" /> and the sanitised result in <see cref="Data" />; data
/// contents (for example attached images) carry the media type in <see cref="Name" /> and the payload's
/// SHA-256 digest and byte length in <see cref="Data" /> — never the payload itself.
/// </summary>
public sealed record LiveLLMContentRecord(
    string Kind,
    string? Text,
    string? CallId,
    string? Name,
    JsonNode? Data);

/// <summary>
/// Sanitised snapshot of the request settings a scenario declared for one exchange: model settings, tool
/// selection mode, and the tool definitions with their JSON schemas.
/// </summary>
public sealed record LiveLLMOptionsRecord(
    string? ModelId,
    double? Temperature,
    int? MaxOutputTokens,
    double? TopP,
    double? FrequencyPenalty,
    double? PresencePenalty,
    IReadOnlyList<string>? StopSequences,
    string? ToolMode,
    IReadOnlyList<LiveLLMToolRecord> Tools);

/// <summary>Immutable snapshot of one tool definition: name, description, and JSON schema.</summary>
public sealed record LiveLLMToolRecord(string Name, string? Description, JsonNode? JsonSchema);

/// <summary>Immutable snapshot of the provider-reported usage for one exchange, when available.</summary>
public sealed record LiveLLMUsageRecord(long? InputTokenCount, long? OutputTokenCount, long? TotalTokenCount);

/// <summary>
/// Immutable snapshot of one response: its messages (including tool-call ids and arguments), provider
/// metadata, and usage when the provider supplies it.
/// </summary>
public sealed record LiveLLMResponseRecord(
    IReadOnlyList<LiveLLMMessageRecord> Messages,
    string? ResponseId,
    string? ModelId,
    DateTimeOffset? CreatedAt,
    string? FinishReason,
    LiveLLMUsageRecord? Usage);

/// <summary>Immutable snapshot of one request: its messages and sanitised settings.</summary>
public sealed record LiveLLMRequestRecord(
    IReadOnlyList<LiveLLMMessageRecord> Messages,
    LiveLLMOptionsRecord? Options);

/// <summary>
/// A tool result supplied by the experiment. <see cref="Simulated" /> is always <see langword="true" />:
/// the harness never executes model-selected functions, so every supplied result is labelled as a simulated
/// result rather than executed gameplay.
/// </summary>
public sealed record LiveLLMToolResultRecord(string CallId, string Name, JsonNode? Result, bool Simulated);

/// <summary>
/// One ordered entry in an evidence trace. Exchanges carry <see cref="Request" /> and, when the provider
/// answered, <see cref="Response" />; supplied continuations carry <see cref="SuppliedMessage" />; supplied
/// tool results carry <see cref="ToolResult" />. A failed exchange keeps its request and carries
/// <see cref="Failure" /> instead of a response.
/// </summary>
public sealed record LiveLLMTraceEntry(
    int Index,
    LiveLLMTraceEntryKind Kind,
    LiveLLMPurpose Purpose,
    DateTimeOffset StartedAtUtc,
    TimeSpan? Duration,
    LiveLLMRequestRecord? Request,
    LiveLLMResponseRecord? Response,
    LiveLLMMessageRecord? SuppliedMessage,
    LiveLLMToolResultRecord? ToolResult,
    LiveLLMFailureRecord? Failure);
