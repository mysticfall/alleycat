using AlleyCat.Mind.AI.Lore;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Lore;

/// <summary>
/// Unit coverage for AI-004 Markdown lore frontmatter validation.
/// </summary>
public sealed class MarkdownLoreQueryServiceTests
{
    private const string SourcePath = "res://lore/wiki/test-page.md";

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
    /// Essential pages require stable source identifiers before they are projected into prompts.
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsEssentialPageWithoutID()
    {
        string markdown = """
            ---
            title: Test Page
            essential: true
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(markdown, SourcePath));

        Assert.Contains(SourcePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("id", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Essential pages require stable display titles before they are projected into prompts.
    /// </summary>
    [Fact]
    public void ParseDocument_RejectsEssentialPageWithoutTitle()
    {
        string markdown = """
            ---
            id: test.page
            essential: true
            ---
            Body.
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MarkdownLoreQueryService.ParseDocument(markdown, SourcePath));

        Assert.Contains(SourcePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("title", exception.Message, StringComparison.Ordinal);
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
