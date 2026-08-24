using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.Vision;
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
    /// Missing diagnostics configuration keeps sensitive AI request/response logging enabled by default while
    /// reasoning logging also stays default-enabled (it only fires at trace level).
    /// </summary>
    [Fact]
    public void AIDiagnosticsSettings_Load_WhenSectionMissing_EnablesRequestResponseLoggingByDefault()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        var settings = AIDiagnosticsSettings.Load(configuration);

        Assert.True(settings.EnableRequestResponseLogging);
        Assert.True(settings.EnableReasoningLogging);
    }

    /// <summary>
    /// Diagnostics configuration with explicit true keeps sensitive AI request/response logging enabled while
    /// reasoning logging remains enabled by default.
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
        Assert.True(settings.EnableReasoningLogging);
    }

    /// <summary>
    /// Diagnostics configuration should opt out of trace-level reasoning logging explicitly; disabling reasoning
    /// logging does not affect request/response logging, which remains enabled by default.
    /// </summary>
    [Fact]
    public void AIDiagnosticsSettings_Load_WhenReasoningLoggingDisabledInConfiguration_DisablesReasoningLogging()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Diagnostics:AI:EnableReasoningLogging"] = "false",
            })
            .Build();

        var settings = AIDiagnosticsSettings.Load(configuration);

        Assert.False(settings.EnableReasoningLogging);
        Assert.True(settings.EnableRequestResponseLogging);
    }

    /// <summary>
    /// AgenticMind must pass the AI-003 render context directly to system-instruction template rendering.
    /// </summary>
    [Fact]
    public async Task RenderSystemInstruction_PassesContextDictionaryToTemplate()
    {
        Dictionary<string, object?> context = new()
        {
            ["displayName"] = "Alley",
        };
        CapturingTemplate template = new();

        string result = await AgenticMind.RenderSystemInstruction(template, context);

        Assert.Equal("Hello Alley", result);
        Assert.Same(context, template.ReceivedContext);
    }

    /// <summary>
    /// AgenticMind exposes curated render views for itself and every explicitly eligible character in ordinal
    /// exact-ID order (AI-003 TR-20).
    /// </summary>
    [Fact]
    public void CreateRenderContext_BuildsDeterministicOwnerAndCharacterViews()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        FakeCharacter last = new()
        {
            Id = "zulu"
        };
        FakeCharacter first = new()
        {
            Id = "alpha"
        };
        FakeCharacter player = new()
        {
            Id = "player"
        };
        ArbitrarySceneContext scene = new([last, owner, first, player])
        {
            PlayerCharacter = player,
        };

        IReadOnlyDictionary<string, object?> result = AgenticMind.CreateRenderContext(
            owner,
            scene,
            ["char:zulu", "char:alpha"]);
        IReadOnlyDictionary<string, object?> characters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            result["characters"]);

        Assert.Equal(["char:alpha", "char:owner", "char:zulu"], characters.Keys);
        CharacterRenderView ownerView = Assert.IsType<CharacterRenderView>(result["character"]);
        Assert.Equal("char:owner", ownerView.FullId);
        Assert.Equal("char:alpha", Assert.IsType<CharacterRenderView>(characters["char:alpha"]).FullId);
        Assert.Equal("char:zulu", Assert.IsType<CharacterRenderView>(characters["char:zulu"]).FullId);
        // The owner appears in both locations referencing the exact same view instance (AI-001 TR-25).
        Assert.Same(ownerView, characters["char:owner"]);
        Assert.All(characters.Values, value => _ = Assert.IsType<CharacterRenderView>(value));
        // Observations never enter the render dictionary (AI-001 TR-25): they reach the model exclusively through
        // AI-002 tool results and interruption injections.
        Assert.False(result.ContainsKey("observations"));
        // The player is not attention-eligible here, so 'characters' omits it while the unconditional 'player' key
        // carries its own curated view.
        CharacterRenderView playerView = Assert.IsType<CharacterRenderView>(result["player"]);
        Assert.Equal("char:player", playerView.FullId);
        _ = Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, object?>)result).Add("mutation", null));
    }

    /// <summary>An owning character outside the scene snapshot is an invalid prompt context.</summary>
    [Fact]
    public void CreateRenderContext_WhenOwnerIsAbsent_FailsClearly()
    {
        FakeCharacter sceneCharacter = new()
        {
            Id = "scene_character"
        };
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        SceneContext scene = new([sceneCharacter]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AgenticMind.CreateRenderContext(owner, scene));

        Assert.Contains("absent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Arbitrary scene contexts cannot bypass AI-001 render-context identity validation.</summary>
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
        FakeCharacter owner = new()
        {
            Id = "owner",
        };
        FakeCharacter invalidSubject = new()
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
    }

    /// <summary>
    /// Valid identities from arbitrary scene-context implementations retain AI-003 render-context semantics.
    /// </summary>
    [Fact]
    public void CreateRenderContext_WithCustomSceneAndValidCharacterIdentities_BuildsContext()
    {
        FakeCharacter owner = new()
        {
            Id = "owner",
        };
        FakeCharacter subject = new()
        {
            Id = "subject",
        };
        ArbitrarySceneContext scene = new([subject, owner])
        {
            PlayerCharacter = subject,
        };

        IReadOnlyDictionary<string, object?> result = AgenticMind.CreateRenderContext(
            owner,
            scene,
            attentionEligibleFullIDs: ["char:subject"]);
        IReadOnlyDictionary<string, object?> characters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            result["characters"]);

        Assert.Equal(["char:owner", "char:subject"], characters.Keys);
        Assert.Equal("char:owner", Assert.IsType<CharacterRenderView>(result["character"]).FullId);
        Assert.Equal("char:subject", Assert.IsType<CharacterRenderView>(characters["char:subject"]).FullId);
        // The attended player's entry is reused verbatim for the unconditional player key.
        Assert.Same(characters["char:subject"], result["player"]);
    }

    /// <summary>
    /// Foreground context always aliases the owner's exact view and omits unresolved or non-character attention
    /// identities without mutating the supplied eligible set or resolving anything twice.
    /// </summary>
    [Fact]
    public void CreateRenderContext_WithAttentionEligibility_ResolvesCharactersOnlyOnceInOrdinalOrder()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        FakeCharacter alpha = new()
        {
            Id = "alpha"
        };
        FakeCharacter zulu = new()
        {
            Id = "zulu"
        };
        var nonCharacter = new FakeIdentifiable("object", "prop");
        CountingMappingSceneContext scene = new(
            [owner],
            new Dictionary<string, IIdentifiable>(StringComparer.Ordinal)
            {
                [owner.FullId] = owner,
                [alpha.FullId] = alpha,
                [zulu.FullId] = zulu,
                [nonCharacter.FullId] = nonCharacter,
            })
        {
            PlayerCharacter = alpha,
        };
        string[] eligibleIDs =
        [
            "char:zulu",
            "char:missing",
            "object:prop",
            "char:alpha",
        ];

        IReadOnlyDictionary<string, object?> result = AgenticMind.CreateRenderContext(
            owner,
            scene,
            attentionEligibleFullIDs: eligibleIDs);
        IReadOnlyDictionary<string, object?> characters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            result["characters"]);

        Assert.Equal(["char:alpha", "char:owner", "char:zulu"], characters.Keys);
        Assert.Same(result["character"], characters[owner.FullId]);
        Assert.Equal("char:alpha", Assert.IsType<CharacterRenderView>(characters[alpha.FullId]).FullId);
        Assert.Equal("char:zulu", Assert.IsType<CharacterRenderView>(characters[zulu.FullId]).FullId);
        Assert.Same(result["player"], characters[alpha.FullId]);
        Assert.Equal(new[] { "char:zulu", "char:missing", "object:prop", "char:alpha" }, eligibleIDs);
        // Exactly one scene lookup per identity (one presence check plus four eligible IDs): no second visual
        // survey and no hidden subject cache sit behind assembly (AI-006 TR-40, AI-001 AC-T18).
        Assert.Equal(5, scene.FindCallCount);
    }

    /// <summary>Attention including the owner keeps one shared view instance across both context locations.</summary>
    [Fact]
    public void CreateRenderContext_WhenAttentionIncludesTheOwner_PreservesSameInstanceInBothLocations()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        FakeCharacter alpha = new()
        {
            Id = "alpha"
        };
        ArbitrarySceneContext scene = new([owner, alpha])
        {
            PlayerCharacter = alpha,
        };

        IReadOnlyDictionary<string, object?> result = AgenticMind.CreateRenderContext(
            owner,
            scene,
            attentionEligibleFullIDs: ["char:owner"]);
        IReadOnlyDictionary<string, object?> characters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            result["characters"]);

        Assert.Equal(["char:owner"], characters.Keys);
        Assert.Same(result["character"], characters[owner.FullId]);
    }

    /// <summary>Two distinct characters resolving to one exact FullId fail context assembly clearly.</summary>
    [Fact]
    public void CreateRenderContext_WhenDistinctInstancesShareAnExactFullId_FailsClearly()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        FactorySceneContext scene = new(owner);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AgenticMind.CreateRenderContext(owner, scene, ["char:twin", "char:twin"]));

        Assert.Contains("duplicate exact FullId 'char:twin'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Prompt build context exposes its required owning character without validating optional subsystem-specific identity.
    /// </summary>
    [Fact]
    public void PromptSectionBuildContext_ExposesOwningCharacterWithEmptyID()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        SceneContext scene = new([]);
        FakeCharacter character = new()
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

        public ValueTask<string> RenderAsync(IReadOnlyDictionary<string, object?> context)
        {
            ReceivedContext = context;
            return ValueTask.FromResult($"Hello {context["displayName"]}");
        }
    }

    private sealed class FakeCharacter : ICharacter
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
    }

    private sealed record ArbitrarySceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter? PlayerCharacter
        {
            get; init;
        }

        public AlleyCat.Core.Content.ContentContext Content => AlleyCat.Core.Content.ContentContext.Default;

        public ICharacter Player => PlayerCharacter
            ?? throw new InvalidOperationException(
                "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public IIdentifiable? Find(string fullId)
        {
            IdentityValidator.ValidateFullId(fullId, nameof(fullId));
            return Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));
        }

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"Current scene does not contain identifiable object '{fullId}'.");
    }

    private sealed class CountingMappingSceneContext(
        IReadOnlyCollection<ICharacter> characters,
        IReadOnlyDictionary<string, IIdentifiable> mappings) : ISceneContext
    {
        public ICharacter? PlayerCharacter
        {
            get; init;
        }

        public int FindCallCount
        {
            get; private set;
        }

        public IReadOnlyCollection<ICharacter> Characters => characters;

        public AlleyCat.Core.Content.ContentContext Content => AlleyCat.Core.Content.ContentContext.Default;

        public ICharacter Player => PlayerCharacter
            ?? throw new InvalidOperationException(
                "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public IIdentifiable? Find(string fullId)
        {
            IdentityValidator.ValidateFullId(fullId, nameof(fullId));
            FindCallCount++;
            return mappings.GetValueOrDefault(fullId);
        }

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"Current scene does not contain identifiable object '{fullId}'.");
    }

    /// <summary>
    /// Yields a freshly created character per <c>char:twin</c> lookup so two resolutions of one exact FullId return
    /// genuinely distinct instances.
    /// </summary>
    private sealed class FactorySceneContext(FakeCharacter owner) : ISceneContext
    {
        public IReadOnlyCollection<ICharacter> Characters => [owner];

        public AlleyCat.Core.Content.ContentContext Content => AlleyCat.Core.Content.ContentContext.Default;

        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public IIdentifiable? Find(string fullId)
        {
            IdentityValidator.ValidateFullId(fullId, nameof(fullId));
            return string.Equals(fullId, owner.FullId, StringComparison.Ordinal)
                ? owner
                : string.Equals(fullId, "char:twin", StringComparison.Ordinal)
                    ? new FakeCharacter
                    {
                        Id = "twin",
                    }
                    : null;
        }

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"Current scene does not contain identifiable object '{fullId}'.");
    }

    private sealed class FakeIdentifiable(string type, string id) : IIdentifiable
    {
        public string Type { get; set; } = type;

        public string Id { get; set; } = id;

        public string FullId => $"{Type}:{Id}";
    }
}
