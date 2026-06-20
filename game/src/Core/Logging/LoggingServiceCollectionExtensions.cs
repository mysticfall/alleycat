using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Dependency injection registration helpers for AlleyCat logging infrastructure.
/// </summary>
public static class LoggingServiceCollectionExtensions
{
    /// <summary>
    /// Registers Godot-backed logging providers and binds log levels from configuration.
    /// </summary>
    public static IServiceCollection AddAlleyCatLogging(
        this IServiceCollection services,
        IConfiguration configuration,
        ILogNotificationSink? notificationSink = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        notificationSink ??= new NullLogNotificationSink();

        _ = services.AddSingleton(notificationSink);
        _ = services.AddLogging(builder =>
        {
            _ = builder.AddConfiguration(configuration.GetSection("Logging"));
            _ = builder.AddProvider(new ConsoleLoggerProvider());
            _ = builder.AddProvider(new NotificationLoggerProvider(notificationSink));
        });

        return services;
    }
}
