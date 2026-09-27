using AlleyCat.Mind.AI.Lore;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Lore;

/// <summary>
/// Unit coverage for the Markdown catalogue formatter: fixed group order, omitted empty groups, exact-ID rows with
/// optional descriptions, and no bodies (AI-004 requirement 45).
/// </summary>
public sealed class MarkdownLoreCataloguePromptFormatterTests
{
    /// <summary>
    /// Groups emit in World, Characters, Locations order regardless of the supplied entry order, preserving the
    /// supplied within-group order.
    /// </summary>
    [Fact]
    public void Format_RendersGroupsInWorldCharactersLocationsOrder()
    {
        MarkdownLoreCataloguePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry("loc.a", "Location A", "Location body.", Kind: LoreSubjectKind.Location),
            new LoreEntry("char.a", "Character A", "Character body.", Kind: LoreSubjectKind.Character),
            new LoreEntry("world.a", "World A", "World body.", Kind: LoreSubjectKind.World),
            new LoreEntry("loc.b", "Location B", "Location body.", Kind: LoreSubjectKind.Location),
        ]);

        int world = content.IndexOf("# World\n", StringComparison.Ordinal);
        int characters = content.IndexOf("# Characters\n", StringComparison.Ordinal);
        int locations = content.IndexOf("# Locations\n", StringComparison.Ordinal);
        Assert.True(world >= 0 && characters > world && locations > characters, $"Unexpected group order: {content}");
        Assert.True(
            content.IndexOf("`loc.a`", StringComparison.Ordinal) < content.IndexOf("`loc.b`", StringComparison.Ordinal),
            "Within-group order must follow the supplied entries.");
    }

    /// <summary>Empty groups are omitted and fully empty input renders empty output.</summary>
    [Fact]
    public void Format_OmitsEmptyGroupsAndRendersEmptyContentForEmptyInput()
    {
        MarkdownLoreCataloguePromptFormatter formatter = new();

        string onlyCharacters = formatter.Format(
            [new LoreEntry("char.only", "Only Character", "Body.", Kind: LoreSubjectKind.Character)]);
        string empty = formatter.Format([]);

        Assert.Equal("# Characters\n\n- `char.only` — Only Character", onlyCharacters);
        Assert.DoesNotContain("World", onlyCharacters, StringComparison.Ordinal);
        Assert.DoesNotContain("Locations", onlyCharacters, StringComparison.Ordinal);
        Assert.Equal(string.Empty, empty);
    }

    /// <summary>
    /// Rows carry the exact entry ID, verbatim title, and description only when present; bodies never render.
    /// </summary>
    [Fact]
    public void Format_RowsCarryExactIDTitleAndOptionalDescriptionWithoutBodies()
    {
        MarkdownLoreCataloguePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry(
                "world.described",
                "Described Entry",
                "Body that must never render.",
                Kind: LoreSubjectKind.World,
                Description: "Short description."),
            new LoreEntry("world.bare", "Bare Entry", "Another body.", Kind: LoreSubjectKind.World),
            new LoreEntry(null, "Null ID Entry", "Body.", Kind: LoreSubjectKind.World),
            new LoreEntry("world.blank", "Blank Description Entry", "Body.", Kind: LoreSubjectKind.World, Description: ""),
        ]);

        Assert.Contains("- `world.described` — Described Entry — Short description.", content, StringComparison.Ordinal);
        Assert.Contains("- `world.bare` — Bare Entry", content, StringComparison.Ordinal);
        Assert.Contains("- `` — Null ID Entry", content, StringComparison.Ordinal);
        // A blank description is no description: the row omits the description segment entirely.
        Assert.Contains("- `world.blank` — Blank Description Entry", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Blank Description Entry —", content, StringComparison.Ordinal);
        Assert.Equal(-1, content.IndexOf("Body that must never render", StringComparison.Ordinal));
        Assert.Equal(-1, content.IndexOf("Another body.", StringComparison.Ordinal));
        Assert.DoesNotContain("res://", content, StringComparison.Ordinal);
    }
}
