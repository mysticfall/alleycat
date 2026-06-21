using System.ClientModel.Primitives;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Creates OpenAI SDK client options wired into the active AlleyCat logging infrastructure.
/// </summary>
internal static class OpenAIClientOptionsFactory
{
    /// <summary>
    /// Creates OpenAI client options with endpoint, timeout, and SDK logging configured.
    /// </summary>
    /// <param name="endpoint">Endpoint URI used by the OpenAI-compatible backend.</param>
    /// <param name="timeoutSeconds">Optional network timeout in seconds.</param>
    /// <returns>Configured OpenAI SDK client options.</returns>
    public static OpenAIClientOptions Create(Uri endpoint, int? timeoutSeconds)
        => Create(endpoint, timeoutSeconds, GameLoggerResolver.ResolveFactoryRequired());

    internal static OpenAIClientOptions Create(Uri endpoint, int? timeoutSeconds, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        OpenAIClientOptions options = new()
        {
            Endpoint = endpoint,
            ClientLoggingOptions = new ClientLoggingOptions
            {
                LoggerFactory = loggerFactory,
            },
        };

        if (timeoutSeconds is int timeout)
        {
            options.NetworkTimeout = TimeSpan.FromSeconds(timeout);
        }

        return options;
    }
}
