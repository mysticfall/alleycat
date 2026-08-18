using System.Text;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Formats lore entries as Markdown: each entry renders a level-one heading from its title followed by the
/// normalised body, with body headings demoted so they always nest beneath the title heading.
/// </summary>
public sealed class MarkdownLorePromptFormatter : ILorePromptFormatter
{
    private const int BodyHeadingLevel = 2;
    private const int MaximumHeadingLevel = 6;

    /// <inheritdoc />
    public string Format(IReadOnlyList<LoreEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<string> entryBlocks = new(entries.Count);
        foreach (LoreEntry entry in entries)
        {
            entryBlocks.Add($"# {entry.Title}\n\n{FormatBody(entry.Body)}".TrimEnd());
        }

        return string.Join("\n\n", entryBlocks);
    }

    /// <summary>
    /// Normalises an entry body: hard-wrapped prose paragraphs are reflowed, ATX headings are demoted beneath the
    /// title heading, and blank-line runs collapse to a single blank line.
    /// </summary>
    private static string FormatBody(string body)
    {
        string normalised = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalised.Trim().Length == 0)
        {
            return string.Empty;
        }

        string[] lines = normalised.Split('\n');
        int headingDemotion = GetHeadingDemotion(lines);

        StringBuilder result = new();
        List<string> paragraphLines = [];
        bool inFence = false;
        bool pendingBlankLine = false;

        foreach (string line in lines)
        {
            if (inFence)
            {
                _ = result.AppendLine(line);
                inFence = !IsCodeFence(line);
                continue;
            }

            if (IsCodeFence(line))
            {
                FlushParagraph();
                FlushPendingBlankLine();
                _ = result.AppendLine(line.Trim());
                inFence = true;
                continue;
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                pendingBlankLine = true;
                continue;
            }

            if (TryGetHeadingLevel(trimmed, out int level))
            {
                FlushParagraph();
                FlushPendingBlankLine();
                _ = result.AppendLine(DemoteHeading(trimmed, level, headingDemotion));
                continue;
            }

            paragraphLines.Add(trimmed);
        }

        FlushParagraph();

        return result.ToString().Trim();

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0)
            {
                return;
            }

            FlushPendingBlankLine();
            _ = result.AppendLine(string.Join(' ', paragraphLines));
            paragraphLines.Clear();
        }

        void FlushPendingBlankLine()
        {
            if (pendingBlankLine)
            {
                _ = result.AppendLine();
                pendingBlankLine = false;
            }
        }
    }

    /// <summary>
    /// Computes the heading demotion that lifts the shallowest body heading to exactly <c>##</c>. Headings already
    /// at that level or deeper are never promoted.
    /// </summary>
    private static int GetHeadingDemotion(string[] lines)
    {
        bool inFence = false;
        int shallowestLevel = int.MaxValue;
        foreach (string line in lines)
        {
            if (inFence)
            {
                inFence = !IsCodeFence(line);
                continue;
            }

            if (IsCodeFence(line))
            {
                inFence = true;
                continue;
            }

            if (TryGetHeadingLevel(line.Trim(), out int level) && level < shallowestLevel)
            {
                shallowestLevel = level;
            }
        }

        return shallowestLevel == int.MaxValue ? 0 : Math.Max(0, BodyHeadingLevel - shallowestLevel);
    }

    /// <summary>
    /// Demotes a trimmed heading line by the supplied offset, capping at the deepest valid ATX level.
    /// </summary>
    private static string DemoteHeading(string trimmedLine, int level, int demotion)
        => demotion == 0
            ? trimmedLine
            : new string('#', Math.Min(level + demotion, MaximumHeadingLevel)) + trimmedLine[level..];

    /// <summary>
    /// Determines whether a trimmed line is an ATX heading: one to six leading hashes followed by a space.
    /// </summary>
    private static bool TryGetHeadingLevel(string trimmedLine, out int level)
    {
        level = 0;
        int hashCount = 0;
        while (hashCount < trimmedLine.Length && trimmedLine[hashCount] == '#')
        {
            hashCount++;
        }

        if (hashCount is < 1 or > MaximumHeadingLevel)
        {
            return false;
        }

        if (hashCount == trimmedLine.Length || trimmedLine[hashCount] != ' ')
        {
            return false;
        }

        level = hashCount;
        return true;
    }

    /// <summary>
    /// Determines whether a line opens or closes a backtick fenced code block. Handling is intentionally simple:
    /// any line whose first non-whitespace characters are three backticks toggles the fence state.
    /// </summary>
    private static bool IsCodeFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);
}
