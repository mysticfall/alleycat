using Godot;

namespace AlleyCat.Core;

/// <summary>
/// Provides string-based configuration access with optional user override merging.
/// </summary>
public sealed class ConfigProvider
{
    /// <summary>
    /// Default project configuration path bundled with the game.
    /// </summary>
    public const string DefaultBaseConfigPath = "res://AlleyCat.cfg";

    /// <summary>
    /// Default per-user override configuration path.
    /// </summary>
    public const string DefaultOverrideConfigPath = "user://AlleyCat.cfg";

    internal readonly record struct LoadedConfigSections(
        Error Error,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Sections = null);

    private readonly Dictionary<string, Dictionary<string, string>> _sections;

    private ConfigProvider(Dictionary<string, Dictionary<string, string>> sections)
    {
        _sections = sections;
    }

    /// <summary>
    /// Loads configuration from a single file without applying overrides.
    /// </summary>
    public static ConfigProvider Load(string configPath)
        => FromSections(LoadRequiredSections(configPath, LoadConfigFile));

    /// <summary>
    /// Loads the project defaults and overlays user overrides when present.
    /// </summary>
    public static ConfigProvider LoadMerged(
        string baseConfigPath = DefaultBaseConfigPath,
        string overrideConfigPath = DefaultOverrideConfigPath)
        => LoadMerged(baseConfigPath, overrideConfigPath, LoadConfigFile);

    /// <summary>
    /// Gets a configuration value from the merged result, or the provided default when missing.
    /// </summary>
    public string? GetValue(string section, string key, string? defaultValue = null)
        => HasKey(section, key) ? _sections[section][key] : defaultValue;

    /// <summary>
    /// Gets a copy of all keys in a section.
    /// </summary>
    public Dictionary<string, string> GetSection(string section)
        => !_sections.TryGetValue(section, out Dictionary<string, string>? values)
            ? []
            : new Dictionary<string, string>(values, StringComparer.Ordinal);

    /// <summary>
    /// Checks whether a section exists.
    /// </summary>
    public bool HasSection(string section)
        => _sections.ContainsKey(section);

    /// <summary>
    /// Checks whether a key exists within a section.
    /// </summary>
    public bool HasKey(string section, string key)
        => _sections.TryGetValue(section, out Dictionary<string, string>? values)
            && values.ContainsKey(key);

    internal static ConfigProvider FromConfigFile(ConfigFile configFile)
        => FromConfigFiles(configFile);

    internal static ConfigProvider FromConfigFiles(ConfigFile baseConfigFile, ConfigFile? overrideConfigFile = null)
    {
        Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.Ordinal);
        MergeInto(sections, baseConfigFile);

        if (overrideConfigFile is not null)
        {
            MergeInto(sections, overrideConfigFile);
        }

        return new ConfigProvider(sections);
    }

    internal static ConfigProvider LoadMerged(
        string baseConfigPath,
        string overrideConfigPath,
        Func<string, LoadedConfigSections> configLoader)
        => FromSections(
            LoadRequiredSections(baseConfigPath, configLoader),
            LoadOptionalSections(overrideConfigPath, configLoader));

    internal static ConfigProvider FromSections(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> baseSections,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? overrideSections = null)
    {
        Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.Ordinal);
        MergeInto(sections, baseSections);

        if (overrideSections is not null)
        {
            MergeInto(sections, overrideSections);
        }

        return new ConfigProvider(sections);
    }

    private static void MergeInto(
        Dictionary<string, Dictionary<string, string>> sections,
        ConfigFile configFile)
    {
        foreach (string section in configFile.GetSections())
        {
            if (!sections.TryGetValue(section, out Dictionary<string, string>? sectionValues))
            {
                sectionValues = new Dictionary<string, string>(StringComparer.Ordinal);
                sections.Add(section, sectionValues);
            }

            foreach (string key in configFile.GetSectionKeys(section))
            {
                sectionValues[key] = configFile.GetValue(section, key).AsString();
            }
        }
    }

    private static void MergeInto(
        Dictionary<string, Dictionary<string, string>> sections,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sourceSections)
    {
        foreach ((string section, IReadOnlyDictionary<string, string> sourceValues) in sourceSections)
        {
            if (!sections.TryGetValue(section, out Dictionary<string, string>? sectionValues))
            {
                sectionValues = new Dictionary<string, string>(StringComparer.Ordinal);
                sections.Add(section, sectionValues);
            }

            foreach ((string key, string value) in sourceValues)
            {
                sectionValues[key] = value;
            }
        }
    }

    private static LoadedConfigSections LoadConfigFile(string configPath)
    {
        ConfigFile configFile = new();
        Error error = configFile.Load(configPath);
        return new LoadedConfigSections(
            error,
            error == Error.Ok ? ExtractSections(configFile) : null);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadRequiredSections(
        string configPath,
        Func<string, LoadedConfigSections> configLoader)
    {
        LoadedConfigSections loadedConfig = configLoader(configPath);
        _ = loadedConfig.Error != Error.Ok
            ? throw new InvalidOperationException(
                $"Failed to load config '{configPath}': {loadedConfig.Error}.")
            : 0;

        return loadedConfig.Sections!;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? LoadOptionalSections(
        string configPath,
        Func<string, LoadedConfigSections> configLoader)
    {
        LoadedConfigSections loadedConfig = configLoader(configPath);

#pragma warning disable IDE0072
        return loadedConfig.Error switch
        {
            Error.Ok => loadedConfig.Sections,
            Error.FileNotFound => null,
            _ => throw new InvalidOperationException(
                $"Failed to load config override '{configPath}': {loadedConfig.Error}."),
        };
#pragma warning restore IDE0072
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ExtractSections(ConfigFile configFile)
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> sections = new(StringComparer.Ordinal);

        foreach (string section in configFile.GetSections())
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (string key in configFile.GetSectionKeys(section))
            {
                values[key] = configFile.GetValue(section, key).AsString();
            }

            sections.Add(section, values);
        }

        return sections;
    }
}
