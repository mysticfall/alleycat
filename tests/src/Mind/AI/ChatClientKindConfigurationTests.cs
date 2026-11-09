using AlleyCat.Core.Configuration;
using AlleyCat.Mind.AI.Provider;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Verifies the <c>AI:ChatClientKind</c> binding (AI-002 TR-35–TR-36): trimmed case-insensitive parsing of both
/// values, unset or blank meaning no override, config-over-resource precedence with the provider property itself
/// defaulting to Responses, merged YAML precedence through the real configuration builder, the local rejection
/// diagnostic naming the key and permitted values, and the effective-kind resolution that selects the chat client
/// adapter and the AI-013 visual-context transport path.
/// </summary>
public sealed class ChatClientKindConfigurationTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates the temporary configuration directory used by each test.
    /// </summary>
    public ChatClientKindConfigurationTests()
    {
        _ = Directory.CreateDirectory(_temporaryDirectory);
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

    /// <summary>Both values bind case-insensitively after trimming; unset or blank means no override.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("chatcompletions", (int)OpenAIChatClientKind.ChatCompletions)]
    [InlineData(" ChatCompletions ", (int)OpenAIChatClientKind.ChatCompletions)]
    [InlineData("CHATCOMPLETIONS", (int)OpenAIChatClientKind.ChatCompletions)]
    [InlineData("responses", (int)OpenAIChatClientKind.Responses)]
    [InlineData(" Responses\t", (int)OpenAIChatClientKind.Responses)]
    [InlineData("RESPONSES", (int)OpenAIChatClientKind.Responses)]
    public void Load_ParsesValuesCaseInsensitivelyAfterTrimming(string? configured, int? expected)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AI:Host"] = "https://unit.test/v1",
                ["AI:ChatClientKind"] = configured,
            }).Build();

        var settings = OpenAIClientProvider.OpenAIClientProviderSettings.Load(
            configuration,
            "unit-config");

        Assert.Equal((OpenAIChatClientKind?)expected, settings.ChatClientKind);
    }

    /// <summary>
    /// A user override takes CORE-006 merge precedence over the shipped value, and a blank override clears back
    /// to no override.
    /// </summary>
    [Fact]
    public void Build_UserOverrideOverridesShippedKindAndBlankClearsToNoOverride()
    {
        string basePath = WriteYaml(
            "base.yaml",
            """
            AI:
                Host: "https://base.example/v1"
                Model: base-model
                ChatClientKind: responses
            """);
        string overridePath = WriteYaml(
            "override.yaml",
            """
            AI:
                ChatClientKind: ChatCompletions
            """);

        Assert.Equal(
            OpenAIChatClientKind.ChatCompletions,
            LoadMerged(basePath, overridePath).ChatClientKind);
        string blankOverridePath = WriteYaml(
            "blank-override.yaml",
            """
            AI:
                ChatClientKind: "  "
            """);

        Assert.Null(LoadMerged(basePath, blankOverridePath).ChatClientKind);
    }

    /// <summary>An unrecognised nonblank value is rejected locally, naming the key and the permitted values.</summary>
    [Theory]
    [InlineData("grpc")]
    [InlineData("chat_completion")]
    [InlineData("Response")]
    public void Build_InvalidMergedKind_FailsClosedNamingKeyAndPermittedValues(string configured)
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
                ChatClientKind: {{configured}}
            """);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => LoadMerged(basePath, overridePath));

        Assert.Equal(
            $"Config key 'AI/ChatClientKind' must be one of 'chatcompletions' or 'responses'. Got '{configured}'.",
            error.Message);
    }

    /// <summary>A custom configuration section carries its own name into invalid-value diagnostics.</summary>
    [Fact]
    public void Build_InvalidCustomSectionKind_NamesCustomSection()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CustomAI:ChatClientKind"] = "websocket",
            })
            .Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            OpenAIClientProvider.OpenAIClientProviderSettings.Load(
                configuration,
                "custom-config",
                "CustomAI"));

        Assert.Equal(
            "Config key 'CustomAI/ChatClientKind' must be one of 'chatcompletions' or 'responses'. Got 'websocket'.",
            error.Message);
    }

    /// <summary>
    /// The effective kind follows config-over-resource precedence (AI-002 TR-36): a configured value overrides
    /// the provider resource property, unset or blank falls back to it, and the provider resource property itself
    /// defaults to Responses — so existing provider resources behave unchanged unless the key is set.
    /// </summary>
    [Fact]
    public void ResolveEffectiveKind_ConfigurationOverridesAndUnsetFallsBackToTheResourceProperty()
    {
        Assert.Equal(OpenAIChatClientKind.Responses, OpenAIClientProvider.DefaultChatClientKind);
        Assert.Equal(
            OpenAIChatClientKind.ChatCompletions,
            OpenAIClientProvider.ResolveEffectiveChatClientKind(
                OpenAIChatClientKind.ChatCompletions,
                OpenAIChatClientKind.Responses));
        Assert.Equal(
            OpenAIChatClientKind.Responses,
            OpenAIClientProvider.ResolveEffectiveChatClientKind(
                null,
                OpenAIChatClientKind.Responses));
        Assert.Equal(
            OpenAIChatClientKind.ChatCompletions,
            OpenAIClientProvider.ResolveEffectiveChatClientKind(
                null,
                OpenAIChatClientKind.ChatCompletions));
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
