using System.Text;
using AlleyCat.Common;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Formats lore entries as pseudo-XML prompt blocks.
/// </summary>
public sealed class PseudoXmlLorePromptFormatter : ILorePromptFormatter
{
    /// <inheritdoc />
    public string Format(IReadOnlyList<LoreEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        StringBuilder builder = new();
        foreach (LoreEntry entry in entries)
        {
            PseudoXmlFormatter.AppendBlock(builder, entry.Title, entry.Body.Trim(), "Lore entry titles");
        }

        return builder.ToString().TrimEnd();
    }
}
