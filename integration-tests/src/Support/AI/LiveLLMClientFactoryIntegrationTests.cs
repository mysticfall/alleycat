using AlleyCat.TestFramework;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Deterministic coverage for the live LLM test client factory: strict <c>AITest</c>-only configuration,
/// refusal to fall back to the production <c>AI</c> section or its defaults, and secret-free failure
/// messages. All configuration is synthetic; no live backend is contacted.
/// </summary>
/// <remarks>
/// The facts need the Godot runtime (the <c>Global</c> autoload provides <see cref="Game" />) but no
/// rendering, so the class runs headless.
/// </remarks>
[Headless]
public sealed class LiveLLMClientFactoryIntegrationTests
{
    private const string SyntheticApiKey = "synthetic-live-test-key-not-a-credential";
    private const string SyntheticHost = "https://live-llm-test.example/v1";
    private const string SyntheticModel = "synthetic-live-model";

    /// <summary>
    /// A fully configured AITest section yields separate target and judge clients.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithValidAITestSection_CreatesSeparateTargetAndJudgeClients()
    {
        IConfiguration configuration = CreateConfiguration(
            ("AITest:Host", SyntheticHost),
            ("AITest:Model", SyntheticModel),
            ("AITest:ApiKey", SyntheticApiKey));

        using LiveLLMClientPair clients = LiveLLMClientFactory.CreateLiveClients(configuration);

        Assert.NotNull(clients.Target);
        Assert.NotNull(clients.Judge);
        Assert.NotSame(clients.Target, clients.Judge);
    }

