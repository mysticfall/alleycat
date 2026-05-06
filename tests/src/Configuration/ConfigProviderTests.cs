using AlleyCat.Configuration;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Configuration;

/// <summary>
/// Unit coverage for merged configuration access.
/// </summary>
public sealed class ConfigProviderTests
{
    /// <summary>
    /// Override values must replace matching keys while preserving untouched base values.
    /// </summary>
    [Fact]
    public void FromConfigFiles_OverrideProvided_MergesPerSectionAndPerKey()
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> baseSections = new(StringComparer.Ordinal)
        {
            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "https://base.example/v1",
                ["Model"] = "whisper-1",
            },
            ["TTS"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Voice"] = "alloy",
            },
        };
        Dictionary<string, IReadOnlyDictionary<string, string>> overrideSections = new(StringComparer.Ordinal)
        {
            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "https://override.example/v1",
                ["ApiKey"] = "sk-override",
            },
            ["UI"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Theme"] = "night",
            },
        };

        var configProvider = ConfigProvider.FromSections(baseSections, overrideSections);

        Assert.Equal("https://override.example/v1", configProvider.GetValue("STT", "Host"));
        Assert.Equal("whisper-1", configProvider.GetValue("STT", "Model"));
        Assert.Equal("sk-override", configProvider.GetValue("STT", "ApiKey"));
        Assert.Equal("alloy", configProvider.GetValue("TTS", "Voice"));
        Assert.Equal("night", configProvider.GetValue("UI", "Theme"));
        Assert.True(configProvider.HasSection("UI"));
        Assert.True(configProvider.HasKey("STT", "ApiKey"));
    }

    /// <summary>
    /// Missing override files must fall back to the base configuration without error.
    /// </summary>
    [Fact]
    public void LoadMerged_OverrideFileMissing_ReturnsBaseConfiguration()
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> baseSections = new(StringComparer.Ordinal)
        {
            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "https://base.example/v1",
                ["Model"] = "whisper-1",
            },
        };

        var configProvider = ConfigProvider.FromSections(baseSections);

        Assert.Equal("https://base.example/v1", configProvider.GetValue("STT", "Host"));
        Assert.Equal("whisper-1", configProvider.GetValue("STT", "Model"));
        Assert.Null(configProvider.GetValue("STT", "ApiKey"));
        Assert.Equal("fallback", configProvider.GetValue("STT", "ApiKey", "fallback"));
    }

    /// <summary>
    /// A missing override file must silently fall back to the base configuration.
    /// </summary>
    [Fact]
    public void LoadMerged_OverrideLoadReturnsFileNotFound_FallsBackToBaseConfiguration()
    {
        const string baseConfigPath = "res://base.cfg";
        const string overrideConfigPath = "user://AlleyCat.cfg";

        var configProvider = ConfigProvider.LoadMerged(
            baseConfigPath,
            overrideConfigPath,
            configPath => configPath switch
            {
                baseConfigPath => CreateLoadedSections(
                    Error.Ok,
                    new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                    {
                        ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Host"] = "https://base.example/v1",
                            ["Model"] = "whisper-1",
                        },
                    }),
                overrideConfigPath => CreateLoadedSections(Error.FileNotFound),
                _ => throw new InvalidOperationException($"Unexpected config path '{configPath}'."),
            });

        Assert.Equal("https://base.example/v1", configProvider.GetValue("STT", "Host"));
        Assert.Equal("whisper-1", configProvider.GetValue("STT", "Model"));
        Assert.Null(configProvider.GetValue("STT", "ApiKey"));
    }

    /// <summary>
    /// An unreadable override file must surface an error instead of silently falling back.
    /// </summary>
    [Fact]
    public void LoadMerged_OverrideLoadReturnsFileCantOpen_Throws()
    {
        const string baseConfigPath = "res://base.cfg";
        const string overrideConfigPath = "user://AlleyCat.cfg";

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfigProvider.LoadMerged(
                baseConfigPath,
                overrideConfigPath,
                configPath => configPath switch
                {
                    baseConfigPath => CreateLoadedSections(
                        Error.Ok,
                        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                        {
                            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["Host"] = "https://base.example/v1",
                            },
                        }),
                    overrideConfigPath => CreateLoadedSections(Error.FileCantOpen),
                    _ => throw new InvalidOperationException($"Unexpected config path '{configPath}'."),
                }));

        Assert.Equal(
            $"Failed to load config override '{overrideConfigPath}': {Error.FileCantOpen}.",
            ex.Message);
    }

    /// <summary>
    /// Returned section dictionaries must be defensive copies of the merged data.
    /// </summary>
    [Fact]
    public void GetSection_ReturnsMergedSectionCopy()
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> baseSections = new(StringComparer.Ordinal)
        {
            ["TTS"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "https://base.example/v1",
            },
        };
        Dictionary<string, IReadOnlyDictionary<string, string>> overrideSections = new(StringComparer.Ordinal)
        {
            ["TTS"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Voice"] = "nova",
            },
        };

        var configProvider = ConfigProvider.FromSections(baseSections, overrideSections);

        Dictionary<string, string> section = configProvider.GetSection("TTS");
        section["Host"] = "mutated";

        Assert.Equal("https://base.example/v1", configProvider.GetValue("TTS", "Host"));
        Assert.Equal("nova", configProvider.GetValue("TTS", "Voice"));
    }

    private static ConfigProvider.LoadedConfigSections CreateLoadedSections(
        Error error,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? sections = null)
        => new(error, sections);
}
