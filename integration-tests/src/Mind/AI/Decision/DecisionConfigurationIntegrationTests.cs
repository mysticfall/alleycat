using AlleyCat.Core.Configuration;
using AlleyCat.Mind.AI.Decision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Decision;

/// <summary>Production Game registration and real shipped/user YAML merging; never invokes evaluation.</summary>
public class DecisionConfigurationIntegrationTests
{
    /// <summary>Subsystem provider is available from Game, and the shared YAML loader preserves user precedence.</summary>
    [Fact]
    public void GameRegistrationAndYAMLOverridesRemainIndependent()
    {
        Assert.NotNull(Game.Instance.GetRequiredService<DecisionClientProvider>());
        IConfigurationPathResolver resolver = Game.Instance.GetRequiredService<IConfigurationPathResolver>();
        string directory = resolver.ToPhysicalPath("res://temp/hosted-decision-api/configuration-fixture");
        _ = Directory.CreateDirectory(directory);
        string shipped = Path.Combine(directory, "shipped.yaml");
        string user = Path.Combine(directory, "user.yaml");
        File.WriteAllText(shipped, """
            AI:
                Model: conversational-only
            Decision:
                APIKind: OpenRouter
                Host: https://example.invalid/api/alpha
                Model: shipped
                ApiKey: synthetic-shipped-key
                Timeout: 30
            """);
        File.WriteAllText(user, """
            Decision:
                Model: user-override
                ApiKey: synthetic-user-key
            """);
        IConfigurationRoot configuration = GameConfiguration.Build(resolver, shipped, user);
        using var configurationLifetime = (IDisposable)configuration;
        var options = DecisionOptions.FromConfiguration(configuration);
        Assert.Equal("user-override", options.Model);
        Assert.Equal("synthetic-user-key", options.ApiKey);
        Assert.Equal("https://example.invalid/api/alpha", options.Host);
        Assert.Equal(30, options.Timeout);
        using DecisionClientProvider provider = new(configuration, NullLogger<DecisionClient>.Instance);
        Assert.Same(provider.GetClient(), provider.GetClient());
        // Explicit acquisition validates locally; only EvaluateAsync can dispatch a paid request.
    }

    /// <summary>Without any Decision keys, the production YAML pipeline yields the local SystemOne defaults.</summary>
    [Fact]
    public void ZeroConfigurationDefaultsToLocalSystemOne()
    {
        IConfigurationPathResolver resolver = Game.Instance.GetRequiredService<IConfigurationPathResolver>();
        string directory = resolver.ToPhysicalPath("res://temp/decision-systemone-01/zero-config-fixture");
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "shipped.yaml"), """
            AI:
                Model: conversational-only
            """);
        File.WriteAllText(Path.Combine(directory, "user.yaml"), """
            Diagnostics: {}
            """);
        IConfigurationRoot configuration = GameConfiguration.Build(resolver,
            Path.Combine(directory, "shipped.yaml"), Path.Combine(directory, "user.yaml"));
        using var configurationLifetime = (IDisposable)configuration;
        var options = DecisionOptions.FromConfiguration(configuration);
        Assert.Null(options.APIKind);
        DecisionSettings settings = options.Validate();
        Assert.Equal(DecisionAPIKind.SystemOne, settings.Kind);
        Assert.Equal("http://localhost:8081/v1/systemone", settings.Endpoint.AbsoluteUri);
        Assert.Null(settings.ApiKey);
        using DecisionClientProvider provider = new(configuration, NullLogger<DecisionClient>.Instance);
        Assert.NotNull(provider.GetClient());
    }
}
