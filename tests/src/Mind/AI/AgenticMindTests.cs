using System.Reflection;
using AlleyCat.Body.Eyes;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

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
    public void ObservedSpeech_RecognisedSpeakerRetainsIdentityAndProvenance()
    {
        ObservedSpeech observation = new("char:speaker", "microphone-7", "hello");

        Assert.Equal("microphone-7", observation.VoiceId);
        Assert.Equal("char:speaker", observation.ActorId);
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
    public void CreateRenderContext_BuildsDeterministicOwnerAndCharacterContext()
    {
        Dictionary<string, object?> ownerContext = new()
        {
            ["FullId"] = "char:owner"
        };
        Dictionary<string, object?> firstContext = new()
        {
            ["FullId"] = "char:alpha"
        };
        FakeCharacter owner = new(ownerContext)
        {
            Id = "owner"
        };
        FakeCharacter last = new(new Dictionary<string, object?> { ["FullId"] = "char:zulu" })
        {
            Id = "zulu"
        };
        FakeCharacter first = new(firstContext)
        {
            Id = "alpha"
        };
        SceneContext scene = new([last, owner, first]);
        ObservedSpeech speech = new("char:alpha", "voice-alpha", "Hello");
        AgentObservation[] timeline = [speech];

        IReadOnlyDictionary<string, object?> result = AgenticMind.CreateRenderContext(owner, scene, timeline);
        IReadOnlyDictionary<string, object?> characters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result["characters"]);
        IReadOnlyList<AgentObservation> observations = Assert.IsAssignableFrom<IReadOnlyList<AgentObservation>>(result["observations"]);

        Assert.Equal(["char:alpha", "char:owner", "char:zulu"], characters.Keys);
        Assert.Same(ownerContext, result["character"]);
        Assert.Same(firstContext, characters["char:alpha"]);
        Assert.Same(characters["char:owner"], result["character"]);
        Assert.Equal("char:owner", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result["character"])["FullId"]);
        Assert.Same(timeline, observations);
        Assert.Same(speech, Assert.Single(observations));
        _ = Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, object?>)result).Add("mutation", null));
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
    public void CreateRenderContext_WhenOwnerIsAbsent_FailsClearly()
    {
        FakeCharacter sceneCharacter = new(new Dictionary<string, object?>())
        {
            Id = "scene_character"
        };
        FakeCharacter owner = new(new Dictionary<string, object?>())
        {
            Id = "owner"
        };
        SceneContext scene = new([sceneCharacter]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AgenticMind.CreateRenderContext(owner, scene));

        Assert.Contains("absent", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, sceneCharacter.ContextRequestCount);
    }

    /// <summary>Arbitrary scene contexts cannot bypass CTX-001 character identity validation.</summary>
    [Theory]
    [InlineData("invalid-type", "subject", null)]
    [InlineData("char", "invalid-id", null)]
    [InlineData("char", "subject", "malformed")]
    [InlineData("char", "subject", "char:other_subject")]
    public void CreateRenderContext_WithCustomSceneAndInvalidCharacterIdentity_FailsClearly(
        string type,
        string id,
        string? fullIdOverride)
    {
        FakeCharacter owner = new(new Dictionary<string, object?>())
        {
            Id = "owner",
        };
        FakeCharacter invalidSubject = new(new Dictionary<string, object?>())
        {
            Type = type,
            Id = id,
            FullIdOverride = fullIdOverride,
        };
        ArbitrarySceneContext scene = new([owner, invalidSubject]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AgenticMind.CreateRenderContext(owner, scene));

        Assert.Contains("invalid identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        ArgumentException innerException = Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Equal("character", innerException.ParamName);
        Assert.Equal(0, owner.ContextRequestCount);
        Assert.Equal(0, invalidSubject.ContextRequestCount);
    }

    /// <summary>Valid identities from arbitrary scene-context implementations retain CTX-001 output semantics.</summary>
    [Fact]
    public void CreateRenderContext_WithCustomSceneAndValidCharacterIdentities_BuildsContext()
    {
        Dictionary<string, object?> ownerContext = [];
        Dictionary<string, object?> subjectContext = [];
        FakeCharacter owner = new(ownerContext)
        {
            Id = "owner",
        };
        FakeCharacter subject = new(subjectContext)
        {
            Id = "subject",
        };
        ArbitrarySceneContext scene = new([subject, owner]);

        IReadOnlyDictionary<string, object?> result = AgenticMind.CreateRenderContext(owner, scene);
        IReadOnlyDictionary<string, object?> characters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result["characters"]);

        Assert.Equal(["char:owner", "char:subject"], characters.Keys);
        Assert.Same(ownerContext, result["character"]);
        Assert.Same(subjectContext, characters["char:subject"]);
        Assert.Same(owner, subject.ReceivedObserver);
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

    /// <summary>Context workers expose dictionary runs without legacy state wrappers or trigger back-references.</summary>
    [Fact]
    public void ContextWorker_UsesConventionBasedDictionaryPublicationWithoutMutualTriggerReference()
    {
        Type workerType = typeof(ContextWorker);
        Assembly assembly = workerType.Assembly;

        Assert.False(typeof(IContextual).IsAssignableFrom(workerType));
        Assert.Null(workerType.GetMethod("GetContext", BindingFlags.Instance | BindingFlags.Public));
        Assert.Equal(
            typeof(IReadOnlyDictionary<string, object?>),
            workerType.GetMethod("GetProjection", BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType);
        Assert.Null(assembly.GetType("AlleyCat.Mind.AI.ContextWorkerState"));
        Assert.Null(assembly.GetType("AlleyCat.Mind.AI.ContextWorkerRunInput"));
        Assert.Null(assembly.GetType("AlleyCat.Mind.AI.ContextualSnapshot"));
        Assert.Equal(
            typeof(Task<IReadOnlyDictionary<string, object?>>),
            workerType.GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType);
        Assert.DoesNotContain(
            typeof(ContextWorkerTrigger).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => typeof(ContextWorker).IsAssignableFrom(field.FieldType));
        Assert.NotNull(typeof(ContextWorkerTrigger).GetEvent(nameof(ContextWorkerTrigger.RunRequested)));
        Assert.DoesNotContain(
            workerType.GetProperties(),
            property => property.PropertyType == typeof(object));
    }

    /// <summary>Observation trigger policies are abstract and must supply a concrete predicate.</summary>
    [Fact]
    public void ObservationContextWorkerTrigger_RequiresConcretePredicateImplementation()
    {
        Type triggerType = typeof(ObservationContextWorkerTrigger);
        MethodInfo predicate = triggerType.GetMethod(
            "ShouldRequestFor",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.True(triggerType.IsAbstract);
        Assert.True(predicate.IsAbstract);
        Assert.False(typeof(ConcreteObservationTrigger).IsAbstract);
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

    private sealed partial class ConcreteObservationTrigger : ObservationContextWorkerTrigger
    {
        protected override bool ShouldRequestFor(AgentObservation observation) => true;
    }

    private sealed class FakeCharacter(IReadOnlyDictionary<string, object?> context) : ICharacter
    {
        public string Id { get; set; } = "fake-character";

        public string Type { get; set; } = "char";

        public string? FullIdOverride
        {
            get; set;
        }

        public string FullId => FullIdOverride ?? $"{Type}:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public ISceneContext? ReceivedScene
        {
            get; private set;
        }

        public IContextual? ReceivedObserver
        {
            get; private set;
        }

        public int ContextRequestCount
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
        {
            ContextRequestCount++;
            ReceivedScene = scene;
            ReceivedObserver = observer;
            return context;
        }
    }

    private sealed record ArbitrarySceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public AlleyCat.Core.Content.ContentContext Content => AlleyCat.Core.Content.ContentContext.Default;

        public IIdentifiable? Find(string fullId)
        {
            IdentityValidator.ValidateFullId(fullId, nameof(fullId));
            return Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));
        }

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"Current scene does not contain identifiable object '{fullId}'.");
    }
}
