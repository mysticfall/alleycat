using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Lore;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Lore;

/// <summary>
/// Unit coverage for AI-004 Markdown lore frontmatter validation.
/// </summary>
public sealed class MarkdownLoreQueryServiceTests
{
    private const string SourcePath = "res://lore/perspectives/test/world/test-page.md";

    /// <summary>
    /// Observer and subject IDs are canonicalised once at the storage-agnostic query boundary.
    /// </summary>
    [Fact]
    public void Constructor_NormalisesIDsAndPreservesDeduplicatedRequestOrder()
    {
        LoreQuery query = new(
            "  VADIM ",
            [
                LoreSubjectRequest.World(),
                LoreSubjectRequest.Location(" LOCATION.INTERROGATION_ROOM "),
                LoreSubjectRequest.Location("location.interrogation_room"),
                LoreSubjectRequest.Character("CHARACTER.ALLY"),
            ]);

        Assert.Equal("vadim", query.ObserverID);
        Assert.Collection(
            query.Subjects,
            request => Assert.Equal(LoreSubjectKind.World, request.Kind),
            request =>
            {
                Assert.Equal(LoreSubjectKind.Location, request.Kind);
                Assert.Equal("location.interrogation_room", request.SubjectID);
            },
            request =>
            {
                Assert.Equal(LoreSubjectKind.Character, request.Kind);
                Assert.Equal("character.ally", request.SubjectID);
            });
    }

    /// <summary>
    /// The AI-004 essential marker must reject malformed values instead of treating them as false.
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsInvalidEssentialValue()
    {
        string markdown = """
            ---
            id: test.page
            title: Test Page
            essential: maybe
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(markdown, SourcePath));

        Assert.Contains(SourcePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("essential", exception.Message, StringComparison.Ordinal);
        Assert.Contains("maybe", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Perspective pages may omit a stable ID and then use title and source path as ordering fallbacks.
    /// </summary>
    [Fact]
    public void ParseDocument_AcceptsPageWithoutID()
    {
        string markdown = """
            ---
            title: Test Page
            ---
            Body.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.Null(document.ID);
        Assert.Equal("Test Page", document.Title);
    }

    /// <summary>
    /// Every perspective page requires a stable display title before it is eligible for querying.
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsPageWithoutTitle()
    {
        string markdown = """
            ---
            id: test.page
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(markdown, SourcePath));

        Assert.Contains(SourcePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("title", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Priority is an integer ordering value and malformed values fail with source attribution.
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsInvalidPriorityValue()
    {
        string markdown = """
            ---
            id: test.page
            title: Test Page
            priority: urgent
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(markdown, SourcePath));

