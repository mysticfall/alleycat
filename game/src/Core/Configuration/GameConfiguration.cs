using Microsoft.Extensions.Configuration;

namespace AlleyCat.Core.Configuration;

/// <summary>
/// Builds the game configuration root from shipped defaults and optional user overrides.
/// </summary>
public static class GameConfiguration
{
    /// <summary>
    /// Default project configuration path bundled with the game.
    /// </summary>
    public const string DefaultBaseConfigPath = "res://AlleyCat.json";

    /// <summary>
    /// Default per-user override configuration path.
    /// </summary>
    public const string DefaultOverrideConfigPath = "user://AlleyCat.json";

    /// <summary>
    /// Builds configuration using the standard .NET JSON configuration provider.
    /// </summary>
    public static IConfigurationRoot Build(
        IConfigurationPathResolver pathResolver,
        string baseConfigPath = DefaultBaseConfigPath,
        string overrideConfigPath = DefaultOverrideConfigPath)
    {
        ArgumentNullException.ThrowIfNull(pathResolver);

        string physicalBasePath = pathResolver.ToPhysicalPath(baseConfigPath);
        string physicalOverridePath = pathResolver.ToPhysicalPath(overrideConfigPath);

        return new ConfigurationBuilder()
            .AddJsonFile(physicalBasePath, optional: false, reloadOnChange: false)
            .AddJsonFile(physicalOverridePath, optional: true, reloadOnChange: false)
            .Build();
    }

    /// <summary>
    /// Builds configuration from one explicit JSON file without applying default/user merging.
    /// </summary>
    public static IConfigurationRoot BuildFile(IConfigurationPathResolver pathResolver, string configPath)
    {
        ArgumentNullException.ThrowIfNull(pathResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        return new ConfigurationBuilder()
            .AddJsonFile(pathResolver.ToPhysicalPath(configPath), optional: false, reloadOnChange: false)
            .Build();
    }
}
