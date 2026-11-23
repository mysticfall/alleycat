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
        Assert.DoesNotContain("FluidTemplateCompiler", source, StringComparison.Ordinal);
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

    /// <summary>
    /// Game registers the game clock as a startup instance so its origin is pinned to game start.
    /// </summary>
    [Fact]
    public void GameRegistersGameClockAsStartupSingleton()
    {
        string source = ReadGameSource();

        Assert.Contains("using AlleyCat.Core.Time;", source, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IGameClock>(new GameClock())", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// CORE-004 TR12 / CORE-011 TR5 and TR24: Game registers the exact registry instance it owns, before the provider
    /// is built, and stops it at the application-shutdown boundary.
    /// </summary>
    [Fact]
    public void GameRegistersOwnedSharedModelRegistryAndStopsItAtShutdown()
    {
        string source = ReadGameSource();

        Assert.Contains("internal SharedModelRegistry SharedModels", source, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<ISharedModelRegistry>(SharedModels)", source, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAll(MotionWorkers.Stop(), SharedModels.Stop())", source, StringComparison.Ordinal);

        int registryIndex = source.IndexOf("AddSingleton<ISharedModelRegistry>(SharedModels)", StringComparison.Ordinal);
        int buildProviderIndex = source.IndexOf("BuildServiceProvider();", StringComparison.Ordinal);
        Assert.True(registryIndex < buildProviderIndex, "The registry must be registered before the provider is built.");
    }

    private static string ReadGameSource() =>
        File.ReadAllText(RepositoryPath.Get("game", "src", "Game.cs"));
}
