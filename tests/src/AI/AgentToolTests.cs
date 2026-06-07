using AlleyCat.AI.Tool;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.Tests.AI;

/// <summary>
/// Unit coverage for Agent Framework tool invocation context wiring.
/// </summary>
public sealed class AgentToolTests
{
    /// <summary>
    /// Tool functions should resolve invocation dependencies from the supplied service provider.
    /// </summary>
    [Fact]
    public async Task Create_WhenToolAcceptsServiceProvider_InjectsConfiguredServices()
    {
        RecordingService service = new();
        RecordingServiceProvider services = new(service);
        AIFunction function = AgentTool.CreateFunction(ToolHost.Speak, services, "speak", "Speak aloud.");
        AIFunctionArguments arguments = new()
        {
            ["speech"] = " Alley speaks. ",
        };

        object? result = await function.InvokeAsync(arguments, CancellationToken.None);

        Assert.Equal("Spoken.", result?.ToString());
        Assert.Equal("Alley speaks.", service.SpokenLine);
    }

    /// <summary>
    /// Tool resources should pass authored metadata through to the generated Agent Framework function.
    /// </summary>
    [Fact]
    public void CreateFunction_WithResourceMetadata_UsesConfiguredNameAndDescription()
    {
        RecordingServiceProvider services = new(new RecordingService());
        AIFunction function = AgentTool.CreateFunction(ToolHost.Speak, services, "speak", "Speak aloud.");

        Assert.Equal("speak", function.Name);
        Assert.Equal("Speak aloud.", function.Description);
    }

    private sealed class RecordingService
    {
        public string? SpokenLine
        {
            get;
            private set;
        }

        public Task<string> SpeakAsync(string speech)
        {
            SpokenLine = speech.Trim();
            return Task.FromResult("Spoken.");
        }
    }

    private sealed class RecordingServiceProvider(RecordingService service) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(RecordingService) ? service : null;
    }

    private static class ToolHost
    {
        public static Task<string> Speak(string speech, IServiceProvider services)
            => services.GetService(typeof(RecordingService)) is RecordingService service
                ? service.SpeakAsync(speech)
                : Task.FromResult("Missing service.");
    }
}
