using AlleyCat.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Delivery mode of AI-013 visual context for one AgenticMind session.
/// </summary>
public enum VisualContextMode
{
    /// <summary>
    /// The model calls the two zero-argument visual tools itself (the default).
    /// </summary>
    Tools,

    /// <summary>
    /// Both images are captured and attached to every logical request; no visual tools exist.
    /// </summary>
    Auto,

    /// <summary>
    /// No visual tools, no captures, and no image content of any kind.
    /// </summary>
    Off,
}

/// <summary>
/// Resolves the AI-013 visual-context mode from the <c>AI</c> configuration section (AI-013 TR-1), following the
/// reasoning-effort binding pattern: values are trimmed and parsed case-insensitively, unset or blank means
/// <see cref="VisualContextMode.Tools" />, and an unrecognised nonblank value is rejected locally with a diagnostic
/// naming the configuration key and the permitted values.
/// </summary>
/// <remarks>
/// Unlike the reasoning settings, the mode is read once at AgenticMind session start (through its injectable
/// loader) and stays fixed for that session; it never reaches the provider transport.
/// </remarks>
internal static class VisualContextModeSettings
{
    internal const string ConfigSection = "AI";

    internal const string ConfigKey = "VisualContextMode";

    private const string DefaultConfigPath = GameConfiguration.DefaultBaseConfigPath;

    /// <summary>
    /// Loads the visual-context mode from the default AlleyCat configuration, applying the default
    /// <see cref="VisualContextMode.Tools" /> when game configuration is unavailable.
    /// </summary>
    /// <remarks>
    /// Only configuration unavailability falls back to the default: an unrecognised nonblank value is a genuine
    /// misconfiguration and stays a loud local rejection (AI-013 TR-1), never silently replaced by the default.
    /// </remarks>
    public static VisualContextMode LoadOrDefault()
    {
        IConfiguration configuration;
        try
        {
            configuration = ResolveDefaultConfiguration();
        }
        catch (InvalidOperationException)
        {
            // Visual context is optional gameplay plumbing: isolated tests and non-Game runtime contexts may not
            // have the Game service provider, and the documented default applies without one (AI-013 TR-1).
            return VisualContextMode.Tools;
        }

        return Load(configuration);
    }

    /// <summary>
    /// Loads the visual-context mode from an <see cref="IConfiguration" />'s <c>AI</c> section.
    /// </summary>
    public static VisualContextMode Load(IConfiguration configuration, string configPathDescription = DefaultConfigPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPathDescription);

        Provider.AIOptions options = new();
        configuration.GetSection(ConfigSection).Bind(options);
        return Parse(options.VisualContextMode);
    }

    /// <summary>
    /// Parses the configured mode case-insensitively after trimming, mapping unset or blank onto the default
    /// <see cref="VisualContextMode.Tools" /> and rejecting unrecognised values locally (AI-013 TR-1).
    /// </summary>
    internal static VisualContextMode Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" => VisualContextMode.Tools,
            "tools" => VisualContextMode.Tools,
            "auto" => VisualContextMode.Auto,
            "off" => VisualContextMode.Off,
            _ => throw new InvalidOperationException(
                $"Config key '{ConfigSection}/{ConfigKey}' must be one of 'tools', 'auto', or 'off'. "
                + $"Got '{value}'."),
        };

    private static IConfiguration ResolveDefaultConfiguration()
        => Game.Instance.GetRequiredService<IConfiguration>();
}
