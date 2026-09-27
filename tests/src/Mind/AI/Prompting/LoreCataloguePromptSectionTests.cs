using System.Runtime.CompilerServices;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Prompting;

/// <summary>
/// Unit coverage for the catalogue prompt section's shared automatic-selection exclusion: the section runs the
/// essential-world and scene-character sections' actual selection queries and excludes exactly their selected entry
/// IDs, never a duplicated rule or whole category (AI-004 requirement 44).
/// </summary>
public sealed class LoreCataloguePromptSectionTests
{
    /// <summary>
    /// Remaining entries exclude exactly the automatically selected entry IDs — never whole categories — and render
    /// through the catalogue formatter in group order.
    /// </summary>
    [Fact]
    public async Task GetContentAsync_ExcludesExactlyTheAutomaticallySelectedEntryIDs()
    {
        RecordingLoreQueryService queryService = new(
            essentialEntries: [new LoreEntry("auto.essential", "Essential Body", "Body.")],
            sceneCharacterEntries: [new LoreEntry("auto.character", "Character Body", "Body.", Kind: LoreSubjectKind.Character)],
            catalogueEntries:
            [
                new LoreEntry("auto.essential", "Essential Body", "Body.", Description: "Essential description."),
                new LoreEntry("auto.character", "Character Body", "Body.", Kind: LoreSubjectKind.Character),
                new LoreEntry("rem.world", "Remaining World", "Body."),
                new LoreEntry("rem.character", "Remaining Character", "Body.", Kind: LoreSubjectKind.Character),
                new LoreEntry("rem.location", "Remaining Location", "Body.", Kind: LoreSubjectKind.Location),
            ]);
        FakeCharacter owner = new("owner");
        ISceneContext scene = new FakeSceneContext([new FakeCharacter("zulu"), owner, new FakeCharacter("ally")]);
        var buildContext = new PromptSectionBuildContext(new ServiceProvider(queryService), scene, owner);
        LoreCataloguePromptSection section = CreateSectionWithoutGodotRuntime();

        string content = await section.GetContentAsync(buildContext);

        Assert.Equal(
            "# World\n\n- `rem.world` — Remaining World\n\n"
            + "# Characters\n\n- `rem.character` — Remaining Character\n\n"
            + "# Locations\n\n- `rem.location` — Remaining Location",
            content);
        Assert.DoesNotContain("Essential Body", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Character Body", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exclusion queries are the shared sections' actual selection queries: the essential world query and the
    /// scene-character query with the owner first and remaining FullIds in ordinal order, all for the owning
    /// observer and the scene's content context (AI-004 requirement 44).
    /// </summary>
    [Fact]
    public async Task GetContentAsync_UsesTheSharedAutomaticSelectionQueries()
    {
        RecordingLoreQueryService queryService = new([], [], []);
        FakeCharacter owner = new("owner");
        ISceneContext scene = new FakeSceneContext([new FakeCharacter("zulu"), owner, new FakeCharacter("ally")]);
        var buildContext = new PromptSectionBuildContext(new ServiceProvider(queryService), scene, owner);
        LoreCataloguePromptSection section = CreateSectionWithoutGodotRuntime();

        _ = await section.GetContentAsync(buildContext);

        Assert.Equal(2, queryService.ContextualQueries.Count);
        LoreQuery essential = queryService.ContextualQueries[0];
        Assert.Equal("char:owner", essential.ObserverID);
        LoreSubjectRequest essentialSubject = Assert.Single(essential.Subjects);
        Assert.Equal(LoreSubjectKind.World, essentialSubject.Kind);
        Assert.Null(essentialSubject.SubjectID);
        LoreQuery sceneCharacters = queryService.ContextualQueries[1];
        Assert.Equal("char:owner", sceneCharacters.ObserverID);
        Assert.Equal(
            ["char:owner", "char:ally", "char:zulu"],
            sceneCharacters.Subjects.Select(subject => subject.SubjectID));
        LoreCatalogueQuery catalogueQuery = Assert.Single(queryService.CatalogueQueries);
        Assert.Equal("char:owner", catalogueQuery.ObserverID);
        Assert.Same(scene.Content, queryService.Content);
    }

    /// <summary>A catalogue whose every entry is automatically injected renders coherent empty content.</summary>
    [Fact]
    public async Task GetContentAsync_WithNoRemainingEntries_ReturnsEmptyContent()
    {
        RecordingLoreQueryService queryService = new(
            essentialEntries: [new LoreEntry("auto.only", "Only Body", "Body.")],
            sceneCharacterEntries: [],
            catalogueEntries: [new LoreEntry("auto.only", "Only Body", "Body.")]);
        FakeCharacter owner = new("owner");
        ISceneContext scene = new FakeSceneContext([owner]);
        var buildContext = new PromptSectionBuildContext(new ServiceProvider(queryService), scene, owner);
        LoreCataloguePromptSection section = CreateSectionWithoutGodotRuntime();

        string content = await section.GetContentAsync(buildContext);

        Assert.Equal(string.Empty, content);
    }

    /// <summary>The catalogue reuses the shared identity validation and reports an unnamed observer at its boundary.</summary>
    [Fact]
    public async Task GetContentAsync_WhenObserverIDIsEmpty_FailsClearlyAtUsageBoundary()
    {
        RecordingLoreQueryService queryService = new([], [], []);
        FakeCharacter owner = new("owner", fullIdOverride: string.Empty);
        ISceneContext scene = new FakeSceneContext([owner]);
        var buildContext = new PromptSectionBuildContext(new ServiceProvider(queryService), scene, owner);
        LoreCataloguePromptSection section = CreateSectionWithoutGodotRuntime();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => section.GetContentAsync(buildContext));

        Assert.Contains("LoreCataloguePromptSection", exception.Message, StringComparison.Ordinal);
        Assert.Contains("observer ID", exception.Message, StringComparison.Ordinal);
        _ = Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Empty(queryService.ContextualQueries);
        Assert.Empty(queryService.CatalogueQueries);
    }

    /// <summary>The catalogue inherits the shared scene-membership validation rather than its own duplicated rule.</summary>
    [Fact]
    public async Task GetContentAsync_WhenOwnerIsAbsentFromScene_FailsClearly()
    {
        RecordingLoreQueryService queryService = new([], [], []);
        FakeCharacter owner = new("owner");
        ISceneContext scene = new FakeSceneContext([new FakeCharacter("other")]);
        var buildContext = new PromptSectionBuildContext(new ServiceProvider(queryService), scene, owner);
        LoreCataloguePromptSection section = CreateSectionWithoutGodotRuntime();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => section.GetContentAsync(buildContext));

        Assert.Contains("present in the scene context", exception.Message, StringComparison.Ordinal);
        Assert.Contains("LoreCataloguePromptSection", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Cancellation propagates through every catalogue read without partial output (AI-004 requirement 49).</summary>
    [Fact]
    public async Task GetContentAsync_WithCancelledToken_ThrowsWithoutContent()
    {
        RecordingLoreQueryService queryService = new([], [], []);
        FakeCharacter owner = new("owner");
        ISceneContext scene = new FakeSceneContext([owner]);
        var buildContext = new PromptSectionBuildContext(new ServiceProvider(queryService), scene, owner);
        LoreCataloguePromptSection section = CreateSectionWithoutGodotRuntime();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => section.GetContentAsync(buildContext, cancellation.Token));

        Assert.Empty(queryService.CatalogueQueries);
    }

    private static LoreCataloguePromptSection CreateSectionWithoutGodotRuntime()
        => (LoreCataloguePromptSection)RuntimeHelpers.GetUninitializedObject(typeof(LoreCataloguePromptSection));

    private sealed class ServiceProvider(ILoreQueryService queryService) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ILoreQueryService)
            ? queryService
            : serviceType == typeof(ILoreCataloguePromptFormatter)
                ? new MarkdownLoreCataloguePromptFormatter()
                : null;
    }

    private sealed class RecordingLoreQueryService(
        IReadOnlyList<LoreEntry> essentialEntries,
        IReadOnlyList<LoreEntry> sceneCharacterEntries,
        IReadOnlyList<LoreEntry> catalogueEntries) : ILoreQueryService
    {
        public List<LoreQuery> ContextualQueries
        {
            get;
        } = [];

        public List<LoreCatalogueQuery> CatalogueQueries
        {
            get;
        } = [];

        public ContentContext? Content
        {
            get;
            private set;
        }

        public Task<IReadOnlyList<LoreEntry>> QueryAsync(
            ContentContext content,
            LoreQuery query,
            CancellationToken cancellationToken = default)
        {
            ContextualQueries.Add(query);
            Content = content;
            cancellationToken.ThrowIfCancellationRequested();
            bool essentialWorld = query.Subjects.Count == 1
                && query.Subjects[0].Kind == LoreSubjectKind.World
                && query.Subjects[0].SubjectID is null;
            return Task.FromResult(essentialWorld ? essentialEntries : sceneCharacterEntries);
        }

        public Task<IReadOnlyList<LoreEntry>> QueryCatalogueAsync(
            ContentContext content,
            LoreCatalogueQuery query,
            CancellationToken cancellationToken = default)
        {
            CatalogueQueries.Add(query);
            Content = content;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(catalogueEntries);
        }

        public Task<IReadOnlyList<LoreEntryLookup>> QueryEntriesAsync(
            ContentContext content,
            LoreEntryIDQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The catalogue-section stub does not serve entry-ID queries.");
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

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
