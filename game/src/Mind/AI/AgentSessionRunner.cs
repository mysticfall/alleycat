using System.ClientModel;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Executes one long-running agent session as an append-only transcript driven through bounded, stateless provider
/// requests (AI-002 Transcript Execution).
/// </summary>
/// <remarks>
/// <para>
/// This loop is deliberately custom rather than hosted by <c>Microsoft.Agents.AI</c>'s
/// <c>ChatClientAgent</c>/<c>AIAgent.RunAsync</c> abstractions. The framework's run loop applies one cancellation
/// token to the whole run, so it cannot cancel only an in-flight generation while a tool in flight continues
/// normally with its cut-short result (AI-002 TR-39 versus TR-40); it does not provide the strict whole-batch
/// tool-only validation before any execution (AI-002 TR-11/12); its history and function-invocation machinery would
/// append protocol entries beyond assistant tool calls, tool results, and injected messages (AI-002 TR-14) and
/// imposes iteration bounds. Where the framework fits without deviation — the stateless transport underneath — it is
/// used unchanged through <see cref="IChatClient"/>.
/// </para>
/// <para>
/// Thread safety: <see cref="SignalInterruption"/> may be called from any thread; the transcript itself is only
/// touched by <see cref="RunAsync"/>.
/// </para>
/// </remarks>
internal sealed class AgentSessionRunner
{
    private static readonly TimeSpan[] _defaultRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private readonly IChatClient _chatClient;
    private readonly IReadOnlyList<ChatMessage> _runInputMessages;
    private readonly IReadOnlyDictionary<string, AIFunction> _functions;
    private readonly ChatOptions _chatOptions;
    private readonly ILogger _logger;
    private readonly bool _enableReasoningLogging;
    private readonly TimeSpan[] _retryDelays;
    private readonly int _maxTransportRetries;
    private readonly ConcurrentQueue<string> _pendingInjections = new();
    private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _phaseCancellation;
    private volatile bool _ended;

