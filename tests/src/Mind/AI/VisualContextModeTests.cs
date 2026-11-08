using AlleyCat.Core.Configuration;
using AlleyCat.Mind.AI;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Verifies the AI-013 visual-context mode binding (TR-1): case-insensitive parsing after trimming, the
/// <c>tools</c> default for unset or blank values, merged YAML precedence through the real configuration builder,
/// and the local rejection diagnostic naming the key and permitted values.
/// </summary>
public sealed class VisualContextModeTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates the temporary configuration directory used by each test.
    /// </summary>
    public VisualContextModeTests()
    {
        _ = Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>
    /// Deletes the temporary configuration directory used by each test.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written; the directory never existed.
        }
    }

    /// <summary>Every accepted value binds case-insensitively after trimming, and blank means the default.</summary>
    [Theory]
    [InlineData(null, VisualContextMode.Tools)]
    [InlineData("", VisualContextMode.Tools)]
    [InlineData("   ", VisualContextMode.Tools)]
    [InlineData("tools", VisualContextMode.Tools)]
    [InlineData(" TOOLS ", VisualContextMode.Tools)]
    [InlineData("Auto", VisualContextMode.Auto)]
    [InlineData(" auto\t", VisualContextMode.Auto)]
    [InlineData("OFF", VisualContextMode.Off)]
    [InlineData(" off ", VisualContextMode.Off)]
    public void Parse_AcceptsValuesCaseInsensitivelyAfterTrimming(string? configured, VisualContextMode expected)
        => Assert.Equal(expected, VisualContextModeSettings.Parse(configured));

    /// <summary>An unrecognised nonblank value is rejected locally, naming the key and the permitted values.</summary>
    [Theory]
    [InlineData("sometimes")]
    [InlineData("always")]
    [InlineData("Tools, Auto")]
    public void Parse_UnrecognisedNonblankValue_FailsClosedNamingKeyAndPermittedValues(string configured)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => VisualContextModeSettings.Parse(configured));

        Assert.Equal(
            $"Config key 'AI/VisualContextMode' must be one of 'tools', 'auto', or 'off'. Got '{configured}'.",
            error.Message);
    }

    /// <summary>The mode binds from an in-memory configuration's <c>AI</c> section.</summary>
    [Fact]
    public void Load_BindsFromTheAiConfigurationSection()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AI:VisualContextMode"] = "auto",
            }).Build();

        Assert.Equal(VisualContextMode.Auto, VisualContextModeSettings.Load(configuration));
    }

    /// <summary>An absent <c>AI</c> section applies the documented default.</summary>
    [Fact]
    public void Load_WithoutTheAiSection_AppliesTheToolsDefault()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Diagnostics:AI:EnableSessionTranscriptLogging"] = "true",
            }).Build();

        Assert.Equal(VisualContextMode.Tools, VisualContextModeSettings.Load(configuration));
    }

    /// <summary>
    /// A user override takes CORE-006 merge precedence over the shipped base value, and blank clears back to the
    /// default.
    /// </summary>
    [Fact]
    public void Build_UserOverrideOverridesShippedModeAndBlankClearsToDefault()
    {
        string basePath = WriteYaml(
            "base.yaml",
            """
            AI:
                Host: "https://base.example/v1"
                Model: base-model
                VisualContextMode: off
            """);
        string overridePath = WriteYaml(
            "override.yaml",
            """
            AI:
                VisualContextMode: AUTO
            """);

        Assert.Equal(VisualContextMode.Auto, LoadMerged(basePath, overridePath));

        string blankOverridePath = WriteYaml(
            "blank-override.yaml",
            """
            AI:
                VisualContextMode: "  "
            """);

        Assert.Equal(VisualContextMode.Tools, LoadMerged(basePath, blankOverridePath));
    }

    /// <summary>An invalid merged value fails closed locally with the production section and value in the diagnostic.</summary>
    [Fact]
    public void Build_InvalidMergedMode_FailsClosed()
    {
        string basePath = WriteYaml(
            "base.yaml",
            """
            AI:
                Host: "https://base.example/v1"
            """);
        string overridePath = WriteYaml(
            "override.yaml",
            """
            AI:
                VisualContextMode: panoramic
            """);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => LoadMerged(basePath, overridePath));

        Assert.Equal(
            "Config key 'AI/VisualContextMode' must be one of 'tools', 'auto', or 'off'. Got 'panoramic'.",
            error.Message);
    }

    private static VisualContextMode LoadMerged(string basePath, string overridePath)
        => VisualContextModeSettings.Load(
            GameConfiguration.Build(new FixedPathResolver(), basePath, overridePath),
            "unit-config");

    private string WriteYaml(string fileName, string content)
    {
        string path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FixedPathResolver : IConfigurationPathResolver
    {
        public string ToPhysicalPath(string path) => path;
    }
}
