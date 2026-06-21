using AlleyCat.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Options for AI diagnostics that may include sensitive request and response payloads.
/// </summary>
public sealed class AIDiagnosticsOptions
{
    /// <summary>
    /// Enables development/debug logging of sensitive AI prompts, model responses, tool payloads, and messages.
    /// </summary>
    public bool EnableRequestResponseLogging
    {
        get;
        init;
    }
}

/// <summary>
/// Resolved AI diagnostics settings loaded from AlleyCat configuration.
/// </summary>
internal sealed record AIDiagnosticsSettings(bool EnableRequestResponseLogging)
{
    private const string ConfigSection = "Diagnostics:AI";
    private const string DefaultConfigPath = GameConfiguration.DefaultBaseConfigPath;

    /// <summary>
    /// Loads AI diagnostics settings from the default AlleyCat configuration.
    /// </summary>
    public static AIDiagnosticsSettings Load()
        => Load(ResolveDefaultConfiguration(), DefaultConfigPath);

    /// <summary>
    /// Loads AI diagnostics settings when game configuration is available, otherwise keeps sensitive logging disabled.
    /// </summary>
    public static AIDiagnosticsSettings LoadOrDefault()
    {
        try
        {
            return Load();
        }
        catch (InvalidOperationException)
        {
            // Sensitive diagnostics are optional development/debug plumbing. Isolated tests and non-Game runtime
            // contexts may not have the Game service provider, so fail closed without enabling AI payload logging.
            return new AIDiagnosticsSettings(EnableRequestResponseLogging: false);
        }
    }

    /// <summary>
    /// Loads AI diagnostics settings from an <see cref="IConfiguration" /> section.
    /// </summary>
    public static AIDiagnosticsSettings Load(IConfiguration configuration, string configPathDescription = DefaultConfigPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPathDescription);

        AIDiagnosticsOptions options = new();
        configuration.GetSection(ConfigSection).Bind(options);
        return Load(options);
    }

    internal static AIDiagnosticsSettings Load(AIDiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AIDiagnosticsSettings(options.EnableRequestResponseLogging);
    }

    private static IConfiguration ResolveDefaultConfiguration()
        => Game.Instance.GetRequiredService<IConfiguration>();
}
