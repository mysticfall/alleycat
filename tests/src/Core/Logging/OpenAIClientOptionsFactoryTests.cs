using AlleyCat.Core.Logging;
using Microsoft.Extensions.Logging;
using OpenAI;
using Xunit;

namespace AlleyCat.Tests.Core.Logging;

/// <summary>
/// Unit coverage for OpenAI SDK option wiring.
/// </summary>
public sealed class OpenAIClientOptionsFactoryTests
{
    /// <summary>
    /// OpenAI SDK clients should receive the active AlleyCat logger factory through ClientLoggingOptions.
    /// </summary>
    [Fact]
    public void Create_WithLoggerFactory_AssignsClientLoggingOptions()
    {
        using ILoggerFactory loggerFactory = new TestLoggerFactory();

        OpenAIClientOptions options = OpenAIClientOptionsFactory.Create(
            new Uri("https://api.openai.com/v1"),
            timeoutSeconds: null,
            loggerFactory);

        Assert.NotNull(options.ClientLoggingOptions);
        Assert.Same(loggerFactory, options.ClientLoggingOptions.LoggerFactory);
    }

    /// <summary>
    /// Existing endpoint and network-timeout behaviour must be preserved when SDK logging is wired.
    /// </summary>
    [Fact]
    public void Create_WithTimeout_PreservesEndpointAndNetworkTimeout()
    {
        using ILoggerFactory loggerFactory = new TestLoggerFactory();
        Uri endpoint = new("https://compatible.example/v1");

        OpenAIClientOptions options = OpenAIClientOptionsFactory.Create(endpoint, timeoutSeconds: 42, loggerFactory);

        Assert.Equal(endpoint, options.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(42), options.NetworkTimeout);
        Assert.NotNull(options.ClientLoggingOptions);
        Assert.Same(loggerFactory, options.ClientLoggingOptions.LoggerFactory);
    }

    /// <summary>
    /// Logging infrastructure is required so SDK diagnostics are not silently suppressed.
    /// </summary>
    [Fact]
    public void Create_MissingLoggerFactory_Throws()
    {
        _ = Assert.Throws<ArgumentNullException>(
            () => OpenAIClientOptionsFactory.Create(
                new Uri("https://api.openai.com/v1"),
                timeoutSeconds: null,
                loggerFactory: null!));
    }

    private sealed class TestLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
            => ArgumentNullException.ThrowIfNull(provider);

        public ILogger CreateLogger(string categoryName)
            => new TestLogger();

        public void Dispose()
        {
        }
    }

    private sealed class TestLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => ArgumentNullException.ThrowIfNull(formatter);
    }
}
