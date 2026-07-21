using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Applies feature-gated request and response diagnostics to a turn-scoped chat client.
/// </summary>
internal static class AIChatClientDiagnostics
{
    /// <summary>
    /// Decorates a fresh chat client when sensitive request and response diagnostics are enabled.
    /// </summary>
    public static IChatClient Decorate(
        IChatClient chatClient,
        AIDiagnosticsSettings settings,
        Func<ILoggerFactory> loggerFactoryResolver)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loggerFactoryResolver);

        if (!settings.EnableRequestResponseLogging)
        {
            return chatClient;
        }

        ILoggerFactory loggerFactory = loggerFactoryResolver()
            ?? throw new InvalidOperationException(
                "AI request and response diagnostics require an active logger factory.");

        return new ChatClientBuilder(chatClient)
            .UseLogging(loggerFactory)
            .Build();
    }
}
