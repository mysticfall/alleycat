using AlleyCat.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Core.Logging;

/// <summary>
/// Unit coverage for the logger-to-notification bridge.
/// </summary>
public sealed class NotificationLoggerProviderTests
{
    /// <summary>
    /// Error logs reach the UI notification sink regardless of category.
    /// </summary>
    [Fact]
    public void LogError_WithTranscriberCategory_PostsNotification()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Speech.Transcription.Transcriber");

        logger.LogError(new InvalidOperationException("backend unavailable"), "Voice transcription failed.");

        string message = Assert.Single(sink.Messages);
        Assert.Contains("AlleyCat.Speech.Transcription.Transcriber", message, StringComparison.Ordinal);
        Assert.Contains("Voice transcription failed.", message, StringComparison.Ordinal);
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    /// <summary>
    /// Non-transcriber error logs still reach the UI notification sink.
    /// </summary>
    [Fact]
    public void LogError_WithOrdinaryCategory_PostsNotification()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Body.Voice.AIVoice");

        logger.LogError("Ordinary runtime failure.");

        string message = Assert.Single(sink.Messages);
        Assert.Contains("AlleyCat.Body.Voice.AIVoice", message, StringComparison.Ordinal);
        Assert.Contains("Ordinary runtime failure.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Logs below the configured minimum level do not reach the UI notification sink.
    /// </summary>
    [Fact]
    public void LogWarning_WhenMinimumLevelIsError_DoesNotPostNotification()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Speech.Transcription.OpenAITranscriber");
        logger.LogWarning("Non-fatal transcription diagnostic.");

        Assert.Empty(sink.Messages);
        Assert.False(logger.IsEnabled(LogLevel.Warning));
    }

    private sealed class CapturingNotificationSink : ILogNotificationSink
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public bool TryPostNotification(string? message, double timeoutSeconds = 3.0)
        {
            if (message is not null)
            {
                _messages.Add(message);
            }

            return true;
        }
    }

}
