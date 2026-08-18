using AlleyCat.Mind.AI.Lore;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Lore;

/// <summary>
/// Unit coverage for the Markdown lore prompt formatter.
/// </summary>
public sealed class MarkdownLorePromptFormatterTests
{
    /// <summary>
    /// Single entries render the title as a level-one heading, a blank line, and the body verbatim.
    /// </summary>
    [Fact]
    public void Format_RendersTitleHeadingBlankLineAndSimpleBody()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format([new LoreEntry("id.charter", "The Charter", "The operating logic of a city.")]);

        Assert.Equal("# The Charter\n\nThe operating logic of a city.", content);
    }

    /// <summary>
    /// The acceptance example: title injection, prose reflow, and heading demotion combine into one entry block.
    /// </summary>
    [Fact]
    public void Format_ReflowsAndDemotesThePeoplesExample()
    {
        MarkdownLorePromptFormatter formatter = new();

        string body = """
            The Compact is home to three communities, each with a distinct standing under the charter. My work gives me cause to
            know all three.

            # Kaelic

            The charter's authors and its natural administrators.
            """;

        string content = formatter.Format([new LoreEntry("vadim.peoples", "The Peoples", body)]);

        Assert.Equal(
            """
            # The Peoples

            The Compact is home to three communities, each with a distinct standing under the charter. My work gives me cause to know all three.

            ## Kaelic

            The charter's authors and its natural administrators.
            """,
            content);
    }

    /// <summary>
    /// An authored level-one body heading — including a duplicated entry title — is demoted to level two.
    /// </summary>
    [Fact]
    public void Format_DemotesShallowestBodyHeadingToLevelTwo()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry(
                "vadim.charter",
                "The Charter",
                "# The Charter\n\nThe operating logic of a city."),
        ]);

        Assert.Equal(
            "# The Charter\n\n## The Charter\n\nThe operating logic of a city.",
            content);
    }

    /// <summary>
    /// Deeper headings keep their relative depth while the shallowest heading is lifted to level two.
    /// </summary>
    [Fact]
    public void Format_DemotesDeeperHeadingsPreservingRelativeDepth()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry(
                "id.page",
                "Page",
                "# First\n\nText.\n\n## Second\n\nMore text.\n\n### Third\n\nEnd."),
        ]);

        Assert.Equal(
            "# Page\n\n## First\n\nText.\n\n### Second\n\nMore text.\n\n#### Third\n\nEnd.",
            content);
    }

    /// <summary>
    /// Bodies whose shallowest heading is already level two or deeper are left untouched.
    /// </summary>
    [Fact]
    public void Format_DoesNotPromoteHeadingsAlreadyAtOrBelowLevelTwo()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry(
                "id.page",
                "Page",
                "## First\n\nText.\n\n### Second\n\nMore text."),
        ]);

        Assert.Equal("# Page\n\n## First\n\nText.\n\n### Second\n\nMore text.", content);
    }

    /// <summary>
    /// Hard-wrapped prose lines — including em-dash continuations — reflow into a single spaced line.
    /// </summary>
    [Fact]
    public void Format_ReflowsHardWrappedProseWithSingleSpaces()
    {
        MarkdownLorePromptFormatter formatter = new();

        string body = """
            Personhood in the Compact is not belonging in any traditional sense
            — there is no comfort in the word, only standing.

            Assets are not persons. The word is
            precise.
            """;

        string content = formatter.Format([new LoreEntry("id.reclass", "Reclassification", body)]);

        Assert.Equal(
            "# Reclassification\n\n" +
            "Personhood in the Compact is not belonging in any traditional sense — there is no comfort in the word, only standing.\n\n" +
            "Assets are not persons. The word is precise.",
            content);
    }

    /// <summary>
    /// Runs of two or more blank lines collapse to exactly one blank line.
    /// </summary>
    [Fact]
    public void Format_CollapsesConsecutiveBlankLinesIntoOne()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry(
                "id.page",
                "Page",
                "First paragraph.\n\n\n\n   \nSecond paragraph."),
        ]);

        Assert.Equal("# Page\n\nFirst paragraph.\n\nSecond paragraph.", content);
    }

    /// <summary>
    /// Entries in a batch are separated by exactly one blank line and trailing whitespace is trimmed.
    /// </summary>
    [Fact]
    public void Format_SeparatesEntriesWithExactlyOneBlankLine()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry("id.first", "First", "First body."),
            new LoreEntry("id.second", "Second", "\nSecond body.\n"),
            new LoreEntry("id.third", "Third", "Third body."),
        ]);

        Assert.Equal(
            "# First\n\nFirst body.\n\n# Second\n\nSecond body.\n\n# Third\n\nThird body.",
            content);
    }

    /// <summary>
    /// Entries without a body render the title heading only.
    /// </summary>
    [Fact]
    public void Format_RendersHeadingOnlyWhenBodyIsEmpty()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format([new LoreEntry("id.empty", "Empty", "  \n\n  ")]);

        Assert.Equal("# Empty", content);
    }

    /// <summary>
    /// Titles are heading text, not tag names: slashes and angle brackets render verbatim.
    /// </summary>
    [Fact]
    public void Format_RendersTitleVerbatimWithoutSanitisation()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format([new LoreEntry("faction.rank.elite", "Faction/Rank <Elite>", "Canon includes / slashes.")]);

        Assert.Equal("# Faction/Rank <Elite>\n\nCanon includes / slashes.", content);
        Assert.DoesNotContain("<lore_entry>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<body>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<source>", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Heading lines stay on their own line and are never reflowed into an adjacent paragraph.
    /// </summary>
    [Fact]
    public void Format_KeepsHeadingLinesSeparateFromAdjacentParagraphs()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry(
                "id.page",
                "Page",
                "Intro that wraps\nonto two lines.\n## Section\nSection body wraps\nonto two lines."),
        ]);

        Assert.Equal(
            "# Page\n\nIntro that wraps onto two lines.\n## Section\nSection body wraps onto two lines.",
            content);
    }

    /// <summary>
    /// Fenced code block content is left verbatim: no reflow, no heading demotion inside the fence.
    /// </summary>
    [Fact]
    public void Format_LeavesFencedCodeBlocksVerbatim()
    {
        MarkdownLorePromptFormatter formatter = new();

        string body = """
            Intro paragraph that wraps
            onto two lines.

            ```csharp
            # not a heading
            wrapped code line
            ```

            Outro paragraph.
            """;

        string content = formatter.Format([new LoreEntry("id.code", "Code", body)]);

        Assert.Equal(
            """
            # Code

            Intro paragraph that wraps onto two lines.

            ```csharp
            # not a heading
            wrapped code line
            ```

            Outro paragraph.
            """,
            content);
    }

    /// <summary>
    /// Empty entry batches format to an empty string.
    /// </summary>
    [Fact]
    public void Format_ReturnsEmptyStringForEmptyEntries()
    {
        MarkdownLorePromptFormatter formatter = new();

        Assert.Equal(string.Empty, formatter.Format([]));
    }

    /// <summary>
    /// Null entry batches fail fast.
    /// </summary>
    [Fact]
    public void Format_ThrowsForNullEntries()
    {
        MarkdownLorePromptFormatter formatter = new();

        _ = Assert.Throws<ArgumentNullException>(() => formatter.Format(null!));
    }
}
