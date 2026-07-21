using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
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
    /// End-of-turn output is a serialisable property-free object with no placeholder payload.
    /// </summary>
    [Fact]
    public void EndTurnResult_SerialisesAsEmptyObject()
    {
        string json = JsonSerializer.Serialize(new EndTurnResult());

        Assert.Equal("{}", json);
        Assert.Empty(typeof(EndTurnResult).GetProperties());
    }

    /// <summary>
    /// Agent Framework schema generation keeps the empty result closed to unknown properties.
    /// </summary>
    [Fact]
    public void EndTurnResult_GeneratesClosedEmptySchema()
    {
        ChatResponseFormatJson format = Assert.IsType<ChatResponseFormatJson>(
            ChatResponseFormat.ForJsonSchema<EndTurnResult>());
        Assert.True(format.Schema.HasValue);
        JsonElement schema = format.Schema.Value;
        if (schema.TryGetProperty("properties", out JsonElement properties))
        {
            Assert.Empty(properties.EnumerateObject());
        }

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    /// <summary>
    /// Speech observations own their default scheduling significance without Mind-specific configuration.
    /// </summary>
    [Fact]
    public void ObservedSpeech_RecognisedSpeakerRetainsIdentityAndProvenance()
    {
        ObservedSpeech observation = new("Speaker", "microphone-7", "hello");

        Assert.Equal("microphone-7", observation.VoiceId);
        Assert.Equal("Speaker", observation.ActorId);
        Assert.Equal("hello", observation.Content);
    }

    /// <summary>
    /// Speech observations retain a null recognition result separately from raw voice provenance.
    /// </summary>
    [Fact]
    public void ObservedSpeech_WhenUnrecognised_RetainsRawVoiceIDSeparately()
    {
        ObservedSpeech observation = new(null, "microphone-7", "hello");

        Assert.Equal("microphone-7", observation.VoiceId);
        Assert.Null(observation.ActorId);
        Assert.Equal("hello", observation.Content);
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
    /// AgenticMind obtains observer-relative CTX-001 data for every character in ordinal exact-ID order.
    /// </summary>
    [Fact]
    public void CreateSystemInstructionContext_BuildsDeterministicOwnerAndCharacterContext()
    {
        Dictionary<string, object?> ownerContext = new()
        {
            ["Id"] = "owner"
        };
        Dictionary<string, object?> firstContext = new()
        {
            ["Id"] = "Alpha"
        };
        FakeCharacter owner = new(ownerContext)
        {
            Id = "owner"
        };
        FakeCharacter last = new(new Dictionary<string, object?> { ["Id"] = "zulu" })
        {
            Id = "zulu"
        };
        FakeCharacter first = new(firstContext)
        {
            Id = "Alpha"
        };
        SceneContext scene = new([last, owner, first]);

        IReadOnlyDictionary<string, object?> result = AgenticMind.CreateSystemInstructionContext(owner, scene);
        Dictionary<string, object?> characters = Assert.IsType<Dictionary<string, object?>>(result["characters"]);

        Assert.Equal(["Alpha", "owner", "zulu"], characters.Keys);
        Assert.Same(ownerContext, result["character"]);
        Assert.Same(characters["owner"], result["character"]);
        Assert.All([first, owner, last], subject =>
        {
            Assert.Same(scene, subject.ReceivedScene);
            Assert.Same(owner, subject.ReceivedObserver);
        });
    }

    /// <summary>
    /// An owning character outside the scene snapshot is an invalid prompt context.
    /// </summary>
    [Fact]
    public void CreateSystemInstructionContext_WhenOwnerIsAbsent_FailsClearly()
    {
        FakeCharacter sceneCharacter = new(new Dictionary<string, object?>())
        {
            Id = "scene-character"
        };
        FakeCharacter owner = new(new Dictionary<string, object?>())
        {
            Id = "owner"
        };
        SceneContext scene = new([sceneCharacter]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AgenticMind.CreateSystemInstructionContext(owner, scene));

        Assert.Contains("absent", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, sceneCharacter.ContextRequestCount);
    }

    /// <summary>
    /// Agent Framework metadata uses exact runtime identity while keeping its description generic.
    /// </summary>
    [Fact]
    public void CreateAgentMetadata_UsesExactCharacterIDAndGenericDescription()
    {
        FakeCharacter character = new(new Dictionary<string, object?>())
        {
            Id = "NPC.Mixed-Case"
        };

        (string name, string description) = AgenticMind.CreateAgentMetadata(character);

        Assert.Equal("NPC.Mixed-Case", name);
        Assert.Equal(AgenticMind.AgentDescription, description);
        Assert.DoesNotContain("NPC.Mixed-Case", description, StringComparison.Ordinal);
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

        public int ContextRequestCount
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, ICharacter? observer)
        {
            ContextRequestCount++;
            ReceivedScene = scene;
            ReceivedObserver = observer;
            return context;
        }
    }
}
