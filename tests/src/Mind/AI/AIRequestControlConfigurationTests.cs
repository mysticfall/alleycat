using AlleyCat.Core.Configuration;
using AlleyCat.Mind.AI.Provider;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Verifies merged YAML precedence for the AI reasoning request controls through the real configuration builder
/// and binding.
/// </summary>
public sealed class AIRequestControlConfigurationTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates the temporary configuration directory used by each test.
    /// </summary>
    public AIRequestControlConfigurationTests()
    {
        _ = Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>
    /// Shipped-style YAML without active request controls leaves both settings unset, so requests defer to
    /// provider defaults.
    /// </summary>
    [Fact]
    public void Build_ShippedStyleDefaults_LeaveRequestControlsUnset()
    {
        string basePath = WriteYaml(
            "base.yaml",
            """
            AI:
                Host: "https://base.example/v1"
                Model: base-model
                Timeout: 30
            """);
        string overridePath = Path.Combine(_temporaryDirectory, "missing.yaml");

        OpenAIClientProvider.OpenAIClientProviderSettings settings = LoadMerged(basePath, overridePath);

        Assert.Null(settings.ReasoningEffortLevel);
        Assert.Null(settings.ReasoningVerbosity);
    }

    /// <summary>
    /// User YAML overrides matching request controls while unlisted controls keep their shipped values.
    /// </summary>
    [Fact]
    public void Build_UserOverridePresent_OverridesShippedRequestControlsAndRetainsUnlistedOnes()
    {
        string basePath = WriteYaml(
            "base.yaml",
            """
            AI:
                Host: "https://base.example/v1"
                Model: base-model
                ReasoningEffort: low
                ReasoningVerbosity: concise
            """);
        string overridePath = WriteYaml(
            "override.yaml",
            """
            AI:
                ReasoningEffort: xhigh
            """);

        OpenAIClientProvider.OpenAIClientProviderSettings settings = LoadMerged(basePath, overridePath);

        Assert.Equal(ReasoningEffort.ExtraHigh, settings.ReasoningEffortLevel);
        Assert.Equal("concise", settings.ReasoningVerbosity);
        Assert.Equal("base-model", settings.Model);
    }

    /// <summary>
    /// A blank user override clears a shipped request control, restoring the unset provider default.
    /// </summary>
    [Fact]
    public void Build_BlankUserOverride_ClearsShippedRequestControl()
    {
        string basePath = WriteYaml(
            "base.yaml",
            """
            AI:
                Host: "https://base.example/v1"
                ReasoningEffort: medium
                ReasoningVerbosity: detailed
            """);
        string overridePath = WriteYaml(
            "override.yaml",
            """
            AI:
                ReasoningEffort: ""
                ReasoningVerbosity: "   "
            """);

        OpenAIClientProvider.OpenAIClientProviderSettings settings = LoadMerged(basePath, overridePath);

        Assert.Null(settings.ReasoningEffortLevel);
        Assert.Null(settings.ReasoningVerbosity);
    }

    /// <summary>
    /// An invalid merged reasoning effort fails closed with the production section and value in the diagnostic.
    /// </summary>
    [Fact]
    public void Build_InvalidMergedReasoningEffort_FailsClosed()
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
                ReasoningEffort: turbo
            """);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => LoadMerged(basePath, overridePath));

        Assert.Equal(
            "Config key 'AI/ReasoningEffort' must be one of 'none', 'low', 'medium', 'high', or 'xhigh'. Got 'turbo'.",
            error.Message);
    }

    /// <summary>
    /// An invalid merged reasoning-summary verbosity fails closed, naming the key and permitted summary values.
    /// </summary>
    [Theory]
    [InlineData("loquacious")]
    [InlineData("medium")]
    [InlineData("high")]
    public void Build_InvalidMergedReasoningVerbosity_FailsClosed(string configured)
    {
        string basePath = WriteYaml(
            "base.yaml",
            """
            AI:
                Host: "https://base.example/v1"
            """);
        string overridePath = WriteYaml(
            "override.yaml",
            $$"""
            AI:
                ReasoningVerbosity: {{configured}}
            """);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => LoadMerged(basePath, overridePath));

        Assert.Equal(
            $"Config key 'AI/ReasoningVerbosity' must be one of 'auto', 'concise', or 'detailed'. Got '{configured}'.",
            error.Message);
    }

    /// <summary>
    /// A custom configuration section carries its own name into invalid-value diagnostics.
    /// </summary>
    [Fact]
    public void Build_InvalidCustomSectionRequestControl_NamesCustomSection()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CustomAI:ReasoningVerbosity"] = "verbose",
            })
            .Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            OpenAIClientProvider.OpenAIClientProviderSettings.Load(
                configuration,
                "custom-config",
                "CustomAI"));

        Assert.Equal(
            "Config key 'CustomAI/ReasoningVerbosity' must be one of 'auto', 'concise', or 'detailed'. Got 'verbose'.",
            error.Message);
    }

    /// <summary>
    /// Custom configuration paths apply their own request controls without consulting the merged game
    /// configuration.
    /// </summary>
    [Fact]
    public void Load_CustomConfigPath_AppliesRequestControlsFromThatFile()
    {
        string customPath = WriteYaml(
            "custom.yaml",
            """
            AI:
                Host: "https://custom.example/v1"
                Model: custom-model
                ReasoningEffort: high
                ReasoningVerbosity: auto
            """);
        string defaultPath = WriteYaml(
            "default.yaml",
            """
            AI:
                ReasoningEffort: none
            """);

        var settings = OpenAIClientProvider.OpenAIClientProviderSettings.Load(
            customPath,
            () => GameConfiguration.BuildFile(new FixedPathResolver(), defaultPath),
            path => GameConfiguration.BuildFile(new FixedPathResolver(), path));

        Assert.Equal(ReasoningEffort.High, settings.ReasoningEffortLevel);
        Assert.Equal("auto", settings.ReasoningVerbosity);
        Assert.Equal("custom-model", settings.Model);
    }

    /// <summary>
    /// Removes test configuration files.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private static OpenAIClientProvider.OpenAIClientProviderSettings LoadMerged(string basePath, string overridePath)
    {
        IConfiguration configuration = GameConfiguration.Build(
            new FixedPathResolver(),
            basePath,
            overridePath);
        return OpenAIClientProvider.OpenAIClientProviderSettings.Load(configuration, "unit-config");
    }

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
