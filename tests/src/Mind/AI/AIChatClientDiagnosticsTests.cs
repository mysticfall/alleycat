using AlleyCat.Mind.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Unit coverage for feature-gated Microsoft.Extensions.AI request and response logging.
/// </summary>
public sealed class AIChatClientDiagnosticsTests
{
    private const string LoggingCategory = "Microsoft.Extensions.AI.LoggingChatClient";

    /// <summary>
    /// Disabled diagnostics must leave the fresh client untouched without requiring logging infrastructure.
    /// </summary>
    [Fact]
    public async Task Decorate_WhenDiagnosticsDisabled_ReturnsOriginalClientWithoutResolvingLoggerFactory()
    {
        StubChatClient client = new("unused response");
        bool resolverCalled = false;

        IChatClient result = AIChatClientDiagnostics.Decorate(
            client,
            new AIDiagnosticsSettings(EnableRequestResponseLogging: false),
            () =>
            {
                resolverCalled = true;
                throw new InvalidOperationException("The logger factory should not be resolved.");
            });

        Assert.Same(client, result);
        Assert.False(resolverCalled);

        ChatResponse response = await result.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "private disabled request")],
            new ChatOptions { ModelId = "private-disabled-model" },
            CancellationToken.None);

        Assert.Equal("unused response", response.Text);
        Assert.Equal(1, client.InvocationCount);
        Assert.False(resolverCalled);
    }

    /// <summary>
    /// Trace logging records the complete request, options, and response through the dedicated category.
    /// </summary>
    [Fact]
    public async Task Decorate_WhenDiagnosticsEnabledAtTrace_LogsRequestOptionsAndResponseDetail()
    {
        const string requestSecret = "private player request";
        const string responseSecret = "private model response";
        CapturingLoggerFactory loggerFactory = new(LogLevel.Trace);
        StubChatClient innerClient = new(responseSecret);
        IChatClient client = AIChatClientDiagnostics.Decorate(
            innerClient,
            new AIDiagnosticsSettings(EnableRequestResponseLogging: true),
            () => loggerFactory);

        _ = Assert.IsType<LoggingChatClient>(client);
        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, requestSecret)],
            new ChatOptions { ModelId = "diagnostic-model" },
            CancellationToken.None);

        Assert.Equal(responseSecret, response.Text);
        Assert.Equal(1, innerClient.InvocationCount);
        Assert.All(
            loggerFactory.Entries.Where(entry => entry.CategoryName == LoggingCategory),
            entry => Assert.Equal(LoggingCategory, entry.CategoryName));
        Assert.Contains(loggerFactory.Entries, entry =>
            entry.Level == LogLevel.Trace
            && entry.Message.Contains(requestSecret, StringComparison.Ordinal)
            && entry.Message.Contains("diagnostic-model", StringComparison.Ordinal));
        Assert.Contains(loggerFactory.Entries, entry =>
            entry.Level == LogLevel.Trace
            && entry.Message.Contains(responseSecret, StringComparison.Ordinal));
    }

    /// <summary>
    /// The production tool-only loop is logged at every model boundary.
    /// </summary>
    [Fact]
    public async Task Decorate_WhenToolOnlyLoopRuns_LogsEveryRequestAndResponse()
    {
        CapturingLoggerFactory loggerFactory = new(LogLevel.Trace);
        ScriptedToolLoopChatClient innerClient = new();
        IChatClient client = AIChatClientDiagnostics.Decorate(
            innerClient,
            new AIDiagnosticsSettings(EnableRequestResponseLogging: true),
            () => loggerFactory);
        List<string> toolInvocations = [];
        AIFunction tool = AIFunctionFactory.Create(
            (string value) =>
            {
                toolInvocations.Add(value);
                return $"tool result: {value}";
            },
            "diagnostic_tool");
        await ToolOnlyTurnRunner.RunAsync(
            client,
            "Complete the scripted diagnostic turn.",
            [],
            [tool],
            false,
            loggerFactory.CreateLogger("test"),
            CancellationToken.None);

        Assert.Equal(3, innerClient.InvocationCount);
        Assert.Equal(["initial", "intermediate"], toolInvocations);
        Assert.Equal(3, innerClient.Requests.Count);
        Assert.DoesNotContain(innerClient.Requests[0].SelectMany(message => message.Contents),
            content => content is FunctionResultContent);
        Assert.Contains(innerClient.Requests[1].SelectMany(message => message.Contents),
            content => content is FunctionResultContent result
                && string.Equals(result.Result?.ToString(), "tool result: initial", StringComparison.Ordinal));
        Assert.Contains(innerClient.Requests[2].SelectMany(message => message.Contents),
            content => content is FunctionResultContent result
                && string.Equals(result.Result?.ToString(), "tool result: intermediate", StringComparison.Ordinal));
        CapturedLogEntry[] requestEntries =
        [
            .. loggerFactory.Entries.Where(entry =>
                entry.Level == LogLevel.Trace && entry.Message.Contains(" invoked:", StringComparison.Ordinal)),
        ];
        CapturedLogEntry[] responseEntries =
        [
            .. loggerFactory.Entries.Where(entry =>
                entry.Level == LogLevel.Trace && entry.Message.Contains(" completed:", StringComparison.Ordinal)),
        ];
        Assert.Equal(3, requestEntries.Length);
        Assert.Equal(3, responseEntries.Length);
        Assert.Contains("initial-call", responseEntries[0].Message, StringComparison.Ordinal);
        Assert.Contains("intermediate-call", responseEntries[1].Message, StringComparison.Ordinal);
        Assert.Contains(ToolOnlyTurnRunner.EndTurnToolName, responseEntries[2].Message, StringComparison.Ordinal);
        Assert.Contains("tool result: initial", requestEntries[1].Message, StringComparison.Ordinal);
        Assert.Contains("tool result: intermediate", requestEntries[2].Message, StringComparison.Ordinal);
        Assert.All(
            loggerFactory.Entries.Where(entry => entry.CategoryName == LoggingCategory),
            entry => Assert.Equal(LoggingCategory, entry.CategoryName));
    }

    /// <summary>
    /// Below Trace, sensitive detail is neither emitted nor traversed by payload serialisation.
    /// </summary>
    [Fact]
    public async Task Decorate_WhenDedicatedCategoryIsBelowTrace_DoesNotSerialiseSensitivePayloadDetail()
    {
        const string requestSecret = "request must remain private";
        const string responseSecret = "response must remain private";
        CapturingLoggerFactory loggerFactory = new(LogLevel.Debug);
        SerializationProbe probe = new();
        StubChatClient innerClient = new(responseSecret);
        IChatClient client = AIChatClientDiagnostics.Decorate(
            innerClient,
            new AIDiagnosticsSettings(EnableRequestResponseLogging: true),
            () => loggerFactory);
        ChatOptions options = new()
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["sensitive-probe"] = probe,
            },
        };

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, requestSecret)],
            options,
            CancellationToken.None);

        Assert.Equal(responseSecret, response.Text);
        Assert.Equal(0, probe.SerializationAccessCount);
        Assert.DoesNotContain(loggerFactory.Entries, entry =>
            entry.Message.Contains(requestSecret, StringComparison.Ordinal)
            || entry.Message.Contains(responseSecret, StringComparison.Ordinal));
        Assert.Equal(2, loggerFactory.Entries.Count(entry => entry.Level == LogLevel.Debug));
    }

    /// <summary>
    /// Enabled diagnostics require the active logger factory resolver to succeed clearly.
    /// </summary>
    [Fact]
    public void Decorate_WhenDiagnosticsEnabledAndLoggerFactoryIsMissing_FailsClearly()
    {
        StubChatClient client = new("unused response");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            AIChatClientDiagnostics.Decorate(
                client,
                new AIDiagnosticsSettings(EnableRequestResponseLogging: true),
                () => null!));

        Assert.Contains("require an active logger factory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubChatClient(params string[] responses) : IChatClient
    {
        public int InvocationCount
        {
            get;
            private set;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            int responseIndex = InvocationCount++;
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, responses[responseIndex])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedToolLoopChatClient : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _requests = [];

        public int InvocationCount => _requests.Count;

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => _requests;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(2, options?.Tools?.Count);
            Assert.Null(options?.ResponseFormat);
            _ = Assert.IsType<RequiredChatToolMode>(options?.ToolMode);
            _requests.Add([.. messages.Select(message => message.Clone())]);

            ChatMessage response = _requests.Count switch
            {
                1 => CreateToolCall("initial-call", "initial"),
                2 => CreateToolCall("intermediate-call", "intermediate"),
                3 => new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "end-call",
                        ToolOnlyTurnRunner.EndTurnToolName,
                        new Dictionary<string, object?>())]),
                _ => throw new InvalidOperationException("The tool-only loop made an unexpected model invocation."),
            };
            return Task.FromResult(new ChatResponse(response));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static ChatMessage CreateToolCall(string callId, string value)
            => new(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    callId,
                    "diagnostic_tool",
                    new Dictionary<string, object?> { ["value"] = value })]);
    }

    private sealed class SerializationProbe
    {
        public int SerializationAccessCount
        {
            get;
            private set;
        }

        public string SensitiveValue
        {
            get
            {
                SerializationAccessCount++;
                return "serialised secret";
            }
        }
    }

    private sealed class CapturingLoggerFactory(LogLevel minimumLevel) : ILoggerFactory
    {
        private readonly List<CapturedLogEntry> _entries = [];

        public IReadOnlyList<CapturedLogEntry> Entries => _entries;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(categoryName, minimumLevel, _entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(
        string categoryName,
        LogLevel minimumLevel,
        List<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed record CapturedLogEntry(string CategoryName, LogLevel Level, string Message);
}
