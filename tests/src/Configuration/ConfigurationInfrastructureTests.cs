using System.Text.Json;
using AlleyCat.Core.Configuration;
using AlleyCat.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Configuration;

/// <summary>
/// Unit coverage for .NET configuration and logging infrastructure registration.
/// </summary>
public sealed class ConfigurationInfrastructureTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates the temporary configuration directory used by each test.
    /// </summary>
    public ConfigurationInfrastructureTests()
    {
        _ = Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>
    /// User JSON settings override matching shipped defaults while preserving untouched values.
    /// </summary>
    [Fact]
    public void Build_UserOverridePresent_OverridesShippedDefaults()
    {
        string basePath = WriteJson(
            "base.json",
            new
            {
                STT = new
                {
                    Host = "https://base.example/v1",
                    Model = "base-model",
                    Timeout = 30,
                },
            });
        string overridePath = WriteJson(
            "override.json",
            new
            {
                STT = new
                {
                    Host = "https://override.example/v1",
                },
            });

        IConfiguration configuration = GameConfiguration.Build(
            new FixedPathResolver(),
            basePath,
            overridePath);

        Assert.Equal("https://override.example/v1", configuration["STT:Host"]);
        Assert.Equal("base-model", configuration["STT:Model"]);
        Assert.Equal("30", configuration["STT:Timeout"]);
    }

    /// <summary>
    /// Missing user JSON settings remain optional.
    /// </summary>
    [Fact]
    public void Build_UserOverrideMissing_UsesShippedDefaults()
    {
        string basePath = WriteJson(
            "base.json",
            new
            {
                TTS = new
                {
                    Host = "https://tts.example/v1",
                    Voice = "alloy",
                },
            });
        string overridePath = Path.Combine(_temporaryDirectory, "missing.json");

        IConfiguration configuration = GameConfiguration.Build(
            new FixedPathResolver(),
            basePath,
            overridePath);

        Assert.Equal("https://tts.example/v1", configuration["TTS:Host"]);
        Assert.Equal("alloy", configuration["TTS:Voice"]);
    }

    /// <summary>
    /// DI registration exposes configuration and logging in one infrastructure slice.
    /// </summary>
    [Fact]
    public void AddGameConfiguration_RegistersConfigurationAndLoggingInfrastructure()
    {
        string basePath = WriteJson(
            "base.json",
            new
            {
                Logging = new
                {
                    LogLevel = new
                    {
                        Default = "Debug",
                    },
                },
            });

        ServiceCollection services = [];
        _ = services.AddGameConfiguration(
            pathResolver: new FixedPathResolver(),
            notificationSink: new CapturingNotificationSink(),
            baseConfigPath: basePath,
            overrideConfigPath: Path.Combine(_temporaryDirectory, "missing.json"));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IConfiguration>());
        Assert.NotNull(provider.GetRequiredService<ILoggerFactory>());
        Assert.Equal("Debug", provider.GetRequiredService<IConfiguration>()["Logging:LogLevel:Default"]);
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

    private string WriteJson<TValue>(string fileName, TValue value)
    {
        string path = Path.Combine(_temporaryDirectory, fileName);
        string content = JsonSerializer.Serialize(value);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FixedPathResolver : IConfigurationPathResolver
    {
        public string ToPhysicalPath(string path) => path;
    }

    private sealed class CapturingNotificationSink : ILogNotificationSink
    {
        public bool TryPostNotification(string? message, double timeoutSeconds = 3.0) => true;
    }
}
