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
    public const string DefaultBaseConfigPath = "res://AlleyCat.yaml";

    /// <summary>
    /// Default per-user override configuration path.
    /// </summary>
    public const string DefaultOverrideConfigPath = "user://AlleyCat.yaml";

    /// <summary>
    /// Builds configuration using the NetEscapades YAML configuration provider.
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
            .AddYamlFile(physicalBasePath, optional: false, reloadOnChange: false)
            .AddYamlFile(physicalOverridePath, optional: true, reloadOnChange: false)
            .Build();
    }

    /// <summary>
    /// Builds configuration from one explicit YAML file without applying default/user merging.
    /// </summary>
    public static IConfigurationRoot BuildFile(IConfigurationPathResolver pathResolver, string configPath)
    {
        ArgumentNullException.ThrowIfNull(pathResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        return new ConfigurationBuilder()
            .AddYamlFile(pathResolver.ToPhysicalPath(configPath), optional: false, reloadOnChange: false)
            .Build();
    }
}
