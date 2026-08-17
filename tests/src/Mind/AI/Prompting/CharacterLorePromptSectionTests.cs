using System.Runtime.CompilerServices;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Prompting;

/// <summary>
/// Unit coverage for character identity validation before runtime lore queries.
/// </summary>
public sealed class CharacterLorePromptSectionTests
{
    /// <summary>
    /// Custom scene contexts cannot supply canonical-looking FullIds that disagree with character identity parts.
    /// </summary>
    [Fact]
    public async Task GetContentAsync_WithCustomSceneAndInconsistentCharacterFullId_FailsBeforeQuerying()
    {
        var owner = new FakeCharacter("owner");
        var subject = new FakeCharacter("subject", fullIdOverride: "char:other_subject");
        ISceneContext scene = new FakeSceneContext([owner, subject]);
        var buildContext = new PromptSectionBuildContext(new EmptyServiceProvider(), scene, owner);
        CharacterLorePromptSection section = CreateSectionWithoutGodotRuntime();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => section.GetContentAsync(buildContext));

        Assert.Contains("matching canonical character Type, ID, and FullId", exception.Message);
        ArgumentException innerException = Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Equal("character", innerException.ParamName);
        Assert.Contains("exactly match", innerException.Message);
    }

    /// <summary>Valid custom scene-context identities retain lore-section validation compatibility.</summary>
    [Fact]
    public async Task GetContentAsync_WithCustomSceneAndValidCharacterFullIds_QueriesCanonicalIdentities()
    {
        var owner = new FakeCharacter("owner");
        var subject = new FakeCharacter("subject");
        ISceneContext scene = new FakeSceneContext([owner, subject]);
        var queryService = new CapturingLoreQueryService();
        var formatter = new TestLorePromptFormatter();
        var services = new ServiceProvider(queryService, formatter);
        var buildContext = new PromptSectionBuildContext(services, scene, owner);
        CharacterLorePromptSection section = CreateSectionWithoutGodotRuntime();

        string content = await section.GetContentAsync(buildContext);

        Assert.Equal("formatted", content);
        LoreQuery query = Assert.IsType<LoreQuery>(queryService.Query);
        Assert.Equal("char:owner", query.ObserverID);
        Assert.Equal(["char:owner", "char:subject"], query.Subjects.Select(subjectRequest => subjectRequest.SubjectID));
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static CharacterLorePromptSection CreateSectionWithoutGodotRuntime()
        => (CharacterLorePromptSection)RuntimeHelpers.GetUninitializedObject(typeof(CharacterLorePromptSection));

    private sealed class ServiceProvider(ILoreQueryService queryService, ILorePromptFormatter formatter) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ILoreQueryService)
            ? queryService
            : serviceType == typeof(ILorePromptFormatter)
                ? formatter
                : null;
    }

    private sealed class CapturingLoreQueryService : ILoreQueryService
    {
        public LoreQuery? Query
        {
            get; private set;
        }

        public Task<IReadOnlyList<LoreEntry>> QueryAsync(
            ContentContext content,
            LoreQuery query,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            return Task.FromResult<IReadOnlyList<LoreEntry>>([]);
        }
    }

    private sealed class TestLorePromptFormatter : ILorePromptFormatter
    {
        public string Format(IReadOnlyList<LoreEntry> entries) => "formatted";
    }

    private sealed record FakeSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
        {
            IdentityValidator.ValidateFullId(fullId, nameof(fullId));
            return Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));
        }

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"Current scene does not contain identifiable object '{fullId}'.");
    }

    private sealed class FakeCharacter(string id, string? fullIdOverride = null) : ICharacter
    {
        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => fullIdOverride ?? $"{Type}:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }
}
