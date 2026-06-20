using AlleyCat.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Core.Configuration;

/// <summary>
/// Dependency injection registration helpers for AlleyCat configuration infrastructure.
/// </summary>
public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the merged configuration root and logging infrastructure.
    /// </summary>
    public static IServiceCollection AddGameConfiguration(
        this IServiceCollection services,
        IConfigurationPathResolver? pathResolver = null,
        ILogNotificationSink? notificationSink = null,
        string baseConfigPath = GameConfiguration.DefaultBaseConfigPath,
        string overrideConfigPath = GameConfiguration.DefaultOverrideConfigPath)
    {
        ArgumentNullException.ThrowIfNull(services);

        pathResolver ??= new GodotPathResolver();
        IConfigurationRoot configuration = GameConfiguration.Build(pathResolver, baseConfigPath, overrideConfigPath);

        _ = services.AddSingleton(pathResolver);
        _ = services.AddSingleton<IConfiguration>(configuration);
        _ = services.AddAlleyCatLogging(configuration, notificationSink);

        return services;
    }
}
