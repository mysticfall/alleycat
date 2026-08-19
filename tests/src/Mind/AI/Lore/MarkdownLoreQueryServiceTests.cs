using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Lore;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Lore;

/// <summary>
/// Unit coverage for AI-004 Markdown lore read-time triage, parse-time body cleaning, and frontmatter
/// validation.
/// </summary>
public sealed class MarkdownLoreQueryServiceTests
{
    private const string SourcePath = "res://lore/perspectives/char/test/world/test-page.md";

    /// <summary>
    /// Observer and subject FullIds are preserved at the storage-agnostic query boundary.
    /// </summary>
    [Fact]
    public void Constructor_PreservesCanonicalFullIdsAndDeduplicatedRequestOrder()
    {
        LoreQuery query = new(
            "char:vadim",
            [
                LoreSubjectRequest.World(),
                LoreSubjectRequest.Location("loc:interrogation_room"),
                LoreSubjectRequest.Location("loc:interrogation_room"),
                LoreSubjectRequest.Character("char:ally"),
            ]);

        Assert.Equal("char:vadim", query.ObserverID);
        Assert.Collection(
            query.Subjects,
            request => Assert.Equal(LoreSubjectKind.World, request.Kind),
            request =>
            {
                Assert.Equal(LoreSubjectKind.Location, request.Kind);
                Assert.Equal("loc:interrogation_room", request.SubjectID);
            },
            request =>
            {
                Assert.Equal(LoreSubjectKind.Character, request.Kind);
                Assert.Equal("char:ally", request.SubjectID);
            });
    }

    /// <summary>
    /// Subject factories accept only canonical FullIds of their required type.
    /// </summary>
    [Theory]
    [InlineData(LoreSubjectKind.Character, "char:ally", "char:ally")]
    [InlineData(LoreSubjectKind.Location, "loc:interrogation_room", "loc:interrogation_room")]
    public void SubjectRequest_AcceptsCanonicalFullId(
        LoreSubjectKind kind,
        string fullID,
        string expectedID)
    {
        LoreSubjectRequest request = kind == LoreSubjectKind.Character
            ? LoreSubjectRequest.Character(fullID)
            : LoreSubjectRequest.Location(fullID);

        Assert.Equal(expectedID, request.SubjectID);
    }

