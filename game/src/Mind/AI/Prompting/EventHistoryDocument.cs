using System.Text.RegularExpressions;
using ObservedSpeechRecord = AlleyCat.Mind.Observation.ObservedSpeech;
using ObservedVisualDescriptionRecord = AlleyCat.Mind.Observation.ObservedVisualDescription;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Authored event-history content parsed from the standalone event-history file (AI-003 TR-12): ordered
/// exact-dispatch fragment sources plus the mandatory fallback source. Not a prompt section and never part of the
/// session-start prompt stack.
/// </summary>
/// <remarks>
/// The file convention uses HTML-comment delimiter lines: <c><![CDATA[<!-- event-history: <TypeKey> -->]]></c>
/// opens the fragment section for that exact, case-sensitive observation key, and
/// <c><![CDATA[<!-- event-history: fallback -->]]></c> opens the mandatory fallback section; section content
/// excludes the delimiter lines. Parsing fails clearly, naming the offending section, for a blank or unknown
/// <c>TypeKey</c>, a duplicate exact key, a missing or blank fallback section, or text outside any section.
/// </remarks>
/// <param name="Fragments">Parsed fragment sections in authored order.</param>
/// <param name="FallbackSource">Authored fallback source rendered when no exact fragment key matches.</param>
internal sealed record EventHistoryDocument(IReadOnlyList<EventHistoryFragment> Fragments, string FallbackSource)
{
    /// <summary>Reserved delimiter key opening the mandatory fallback section.</summary>
    public const string FallbackSectionKey = "fallback";

    /// <summary>Fallback contract used when a Mind configures no event-history file (AI-003 TR-16 wording).</summary>
    public const string DefaultFallbackSource = "((Received {{ TypeKey }} event.))";

    private static readonly Regex _delimiterLine = new(
        @"^\s*<!--\s*event-history:(.*?)-->\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Exact keys an authored fragment may target: one entry per concrete observation type (AI-003 TR-13). Extend
    // when a new concrete observation type is introduced.
    private static readonly HashSet<string> _knownFragmentKeys =
        [ObservedSpeechRecord.TypeKeyValue, ObservedVisualDescriptionRecord.TypeKeyValue];

    /// <summary>
    /// Parses the authored file content into individually compiled-at-session-start template sources.
    /// </summary>
    /// <param name="source">Authored event-history file text.</param>
    /// <returns>The parsed document holding every fragment source and the fallback source.</returns>
    /// <exception cref="InvalidOperationException">Thrown with clear authoring guidance when invalid.</exception>
    public static EventHistoryDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<EventHistoryFragment> fragments = [];
        HashSet<string> seenKeys = new(StringComparer.Ordinal);
        string? fallbackSource = null;
        string? currentKey = null;
        List<string> currentContent = [];

        void CloseSection()
        {
            if (currentKey is null)
            {
                return;
            }

            string sectionSource = JoinSectionSource(currentContent);
            if (string.Equals(currentKey, FallbackSectionKey, StringComparison.Ordinal))
            {
                fallbackSource = sectionSource;
            }
            else
            {
                fragments.Add(new EventHistoryFragment(currentKey, sectionSource));
            }

            // 'currentKey' is always reassigned by the caller right after closing on a delimiter, so it is left
            // holding the closed key here; the fresh content list is what matters between sections.
            currentContent = [];
        }

        string normalised = NormaliseLineEndings(source);
        if (normalised.EndsWith('\n'))
        {
            // Drop the artifact of splitting so a file's final newline is not appended to the open section; each
            // section still ends with exactly its authored trailing newline via JoinSectionSource.
            normalised = normalised[..^1];
        }

        string[] lines = normalised.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            Match delimiter = _delimiterLine.Match(line);
            if (!delimiter.Success)
            {
                if (currentKey is not null)
                {
                    currentContent.Add(line);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    throw new InvalidOperationException(
                        $"Event history contains text outside any section on line {index + 1}: '{line.Trim()}'. "
                        + "Every authored line must belong to a '<!-- event-history: ... -->' section.");
                }

                continue;
            }

            CloseSection();
            string sectionKey = delimiter.Groups[1].Value.Trim();
            if (sectionKey.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Event history delimiter on line {index + 1} requires a nonblank TypeKey.");
            }

            if (!seenKeys.Add(sectionKey))
            {
                throw new InvalidOperationException(
                    $"Event history contains duplicate exact TypeKey '{sectionKey}' on line {index + 1}.");
            }

            if (!string.Equals(sectionKey, FallbackSectionKey, StringComparison.Ordinal)
                && !_knownFragmentKeys.Contains(sectionKey))
            {
                throw new InvalidOperationException(
                    $"Event history section on line {index + 1} has unknown TypeKey '{sectionKey}': no known "
                    + "observation type matches this exact key.");
            }

            currentKey = sectionKey;
        }

        CloseSection();

        return fallbackSource is null
            ? throw new InvalidOperationException(
                $"Event history requires a nonblank fallback template: no '<!-- event-history: "
                + $"{FallbackSectionKey} -->' section was found.")
            : string.IsNullOrWhiteSpace(fallbackSource)
                ? throw new InvalidOperationException(
                    "Event history requires a nonblank fallback template: the 'fallback' section is blank.")
                : new EventHistoryDocument([.. fragments], fallbackSource);
    }

    /// <summary>Rebuilds the verbatim section source: authored lines joined with newlines and one trailing newline.</summary>
    private static string JoinSectionSource(List<string> contentLines)
        => contentLines.Count == 0 ? string.Empty : string.Join('\n', contentLines) + "\n";

    private static string NormaliseLineEndings(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
