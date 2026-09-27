using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Lore;

/// <summary>
/// Godot-runtime coverage for AI-004 discovery and entry-ID retrieval: observer-scoped catalogues, exact-ID
/// batch lookups, scoped entry-ID uniqueness, and scope isolation for the Markdown lore backend.
/// </summary>
[Headless]
public sealed class LoreDiscoveryIntegrationTests
{
    private static readonly ContentContext _mainFixture = new("lore-query-fixture", "res://tests/lore-query-fixture");

    private static readonly ContentContext _scopeFixture = new("lore-scope-fixture", "res://tests/lore-scope-fixture");

    /// <summary>Canonical wiki pages inside the fixture's lore root, mirroring <c>game/lore/wiki/</c>.</summary>
    private const string CanonicalWikiWorldRoot = "res://tests/lore-query-fixture/lore/wiki/world/";

    /// <summary>
    /// Guards that the named canonical page genuinely sits inside the fixture lore root, so no-fallback
    /// assertions fail loudly instead of passing vacuously when the canonical fixture is misplaced.
    /// </summary>
    private static void AssertCanonicalWorldPageExists(string fileName)
    {
        using var file = Godot.FileAccess.Open(
            CanonicalWikiWorldRoot + fileName,
            Godot.FileAccess.ModeFlags.Read);
        Assert.NotNull(file);
    }

    /// <summary>
    /// The catalogue lists every runtime-eligible entry across the world, character, and location collections,
    /// including nested subdirectory pages, in World, Characters, Locations group order with priority-then-ID
    /// ordering inside each group (AI-004 requirements 44 and 45; prompt-side automatic-injection exclusion is
    /// not a backend concern).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_ListsAllRuntimeEligibleEntriesAcrossCategoriesInDeterministicOrder()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> entries = await service.QueryCatalogueAsync(_mainFixture, new LoreCatalogueQuery("char:test"));

