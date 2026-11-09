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

    /// <summary>Normal registration and provider construction tolerate absent settings without inheriting AI.</summary>
    [Fact]
    public void MissingDecisionIsLazyAndDoesNotInheritAI()
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
        Assert.Null(DecisionOptions.FromConfiguration(configuration).Model);
        DecisionException error = Assert.Throws<DecisionException>(provider.GetClient);
        Assert.Equal(DecisionFailure.Configuration, error.Failure);
        Assert.Contains("Decision:APIKind", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
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
    [InlineData("APIKind", null)]
    [InlineData("APIKind", "Unsupported")]
    [InlineData("Host", "https://user:private-fixture-key@example.invalid/v1")]
    [InlineData("Host", "https://example.invalid/v1?secret=private-fixture-key")]
    [InlineData("Host", "https://example.invalid/v1#fragment")]
    [InlineData("Host", "https://example.invalid/v1/decisions/")]
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
