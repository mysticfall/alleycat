namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Markdown catalogue formatter: each group renders a level-one heading followed by one row per entry with its
/// exact ID, title, and optional description, never its body or source path (AI-004 requirement 45). Groups emit
/// in World, Characters, Locations order, empty groups are omitted, and empty input renders empty output.
/// </summary>
public sealed class MarkdownLoreCataloguePromptFormatter : ILoreCataloguePromptFormatter
{
    private static readonly LoreSubjectKind[] _groupOrder =
        [LoreSubjectKind.World, LoreSubjectKind.Character, LoreSubjectKind.Location];

    /// <inheritdoc />
    public string Format(IReadOnlyList<LoreEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<string> groupBlocks = [];
        foreach (LoreSubjectKind kind in _groupOrder)
        {
            List<string> rows = [];
            foreach (LoreEntry entry in entries)
            {
                if (entry.Kind == kind)
                {
                    rows.Add(FormatRow(entry));
                }
            }

            if (rows.Count > 0)
            {
                groupBlocks.Add($"# {GetGroupHeading(kind)}\n\n{string.Join("\n", rows)}");
            }
        }

        return string.Join("\n\n", groupBlocks);
    }

    private static string FormatRow(LoreEntry entry)
        => entry.Description is { Length: > 0 } description
            ? $"- `{entry.ID ?? string.Empty}` — {entry.Title} — {description}"
            : $"- `{entry.ID ?? string.Empty}` — {entry.Title}";

    private static string GetGroupHeading(LoreSubjectKind kind) => kind switch
    {
        LoreSubjectKind.World => "World",
        LoreSubjectKind.Character => "Characters",
        LoreSubjectKind.Location => "Locations",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported lore subject kind."),
    };
}
