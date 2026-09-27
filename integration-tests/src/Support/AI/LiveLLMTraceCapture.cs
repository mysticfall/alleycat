using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Captures live Microsoft.Extensions.AI objects into immutable, sanitised trace records and serialises
/// traces to JSON. Capture deep-copies every message, content, option, and tool value at record time, so
/// traces are stable snapshots of what was sent and received; later mutation of the caller's objects cannot
/// alter recorded evidence.
/// </summary>
internal static class LiveLLMTraceCapture
{
    private const int MaximumDepth = 8;
    private const int MaximumItems = 100;

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Captures one request as an immutable record.</summary>
    public static LiveLLMRequestRecord CaptureRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return new LiveLLMRequestRecord([.. messages.Select(CaptureMessage)], CaptureOptions(options));
    }

    /// <summary>Captures one response, including tool-call ids and arguments, as an immutable record.</summary>
    public static LiveLLMResponseRecord CaptureResponse(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new LiveLLMResponseRecord(
            [.. response.Messages.Select(CaptureMessage)],
            LiveLLMTraceSanitiser.SanitiseText(response.ResponseId),
            LiveLLMTraceSanitiser.SanitiseText(response.ModelId),
            response.CreatedAt,
            response.FinishReason is { } finishReason ? LiveLLMTraceSanitiser.SanitiseText(finishReason.Value) : null,
            response.Usage is { } usage
                ? new LiveLLMUsageRecord(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount)
                : null);
    }

    internal static LiveLLMMessageRecord CaptureMessage(ChatMessage message)
        => new(message.Role.Value, [.. message.Contents.Select(CaptureContent)]);

    private static LiveLLMContentRecord CaptureContent(AIContent content)
        => content switch
        {
            TextContent text => new LiveLLMContentRecord(
                "text",
                LiveLLMTraceSanitiser.SanitiseText(text.Text),
                null,
                null,
                null),
            FunctionCallContent call => new LiveLLMContentRecord(
                "functionCall",
                null,
                LiveLLMTraceSanitiser.SanitiseText(call.CallId),
                LiveLLMTraceSanitiser.SanitiseText(call.Name),
                SafeDictionary(call.Arguments)),
            FunctionResultContent result => new LiveLLMContentRecord(
                "functionResult",
                null,
                LiveLLMTraceSanitiser.SanitiseText(result.CallId),
                null,
                SafeValue(result.Result)),
            DataContent data => new LiveLLMContentRecord(
                "data",
                null,
                null,
                LiveLLMTraceSanitiser.SanitiseText(data.MediaType),
                new JsonObject
                {
                    // The digest and length — never the payload itself — so traces prove exactly which
                    // image bytes were sent without bloating or leaking content into artefacts.
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data.Data.Span)),
                    ["bytes"] = data.Data.Length,
                }),
            _ => new LiveLLMContentRecord(
                "other",
                null,
                null,
                null,
                SafeValue(content.ToString())),
        };

    private static LiveLLMOptionsRecord? CaptureOptions(ChatOptions? options)
        => options is null
            ? null
            : new LiveLLMOptionsRecord(
            LiveLLMTraceSanitiser.SanitiseText(options.ModelId),
            options.Temperature,
            options.MaxOutputTokens,
            options.TopP,
            options.FrequencyPenalty,
            options.PresencePenalty,
            options.StopSequences is { Count: > 0 } stopSequences
                ? [.. stopSequences.Select(LiveLLMTraceSanitiser.SanitiseText)]
                : null,
            options.ToolMode is { } toolMode ? LiveLLMTraceSanitiser.SanitiseText(toolMode.ToString()) : null,
            options.Tools is { Count: > 0 } tools ? [.. tools.Select(CaptureTool)] : []);

    private static LiveLLMToolRecord CaptureTool(AITool tool)
    {
        JsonNode? jsonSchema = tool is AIFunctionDeclaration { JsonSchema: { ValueKind: not JsonValueKind.Undefined } toolSchema }
            ? SafeValue(toolSchema)
            : null;

        return new LiveLLMToolRecord(
            LiveLLMTraceSanitiser.SanitiseText(tool.Name),
            LiveLLMTraceSanitiser.SanitiseText(tool.Description),
            jsonSchema);
    }

    private static JsonObject? SafeDictionary(IDictionary<string, object?>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        JsonObject result = [];
        foreach ((string key, object? value) in values)
        {
            result[LiveLLMTraceSanitiser.SanitiseText(key)] = SafeValue(value);
        }

        return result;
    }

    /// <summary>
    /// Converts an arbitrary value into a sanitised JSON node, redacting credential-shaped strings and
    /// bounding depth and collection size. Unknown object types fall back to a bounded, sanitised
    /// <see cref="object.ToString" /> rendering; configuration and client objects must never be passed here.
    /// </summary>
    public static JsonNode? SafeValue(object? value, int depth = 0)
        => value is null
            ? null
            : depth > MaximumDepth
            ? "[elided]"
            : value switch
            {
                string text => LiveLLMTraceSanitiser.SanitiseText(text),
                bool flag => flag,
                int number => number,
                long number => number,
                double number => number,
                float number => number,
                decimal number => number,
                JsonElement element => SafeElement(element, depth),
                JsonNode node => SafeNode(node, depth),
                IDictionary<string, object?> dictionary => SafeDictionary(dictionary),
                IEnumerable sequence => SafeSequence(sequence, depth),
                _ => LiveLLMTraceSanitiser.SanitiseText(value.ToString()),
            };

    private static JsonNode? SafeElement(JsonElement element, int depth)
        => element.ValueKind switch
        {
            JsonValueKind.String => LiveLLMTraceSanitiser.SanitiseText(element.GetString()),
            JsonValueKind.Number => element.TryGetInt64(out long integer)
                ? JsonValue.Create(integer)
                : JsonValue.Create(element.GetDouble()),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => SafeSequence(element.EnumerateArray().Select(static item => (object?)item), depth),
            JsonValueKind.Object => SafeElementObject(element, depth),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => null,
        };

    private static JsonNode SafeElementObject(JsonElement element, int depth)
    {
        JsonObject result = [];
        foreach (JsonProperty property in element.EnumerateObject())
        {
            result[LiveLLMTraceSanitiser.SanitiseText(property.Name)] = SafeElement(property.Value, depth + 1);
        }

        return result;
    }

    private static JsonNode? SafeNode(JsonNode node, int depth)
        => node switch
        {
            JsonObject jsonObject => SafeObject(jsonObject, depth),
            JsonArray array => SafeSequence(array.Select(static item => (object?)item), depth),
            JsonValue value => SafeValueNode(value),
            _ => LiveLLMTraceSanitiser.SanitiseText(node.ToJsonString()),
        };

    private static JsonNode SafeObject(JsonObject jsonObject, int depth)
    {
        JsonObject result = [];
        foreach (KeyValuePair<string, JsonNode?> property in jsonObject)
        {
            result[LiveLLMTraceSanitiser.SanitiseText(property.Key)] =
                property.Value is null ? null : SafeNode(property.Value, depth + 1);
        }

        return result;
    }

    private static JsonNode SafeValueNode(JsonValue value)
        => value.TryGetValue(out bool flag) ? flag
            : value.TryGetValue<string>(out string? text) ? LiveLLMTraceSanitiser.SanitiseText(text)
            : value.TryGetValue<long>(out long integer) ? integer
            : value.TryGetValue<double>(out double number) ? number
            : LiveLLMTraceSanitiser.SanitiseText(value.ToJsonString());

    private static JsonNode SafeSequence(IEnumerable sequence, int depth)
    {
        JsonArray result = [];
        int count = 0;
        foreach (object? item in sequence)
        {
            if (count >= MaximumItems)
            {
                result.Add("[elided]");
                break;
            }

            result.Add(SafeValue(item, depth + 1));
            count++;
        }

        return result;
    }
}
