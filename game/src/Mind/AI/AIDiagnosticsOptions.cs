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
    } = true;

    /// <summary>
    /// Enables trace-level logging of model reasoning content in tool-only responses. Enabled by default.
    /// </summary>
    public bool EnableReasoningLogging
    {
        get;
        init;
    } = true;

    /// <summary>
    /// Enables per-cycle Markdown session transcript files under <c>user://logs/mind/</c> (AI-011). Disabled by
    /// default and independent of <see cref="EnableRequestResponseLogging" />: enabling either never enables the
    /// other.
    /// </summary>
    public bool EnableSessionTranscriptLogging
    {
        get;
        init;
    }
}

/// <summary>
/// Resolved AI diagnostics settings loaded from AlleyCat configuration.
/// </summary>
internal sealed record AIDiagnosticsSettings(
    bool EnableRequestResponseLogging,
    bool EnableReasoningLogging = true,
    bool EnableSessionTranscriptLogging = false)
{
    private const string ConfigSection = "Diagnostics:AI";
    private const string DefaultConfigPath = GameConfiguration.DefaultBaseConfigPath;

    private static volatile bool _sessionTranscriptsDisabledForTesting;

    /// <summary>
    /// Loads AI diagnostics settings from the default AlleyCat configuration.
    /// </summary>
    public static AIDiagnosticsSettings Load()
        => Load(ResolveDefaultConfiguration(), DefaultConfigPath);

    /// <summary>
    /// Loads AI diagnostics settings when game configuration is available, otherwise keeps sensitive logging disabled.
    /// </summary>
    /// <remarks>
    /// When the integration-test runtime has disabled session transcripts process-wide through
    /// <see cref="DisableSessionTranscriptsForTesting" />, the resolved settings are clamped so every mind
    /// inheriting this default loader records nothing; tests that need transcripts inject an explicit enabling
    /// loader through <see cref="AgenticMind.SetDiagnosticsSettingsLoaderForTesting" />, which bypasses this path
    /// entirely.
    /// </remarks>
    public static AIDiagnosticsSettings LoadOrDefault()
    {
        AIDiagnosticsSettings settings;
        try
        {
            settings = Load();
        }
        catch (InvalidOperationException)
        {
            // Sensitive diagnostics are optional development/debug plumbing. Isolated tests and non-Game runtime
            // contexts may not have the Game service provider. Request/response payload logging fails closed without
            // configuration, while reasoning logging remains default-enabled because it only fires at trace level.
            // Session transcript logging also fails closed: no transcript files are written without configuration
            // (AI-011 TR-1).
            settings = new AIDiagnosticsSettings(
                EnableRequestResponseLogging: false,
                EnableReasoningLogging: true,
                EnableSessionTranscriptLogging: false);
        }

        return _sessionTranscriptsDisabledForTesting && settings.EnableSessionTranscriptLogging
            ? settings with
            {
                EnableSessionTranscriptLogging = false
            }
            : settings;
    }

    /// <summary>
    /// Internal test-framework seam (AI-011 TR-5/TR-12): disables session transcript logging process-wide for the
    /// integration-test runtime, so every <see cref="AgenticMind" /> inheriting the default loader records nothing
    /// regardless of any developer's user-configuration override. Installed once at the test-session bootstrap and
    /// never invoked by production. Not public API and not game configuration.
    /// </summary>
    internal static void DisableSessionTranscriptsForTesting() => _sessionTranscriptsDisabledForTesting = true;

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

        return new AIDiagnosticsSettings(
            options.EnableRequestResponseLogging,
            options.EnableReasoningLogging,
            options.EnableSessionTranscriptLogging);
    }

    private static IConfiguration ResolveDefaultConfiguration()
        => Game.Instance.GetRequiredService<IConfiguration>();
}
