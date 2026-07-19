using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Unit coverage for observation contracts consumed by agentic minds.
/// </summary>
public sealed class AgenticMindTests
{
    /// <summary>
    /// Speech observations own their default scheduling significance without Mind-specific configuration.
    /// </summary>
    [Fact]
    public void SpeechObservation_DefaultWeight_IsInherentToObservationType()
    {
        SpeechObservation observation = new("player", "hello");

        Assert.Equal(1f, observation.Weight);
    }

    /// <summary>
    /// Speech observations own agent prompt formatting so the runtime does not type-switch on observation subtypes.
    /// </summary>
    [Fact]
    public void SpeechObservation_ToPromptString_DescribesSpeakerAndContent()
    {
        Observation observation = new SpeechObservation("player", "hello");

        Assert.Equal("Speech from player: hello", observation.ToPromptString());
    }

    /// <summary>
    /// Trial response diagnostics should use AgentResponse primary text and SDK message abstractions.
    /// </summary>
    [Fact]
    public void CreateSensitiveTrialAgentResponseDiagnostics_IncludesTextAndMessages()
    {
        AgentResponse response = new(new ChatMessage(ChatRole.Assistant, "hello from response"));

        string diagnostics = AgenticMind.CreateSensitiveTrialAgentResponseDiagnostics(response);

        Assert.Contains("Text=hello from response", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Messages=1", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Message[0].Role=assistant", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Message[0].Text=hello from response", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Empty SDK responses are reported explicitly rather than substituting speech-tool arguments.
    /// </summary>
    [Fact]
    public void CreateSensitiveTrialAgentResponseDiagnostics_WhenTextIsEmpty_ReportsEmptySdkContent()
    {
        AgentResponse response = new(new ChatMessage(ChatRole.Assistant, string.Empty));

        string diagnostics = AgenticMind.CreateSensitiveTrialAgentResponseDiagnostics(response);

        Assert.Contains("Text=<empty>", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Message[0].Text=<empty>", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sensitive AgentResponse diagnostics should be suppressed when request/response logging is disabled.
    /// </summary>
    [Fact]
    public void CreateSensitiveAgentResponseDiagnosticsOrDefault_WhenDisabled_ReturnsNull()
    {
        AgentResponse response = new(new ChatMessage(ChatRole.Assistant, "secret response"));

        string? diagnostics = AgenticMind.CreateSensitiveAgentResponseDiagnosticsOrDefault(
            response,
            enableRequestResponseDiagnostics: false);

        Assert.Null(diagnostics);
    }

    /// <summary>
    /// Sensitive AgentResponse diagnostics should include response payloads when request/response logging is enabled.
    /// </summary>
    [Fact]
    public void CreateSensitiveAgentResponseDiagnosticsOrDefault_WhenEnabled_ReturnsDiagnostics()
    {
        AgentResponse response = new(new ChatMessage(ChatRole.Assistant, "secret response"));

        string? diagnostics = AgenticMind.CreateSensitiveAgentResponseDiagnosticsOrDefault(
            response,
            enableRequestResponseDiagnostics: true);

        Assert.NotNull(diagnostics);
        Assert.Contains("Text=secret response", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Missing diagnostics configuration should keep sensitive AI request/response logging disabled by default.
    /// </summary>
    [Fact]
    public void AIDiagnosticsSettings_Load_WhenSectionMissing_DisablesRequestResponseLogging()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        var settings = AIDiagnosticsSettings.Load(configuration);

        Assert.False(settings.EnableRequestResponseLogging);
    }

    /// <summary>
    /// Diagnostics configuration should opt in to sensitive AI request/response logging explicitly.
    /// </summary>
    [Fact]
    public void AIDiagnosticsSettings_Load_WhenEnabledInConfiguration_EnablesRequestResponseLogging()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Diagnostics:AI:EnableRequestResponseLogging"] = "true",
            })
            .Build();

        var settings = AIDiagnosticsSettings.Load(configuration);

        Assert.True(settings.EnableRequestResponseLogging);
    }

    /// <summary>
    /// AgenticMind must pass the CTX-001 dictionary directly to system-instruction template rendering.
    /// </summary>
    [Fact]
    public void RenderSystemInstruction_PassesContextDictionaryToTemplate()
    {
        Dictionary<string, object?> context = new()
        {
            ["displayName"] = "Alley",
        };
        CapturingTemplate template = new();

        string result = AgenticMind.RenderSystemInstruction(template, context);

        Assert.Equal("Hello Alley", result);
        Assert.Same(context, template.ReceivedContext);
    }

    /// <summary>
    /// AgenticMind obtains CTX-001 data from the associated character with the current scene and no observer.
    /// </summary>
    [Fact]
    public void CreateSystemInstructionContext_UsesAssociatedCharacterContext()
    {
        Dictionary<string, object?> context = new()
        {
            ["displayName"] = "Alley",
        };
        SceneContext scene = new([]);
        FakeCharacter character = new(context);

        IReadOnlyDictionary<string, object?> result = AgenticMind.CreateSystemInstructionContext(character, scene);

        Assert.Same(context, result);
        Assert.Same(scene, character.ReceivedScene);
        Assert.Null(character.ReceivedObserver);
    }

    /// <summary>
    /// Prompt build context exposes its required owning character without validating optional subsystem-specific identity.
    /// </summary>
    [Fact]
    public void PromptSectionBuildContext_ExposesOwningCharacterWithEmptyID()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        SceneContext scene = new([]);
        FakeCharacter character = new(new Dictionary<string, object?>())
        {
            Id = string.Empty,
        };

        PromptSectionBuildContext context = new(services, scene, character);

        Assert.Same(character, context.Character);
        Assert.Same(scene, context.Scene);
        Assert.Same(services, context.Services);
        _ = Assert.Throws<ArgumentNullException>(() => new PromptSectionBuildContext(services, scene, null!));
    }

    private sealed class CapturingTemplate : ITemplate
    {
        public IReadOnlyDictionary<string, object?>? ReceivedContext
        {
            get; private set;
        }

        public string Render(IReadOnlyDictionary<string, object?> context)
        {
            ReceivedContext = context;
            return $"Hello {context["displayName"]}";
        }
    }

    private sealed class FakeCharacter(IReadOnlyDictionary<string, object?> context) : ICharacter
    {
        public string Id { get; set; } = "fake-character";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public ISceneContext? ReceivedScene
        {
            get; private set;
        }

        public ICharacter? ReceivedObserver
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, ICharacter? observer)
        {
            ReceivedScene = scene;
            ReceivedObserver = observer;
            return context;
        }
    }
}
