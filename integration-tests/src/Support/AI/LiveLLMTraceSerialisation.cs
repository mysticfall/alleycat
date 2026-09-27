using System.Globalization;
using System.Text.Json.Nodes;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Builds the JSON document for a trace. Records are already sanitised at capture time, so serialisation
/// only shapes them; no credential-bearing or provider-object data reaches this stage.
/// </summary>
internal static class LiveLLMTraceSerialisation
{
    public static JsonObject Serialise(LiveLLMTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        return new JsonObject
        {
            ["version"] = LiveLLMTrace.CurrentVersion,
            ["scenarioName"] = trace.ScenarioName,
            ["status"] = trace.Status.ToString(),
            ["startedAtUtc"] = trace.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["completedAtUtc"] = trace.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            ["failure"] = SerialiseFailure(trace.Failure),
            ["entries"] = new JsonArray([.. trace.Entries.Select(SerialiseEntry)]),
        };
    }

    private static JsonNode? SerialiseFailure(LiveLLMFailureRecord? failure)
        => failure is null
            ? null
            : new JsonObject
            {
                ["classification"] = failure.Classification.ToString(),
                ["exceptionType"] = failure.ExceptionType,
                ["safeMessage"] = failure.SafeMessage,
            };

    private static JsonNode SerialiseEntry(LiveLLMTraceEntry entry)
        => new JsonObject
        {
            ["index"] = entry.Index,
            ["kind"] = entry.Kind.ToString(),
            ["purpose"] = entry.Purpose.ToString(),
            ["startedAtUtc"] = entry.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = entry.Duration is { } duration ? duration.TotalMilliseconds : null,
            ["request"] = SerialiseRequest(entry.Request),
            ["response"] = SerialiseResponse(entry.Response),
            ["suppliedMessage"] = entry.SuppliedMessage is null ? null : SerialiseMessage(entry.SuppliedMessage),
            ["toolResult"] = entry.ToolResult is null
                ? null
                : new JsonObject
                {
                    ["callId"] = entry.ToolResult.CallId,
                    ["name"] = entry.ToolResult.Name,
                    ["result"] = entry.ToolResult.Result?.DeepClone(),
                    ["simulated"] = entry.ToolResult.Simulated,
                },
            ["failure"] = SerialiseFailure(entry.Failure),
        };

    private static JsonNode? SerialiseRequest(LiveLLMRequestRecord? request)
        => request is null
            ? null
            : new JsonObject
            {
                ["messages"] = new JsonArray([.. request.Messages.Select(SerialiseMessage)]),
                ["options"] = SerialiseOptions(request.Options),
            };

    private static JsonNode? SerialiseResponse(LiveLLMResponseRecord? response)
        => response is null
            ? null
            : new JsonObject
            {
                ["messages"] = new JsonArray([.. response.Messages.Select(SerialiseMessage)]),
                ["responseId"] = response.ResponseId,
                ["modelId"] = response.ModelId,
                ["createdAt"] = response.CreatedAt?.ToString("O", CultureInfo.InvariantCulture),
                ["finishReason"] = response.FinishReason,
                ["usage"] = response.Usage is null
                    ? null
                    : new JsonObject
                    {
                        ["inputTokenCount"] = response.Usage.InputTokenCount,
                        ["outputTokenCount"] = response.Usage.OutputTokenCount,
                        ["totalTokenCount"] = response.Usage.TotalTokenCount,
                    },
            };

    private static JsonNode SerialiseMessage(LiveLLMMessageRecord message)
        => new JsonObject
        {
            ["role"] = message.Role,
            ["contents"] = new JsonArray([.. message.Contents.Select(SerialiseContent)]),
        };

    private static JsonNode SerialiseContent(LiveLLMContentRecord content)
        => new JsonObject
        {
            ["kind"] = content.Kind,
            ["text"] = content.Text,
            ["callId"] = content.CallId,
            ["name"] = content.Name,
            ["data"] = content.Data?.DeepClone(),
        };

    private static JsonNode? SerialiseOptions(LiveLLMOptionsRecord? options)
        => options is null
            ? null
            : new JsonObject
            {
                ["modelId"] = options.ModelId,
                ["temperature"] = options.Temperature,
                ["maxOutputTokens"] = options.MaxOutputTokens,
                ["topP"] = options.TopP,
                ["frequencyPenalty"] = options.FrequencyPenalty,
                ["presencePenalty"] = options.PresencePenalty,
                ["stopSequences"] = options.StopSequences is null
                    ? null
                    : new JsonArray([.. options.StopSequences.Select(static stop => (JsonNode?)stop)]),
                ["toolMode"] = options.ToolMode,
                ["tools"] = new JsonArray([.. options.Tools.Select(SerialiseTool)]),
            };

    private static JsonNode SerialiseTool(LiveLLMToolRecord tool)
        => new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["jsonSchema"] = tool.JsonSchema?.DeepClone(),
        };
}
