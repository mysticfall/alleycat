using AlleyCat.Mind.AI.Decision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Decision;

/// <summary>Offline subsystem binding, lifetime and lazy startup contracts.</summary>
public class DecisionConfigurationTests
{
    private static Dictionary<string, string?> Values() => new()
    {
        ["Decision:APIKind"] = "OpenRouter",
        ["Decision:Host"] = "https://example.invalid/api/alpha",
        ["Decision:Model"] = "configured",
        ["Decision:ApiKey"] = "private-fixture-key",
        ["Decision:Timeout"] = "30"
    };

    /// <summary>Absent Decision keys never inherit AI values; the section defaults to the local SystemOne backend.</summary>
    [Fact]
    public void MissingDecisionSectionDefaultsToLocalSystemOne()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Host"] = "https://example.invalid/v1",
            ["AI:Model"] = "chat",
            ["AI:ApiKey"] = "secret",
            ["AI:Timeout"] = "30"
        }).Build();
        using ServiceProvider services = new ServiceCollection().AddSingleton(configuration).AddLogging().AddDecisionClient().BuildServiceProvider();
        DecisionClientProvider provider = services.GetRequiredService<DecisionClientProvider>();
        var options = DecisionOptions.FromConfiguration(configuration);
        Assert.Null(options.APIKind);
        Assert.Null(options.Host);
        DecisionSettings settings = options.Validate();
        Assert.Equal(DecisionAPIKind.SystemOne, settings.Kind);
        Assert.Equal("http://localhost:8081/v1/systemone", settings.Endpoint.AbsoluteUri);
        Assert.Equal(string.Empty, settings.Model);
        Assert.Null(settings.ApiKey);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.Timeout);
        _ = provider.GetClient();
    }

    /// <summary>An explicit SystemOne section receives the documented defaults for absent keys.</summary>
    [Fact]
    public void ExplicitSystemOneSectionDefaultsAbsentKeys()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Decision:APIKind"] = "SystemOne" }).Build();
        DecisionSettings settings = DecisionOptions.FromConfiguration(configuration).Validate();
        Assert.Equal(DecisionAPIKind.SystemOne, settings.Kind);
        Assert.Equal("http://localhost:8081/v1/systemone", settings.Endpoint.AbsoluteUri);
        Assert.Equal(string.Empty, settings.Model);
        Assert.Null(settings.ApiKey);
        Assert.Equal(TimeSpan.FromSeconds(DecisionOptions.DefaultTimeoutSeconds), settings.Timeout);
    }

    /// <summary>Present keys override the SystemOne defaults; absent Host and Timeout still default locally.</summary>
    [Fact]
    public void PresentKeysOverrideSystemOneDefaults()
    {
        DecisionOptions pinned = new()
        {
            APIKind = "SystemOne",
            Host = "http://127.0.0.1:9000/v1/",
            Model = "custom",
            Timeout = 7
        };
        DecisionSettings pinnedSettings = pinned.Validate();
        Assert.Equal("http://127.0.0.1:9000/v1/systemone", pinnedSettings.Endpoint.AbsoluteUri);
        Assert.Equal("custom", pinnedSettings.Model);
        Assert.Equal(TimeSpan.FromSeconds(7), pinnedSettings.Timeout);
        DecisionSettings defaulted = new DecisionOptions { APIKind = "SystemOne" }.Validate();
        Assert.Equal(DecisionOptions.DefaultSystemOneHost + "/systemone", defaulted.Endpoint.AbsoluteUri);
    }

    /// <summary>SystemOne hosts stay base URLs: nonempty path, no userinfo/query, never an already-composed operation URL.</summary>
    [Theory]
    [InlineData("http://localhost:8081")]
    [InlineData("http://localhost:8081/")]
    [InlineData("http://localhost:8081/v1/systemone")]
    [InlineData("http://localhost:8081/v1/systemone/")]
    [InlineData("http://localhost:8081/v1/decisions")]
    [InlineData("http://user:private-fixture-key@localhost:8081/v1")]
    [InlineData("http://localhost:8081/v1?query=1")]
    [InlineData("ftp://localhost:8081/v1")]
    public void SystemOneRejectsNonBaseHosts(string? host)
    {
        DecisionOptions options = new()
        {
            APIKind = "SystemOne",
            Host = host,
            Timeout = 30
        };
        DecisionException error = Assert.Throws<DecisionException>(options.Validate);
        Assert.Equal(DecisionFailure.Configuration, error.Failure);
        Assert.Equal("Invalid or missing Decision:Host.", error.Message);
        Assert.DoesNotContain("private-fixture-key", error.ToString());
    }

    /// <summary>Later sources override the same independent keys; explicit resolution returns a reusable client.</summary>
    [Fact]
    public void SharedConfigurationPrecedenceAndReusableResolution()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(Values())
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Decision:Model"] = "override" }).Build();
        Assert.Equal("override", DecisionOptions.FromConfiguration(configuration).Model);
        Assert.Equal("https://example.invalid/api/alpha", DecisionOptions.FromConfiguration(configuration).Host);
        using ServiceProvider services = new ServiceCollection().AddSingleton(configuration).AddLogging().AddDecisionClient().BuildServiceProvider();
        Assert.Same(services.GetRequiredService<IDecisionClient>(), services.GetRequiredService<DecisionClientProvider>().GetClient());
    }

    /// <summary>Invalid values identify only the affected key, never its raw value.</summary>
    [Theory]
    [InlineData("APIKind", "Unsupported")]
    [InlineData("APIKind", "systemone")]
    [InlineData("Host", "https://user:private-fixture-key@example.invalid/v1")]
    [InlineData("Host", "https://example.invalid/v1?secret=private-fixture-key")]
    [InlineData("Host", "https://example.invalid/v1#fragment")]
    [InlineData("Host", "https://example.invalid/v1/decisions/")]
    [InlineData("Host", "https://example.invalid/v1/systemone")]
    [InlineData("Host", "https://example.invalid/")]
    [InlineData("Model", " ")]
    [InlineData("ApiKey", null)]
    [InlineData("ApiKey", "private-fixture-key\r\nInjected")]
    [InlineData("Timeout", "0")]
    [InlineData("Timeout", "-1")]
    [InlineData("Timeout", "NaN")]
    [InlineData("Timeout", "Infinity")]
    [InlineData("Timeout", "not-a-number")]
    public void InvalidSettingsAreSafeAndLocal(string key, string? value)
    {
        Dictionary<string, string?> values = Values();
        values["Decision:" + key] = value;
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using ServiceProvider services = new ServiceCollection().AddSingleton(configuration).AddLogging().AddDecisionClient().BuildServiceProvider();
        DecisionException error = Assert.Throws<DecisionException>(services.GetRequiredService<IDecisionClient>);
        Assert.Equal(DecisionFailure.Configuration, error.Failure);
        Assert.Equal($"Invalid or missing Decision:{key}.", error.Message);
        Assert.DoesNotContain("private-fixture-key", error.ToString());
    }
}
