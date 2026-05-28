using Xunit;

namespace AlleyCat.Tests.Core;

/// <summary>
/// Source-level coverage for Game service registration boundaries.
/// </summary>
public sealed class GameServiceRegistrationTests
{
    /// <summary>
    /// Game must not depend directly on the templating implementation or contract.
    /// </summary>
    [Fact]
    public void GameSourceHasNoDirectTemplatingCoupling()
    {
        string source = ReadGameSource();

        Assert.DoesNotContain("using AlleyCat.Templating;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HandlebarsTemplateCompiler", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ITemplateCompiler", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TemplateCompiler", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Game invokes generic resource registrars before building the provider.
    /// </summary>
    [Fact]
    public void GameRegistersResourceRegistrarsBeforeProviderBuild()
    {
        string source = ReadGameSource();

        Assert.Contains("public Godot.Collections.Array<Resource> ServiceRegistrars", source, StringComparison.Ordinal);
        Assert.Contains("private void RegisterConfiguredServices(IServiceCollection services)", source, StringComparison.Ordinal);
        Assert.Contains("DiscoverResourceServiceRegistrars(ServiceRegistrars)", source, StringComparison.Ordinal);

        int registerServicesIndex = source.IndexOf("RegisterServices(_services);", StringComparison.Ordinal);
        int registerConfiguredIndex = source.IndexOf("RegisterConfiguredServices(_services);", StringComparison.Ordinal);
        int registerSceneIndex = source.IndexOf("RegisterSceneOwnedServices(_services);", StringComparison.Ordinal);
        int buildProviderIndex = source.IndexOf("BuildServiceProvider();", StringComparison.Ordinal);

        Assert.True(registerServicesIndex >= 0, "Game should register built-in services first.");
        Assert.True(registerConfiguredIndex > registerServicesIndex, "Resource registrars should run after built-ins.");
        Assert.True(registerSceneIndex > registerConfiguredIndex, "Scene-owned registrars should still be discovered.");
        Assert.True(buildProviderIndex > registerSceneIndex, "All registrars must run before the provider is built.");
    }

    private static string ReadGameSource() =>
        File.ReadAllText(RepositoryPath.Get("game", "src", "Game.cs"));
}