    public AgentSessionRunner(
        IChatClient chatClient,
        string instructions,
        IReadOnlyList<ChatMessage> runInputMessages,
        IList<AITool> productionTools,
        bool allowMultipleToolCalls,
        ILogger logger,
        bool enableReasoningLogging = true,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _runInputMessages = runInputMessages ?? throw new ArgumentNullException(nameof(runInputMessages));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enableReasoningLogging = enableReasoningLogging;
        _retryDelays = [.. retryDelays ?? _defaultRetryDelays];
        _maxTransportRetries = _retryDelays.Length;
        _functions = ResolveFunctions(productionTools);
        _chatOptions = new ChatOptions
        {
            Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions)),
            Tools = [.. _functions.Values],
            ToolMode = ChatToolMode.RequireAny,
            AllowMultipleToolCalls = allowMultipleToolCalls,
            ResponseFormat = null,
        };
    }

    /// <summary>
    /// Signals newly notable observations while the session runs (AI-002 TR-41): the active phase is cancelled —
    /// discarding partial generation output, or letting an in-flight tool return its cut-short result — and the
    /// supplied message is appended as an injected user message before the next request replays the transcript.
    /// </summary>
    /// <param name="injectedMessage">Concise rendered summary of the notable observations.</param>
    public void SignalInterruption(string injectedMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(injectedMessage);
        if (_ended)
        {
            return;
        }

        _pendingInjections.Enqueue(injectedMessage);
        try
        {
            Volatile.Read(ref _phaseCancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The phase completed naturally after the signal was queued; the injection drains before the next
            // request instead.
        }
    }

    /// <summary>
    /// Runs the session until node-lifetime cancellation or a fatal, contained failure.
    /// </summary>
    /// <param name="lifetimeToken">Node-lifetime cancellation that ends the session quietly.</param>
    public async Task RunAsync(CancellationToken lifetimeToken)
    {
        // Session-scoped transient protocol state, discarded at session end (AI-002 TR-15).
        List<ChatMessage> transcript = [.. _runInputMessages];
        int requestCount = 0;
        _logger.LogInformation("Agent session starting with {ToolCount} tool(s).", _functions.Count);
        try
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                DrainPendingInjections(transcript);
                requestCount++;

                ChatResponse? response = await RequestWithTransportRetryAsync(transcript, requestCount, lifetimeToken);
                if (response is null)
                {
                    // Interrupted mid-generation: partial assistant output was discarded; the injected message
                    // drains above and a fresh request replays the transcript (AI-002 TR-40).
                    continue;
                }

                FunctionCallContent[] calls = ValidateResponse(response, requestCount);
                transcript.AddRange(response.Messages);
                List<AIContent> results = new(calls.Length);
                foreach (FunctionCallContent call in calls)
                {
                    results.Add(new FunctionResultContent(call.CallId, await InvokeToolAsync(call, lifetimeToken)));
                }

                transcript.Add(new ChatMessage(ChatRole.Tool, results));
            }

            _logger.LogInformation("Agent session ended after {RequestCount} request(s).", requestCount);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            // Expected node-lifetime interruption is never a backend failure (AI-002 TR-44).
            _logger.LogInformation("Agent session ended by cancellation after {RequestCount} request(s).", requestCount);
        }
        finally
        {
            _ended = true;
            transcript.Clear();
        }
    }

    private async Task<ChatResponse?> RequestWithTransportRetryAsync(
        List<ChatMessage> transcript,
        int requestCount,
        CancellationToken lifetimeToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            lifetimeToken.ThrowIfCancellationRequested();
            var phase = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            Volatile.Write(ref _phaseCancellation, phase);
            try
            {
                _logger.LogDebug("Agent session request {RequestCount} starting.", requestCount);
                return await _chatClient.GetResponseAsync(transcript, _chatOptions, phase.Token);
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (phase.Token.IsCancellationRequested)
            {
                // Self-inflicted phase cancellation — issued only by SignalInterruption, always before its own
                // cancellation can arrive — is the expected interruption path: never a backend failure, never
                // retried (AI-002 TR-41).
                _logger.LogDebug("Agent session request {RequestCount} interrupted.", requestCount);
                return null;
            }
            catch (Exception exception) when (attempt < _maxTransportRetries && IsTransientTransportFailure(exception, phase.Token))
            {
                TimeSpan delay = _retryDelays[Math.Min(attempt, _retryDelays.Length - 1)];
                _logger.LogWarning(
                    exception,
                    "Agent session request {RequestCount} failed transiently; retrying in {RetryDelay}.",
                    requestCount,
                    delay);
                await Task.Delay(delay, lifetimeToken);
            }
            catch (Exception exception) when (IsTransientTransportFailure(exception, phase.Token))
            {
                // Retry exhaustion ends the session through the contained failure path (AI-002 TR-43): the failure
                // is never surfaced to the agent as a tool result or transcript entry.
                throw new AgentSessionException(
                    $"The agent session request {requestCount} exhausted its transport retries: {exception.Message}",
                    exception);
            }
            catch (Exception exception)
            {
                throw new AgentSessionException(
                    $"The agent session request {requestCount} failed: {exception.Message}",
                    exception);
            }
            finally
            {
                if (ReferenceEquals(Volatile.Read(ref _phaseCancellation), phase))
                {
                    Volatile.Write(ref _phaseCancellation, null);
                }

                phase.Dispose();
            }
        }
    }

    private async Task<object?> InvokeToolAsync(
        FunctionCallContent call,
        CancellationToken lifetimeToken)
    {
        AIFunction function = _functions[call.Name];
        var phase = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        Volatile.Write(ref _phaseCancellation, phase);
        try
        {
            _logger.LogDebug("Agent session tool '{ToolName}' starting.", function.Name);
            return await function.InvokeAsync(new AIFunctionArguments(call.Arguments), phase.Token);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Interruption makes a tool return early with an interrupted result (AI-002 TR-39); production tools
            // normally report their own cut-short wording.
            _logger.LogDebug("Agent session tool '{ToolName}' interrupted.", function.Name);
            return "The action was interrupted before it completed.";
        }
        catch (Exception exception)
        {
            // Tool errors surface through the tool result so the agent decides whether and how to retry
            // (AI-002 TR-42); they never end the session.
            _logger.LogWarning(exception, "Agent session tool '{ToolName}' failed.", function.Name);
            return $"The action failed: {exception.Message}";
        }
        finally
        {
            _logger.LogDebug("Agent session tool '{ToolName}' completed.", function.Name);
            if (ReferenceEquals(Volatile.Read(ref _phaseCancellation), phase))
            {
                Volatile.Write(ref _phaseCancellation, null);
            }

            phase.Dispose();
        }
    }

    private void DrainPendingInjections(List<ChatMessage> transcript)
    {
        while (_pendingInjections.TryDequeue(out string? injectedMessage))
        {
            _logger.LogDebug("Agent session appending injected message after interruption.");
            transcript.Add(new ChatMessage(ChatRole.User, injectedMessage));
        }
    }

    private static IReadOnlyDictionary<string, AIFunction> ResolveFunctions(IList<AITool> productionTools)
    {
        Dictionary<string, AIFunction> functions = new(StringComparer.Ordinal);
        foreach (AITool tool in productionTools)
        {
            if (tool is not AIFunction function || string.IsNullOrWhiteSpace(function.Name))
            {
                throw new AgentSessionException("The agent session requires named production functions.");
            }

            if (!functions.TryAdd(function.Name, function))
            {
                throw new AgentSessionException("The agent session rejects duplicate production function names.");
            }
        }

        return functions;
    }

    /// <summary>
    /// Validates the complete response batch — every call, identifier, argument, and content item — before any tool
    /// in the batch executes (AI-002 TR-11/12).
    /// </summary>
    private FunctionCallContent[] ValidateResponse(ChatResponse response, int requestCount)
    {
        if (response.Messages.Count == 0)
        {
            throw InvalidResponse(requestCount);
        }

        List<FunctionCallContent> calls = [];
        foreach (ChatMessage message in response.Messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                throw InvalidResponse(requestCount);
            }

            foreach (AIContent content in message.Contents)
            {
                if (content is TextReasoningContent reasoning)
                {
                    if (_enableReasoningLogging
                        && _logger.IsEnabled(LogLevel.Trace)
                        && !string.IsNullOrWhiteSpace(reasoning.Text))
                    {
                        _logger.LogTrace("Reasoning: {}", reasoning.Text);
                    }

                    continue;
                }

                if (content is not FunctionCallContent call)
                {
                    throw InvalidResponse(requestCount);
                }

                if (call.Exception is not null
                    || call.InformationalOnly
                    || string.IsNullOrWhiteSpace(call.CallId)
                    || !_callIds.Add(call.CallId)
                    || !_functions.ContainsKey(call.Name))
                {
                    throw InvalidResponse(requestCount);
                }

                calls.Add(call);
            }
        }

        if (calls.Count == 0)
        {
            throw InvalidResponse(requestCount);
        }

        foreach (FunctionCallContent call in calls)
        {
            ValidateArguments(call, requestCount);
        }

        return [.. calls];
    }

    private void ValidateArguments(FunctionCallContent call, int requestCount)
    {
        AIFunction function = _functions[call.Name];
        if (call.Arguments is null || !ArgumentsMatchSchema(call.Arguments, function))
        {
            throw InvalidResponse(requestCount);
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

    /// <summary>
    /// Classifies transport failures requiring bounded transparent retry (AI-002 TR-43). Provider and HTTP
    /// timeouts — the OpenAI SDK's network timeout and <c>HttpClient</c> timeouts — surface as
    /// <see cref="TaskCanceledException" />/<see cref="OperationCanceledException" /> on a linked token rather than as
    /// <see cref="TimeoutException" /> and without cancelling the phase token, so a cancellation that cancelled
    /// neither the lifetime token nor the phase token is by elimination such a transport timeout:
    /// <see cref="SignalInterruption(string)" /> always cancels the phase token before its own cancellation can
    /// arrive, making phase-cancellation state the only self-inflicted-interruption discriminator.
    /// </summary>
    private static bool IsTransientTransportFailure(Exception exception, CancellationToken phaseToken)
    {
        if (exception is OperationCanceledException
            && (!phaseToken.IsCancellationRequested || exception.InnerException is TimeoutException))
        {
            return true;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException:
                case IOException:
                case TimeoutException:
                    return true;
                case ClientResultException clientResult:
                    int status = clientResult.Status;
                    return status is 408 or 429 or (>= 500 and < 600);
                default:
                    break;
            }
        }

        return false;
    }

    private static AgentSessionException InvalidResponse(int requestCount)
        => new($"The agent session received an invalid response shape at request {requestCount}.");
}

/// <summary>
/// Contained, session-ending failure of an agent session: logged without crashing the scene and never retried
/// automatically (AI-002 TR-43, TR-12).
/// </summary>
internal sealed class AgentSessionException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
