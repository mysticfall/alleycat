using System.Collections.Concurrent;
using System.Diagnostics;
using AlleyCat.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Core.Logging;

/// <summary>
/// Unit coverage for the temporary Agent Framework OpenTelemetry log listener.
/// </summary>
public sealed class AgenticMindActivityLogListenerTests
{
    /// <summary>
    /// Stopped activities should expose sensitive trial payload fields in the AlleyCat log for runtime verification.
    /// </summary>
    [Fact]
    public void Start_WhenActivityStops_LogsTagsEventsAndBaggage()
    {
        string sourceName = CreateUniqueSourceName();
        using TestLoggerFactory loggerFactory = new();

        AgenticMindActivityLogListener.Start(loggerFactory, sourceName);

        using ActivitySource source = new(sourceName);
        using (Activity activity = source.StartActivity("chat-turn") ?? throw new InvalidOperationException("Activity was not sampled."))
        {
            _ = activity.SetTag("gen_ai.prompt", "player said hello");
            _ = activity.AddEvent(new ActivityEvent(
                "model-response",
                tags: new ActivityTagsCollection
                {
                    ["gen_ai.response"] = "alley replied",
                }));
            _ = activity.AddBaggage("tool.payload", "speak tool content");
        }

        string message = Assert.Single(loggerFactory.Messages);
        Assert.Contains("chat-turn", message, StringComparison.Ordinal);
        Assert.Contains("gen_ai.prompt=player said hello", message, StringComparison.Ordinal);
        Assert.Contains("gen_ai.response=alley replied", message, StringComparison.Ordinal);
        Assert.Contains("tool.payload=speak tool content", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Multiple AgenticMind instances may try to start the trial listener; only one listener should subscribe per source.
    /// </summary>
    [Fact]
    public void Start_WhenCalledTwiceForSameSource_RegistersOnlyOnce()
    {
        string sourceName = CreateUniqueSourceName();
        using TestLoggerFactory loggerFactory = new();

        AgenticMindActivityLogListener.Start(loggerFactory, sourceName);
        AgenticMindActivityLogListener.Start(loggerFactory, sourceName);

        using ActivitySource source = new(sourceName);
        using (Activity activity = source.StartActivity("single-log-turn") ?? throw new InvalidOperationException("Activity was not sampled."))
        {
        }

        _ = Assert.Single(loggerFactory.Messages);
    }

    private static string CreateUniqueSourceName()
        => $"AlleyCat.Tests.AgenticMindActivityLogListener.{Guid.NewGuid():N}";

    private sealed class TestLoggerFactory : ILoggerFactory
    {
        private readonly TestLogger _logger = new();

        public IReadOnlyCollection<string> Messages => _logger.Messages;

        public void AddProvider(ILoggerProvider provider)
            => ArgumentNullException.ThrowIfNull(provider);

        public ILogger CreateLogger(string categoryName)
            => _logger;

        public void Dispose()
        {
        }
    }

    private sealed class TestLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _messages.Enqueue(formatter(state, exception));
    }
}
