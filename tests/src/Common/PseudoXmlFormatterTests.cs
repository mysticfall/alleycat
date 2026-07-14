using System.Text;
using AlleyCat.Common;
using Xunit;

namespace AlleyCat.Tests.Common;

/// <summary>
/// Unit coverage for the shared pseudo-XML formatter used by prompt and lore output.
/// </summary>
public sealed class PseudoXmlFormatterTests
{
    /// <summary>
    /// Shared block formatting preserves the prompt writer newline contract and tag sanitisation rules.
    /// </summary>
    [Fact]
    public void AppendBlock_UsesSharedSanitisationAndPromptWriterNewlineShape()
    {
        StringBuilder builder = new();

        PseudoXmlFormatter.AppendBlock(builder, "Faction/Rank <Elite>", "Canon includes / slashes.");

        Assert.Equal(
            "<Faction_Rank _Elite_>\n" +
            "Canon includes / slashes.\n" +
            "</Faction_Rank _Elite_>\n" +
            "\n",
            builder.ToString());
    }

    /// <summary>
    /// Empty authored tag values fail before emitting malformed pseudo-XML.
    /// </summary>
    [Fact]
    public void AppendBlock_RejectsEmptyTagsClearly()
    {
        StringBuilder builder = new();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            PseudoXmlFormatter.AppendBlock(builder, " ", "content", "Lore entry titles"));

        Assert.Contains("Lore entry titles", exception.Message, StringComparison.Ordinal);
        Assert.Contains("non-empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