        Assert.Equal(
            [
                "test.nonessential",
                "test.authoring_material",
                "test.nested_note",
                "test.stable",
                "test.shared",
                "test.ally",
                "test.vadim_self",
                "test.interrogation_room",
            ],
            entries.Select(entry => entry.ID));
        Assert.Equal(
            [
                LoreSubjectKind.World,
                LoreSubjectKind.World,
                LoreSubjectKind.World,
                LoreSubjectKind.World,
                LoreSubjectKind.World,
                LoreSubjectKind.Character,
                LoreSubjectKind.Character,
                LoreSubjectKind.Location,
            ],
            entries.Select(entry => entry.Kind));
        Assert.Equal(
            ["char:ally", "char:vadim", "loc:interrogation_room"],
            entries.Select(entry => entry.SubjectID).Where(subjectID => subjectID is not null));
    }

    /// <summary>
    /// Optional descriptions map from single-line frontmatter metadata: populated values carry through while
    /// absent and blank values stay <c>null</c>, with no body-excerpt fallback (AI-004 requirement 42).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_MapsOptionalDescriptionsWithoutFallback()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> entries = await service.QueryCatalogueAsync(_mainFixture, new LoreCatalogueQuery("char:test"));

        Assert.Equal("Riverside routes I keep to myself.", entries.Single(entry => entry.ID == "test.shared").Description);
        Assert.Equal(
            "What I think of the outsider.",
            entries.Single(entry => entry.ID == "test.ally").Description);
        Assert.Equal(
            "The room where they ask their questions.",
            entries.Single(entry => entry.ID == "test.interrogation_room").Description);
        Assert.Null(entries.Single(entry => entry.ID == "test.vadim_self").Description);
        Assert.Null(entries.Single(entry => entry.ID == "test.authoring_material").Description);
        // 'description: ""' on the page is a blank value and therefore no description.
        Assert.Null(entries.Single(entry => entry.ID == "test.nonessential").Description);
    }

    /// <summary>
    /// Authoring-time pages stay out of the catalogue and canonical wiki pages never join it, so a canonical
    /// page sharing a perspective entry's ID neither doubles the listing nor substitutes its body (AI-004
    /// requirements 17 and 43). The canonical pages genuinely sit inside the lore root at
    /// <c>lore/wiki/world/</c>, mirroring the production canonical wiki layout.
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_ExcludesAuthoringTimeAndCanonicalPagesWithoutCanonicalFallback()
    {
        AssertCanonicalWorldPageExists("canonical_only.md");
        AssertCanonicalWorldPageExists("stable.md");
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> entries = await service.QueryCatalogueAsync(_mainFixture, new LoreCatalogueQuery("char:test"));

        LoreEntry stable = Assert.Single(entries, entry => entry.ID == "test.stable");
        Assert.Equal("Stable ID entry.", stable.Body);
        Assert.DoesNotContain(entries, entry => entry.ID is "test.canonical_only" or "test.unterminated");
        Assert.DoesNotContain(
            entries,
            entry => entry.Body.Contains("Canonical authoring page", StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries,
            entry => entry.Body.Contains("source-path fallback", StringComparison.Ordinal));
    }

    /// <summary>
    /// Catalogue scope is one observer and one content root: another observer's entries and another root's
    /// entries stay invisible, while reusing the same entry ID across observers or roots stays valid (AI-004
    /// requirement 43).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_IsolatesScopesAndPermitsCrossScopeIDReuse()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> otherObserver = await service.QueryCatalogueAsync(
            _mainFixture,
            new LoreCatalogueQuery("char:other"));
        IReadOnlyList<LoreEntry> otherRoot = await service.QueryCatalogueAsync(_scopeFixture, new LoreCatalogueQuery("char:test"));

        Assert.Equal(["test.shared"], otherObserver.Select(entry => entry.ID));
        Assert.Contains("Another observer knows these riverside routes too.", otherObserver[0].Body, StringComparison.Ordinal);
        Assert.Equal(
            ["scope.isolated", "test.shared"],
            otherRoot.Select(entry => entry.ID));
        Assert.Contains("A different content root carries this entry ID", otherRoot[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(otherRoot, entry => entry.ID is "test.stable" or "test.ally");
    }

    /// <summary>
    /// An absent perspective is absent knowledge: the catalogue is empty and never falls back to canonical
    /// pages inside the lore root (AI-004 requirement 17).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_WhenPerspectiveIsAbsent_ReturnsEmptyWithoutCanonicalFallback()
    {
        AssertCanonicalWorldPageExists("canonical_only.md");
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> entries = await service.QueryCatalogueAsync(
            _mainFixture,
            new LoreCatalogueQuery("char:observer_without_perspective"));

        Assert.Empty(entries);
    }

    /// <summary>
    /// Duplicate entry IDs across collections within one observer/content scope fail the catalogue query with
    /// diagnostics naming the conflicting sources, which are internal only (AI-004 requirement 43).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_RejectsDuplicateScopedEntryIDsWithInternalDiagnostics()
    {
        MarkdownLoreQueryService service = new();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.QueryCatalogueAsync(_mainFixture, new LoreCatalogueQuery("char:conflicted")));

        Assert.Contains("conflicted.entry", exception.Message, StringComparison.Ordinal);
        Assert.Contains("world/duplicate.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("characters/duplicate.md", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exact-ID batches return one result per distinct ID in first-request order: found entries carry their
    /// category and subject metadata, unknown IDs are explicitly unavailable, and case-folded, padded, or
    /// canonical-only IDs never match (AI-004 requirements 43, 47, and 48).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_ReturnsFirstRequestOrderWithExactMatchesAndUnavailableStatuses()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntryLookup> lookups = await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery(
                "char:test",
                ["test.ally", "test.missing", "test.stable", "test.ally", "TEST.STABLE", " test.stable", "test.canonical_only"]));

        Assert.Equal(
            ["test.ally", "test.missing", "test.stable", "TEST.STABLE", " test.stable", "test.canonical_only"],
            lookups.Select(lookup => lookup.RequestedID));

        LoreEntryLookup ally = lookups[0];
        Assert.True(ally.Found);
        Assert.Equal(LoreSubjectKind.Character, ally.Entry!.Kind);
        Assert.Equal("char:ally", ally.Entry.SubjectID);
        Assert.Contains("char:ally", ally.Entry.Body, StringComparison.Ordinal);

        Assert.False(lookups[1].Found);
        Assert.Null(lookups[1].Entry);

        // 'test.stable' is essential world lore, so automatically injected entries stay retrievable by ID.
        LoreEntryLookup stable = lookups[2];
        Assert.True(stable.Found);
        Assert.Equal("Stable ID entry.", stable.Entry!.Body);

        Assert.False(lookups[3].Found);
        Assert.False(lookups[4].Found);
        Assert.False(lookups[5].Found);
        Assert.DoesNotContain(
            lookups,
            lookup => lookup.Entry?.Body.Contains("Canonical authoring page", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Duplicate scoped entry IDs fail the whole entry-ID query rather than returning a partial batch (AI-004
    /// requirement 43).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_RejectsDuplicateScopedEntryIDs()
    {
        MarkdownLoreQueryService service = new();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.QueryEntriesAsync(_mainFixture, new LoreEntryIDQuery("char:conflicted", ["conflicted.entry"])));

        Assert.Contains("conflicted.entry", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// IDs valid in one scope stay unavailable in another without revealing the other scope's entry (AI-004
    /// requirement 48).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_MarksScopeForeignIDsUnavailable()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntryLookup> lookups = await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery("char:other", ["test.shared", "scope.isolated"]));

        LoreEntryLookup shared = lookups[0];
        Assert.True(shared.Found);
        Assert.Contains("Another observer knows these riverside routes too.", shared.Entry!.Body, StringComparison.Ordinal);
        Assert.False(lookups[1].Found);
    }

    /// <summary>
    /// Cancellation observed during the final location read — after earlier reads completed and before query
    /// completion — cancels the whole catalogue query instead of returning a successful result (AI-004
    /// requirement 49).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_WithCancellationDuringFinalRead_CancelsInsteadOfSucceeding()
        => await AssertCancellationDuringFinalReadCancelsInsteadOfSucceeding(
            (service, cancellation) => service.QueryCatalogueAsync(
                _mainFixture,
                new LoreCatalogueQuery("char:test"),
                cancellation.Token));

    /// <summary>
    /// Cancellation observed during the final location read — after earlier reads completed and before query
    /// completion — cancels the whole entry-ID query instead of returning a successful lookup batch (AI-004
    /// requirement 49).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_WithCancellationDuringFinalRead_CancelsInsteadOfSucceeding()
        => await AssertCancellationDuringFinalReadCancelsInsteadOfSucceeding(
            (service, cancellation) => service.QueryEntriesAsync(
                _mainFixture,
                new LoreEntryIDQuery("char:test", ["test.ally"]),
                cancellation.Token));

    /// <summary>
    /// Shared red/green harness for AI-004 requirement 49: cancellation is triggered inside the final location
    /// read (locations are the last scope collection read) through the read-observer instrumentation seam, and
    /// the query must observe it before completing successfully.
    /// </summary>
    private static async Task AssertCancellationDuringFinalReadCancelsInsteadOfSucceeding(
        Func<MarkdownLoreQueryService, CancellationTokenSource, Task> query)
    {
        const string finalReadPath =
            "res://tests/lore-query-fixture/lore/perspectives/char/test/locations/interrogation_room.md";
        List<string> readPaths = [];
        using CancellationTokenSource cancellation = new();
        MarkdownLoreQueryService service = new()
        {
            FileReadObserver = path =>
            {
                readPaths.Add(path);
                if (path == finalReadPath)
                {
                    cancellation.Cancel();
                }
            },
        };

        Exception? thrown = await Record.ExceptionAsync(() => query(service, cancellation));

        // The cancellation genuinely landed inside the final location read, after the scope's earlier reads.
        Assert.True(cancellation.IsCancellationRequested);
        Assert.NotEmpty(readPaths);
        Assert.Equal(finalReadPath, readPaths[^1]);
        Assert.True(
            thrown is OperationCanceledException,
            $"Cancellation during the final read must cancel the query, but it completed with "
            + $"{(thrown is null ? "a successful result" : thrown.GetType().Name)} after {readPaths.Count} reads.");
    }
}
