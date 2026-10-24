using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Lore;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Lore;

/// <summary>
/// Unit coverage for AI-004 path-authoritative Markdown lore loading: read-time triage, parse-time body
/// cleaning, frontmatter validation, canonical-ID derivation, and the canonical query surface.
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
    /// Identity is path-authoritative: a <c>subject_id</c> frontmatter field fails load validation outright on
    /// both bases (AI-004 requirements 9 and 36).
    /// </summary>
    [Theory]
    [InlineData(LoreBase.Perspective)]
    [InlineData(LoreBase.Wiki)]
    public void ParseDocument_RejectsSubjectIDFieldOutright(LoreBase loreBase)
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            subject_id: char:ally
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(
                markdown,
                SourcePath,
                LoreSubjectKind.Character,
                "ally",
                loreBase));

        Assert.Contains(SourcePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("subject_id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("path-authoritative", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page whose frontmatter carries a <c>title</c> but no <c>id</c> loads with its path-derived authoring
    /// identity: the authored dot-handle id stays <c>null</c> while the canonical ID addresses the entry like
    /// any other (AI-004 requirements 9 and 36).
    /// </summary>
    [Fact]
    public void ParseDocument_LoadsTitleBearingIDLessPageWithPathDerivedIdentity()
    {
        const string markdown = """
            ---
            title: Fallback Entry
            essential: true
            priority: 1
            ---
            Body.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            "res://lore/perspectives/char/test/world/without-id-a.md",
            LoreSubjectKind.World,
            "without-id-a");

        Assert.NotNull(document);
        Assert.Null(document.ID);
        Assert.Equal("world:without-id-a", document.CanonicalID);
        Assert.Equal("world:without-id-a", document.AuthoringID);
        Assert.Equal("Fallback Entry", document.Title);
        Assert.Equal("Body.", document.Body);
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
    /// Field validation applies to every runtime-eligible page — any page carrying an <c>id</c> or a
    /// <c>title</c> — so a title-bearing id-less page with an invalid <c>essential</c> value fails hard instead
    /// of skipping (AI-004 requirement 36).
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsInvalidFieldValueOnTitleBearingIDLessPage()
    {
        const string markdown = """
            ---
            title: Test Page
            essential: maybe
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(markdown, SourcePath));

        Assert.Contains("essential", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Frontmatter carrying neither an <c>id</c> nor a <c>title</c> is authoring-time only: the page is skipped
    /// silently (AI-004 requirement 36).
    /// </summary>
    [Fact]
    public void ParseDocument_SkipsPageWithoutIDAndTitleSilently()
    {
        const string markdown = """
            ---
            essential: true
            priority: 3
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
    /// An unterminated frontmatter block must not break runtime, but dropping a possibly runtime-eligible entry
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
    /// Every runtime page requires a stable display title, and id-bearing pages fail hard without one.
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
    /// Kind, canonical subject binding, and canonical full ID derive from the collection directory and path
    /// segments for every kind, including nested world pages (AI-004 requirements 6 and 43).
    /// </summary>
    [Theory]
    [InlineData(LoreSubjectKind.World, "charter", "world:charter", null)]
    [InlineData(LoreSubjectKind.World, "notes/nested", "world:notes/nested", null)]
    [InlineData(LoreSubjectKind.Character, "vadim", "char:vadim", "char:vadim")]
    [InlineData(LoreSubjectKind.Location, "interrogation-room", "loc:interrogation-room", "loc:interrogation-room")]
    public void ParseDocument_DerivesIdentityFromThePath(
        LoreSubjectKind kind,
        string relativeStem,
        string expectedCanonicalID,
        string? expectedSubjectID)
    {
        const string markdown = """
            ---
            id: authoring.handle
            title: Test Page
            ---
            Body.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            kind,
            relativeStem);

        Assert.NotNull(document);
        Assert.Equal(expectedCanonicalID, document.CanonicalID);
        Assert.Equal(expectedSubjectID, document.SubjectID);
        Assert.Equal(kind, document.Kind);
        // The authored dot-handle id is an authoring identity only; it never overrides the path.
        Assert.Equal("authoring.handle", document.AuthoringID);
    }

    /// <summary>
    /// A character or location page's path-derived subject binding is what contextual subject requests match —
    /// with hyphen/underscore equivalence (AI-004 requirement 31).
    /// </summary>
    [Fact]
    public void ParseDocument_ParsesPriorityAndDerivesSubjectBindingFromPath()
    {
        string markdown = """
            ---
            id: test.page
            title: Test Page
            priority: -10
            ---
            Body.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath,
            LoreSubjectKind.Character,
            "ally");

        Assert.NotNull(document);
        Assert.Equal(-10, document.Priority);
        Assert.Equal("char:ally", document.SubjectID);
        Assert.Equal("Body.", document.Body);
    }

    /// <summary>
    /// Wiki pages carry a bare <c>id</c> plus load-bearing <c>type</c> validated against the path: a hyphen in
    /// the file name matches an underscore in the authored id (AI-004 requirement 9).
    /// </summary>
    [Fact]
    public void ParseDocument_AcceptsWikiBareIDAndTypeMatchingThePath()
    {
        const string markdown = """
            ---
            id: interrogation_room
            type: loc
            title: The Interrogation Room
            ---
            Body.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            "res://lore/wiki/locations/interrogation-room.md",
            LoreSubjectKind.Location,
            "interrogation-room",
            LoreBase.Wiki);

        Assert.NotNull(document);
        Assert.Equal("loc:interrogation-room", document.CanonicalID);
    }

    /// <summary>
    /// A wiki <c>id</c> that disagrees with the page's path — beyond hyphen/underscore equivalence — fails load
    /// validation because the path owns the identity (AI-004 requirement 9).
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsWikiIDThatDoesNotMatchThePath()
    {
        const string markdown = """
            ---
            id: somewhere_else
            type: world
            title: Test Page
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(
                markdown,
                "res://lore/wiki/world/charter.md",
                LoreSubjectKind.World,
                "charter",
                LoreBase.Wiki));

        Assert.Contains("somewhere_else", exception.Message, StringComparison.Ordinal);
        Assert.Contains("charter", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Composed <c>type:id</c> values never appear as frontmatter values: a wiki <c>id</c> carrying its type
    /// prefix fails load validation (AI-004 requirement 9).
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsComposedWikiIDValue()
    {
        const string markdown = """
            ---
            id: char:vadim
            type: char
            title: Vadim
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(
                markdown,
                "res://lore/wiki/characters/vadim.md",
                LoreSubjectKind.Character,
                "vadim",
                LoreBase.Wiki));

        Assert.Contains("char:vadim", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bare", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wiki <c>type</c> is load-bearing: an id-bearing wiki page without one fails load validation.
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsWikiPageWithoutLoadBearingType()
    {
        const string markdown = """
            ---
            id: charter
            title: Test Page
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(
                markdown,
                "res://lore/wiki/world/charter.md",
                LoreSubjectKind.World,
                "charter",
                LoreBase.Wiki));

        Assert.Contains("type", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An optional frontmatter <c>type</c> that contradicts its collection directory fails load validation on
    /// either base (AI-004 requirements 9 and 36).
    /// </summary>
    [Theory]
    [InlineData(LoreBase.Perspective)]
    [InlineData(LoreBase.Wiki)]
    public void ParseDocument_RejectsTypeContradictingTheCollectionDirectory(LoreBase loreBase)
    {
        string markdown = """
            ---
            id: authoring.handle
            title: Test Page
            type: loc
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(
                markdown,
                SourcePath,
                LoreSubjectKind.World,
                "page",
                loreBase));

        Assert.Contains("loc", exception.Message, StringComparison.Ordinal);
        Assert.Contains("world", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The optional description is single-line authored metadata: absent or blank values mean no description
    /// and populated values carry through without quotes (AI-004 requirement 42).
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\"\"", null)]
    [InlineData("Hidden riverside cache under the charter office.", "Hidden riverside cache under the charter office.")]
    [InlineData("\"A hidden cache\"", "A hidden cache")]
    public void ParseDocument_ParsesOptionalSingleLineDescription(string? descriptionValue, string? expected)
    {
        string descriptionLine = descriptionValue is null ? string.Empty : $"description: {descriptionValue}\n";
        string markdown = $"""
            ---
            id: test.page
            title: Test Page
            {descriptionLine}---
            Body.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(
            markdown,
            SourcePath);

        Assert.NotNull(document);
        Assert.Equal(expected, document.Description);
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
    /// The user-supplied internal reference syntax reduces to its label text with the entry-ID annotation
    /// retained: <c>[Title](../relative_path_to_another_page)(`target_entry_id`)</c> becomes
    /// <c>Title(`target_entry_id`)</c>, and no source path reaches the body.
    /// </summary>
    [Fact]
    public void ParseDocument_ReducesSuppliedInternalReferenceSyntaxToLabelAndIDAnnotation()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            See [Title](../relative_path_to_another_page)(`target_entry_id`) now.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal("See Title(`target_entry_id`) now.", document.Body);
    }

    /// <summary>
    /// The authored Obsidian wikilink reference form <c>[[../relative_path_to_another_page|Title]](`target_entry_id`)</c>
    /// must reach lore as <c>Title(`target_entry_id`)</c>: the label and backtick ID annotation are retained and no
    /// path or wiki-style pipe corruption remains. This is the on-disk source form of the user-reported reference
    /// corruption; the criterion is the user's exact expected output.
    /// </summary>
    [Fact]
    public void ParseDocument_ReducesWikilinkInternalReferenceToLabelAndIDAnnotation()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            See [[../relative_path_to_another_page|Title]](`target_entry_id`) now.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal("See Title(`target_entry_id`) now.", document.Body);
    }

    /// <summary>
    /// A pipe wiki link without an annotation is a plain wiki link and passes through untouched.
    /// </summary>
    [Fact]
    public void ParseDocument_LeavesUnannotatedPipeWikiLinksUntouched()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            See [[wiki/world/slaves/Rules|Rules]] as written.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal("See [[wiki/world/slaves/Rules|Rules]] as written.", document.Body);
    }

    /// <summary>
    /// An annotated pipe wiki reference reduces to its exact alias — including spaces and punctuation — with the
    /// annotation kept verbatim and the vault path omitted, matching the authored perspective-page form.
    /// </summary>
    [Fact]
    public void ParseDocument_ReducesAnnotatedPipeWikiReferenceWithSpacedAlias()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            See [[perspectives/char/mali/world/slaves/Slaves|What I Am Now]](`mali.slaves`) now.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal("See What I Am Now(`mali.slaves`) now.", document.Body);
    }

    /// <summary>
    /// An embed (<c>![[...]]</c>) is not a reference: wiki syntax preceded by an exclamation mark stays as
    /// written, mirroring the image-syntax guard.
    /// </summary>
    [Fact]
    public void ParseDocument_LeavesWikiEmbedsUntouched()
    {
        const string markdown = """
            ---
            id: test.page
            title: Test Page
            ---
            An embed ![[wiki/world/slaves/Rules|Rules]](`target_entry_id`) stays as written.
            """;

        MarkdownLoreQueryService.LoreMarkdownDocument? document = MarkdownLoreQueryService.ParseDocument(markdown, SourcePath);

        Assert.NotNull(document);
        Assert.Equal(
            "An embed ![[wiki/world/slaves/Rules|Rules]](`target_entry_id`) stays as written.",
            document.Body);
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
    /// A runtime-eligible page whose body is empty after parse-time cleaning is excluded from query results with a
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
    /// Entry ordering applies priority, authoring identity — the authored dot-handle <c>id</c> or its
    /// path-derived canonical fallback — title, and source path in that order using ordinal comparisons
    /// (AI-004 requirement 14).
    /// </summary>
    [Fact]
    public void CompareDocuments_AppliesDeterministicOrderingContractBeforePublicProjection()
    {
        List<MarkdownLoreQueryService.LoreMarkdownDocument> documents =
        [
            Document("same", "world:same", "Same", "z.md", priority: 1),
            Document("same", "world:same", "Zed", "a.md", priority: 1),
            Document(null, "world:z", "First", "a.md", priority: -1),
            Document(null, "world:a", "First", "a.md", priority: 1),
            Document("same", "world:same", "Same", "a.md", priority: 1),
            Document(null, "world:fallback", "Alpha", "z.md", priority: 1),
            Document(null, "world:fallback", "Alpha", "a.md", priority: 1),
            Document(null, "world:fallback", "Beta", "a.md", priority: 1),
        ];

        documents.Sort(MarkdownLoreQueryService.CompareDocuments);

        Assert.Equal(
            [
                (Priority: -1, AuthoringID: "world:z", Title: "First", SourcePath: "a.md"),
                (Priority: 1, AuthoringID: "same", Title: "Same", SourcePath: "a.md"),
                (Priority: 1, AuthoringID: "same", Title: "Same", SourcePath: "z.md"),
                (Priority: 1, AuthoringID: "same", Title: "Zed", SourcePath: "a.md"),
                (Priority: 1, AuthoringID: "world:a", Title: "First", SourcePath: "a.md"),
                (Priority: 1, AuthoringID: "world:fallback", Title: "Alpha", SourcePath: "a.md"),
                (Priority: 1, AuthoringID: "world:fallback", Title: "Alpha", SourcePath: "z.md"),
                (Priority: 1, AuthoringID: "world:fallback", Title: "Beta", SourcePath: "a.md"),
            ],
            documents.Select(document => (document.Priority, document.AuthoringID, document.Title, document.SourcePath)));
    }

    /// <summary>
    /// Storage-specific source paths remain an internal Markdown ordering detail rather than public lore data.
    /// </summary>
    [Fact]
    public void LoreEntry_PublicContract_DoesNotExposeSourcePath() => Assert.Null(typeof(LoreEntry).GetProperty("SourcePath"));

    private static MarkdownLoreQueryService.LoreMarkdownDocument Document(
        string? id,
        string canonicalID,
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
            SourcePath: sourcePath,
            Description: null,
            CanonicalID: canonicalID,
            Kind: LoreSubjectKind.World);

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
    /// Essential metadata never makes a location or character entry match an unrequested subject, and subject
    /// matching is kind-scoped with hyphen/underscore equivalence — a hyphenated file stem matches the
    /// canonical snake_case subject FullId.
    /// </summary>
    [Theory]
    [InlineData(LoreSubjectKind.Location, "loc:requested", "loc:other", false)]
    [InlineData(LoreSubjectKind.Character, "char:requested", "char:other", false)]
    [InlineData(LoreSubjectKind.Location, "loc:requested_room", "loc:requested-room", true)]
    [InlineData(LoreSubjectKind.Character, "char:requested", "loc:requested", false)]
    public void Matches_SubjectScopedEntry_SelectsByKindAndEquivalentSubject(
        LoreSubjectKind kind,
        string requestedSubjectID,
        string documentSubjectID,
        bool expectedMatch)
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
            SourcePath: SourcePath,
            Description: null,
            CanonicalID: documentSubjectID,
            Kind: kind);

        Assert.Equal(expectedMatch, MarkdownLoreQueryService.Matches(request, document));
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

    /// <summary>
    /// A pre-cancelled catalogue query stops before attempting any storage access.
    /// </summary>
    [Fact]
    public async Task QueryCatalogueAsync_WhenAlreadyCancelled_ThrowsCancellation()
    {
        MarkdownLoreQueryService service = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.QueryCatalogueAsync(
                ContentContext.Default,
                new LoreCatalogueQuery("char:vadim"),
                cancellation.Token));
    }

    /// <summary>
    /// A pre-cancelled canonical-ID query stops before attempting any storage access.
    /// </summary>
    [Fact]
    public async Task QueryEntriesAsync_WhenAlreadyCancelled_ThrowsCancellation()
    {
        MarkdownLoreQueryService service = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.QueryEntriesAsync(
                ContentContext.Default,
                new LoreEntryIDQuery("char:vadim", ["world:charter"]),
                cancellation.Token));
    }

    /// <summary>
    /// Catalogue queries validate their observer FullId at construction, before any read.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("vadim")]
    [InlineData("Char:vadim")]
    public void CatalogueQuery_RejectsNonCanonicalObserverID(string observerID)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new LoreCatalogueQuery(observerID));

        Assert.NotEmpty(exception.Message);
    }

    /// <summary>
    /// Catalogue queries preserve the canonical observer FullId (AI-004 requirement 44).
    /// </summary>
    [Fact]
    public void CatalogueQuery_PreservesCanonicalObserverFullId()
    {
        LoreCatalogueQuery query = new("char:vadim");

        Assert.Equal("char:vadim", query.ObserverID);
    }

    /// <summary>
    /// Canonical-ID queries validate their observer FullId at construction, before any read.
    /// </summary>
    [Fact]
    public void EntryIDQuery_RejectsNonCanonicalObserverID()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new LoreEntryIDQuery("vadim", ["world:charter"]));

        Assert.NotEmpty(exception.Message);
    }

    /// <summary>
    /// The complete ID batch is validated before reads: a null batch is rejected outright (AI-004 requirement
    /// 47).
    /// </summary>
    [Fact]
    public void EntryIDQuery_RejectsNullBatch()
        => _ = Assert.Throws<ArgumentNullException>(() => new LoreEntryIDQuery("char:vadim", null!));

    /// <summary>
    /// The complete ID batch is validated before reads: an empty batch is invalid input, not an empty lookup
    /// (AI-004 requirement 47).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void EntryIDQuery_RejectsBlankIDItems(string blankID)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new LoreEntryIDQuery("char:vadim", ["world:charter", blankID]));

        Assert.Contains("entry ID", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The complete ID batch is validated before reads: null items are invalid input (AI-004 requirement 47).
    /// </summary>
    [Fact]
    public void EntryIDQuery_RejectsNullIDItem()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new LoreEntryIDQuery("char:vadim", ["world:charter", null!]));

        Assert.Contains("entry ID", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The batch must contain at least one ID: an empty list is invalid input rather than an empty result
    /// (AI-004 requirement 47).
    /// </summary>
    [Fact]
    public void EntryIDQuery_RejectsEmptyBatch()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new LoreEntryIDQuery("char:vadim", []));

        Assert.Contains("At least one lore entry ID", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Canonical IDs are exact opaque strings: duplicates collapse by ordinal equality in first-request order with
    /// no case folding and no trimming of requested values (AI-004 requirements 43 and 47).
    /// </summary>
    [Fact]
    public void EntryIDQuery_DeduplicatesByOrdinalEqualityPreservingFirstRequestOrder()
    {
        LoreEntryIDQuery query = new(
            "char:vadim",
            ["world:b", "world:a", "world:b", "world:a ", "WORLD:B", "world:a"]);

        Assert.Equal(["world:b", "world:a", "world:a ", "WORLD:B"], query.EntryIDs);
    }

    /// <summary>
    /// The query surface selects its base explicitly — observer-scoped perspective or canonical wiki — or defers
    /// to the service's configured scope (AI-004 requirement 52).
    /// </summary>
    [Fact]
    public void EntryIDQuery_SelectsBaseExplicitlyOrDefersToTheConfiguredScope()
    {
        LoreEntryIDQuery observerScoped = new("char:vadim", ["world:charter"]);
        LoreEntryIDQuery wikiExplicit = new(LoreBase.Wiki, ["world:charter"]);
        LoreEntryIDQuery deferred = new(["world:charter"]);

        Assert.Equal(LoreBase.Perspective, observerScoped.Base);
        Assert.Equal("char:vadim", observerScoped.ObserverID);
        Assert.Equal(LoreBase.Wiki, wikiExplicit.Base);
        Assert.Null(wikiExplicit.ObserverID);
        Assert.Null(deferred.Base);
        Assert.Null(deferred.ObserverID);
    }

    /// <summary>
    /// Retrieval accepts a short id with an explicit kind, composing the canonical ID — including multi-segment
    /// world paths — and rejects malformed short ids (AI-004 requirement 52).
    /// </summary>
    [Fact]
    public void FromShortID_ComposesCanonicalIDsFromKindAndShortID()
    {
        Assert.Equal(
            ["char:vadim"],
            LoreEntryIDQuery.FromShortID("char:vadim", LoreSubjectKind.Character, "vadim").EntryIDs);
        Assert.Equal(
            ["world:notes/nested"],
            LoreEntryIDQuery.FromShortID(LoreBase.Wiki, LoreSubjectKind.World, "notes/nested").EntryIDs);
        Assert.Equal(LoreBase.Wiki, LoreEntryIDQuery.FromShortID(LoreBase.Wiki, LoreSubjectKind.World, "charter").Base);

        _ = Assert.Throws<ArgumentException>(
            () => LoreEntryIDQuery.FromShortID(LoreBase.Wiki, LoreSubjectKind.World, "charter:extra"));
        _ = Assert.Throws<ArgumentException>(
            () => LoreEntryIDQuery.FromShortID(LoreBase.Wiki, LoreSubjectKind.World, "notes//nested"));
        _ = Assert.Throws<ArgumentException>(
            () => LoreEntryIDQuery.FromShortID(LoreBase.Wiki, LoreSubjectKind.World, " "));
    }

    /// <summary>
    /// Lookup results associate the requested canonical ID with either a found entry or an explicit unavailable
    /// status, and a miss carries a canonical hint exactly when the request matched an authored dot-handle id
    /// (AI-004 requirement 48).
    /// </summary>
    [Fact]
    public void LoreEntryLookup_MarksExplicitUnavailableStatusAndCanonicalHint()
    {
        LoreEntry entry = new("char:vadim", "Title", "Body.");
        LoreEntryLookup found = new("char:vadim", entry);
        LoreEntryLookup missing = new("world:missing", null);
        LoreEntryLookup hinted = new("vadim.self", null, HintCanonicalID: "char:vadim");

        Assert.True(found.Found);
        Assert.Same(entry, found.Entry);
        Assert.Null(found.HintCanonicalID);
        Assert.False(missing.Found);
        Assert.Null(missing.Entry);
        Assert.Null(missing.HintCanonicalID);
        Assert.False(hinted.Found);
        Assert.Equal("char:vadim", hinted.HintCanonicalID);
    }
}