    /// <summary>
    /// Bare, malformed, and wrong-type subject values are authoring errors.
    /// </summary>
    [Theory]
    [InlineData(LoreSubjectKind.Character, "ally")]
    [InlineData(LoreSubjectKind.Character, "loc:ally")]
    [InlineData(LoreSubjectKind.Location, "interrogation_room")]
    [InlineData(LoreSubjectKind.Location, "char:interrogation_room")]
    public void SubjectRequest_RejectsNonCanonicalOrWrongTypeInput(LoreSubjectKind kind, string subjectID)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            kind == LoreSubjectKind.Character
                ? LoreSubjectRequest.Character(subjectID)
                : LoreSubjectRequest.Location(subjectID));

        Assert.NotEmpty(exception.Message);
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
    /// Non-canonical subject values on an ID-bearing subject page remain fail-hard authoring errors.
    /// </summary>
    [Theory]
    [InlineData("ally")]
    [InlineData("loc:ally")]
    public void ParseDocument_RejectsInvalidSubjectIDValueOnIDBearingPage(string subjectID)
    {
        string markdown = $"""
            ---
            id: test.page
            title: Test Page
            subject_id: {subjectID}
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(markdown, SourcePath, LoreSubjectKind.Character));

        Assert.Contains(SourcePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("subject_id", exception.Message, StringComparison.Ordinal);
        Assert.Contains(subjectID, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Frontmatter without an <c>id</c> is authoring-time only: the page is skipped silently at read time.
    /// </summary>
    [Fact]
    public void ParseDocument_SkipsPageWithoutIDSilently()
    {
        string markdown = """
            ---
            title: Test Page
            ---
            Body.
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.Null(document);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A file with no frontmatter block at all is authoring-time only: the page is skipped silently.
    /// </summary>
    [Fact]
    public void ParseDocument_SkipsPageWithoutFrontmatterSilently()
    {
        const string markdown = """
            Just a scratch page with prose.

            It has no frontmatter block at all.
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.Null(document);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Field validation applies only to ID-bearing pages: a page without an <c>id</c> skips silently even when
    /// its frontmatter carries values that would fail hard on a runtime page.
    /// </summary>
    [Fact]
    public void ParseDocument_SkipsPageWithoutIDBeforeFieldValidation()
    {
        const string markdown = """
            ---
            title: Test Page
            essential: maybe
            ---
            Body.
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.Null(document);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// An unterminated frontmatter block must not break runtime, but dropping a possibly ID-bearing entry
    /// deserves a signal: the page is skipped with a logged warning.
    /// </summary>
    [Fact]
    public void ParseDocument_SkipsUnterminatedFrontmatterWithWarning()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            essential: true
            Body.
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.Null(document);
        string warning = Assert.Single(warnings);
        Assert.Contains(SourcePath, warning, StringComparison.Ordinal);
        Assert.Contains("frontmatter", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every runtime page requires a stable display title, and ID-bearing pages fail hard without one.
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
    /// Valid optional ordering and subject metadata is represented in its canonical form, with the cleaned
    /// body carried through unchanged when no authoring-time material is present.
    /// </summary>
    [Fact]
    public void ParseDocument_ParsesPriorityAndNormalisesSubjectID()
    {
        string markdown = """
            ---
            id: test.page
            title: Test Page
            subject_id: char:ally
            priority: -10
            ---
            Body.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            LoreSubjectKind.Character);

        Assert.NotNull(document);
        Assert.Equal(-10, document.Priority);
        Assert.Equal("char:ally", document.SubjectID);
        Assert.Equal("Body.", document.Body);
    }

    /// <summary>
    /// A closed <c>lore:ignore</c> block is removed inclusively: both markers and all content between them are
    /// gone, while surrounding content is retained.
    /// </summary>
    [Fact]
    public void ParseDocument_RemovesLoreIgnoreBlockInclusively()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            Before the block.
            <!-- lore:ignore -->
            Scratch note that must not reach prompts.
            <!-- /lore:ignore -->
            After the block.
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.NotNull(document);
        Assert.Equal("Before the block.\nAfter the block.", document.Body);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// An unclosed <c>lore:ignore</c> block removes everything after the opening marker and logs a warning.
    /// </summary>
    [Fact]
    public void ParseDocument_RemovesTrailingContentAndWarnsOnUnclosedLoreIgnore()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            Kept prose.
            <!-- lore:ignore -->
            Everything here disappears.
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.NotNull(document);
        Assert.Equal("Kept prose.", document.Body);
        string warning = Assert.Single(warnings);
        Assert.Contains(SourcePath, warning, StringComparison.Ordinal);
        Assert.Contains("lore:ignore", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bare HTML comments are stripped from prose, including comments that span multiple lines.
    /// </summary>
    [Fact]
    public void ParseDocument_StripsBareHtmlCommentsFromProse()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            Shown text <!-- hidden author note --> continues.

            <!--
            A multi-line author note.
            -->
            More shown text.
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.NotNull(document);
        Assert.StartsWith("Shown text", document.Body, StringComparison.Ordinal);
        Assert.EndsWith("More shown text.", document.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden author note", document.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("multi-line author note", document.Body, StringComparison.Ordinal);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// HTML comments inside fenced code blocks are authoring examples and pass through verbatim.
    /// </summary>
    [Fact]
    public void ParseDocument_RetainsHtmlCommentsInsideCodeFences()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            ```csharp
            // Rendering note: <!-- keep this comment -->
            ```
            Prose <!-- strip this --> stays.
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.NotNull(document);
        Assert.Contains("```csharp", document.Body, StringComparison.Ordinal);
        Assert.Contains("<!-- keep this comment -->", document.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("strip this", document.Body, StringComparison.Ordinal);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// <c>lore:ignore</c> markers inside a fenced code block are literal text, not omission markers.
    /// </summary>
    [Fact]
    public void ParseDocument_TreatsLoreIgnoreMarkersInsideCodeFencesAsLiteralText()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            ```markdown
            <!-- lore:ignore -->
            ```
            Kept prose.
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.NotNull(document);
        Assert.Contains("<!-- lore:ignore -->", document.Body, StringComparison.Ordinal);
        Assert.Contains("Kept prose.", document.Body, StringComparison.Ordinal);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Inline links are reduced to their label text at parse time.
    /// </summary>
    [Fact]
    public void ParseDocument_ReducesInlineLinksToLabels()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            I walked through [the market](loc:market) at dawn.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal("I walked through the market at dawn.", document.Body);
    }

    /// <summary>
    /// Reference links are reduced to their label text and their definition lines are dropped.
    /// </summary>
    [Fact]
    public void ParseDocument_ReducesReferenceLinksAndDropsDefinitionLines()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            See [the vault][vault] for details.

            [vault]: loc:vault
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal("See the vault for details.", document.Body);
    }

    /// <summary>
    /// Collapsed reference links are reduced to their label text.
    /// </summary>
    [Fact]
    public void ParseDocument_ReducesCollapsedReferenceLinksToLabels()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            Ask about [the vault][] sometime.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal("Ask about the vault sometime.", document.Body);
    }

    /// <summary>
    /// Autolinks are reduced to their URL text.
    /// </summary>
    [Fact]
    public void ParseDocument_ReducesAutolinksToUrls()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            Read <https://example.com/lore> for background.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal("Read https://example.com/lore for background.", document.Body);
    }

    /// <summary>
    /// Bare bracket labels, image syntax, and wiki links are not link targets and stay as written.
    /// </summary>
    [Fact]
    public void ParseDocument_LeavesNonLinkBracketFormsIntact()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            A bare [label], an image ![cat](char:cat), and a [[wiki link]] stay as written.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal(
            "A bare [label], an image ![cat](char:cat), and a [[wiki link]] stay as written.",
            document.Body);
    }

    /// <summary>
    /// Link syntax inside fenced code blocks is example material and passes through verbatim.
    /// </summary>
    [Fact]
    public void ParseDocument_PassesLinkSyntaxInsideCodeFencesThroughVerbatim()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            Prompt material:

            ```markdown
            See [the vault][vault] and <https://example.com>.
            [vault]: loc:vault
            ```

            Reduced [outside](loc:market) the fence.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Contains("See [the vault][vault] and <https://example.com>.", document.Body, StringComparison.Ordinal);
        Assert.Contains("[vault]: loc:vault", document.Body, StringComparison.Ordinal);
        Assert.Contains("Reduced outside the fence.", document.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ID-bearing page whose body is empty after parse-time cleaning is excluded from query results with a
    /// logged warning rather than an error.
    /// </summary>
    [Fact]
    public void ParseDocument_ExcludesEntryWhenBodyCleansToEmptyWithWarning()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            <!-- nothing but an author note -->
            """;

        List<string> warnings = [];

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            warn: warnings.Add);

        Assert.Null(document);
        string warning = Assert.Single(warnings);
        Assert.Contains(SourcePath, warning, StringComparison.Ordinal);
        Assert.Contains("empty", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Entry ordering applies priority, ID, title, and source path in that order using ordinal comparisons.
    /// Null IDs cannot occur at runtime because pages without an ID are skipped at read time; they sort first
    /// only as a defensive comparator guarantee for directly constructed documents.
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
                (Priority: 1, ID: null, Title: "Alpha", SourcePath: "a.md"),
                (Priority: 1, ID: null, Title: "Alpha", SourcePath: "z.md"),
                (Priority: 1, ID: null, Title: "Beta", SourcePath: "a.md"),
                (Priority: 1, ID: "a", Title: "First", SourcePath: "a.md"),
                (Priority: 1, ID: "same", Title: "Same", SourcePath: "a.md"),
                (Priority: 1, ID: "same", Title: "Same", SourcePath: "z.md"),
                (Priority: 1, ID: "same", Title: "Zed", SourcePath: "a.md"),
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
    /// Observer and subject FullIds reject traversal, malformed, and mixed-case values.
    /// </summary>
    [Theory]
    [InlineData("char:.")]
    [InlineData("char:..")]
    [InlineData("char:../vadim")]
    [InlineData("char:vadim/other")]
    [InlineData("Char:vadim")]
    public void QueryIDs_RejectTraversalIdentifiers(string id)
    {
        _ = Assert.Throws<ArgumentException>(() => LoreQuery.Essential(id));
        _ = Assert.Throws<ArgumentException>(() => LoreSubjectRequest.Character(id));
    }

    /// <summary>
    /// Essential metadata never makes a location or character entry match an unrequested subject.
    /// </summary>
    [Theory]
    [InlineData(LoreSubjectKind.Location, "loc:requested", "loc:other")]
    [InlineData(LoreSubjectKind.Character, "char:requested", "char:other")]
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
            () => service.QueryAsync(ContentContext.Default, LoreQuery.Essential("char:vadim"), cancellation.Token));
    }
}