    /// <summary>
    /// A missing AITest section fails even when the production AI section is fully configured.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithoutAITestSection_FailsEvenWhenAIIsFullyConfigured()
    {
        IConfiguration configuration = CreateConfiguration(
            ("AI:Host", "https://production.example/v1"),
            ("AI:Model", "production-model"),
            ("AI:ApiKey", "production-key"),
            ("AI:Timeout", "30"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => LiveLLMClientFactory.CreateLiveClients(configuration));

        Assert.Contains("AITest:Host", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AI:Host", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A partial AITest section fails on the next missing required key despite a fully configured AI section.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithPartialAITestSection_FailsOnNextRequiredKey()
    {
        IConfiguration configuration = CreateConfiguration(
            ("AI:Host", "https://production.example/v1"),
            ("AI:Model", "production-model"),
            ("AI:ApiKey", "production-key"),
            ("AITest:Host", SyntheticHost));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => LiveLLMClientFactory.CreateLiveClients(configuration));

        Assert.Contains("AITest:Model", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AITest:Host", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blank model is rejected instead of acquiring the production gpt-4o-mini default.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithBlankModel_RejectsInsteadOfApplyingProductionDefault()
    {
        IConfiguration configuration = CreateConfiguration(
            ("AITest:Host", SyntheticHost),
            ("AITest:Model", "  "),
            ("AITest:ApiKey", SyntheticApiKey));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => LiveLLMClientFactory.CreateLiveClients(configuration));

        Assert.Contains("AITest:Model", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("gpt-4o-mini", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A missing API key is rejected instead of acquiring the compatible-backend placeholder key.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithMissingApiKey_RejectsInsteadOfApplyingPlaceholderKey()
    {
        IConfiguration configuration = CreateConfiguration(
            ("AITest:Host", SyntheticHost),
            ("AITest:Model", SyntheticModel));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => LiveLLMClientFactory.CreateLiveClients(configuration));

        Assert.Contains("AITest:ApiKey", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unused-api-key", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero timeout is rejected before any client construction, without echoing the value.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithZeroTimeout_FailsBeforeClientConstruction()
        => AssertNonPositiveTimeoutFailsBeforeClientConstruction(0);

    /// <summary>
    /// A negative timeout is rejected before any client construction, without echoing the value.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithNegativeTimeout_FailsBeforeClientConstruction()
        => AssertNonPositiveTimeoutFailsBeforeClientConstruction(-7);

    /// <summary>
    /// A non-HTTP scheme is rejected before any client construction, without echoing the supplied value.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithNonHttpSchemeHost_FailsBeforeClientConstruction()
        => AssertInvalidHostFailsBeforeClientConstruction("ftp://live-llm-test.example/v1");

    /// <summary>
    /// A scheme-less host is rejected before any client construction, without echoing the supplied value.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithSchemeLessHost_FailsBeforeClientConstruction()
        => AssertInvalidHostFailsBeforeClientConstruction("live-llm-test.example/v1");

    /// <summary>
    /// A host without an API base path is rejected before any client construction, without echoing the value.
    /// </summary>
    [Fact]
    public void CreateLiveClients_WithHostMissingBasePath_FailsBeforeClientConstruction()
        => AssertInvalidHostFailsBeforeClientConstruction("https://live-llm-test.example");

    /// <summary>
    /// Every configuration failure path produces a message that never contains the configured API key.
    /// </summary>
    [Fact]
    public void CreateLiveClients_ConfigurationFailures_NeverContainTheConfiguredApiKey()
    {
        List<IConfiguration> failureConfigurations =
        [
            CreateConfiguration(("AITest:Model", SyntheticModel), ("AITest:ApiKey", SyntheticApiKey)),
            CreateConfiguration(
                ("AITest:Host", "not-a-valid-endpoint"),
                ("AITest:Model", SyntheticModel),
                ("AITest:ApiKey", SyntheticApiKey)),
            CreateConfiguration(("AITest:Host", SyntheticHost), ("AITest:ApiKey", SyntheticApiKey)),
            CreateConfiguration(("AITest:Host", SyntheticHost), ("AITest:Model", SyntheticModel)),
            CreateConfiguration(
                ("AITest:Host", SyntheticHost),
                ("AITest:Model", SyntheticModel),
                ("AITest:ApiKey", SyntheticApiKey),
                ("AITest:Timeout", "0")),
        ];

        foreach (IConfiguration configuration in failureConfigurations)
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => LiveLLMClientFactory.CreateLiveClients(configuration));

            Assert.Contains("AITest:", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticApiKey, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The parameterless runtime entry resolves only the live merged game configuration: it either constructs
    /// a disposable client pair from configured AITest settings or fails naming the AITest section — never any
    /// other outcome.
    /// </summary>
    [Fact]
    public void CreateLiveClients_Parameterless_ResolvesTheLiveMergedGameConfiguration()
    {
        try
        {
            using LiveLLMClientPair clients = LiveLLMClientFactory.CreateLiveClients();
        }
        catch (InvalidOperationException error) when (error.Message.Contains("AITest", StringComparison.Ordinal))
        {
            // No AITest section is configured on this machine: the runtime entry correctly refuses rather
            // than falling back to production settings.
        }
    }

    private static void AssertNonPositiveTimeoutFailsBeforeClientConstruction(int timeout)
    {
        IConfiguration configuration = CreateConfiguration(
            ("AITest:Host", SyntheticHost),
            ("AITest:Model", SyntheticModel),
            ("AITest:ApiKey", SyntheticApiKey),
            ("AITest:Timeout", timeout.ToString()));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => LiveLLMClientFactory.CreateLiveClients(configuration));

        Assert.Contains("AITest:Timeout", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(timeout.ToString(), error.Message, StringComparison.Ordinal);
    }

    private static void AssertInvalidHostFailsBeforeClientConstruction(string host)
    {
        IConfiguration configuration = CreateConfiguration(
            ("AITest:Host", host),
            ("AITest:Model", SyntheticModel),
            ("AITest:ApiKey", SyntheticApiKey));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => LiveLLMClientFactory.CreateLiveClients(configuration));

        Assert.Contains("AITest:Host", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host, error.Message, StringComparison.Ordinal);
    }

    private static IConfiguration CreateConfiguration(params (string Key, string Value)[] values)
    {
        Dictionary<string, string?> configurationValues = [];
        foreach ((string key, string value) in values)
        {
            configurationValues[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();
    }
}
