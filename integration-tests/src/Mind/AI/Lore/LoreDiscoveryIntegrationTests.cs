using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Lore;

/// <summary>
/// Godot-runtime coverage for AI-004 discovery and canonical-ID retrieval: observer-scoped catalogues,
/// canonical-ID batch lookups through the type→directory map with hyphen/underscore equivalence, dot-handle
/// hints, unsupported-type unavailability, per-observer resolution, scoped authoring-identity uniqueness,
/// segment-collision validation, layered both-bases reads, and scope isolation for the Markdown lore backend.
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
    /// The catalogue lists every runtime-eligible entry across the world, character, and location collections —
    /// including nested subdirectory pages and title-bearing id-less pages — in World, Characters, Locations
    /// group order with priority-then-authoring-identity ordering inside each group, each row carrying its
    /// canonical full ID (AI-004 requirements 44 and 45; prompt-side automatic-injection exclusion is not a
    /// backend concern).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_ListsAllRuntimeEligibleEntriesAcrossCategoriesInDeterministicOrder()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> entries = await service.QueryCatalogueAsync(_mainFixture, new LoreCatalogueQuery("char:test"));

        Assert.Equal(
            [
                "world:nonessential",
                "world:with-id-authoring-material",
                "world:notes/nested",
                "world:with-id",
                "world:without-id-a",
                "world:without-id-b",
                "world:shared",
                "char:ally",
                "char:vadim",
                "loc:interrogation_room",
            ],
            entries.Select(entry => entry.ID));
        Assert.Equal(
            [
                LoreSubjectKind.World,
                LoreSubjectKind.World,
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

        Assert.Equal("Riverside routes I keep to myself.", entries.Single(entry => entry.ID == "world:shared").Description);
        Assert.Equal(
            "What I think of the outsider.",
            entries.Single(entry => entry.ID == "char:ally").Description);
        Assert.Equal(
            "The room where they ask their questions.",
            entries.Single(entry => entry.ID == "loc:interrogation_room").Description);
        Assert.Null(entries.Single(entry => entry.ID == "char:vadim").Description);
        Assert.Null(entries.Single(entry => entry.ID == "world:with-id-authoring-material").Description);
        // 'description: ""' on the page is a blank value and therefore no description.
        Assert.Null(entries.Single(entry => entry.ID == "world:nonessential").Description);
    }

    /// <summary>
    /// Authoring-time pages stay out of the catalogue and canonical wiki pages never join it, so a canonical
    /// page sharing a perspective entry's subject neither doubles the listing nor substitutes its body (AI-004
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

        LoreEntry stable = Assert.Single(entries, entry => entry.ID == "world:with-id");
        Assert.Equal("Stable ID entry.", stable.Body);
        Assert.DoesNotContain(entries, entry => entry.ID is "world:canonical-only" or "world:unterminated");
        Assert.DoesNotContain(
            entries,
            entry => entry.Body.Contains("Canonical authoring page", StringComparison.Ordinal));
    }

    /// <summary>
    /// Catalogue scope is one observer and one content root: another observer's entries and another root's
    /// entries stay invisible, while reusing the same canonical ID across observers or roots stays valid
    /// (AI-004 requirement 43).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_IsolatesScopesAndPermitsCrossScopeIDReuse()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> otherObserver = await service.QueryCatalogueAsync(
            _mainFixture,
            new LoreCatalogueQuery("char:other"));
        IReadOnlyList<LoreEntry> otherRoot = await service.QueryCatalogueAsync(_scopeFixture, new LoreCatalogueQuery("char:test"));

        Assert.Equal(["world:shared"], otherObserver.Select(entry => entry.ID));
        Assert.Contains("Another observer knows these riverside routes too.", otherObserver[0].Body, StringComparison.Ordinal);
        Assert.Equal(
            ["world:isolated", "world:shared"],
            otherRoot.Select(entry => entry.ID));
        Assert.Contains("A different content root carries this entry ID", otherRoot[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(otherRoot, entry => entry.ID is "world:with-id" or "char:ally");
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
    /// Duplicate authoring identities across collections within one lore-base scope fail the catalogue query
    /// with diagnostics naming the conflicting sources, which are internal only (AI-004 requirement 43).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_RejectsDuplicateScopedAuthoringIdentitiesWithInternalDiagnostics()
    {
        MarkdownLoreQueryService service = new();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.QueryCatalogueAsync(_mainFixture, new LoreCatalogueQuery("char:conflicted")));

        Assert.Contains("conflicted.entry", exception.Message, StringComparison.Ordinal);
        Assert.Contains("world/duplicate.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("characters/duplicate.md", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two runtime-eligible files in one directory whose stems collide under the hyphen/underscore equivalence
    /// fail load validation, keeping one canonical ID mapped to at most one file per base (AI-004 requirement
    /// 43; acceptance 41).
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_RejectsCollidingHyphenUnderscoreStemsInOneDirectory()
    {
        MarkdownLoreQueryService service = new();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.QueryCatalogueAsync(_mainFixture, new LoreCatalogueQuery("char:colliding")));

        Assert.Contains("dupe-name.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dupe_name.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("hyphen/underscore", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Canonical IDs resolve through the type→directory map and segment path with hyphen/underscore
    /// equivalence — <c>loc:interrogation-room</c> and <c>loc:interrogation_room</c> both address the same
    /// entry — while case-folded or padded variants never match, multi-segment <c>world:</c> paths resolve,
    /// and results keep first-request order with one result per distinct ID (AI-004 requirements 43, 47, and
    /// 48).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_ResolvesThroughTypeMapAndSegmentEquivalence()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntryLookup> lookups = await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery(
                "char:test",
                [
                    "loc:interrogation_room",
                    "char:ally",
                    "world:missing",
                    "world:with-id",
                    "loc:interrogation_room",
                    "LOC:INTERROGATION_ROOM",
                    " world:with-id",
                    "world:notes/nested",
                ]));

        Assert.Equal(
            [
                "loc:interrogation_room",
                "char:ally",
                "world:missing",
                "world:with-id",
                "LOC:INTERROGATION_ROOM",
                " world:with-id",
                "world:notes/nested",
            ],
            lookups.Select(lookup => lookup.RequestedID));

        LoreEntryLookup room = lookups[0];
        Assert.True(room.Found);
        Assert.Equal(LoreSubjectKind.Location, room.Entry!.Kind);
        Assert.Equal("loc:interrogation_room", room.Entry.SubjectID);
        Assert.Contains("sits below the charter office", room.Entry.Body, StringComparison.Ordinal);

        LoreEntryLookup ally = lookups[1];
        Assert.True(ally.Found);
        Assert.Equal("char:ally", ally.Entry!.SubjectID);

        Assert.False(lookups[2].Found);
        Assert.Null(lookups[2].HintCanonicalID);

        // 'world:with-id' is essential world lore, so automatically injected entries stay retrievable.
        LoreEntryLookup stable = lookups[3];
        Assert.True(stable.Found);
        Assert.Equal("Stable ID entry.", stable.Entry!.Body);

        Assert.False(lookups[4].Found);
        Assert.False(lookups[5].Found);

        LoreEntryLookup nested = lookups[6];
        Assert.True(nested.Found);
        Assert.Equal("Kept prose from a nested subdirectory page.", nested.Entry!.Body);

        // The hyphenated spelling addresses the same file as the underscore spelling.
        IReadOnlyList<LoreEntryLookup> hyphenated = await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery("char:test", ["loc:interrogation-room"]));
        Assert.True(hyphenated[0].Found);
        Assert.Equal(room.Entry.ID, hyphenated[0].Entry!.ID);
    }

    /// <summary>
    /// Authoring dot-handle ids, unsupported <c>scenario:</c> types, wiki-only canonical IDs, and malformed
    /// strings are all unavailable without revealing whether another scope contains them; a request that equals
    /// an in-scope entry's authored dot-handle id appends a hint naming the canonical form (AI-004 requirement
    /// 48; acceptance 42).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_MarksUnsupportedAndDotHandleRequestsUnavailableWithCanonicalHint()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntryLookup> lookups = await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery(
                "char:test",
                ["scenario:interrogation", "test.ally", "test.stable", "world:canonical-only", "not-a-canonical-id", "item:torch"]));

        Assert.All(lookups, lookup => Assert.False(lookup.Found));

        Assert.Null(lookups[0].HintCanonicalID);
        Assert.Equal("char:ally", lookups[1].HintCanonicalID);
        Assert.Equal("world:with-id", lookups[2].HintCanonicalID);
        Assert.Null(lookups[3].HintCanonicalID);
        Assert.Null(lookups[4].HintCanonicalID);
        Assert.Null(lookups[5].HintCanonicalID);
    }

    /// <summary>
    /// Canonical addressing resolves per observer: the same canonical ID retrieves an entry for one observer
    /// and returns unavailable for another (AI-004 requirement 43; acceptance 42).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_ResolvesCanonicalIDsPerObserver()
    {
        MarkdownLoreQueryService service = new();

        LoreEntryLookup forTest = (await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery("char:test", ["world:shared"])))[0];
        LoreEntryLookup forOther = (await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery("char:other", ["world:shared"])))[0];
        LoreEntryLookup foreignToOther = (await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery("char:other", ["char:ally"])))[0];

        Assert.True(forTest.Found);
        Assert.Contains("Riverside routes I keep to myself.", forTest.Entry!.Body, StringComparison.Ordinal);
        Assert.True(forOther.Found);
        Assert.Contains("Another observer knows these riverside routes too.", forOther.Entry!.Body, StringComparison.Ordinal);
        Assert.False(foreignToOther.Found);
    }

    /// <summary>
    /// The service layer reads both bases (AI-004 requirement 52; acceptance 43): the canonical wiki serves
    /// no-observer canonical reads — by full canonical ID and by short id with an explicit kind — while those
    /// same wiki pages never reach an observer-scoped LLM-facing lookup: <c>world:stable</c> resolves to the
    /// observer's perspective body, and <c>world:canonical-only</c> is unavailable outside the wiki base.
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_ReadsBothBasesWithWikiIsolatedFromObserverLookups()
    {
        AssertCanonicalWorldPageExists("stable.md");
        AssertCanonicalWorldPageExists("canonical_only.md");
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntryLookup> canonical = await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery(LoreBase.Wiki, ["world:stable", "world:canonical-only"]));

        Assert.True(canonical[0].Found);
        Assert.Contains("Canonical authoring page for the stable subject.", canonical[0].Entry!.Body, StringComparison.Ordinal);
        Assert.True(canonical[1].Found);
        Assert.Contains("no perspective entry for any observer", canonical[1].Entry!.Body, StringComparison.Ordinal);

        // Short id with an explicit kind addresses the same canonical entry (AI-004 requirement 52).
        LoreEntryLookup shortID = (await service.QueryEntriesAsync(
            _mainFixture,
            LoreEntryIDQuery.FromShortID(LoreBase.Wiki, LoreSubjectKind.World, "stable")))[0];
        Assert.True(shortID.Found);
        Assert.Equal("world:stable", shortID.Entry!.ID);

        // The configured scope defaults to the canonical wiki; explicit observer selection stays perspective-only.
        LoreEntryLookup configured = (await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery(["world:canonical-only"])))[0];
        Assert.True(configured.Found);

        // LLM-facing isolation: the observer-scoped lookup never substitutes canonical wiki content — the
        // wiki-only 'world:stable' is unavailable to the observer, whose own stable entry is 'world:with-id'.
        IReadOnlyList<LoreEntryLookup> observerScoped = await service.QueryEntriesAsync(
            _mainFixture,
            new LoreEntryIDQuery("char:test", ["world:with-id", "world:stable"]));
        Assert.True(observerScoped[0].Found);
        Assert.Contains("Stable ID entry.", observerScoped[0].Entry!.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Canonical authoring page", observerScoped[0].Entry!.Body, StringComparison.Ordinal);
        Assert.False(observerScoped[1].Found);
    }

    /// <summary>
    /// The shipped canonical wiki passes path-authoritative load validation and serves no-observer canonical
    /// reads for every collection (AI-004 requirements 9 and 52): bare ids plus load-bearing types validate
    /// against their paths, hyphenated files resolve from snake_case canonical spellings, and the authoring-only
    /// <c>scenarios/</c> collection stays unread so <c>scenario:</c> IDs are unavailable even on the wiki base.
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_ReadsTheShippedCanonicalWikiForEveryCollection()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntryLookup> lookups = await service.QueryEntriesAsync(
            ContentContext.Default,
            new LoreEntryIDQuery(
                LoreBase.Wiki,
                ["world:charter", "char:vadim", "loc:interrogation_room", "scenario:interrogation"]));

        Assert.True(lookups[0].Found);
        Assert.Equal("The Charter", lookups[0].Entry!.Title);
        Assert.Equal(LoreSubjectKind.World, lookups[0].Entry!.Kind);

        Assert.True(lookups[1].Found);
        Assert.Equal(LoreSubjectKind.Character, lookups[1].Entry!.Kind);
        Assert.Equal("char:vadim", lookups[1].Entry!.SubjectID);

        // The hyphenated file 'interrogation-room.md' resolves from the snake_case canonical spelling.
        Assert.True(lookups[2].Found);
        Assert.Equal(LoreSubjectKind.Location, lookups[2].Entry!.Kind);
        Assert.Equal("loc:interrogation-room", lookups[2].Entry!.SubjectID);

        Assert.False(lookups[3].Found);
    }

    /// <summary>
    /// A query that defers base selection fails clearly when the service's configured scope is the perspective
    /// base and no observer was supplied (AI-004 requirement 52).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_WithConfiguredPerspectiveBaseAndNoObserver_FailsClearly()
    {
        MarkdownLoreQueryService service = new()
        {
            ConfiguredBase = LoreBase.Perspective
        };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.QueryEntriesAsync(_mainFixture, new LoreEntryIDQuery(["world:stable"])));

        Assert.Contains("observer", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Duplicate scoped authoring identities fail the whole canonical-ID query rather than returning a partial
    /// batch (AI-004 requirement 43).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_RejectsDuplicateScopedAuthoringIdentities()
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
            new LoreEntryIDQuery("char:other", ["world:shared", "world:isolated"]));

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
    /// completion — cancels the whole canonical-ID query instead of returning a successful lookup batch (AI-004
    /// requirement 49).
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_WithCancellationDuringFinalRead_CancelsInsteadOfSucceeding()
        => await AssertCancellationDuringFinalReadCancelsInsteadOfSucceeding(
            (service, cancellation) => service.QueryEntriesAsync(
                _mainFixture,
                new LoreEntryIDQuery("char:test", ["char:ally"]),
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
