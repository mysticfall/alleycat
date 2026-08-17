using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Executes one bounded tool-only turn directly on a chat client.
/// </summary>
internal static class ToolOnlyTurnRunner
{
    internal const int MaxModelRequests = 8;
    internal const int MaxToolActions = 8;
    internal const string EndTurnToolName = "end_turn";

    private static readonly AIFunction _endTurnFunction = AIFunctionFactory.Create(
        (Func<string>)(() => throw new InvalidOperationException("The local end_turn marker must not be invoked.")),
        new AIFunctionFactoryOptions
        {
            Name = EndTurnToolName,
            Description = "Reserved non-action marker. Call exactly once in final position, alone for zero actions or after actions when their results are not needed. Omit it when waiting for action results.",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["Strict"] = true,
            },
        });

    public static async Task RunAsync(
        IChatClient chatClient,
        string instructions,
        IReadOnlyList<ChatMessage> runInputMessages,
        IList<AITool> productionTools,
        bool allowMultipleToolCalls,
        ILogger logger,
        CancellationToken cancellationToken,
        bool enableReasoningLogging = true)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(runInputMessages);
        ArgumentNullException.ThrowIfNull(productionTools);
        ArgumentNullException.ThrowIfNull(logger);

        IReadOnlyDictionary<string, AIFunction> functions = ResolveFunctions(productionTools);
        AITool[] requestTools = [.. functions.Values, _endTurnFunction];
        List<ChatMessage> messages = [.. runInputMessages];
        HashSet<string> callIDs = new(StringComparer.Ordinal);
        int actionCount = 0;

        for (int requestCount = 1; requestCount <= MaxModelRequests; requestCount++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug("Tool-only request {RequestCount} starting.", requestCount);

            ChatResponse response;
            try
            {
                response = await chatClient.GetResponseAsync(
                    messages,
                    CreateRequestOptions(instructions, requestTools, allowMultipleToolCalls),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw new ToolOnlyTurnException("The tool-only model request failed.");
            }

            FunctionCallContent[] calls = ValidateResponse(response, functions, callIDs, logger, enableReasoningLogging);
            bool completesTurn = string.Equals(calls[^1].Name, EndTurnToolName, StringComparison.Ordinal);
            int productionCallCount = completesTurn ? calls.Length - 1 : calls.Length;
            logger.LogDebug(
                "Tool-only request {RequestCount} returned {ActionCount} action(s).",
                requestCount,
                productionCallCount);

            if (productionCallCount > MaxToolActions - actionCount)
            {
                throw new ToolOnlyTurnException("The tool-only turn exhausted its action limit.");
            }

            List<AIContent>? results = completesTurn ? null : new(productionCallCount);
            for (int index = 0; index < productionCallCount; index++)
            {
                FunctionCallContent call = calls[index];
                AIFunction function = functions[call.Name];
                AIFunctionArguments arguments = new(call.Arguments);
                object? result;
                try
                {
                    result = await function.InvokeAsync(arguments, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw new ToolOnlyTurnException("A tool-only action failed.");
                }

                actionCount++;
                logger.LogDebug("Tool-only action {ActionCount} completed.", actionCount);
                results?.Add(new FunctionResultContent(call.CallId, result));
            }

            if (completesTurn)
            {
                logger.LogInformation(
                    "Tool-only turn terminated after {RequestCount} request(s) and {ActionCount} action(s).",
                    requestCount,
                    actionCount);
                return;
            }

            messages.AddRange(response.Messages);
            messages.Add(new ChatMessage(ChatRole.Tool, results!));
        }

        throw new ToolOnlyTurnException("The tool-only turn exhausted its model-request limit.");
    }

    internal static ChatOptions CreateRequestOptions(
        string instructions,
        IList<AITool> tools,
        bool allowMultipleToolCalls)
        => new()
        {
            Instructions = instructions,
            Tools = tools,
            ToolMode = ChatToolMode.RequireAny,
            AllowMultipleToolCalls = allowMultipleToolCalls,
            ResponseFormat = null,
        };

    private static IReadOnlyDictionary<string, AIFunction> ResolveFunctions(IList<AITool> productionTools)
    {
        Dictionary<string, AIFunction> functions = new(StringComparer.Ordinal);
        foreach (AITool tool in productionTools)
        {
            if (tool is not AIFunction function || string.IsNullOrWhiteSpace(function.Name))
            {
                throw new ToolOnlyTurnException("The tool-only turn requires named production functions.");
            }

            if (string.Equals(function.Name, EndTurnToolName, StringComparison.Ordinal))
            {
                throw new ToolOnlyTurnException("A production function collides with the reserved end_turn name.");
            }

            if (!functions.TryAdd(function.Name, function))
            {
                throw new ToolOnlyTurnException("The tool-only turn rejects duplicate production function names.");
            }
        }

        return functions;
    }

    private static FunctionCallContent[] ValidateResponse(
        ChatResponse response,
        IReadOnlyDictionary<string, AIFunction> functions,
        ISet<string> callIDs,
        ILogger logger,
        bool enableReasoningLogging)
    {
        if (response.Messages.Count == 0)
        {
            throw InvalidResponse();
        }

        List<FunctionCallContent> calls = [];
        foreach (ChatMessage message in response.Messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                throw InvalidResponse();
            }

            foreach (AIContent content in message.Contents)
            {
                if (content is TextReasoningContent reasoning)
                {
                    if (enableReasoningLogging
                        && logger.IsEnabled(LogLevel.Trace)
                        && !string.IsNullOrWhiteSpace(reasoning.Text))
                    {
                        logger.LogTrace("Reasoning: {}", reasoning.Text);
                    }

                    continue;
                }

                if (content is not FunctionCallContent call)
                {
                    throw InvalidResponse();
                }

                if (call.Exception is not null
                    || call.InformationalOnly
                    || string.IsNullOrWhiteSpace(call.CallId)
                    || !callIDs.Add(call.CallId)
                    || (!functions.ContainsKey(call.Name)
                        && !string.Equals(call.Name, EndTurnToolName, StringComparison.Ordinal)))
                {
                    throw InvalidResponse();
                }

                calls.Add(call);
            }
        }

        int markerCount = calls.Count(call => string.Equals(call.Name, EndTurnToolName, StringComparison.Ordinal));
        if (calls.Count == 0
            || markerCount > 1
            || (markerCount == 1
                && !string.Equals(calls[^1].Name, EndTurnToolName, StringComparison.Ordinal)))
        {
            throw InvalidResponse();
        }

        foreach (FunctionCallContent call in calls)
        {
            ValidateArguments(call, functions);
        }

        return [.. calls];
    }

    private static void ValidateArguments(
        FunctionCallContent call,
        IReadOnlyDictionary<string, AIFunction> functions)
    {
        if (string.Equals(call.Name, EndTurnToolName, StringComparison.Ordinal))
        {
            if (call.Arguments is null || call.Arguments.Count != 0)
            {
                throw InvalidResponse();
            }

            return;
        }

        if (call.Arguments is null || !ArgumentsMatchSchema(call.Arguments, functions[call.Name]))
        {
            throw InvalidResponse();
        }
    }

    private static bool ArgumentsMatchSchema(IDictionary<string, object?> arguments, AIFunction function)
    {
        try
        {
            JsonElement value = JsonSerializer.SerializeToElement(arguments, function.JsonSerializerOptions);
            return MatchesSchema(value, function.JsonSchema);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool MatchesSchema(JsonElement value, JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.Undefined)
        {
            return true;
        }

        if (schema.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        if (schema.TryGetProperty("type", out JsonElement type) && !MatchesType(value, type))
        {
            return false;
        }

        if (schema.TryGetProperty("enum", out JsonElement enumValues)
            && !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> required = schema.TryGetProperty("required", out JsonElement requiredElement)
                ? [.. requiredElement.EnumerateArray().Select(item => item.GetString()!)]
                : [];
            foreach (string name in required)
            {
                if (!value.TryGetProperty(name, out _))
                {
                    return false;
                }
            }

            JsonElement properties = schema.TryGetProperty("properties", out JsonElement propertyElement)
                ? propertyElement
                : default;
            bool allowAdditional = schema.TryGetProperty("additionalProperties", out JsonElement additional)
                && additional.ValueKind != JsonValueKind.False;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                {
                    if (!MatchesSchema(property.Value, propertySchema))
                    {
                        return false;
                    }
                }
                else if (!allowAdditional)
                {
                    return false;
                }
            }
        }

        return value.ValueKind != JsonValueKind.Array
            || !schema.TryGetProperty("items", out JsonElement itemSchema)
            || !value.EnumerateArray().Any(item => !MatchesSchema(item, itemSchema));
    }

    private static bool MatchesType(JsonElement value, JsonElement type)
        => type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(candidate => MatchesType(value, candidate))
            : type.ValueKind == JsonValueKind.String && type.GetString() switch
            {
                "null" => value.ValueKind == JsonValueKind.Null,
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                _ => false,
            };

    private static ToolOnlyTurnException InvalidResponse()
        => new("The tool-only turn received an invalid response shape.");
}

internal sealed class ToolOnlyTurnException(string message) : InvalidOperationException(message);