        Assert.Contains(SourcePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("priority", exception.Message, StringComparison.Ordinal);
        Assert.Contains("urgent", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every location and character page needs a canonical subject key, irrespective of essential status.
    /// </summary>
    [Theory]
    [InlineData(LoreSubjectKind.Location)]
    [InlineData(LoreSubjectKind.Character)]
    public void ParseDocument_RejectsSubjectScopedPageWithoutSubjectID(LoreSubjectKind kind)
    {
        string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(markdown, SourcePath, kind));

        Assert.Contains(SourcePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("subject_id", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Valid optional ordering and subject metadata is represented in its canonical form.
    /// </summary>
    [Fact]
    public void ParseDocument_ParsesPriorityAndNormalisesSubjectID()
    {
        string markdown = """
            ---
            id: test.page
            title: Test Page
            subject_id: CHARACTER.ALLY
            priority: -10
            ---
            Body.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            LoreSubjectKind.Character);

        Assert.Equal(-10, document.Priority);
        Assert.Equal("character.ally", document.SubjectID);
    }

    /// <summary>
    /// Entry ordering applies priority, ID, title, and source path in that order using ordinal comparisons.
    /// </summary>
    [Fact]
    public void CompareDocuments_AppliesDeterministicOrderingContractBeforePublicProjection()
    {
        List<MarkdownLoreQueryService.LoreMarkdownDocument> documents =
        [
            Document("same", "Same", "z.md", priority: 1),
            Document("same", "Zed", "a.md", priority: 1),
            Document("z", "First", "a.md", priority: -1),
            Document("a", "First", "a.md", priority: 1),
            Document("same", "Same", "a.md", priority: 1),
            Document(null, "Alpha", "z.md", priority: 1),
            Document(null, "Alpha", "a.md", priority: 1),
            Document(null, "Beta", "a.md", priority: 1),
        ];

        documents.Sort(MarkdownLoreQueryService.CompareDocuments);

        Assert.Equal(
            [
                (Priority: -1, ID: "z", Title: "First", SourcePath: "a.md"),
                (Priority: 1, ID: "a", Title: "First", SourcePath: "a.md"),
                (Priority: 1, ID: "same", Title: "Same", SourcePath: "a.md"),
                (Priority: 1, ID: "same", Title: "Same", SourcePath: "z.md"),
                (Priority: 1, ID: "same", Title: "Zed", SourcePath: "a.md"),
                (Priority: 1, ID: null, Title: "Alpha", SourcePath: "a.md"),
                (Priority: 1, ID: null, Title: "Alpha", SourcePath: "z.md"),
                (Priority: 1, ID: null, Title: "Beta", SourcePath: "a.md"),
            ],
            documents.Select(document => (document.Priority, document.ID, document.Title, document.SourcePath)));
    }

    /// <summary>
    /// Storage-specific source paths remain an internal Markdown ordering detail rather than public lore data.
    /// </summary>
    [Fact]
    public void LoreEntry_PublicContract_DoesNotExposeSourcePath() => Assert.Null(typeof(LoreEntry).GetProperty("SourcePath"));

    private static MarkdownLoreQueryService.LoreMarkdownDocument Document(
        string? id,
        string title,
        string sourcePath,
        int priority)
        => new(
            ID: id,
            Title: title,
            SubjectID: null,
            Essential: true,
            Priority: priority,
            Body: string.Empty,
            SourcePath: sourcePath);

    /// <summary>
    /// Observer and subject keys reject traversal segments while preserving dotted canonical identifiers.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../vadim")]
    [InlineData("character\\vadim")]
    public void QueryIDs_RejectTraversalIdentifiers(string id)
    {
        _ = Assert.Throws<ArgumentException>(() => LoreQuery.Essential(id));
        _ = Assert.Throws<ArgumentException>(() => LoreSubjectRequest.Character(id));
    }

    /// <summary>
    /// Essential metadata never makes a location or character entry match an unrequested subject.
    /// </summary>
    [Theory]
    [InlineData(LoreSubjectKind.Location, "location.requested", "location.other")]
    [InlineData(LoreSubjectKind.Character, "character.requested", "character.other")]
    public void Matches_SubjectScopedEssentialEntry_DoesNotBypassSubjectSelection(
        LoreSubjectKind kind,
        string requestedSubjectID,
        string documentSubjectID)
    {
        LoreSubjectRequest request = kind == LoreSubjectKind.Location
            ? LoreSubjectRequest.Location(requestedSubjectID)
            : LoreSubjectRequest.Character(requestedSubjectID);
        var document = new MarkdownLoreQueryService.LoreMarkdownDocument(
            ID: null,
            Title: "Other Subject",
            SubjectID: documentSubjectID,
            Essential: true,
            Priority: 0,
            Body: "Body.",
            SourcePath: SourcePath);

        Assert.False(MarkdownLoreQueryService.Matches(request, document));
    }

    /// <summary>
    /// A pre-cancelled query stops before attempting any storage access.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WhenAlreadyCancelled_ThrowsCancellation()
    {
        MarkdownLoreQueryService service = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.QueryAsync(ContentContext.Default, LoreQuery.Essential("vadim"), cancellation.Token));
    }

    /// <summary>
    /// Unterminated frontmatter is malformed lore input and should fail with source attribution.
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsUnterminatedFrontmatter()
    {
        string markdown = """
            ---
            id: test.page
            title: Test Page
            essential: true
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(markdown, SourcePath));

        Assert.Contains(SourcePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("unterminated frontmatter", exception.Message, StringComparison.Ordinal);
    }
}
