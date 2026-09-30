using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Renders one captured mind session request cycle as a self-contained Markdown transcript document (AI-011
/// TR-8–TR-11).
/// </summary>
/// <remarks>
/// <para>
/// The formatter is presentational only: every rendered value comes from the live .NET objects the runner captured,
/// never from reparsed JSON log text, so real newlines are preserved by construction (TR-8). It never alters the
/// model-facing instruction text (TR-9).
/// </para>
/// <para>
/// Heading hierarchy (TR-10): document sections render at level two, and the instructions parser plus every message
/// body demote their internal Markdown headings — ATX and setext alike, and never inside fenced code blocks — below
/// their own heading, so nested content never collides with or outranks its section heading.
/// </para>
/// </remarks>
internal static partial class MindSessionTranscriptFormatter
{
    /// <summary>The shallowest heading level permitted inside a level-three container heading.</summary>
    private const int MinimumContentHeadingLevel = 4;

    private const int TurnNumberMinimumDigits = 4;

    private const string LineFeed = "\n";

    private static readonly JsonSerializerOptions _prettyJsonOptions = new() { WriteIndented = true };

    [GeneratedRegex(@"^[ \t]*<[ \t]*([^/>\s][^>]*)>[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenTagLine();

    [GeneratedRegex(@"^[ \t]*</([ \t]*[^>\s][^>]*)>[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CloseTagLine();

    [GeneratedRegex(@"[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// Renders one cycle as a complete Markdown document in the TR-11 order: metadata header; rendered
    /// instructions; request options; the role-labelled request transcript; the response contents with any anomaly
    /// annotations; and one block per tool invocation.
    /// </summary>
    /// <param name="cycle">The captured cycle.</param>
    /// <param name="turnNumber">The per-character turn number assigned by the recorder.</param>
    /// <param name="characterId">The owning character's validated local identifier.</param>
    internal static string Render(MindSessionCycleTranscript cycle, int turnNumber, string characterId)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentOutOfRangeException.ThrowIfLessThan(turnNumber, 1);

        StringBuilder document = new();
        AppendHeader(document, cycle, turnNumber, characterId);
        AppendInstructions(document, cycle.Instructions);
        AppendRequestOptions(document, cycle.RequestOptions);
        AppendMessages(document, "## Request Transcript", "Message", cycle.RequestMessages);
        AppendResponse(document, cycle);
        AppendToolInvocations(document, cycle.ToolInvocations);
        return document.ToString();
    }

    /// <summary>
    /// Parses the pseudo-XML section format produced by <c>PseudoXmlFormatter</c> —
    /// <c>&lt;SectionName&gt;\ncontent\n&lt;/SectionName&gt;</c> blocks — and renders each recognised section as a
    /// level-three Markdown heading with its content nested beneath (AI-011 TR-9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Section-name matching is lax: open and close tags may differ in case and in whitespace runs or tabs inside
    /// or around the name, and names may contain the <c>_</c> characters that replace <c>&lt;</c>, <c>&gt;</c>,
    /// and <c>/</c> during tag-name sanitisation. Content outside recognised blocks passes through verbatim, and an
    /// open tag without a matching close tag is not a recognised block.
    /// </para>
    /// <para>
    /// Internal Markdown headings are demoted below the section heading (TR-10); the formatter never alters the
    /// model-facing instruction text itself.
    /// </para>
    /// </remarks>
    internal static string RenderPseudoXmlSections(string instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        string[] lines = NormaliseLineEndings(instructions).TrimEnd('\n').Split(LineFeed);
        StringBuilder output = new();
        int index = 0;
        while (index < lines.Length)
        {
            Match open = OpenTagLine().Match(lines[index]);
            if (open.Success)
            {
                string normalisedName = NormaliseTagName(open.Groups[1].Value);
                int closeIndex = FindClosingTagIndex(lines, index + 1, normalisedName);
                if (closeIndex > index)
                {
                    AppendSection(output, open.Groups[1].Value, lines, index, closeIndex);
                    index = closeIndex + 1;
                    continue;
                }
            }

            _ = output.Append(lines[index]).Append(LineFeed);
            index++;
        }

        return output.ToString();
    }

    /// <summary>
    /// Demotes every Markdown heading — ATX and setext alike — in the content so no heading is shallower than
    /// <paramref name="minimumLevel" />, clamping at the six levels Markdown defines; setext headings are rewritten
    /// to their demoted ATX form. Headings inside fenced code blocks are left untouched: fences are tracked by
    /// delimiter character and length, so a longer fence may contain shorter runs of the same delimiter without
    /// corrupting the demotion of following headings (AI-011 TR-10).
    /// </summary>
    internal static string DemoteHeadings(string content, int minimumLevel)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumLevel, 1);
        if (content.Length == 0)
        {
            return content;
        }

        string[] lines = content.Split(LineFeed);
        ClassifiedHeading[] classifications = ClassifyHeadings(lines);
        int shallowestLevel = 0;
        foreach (ClassifiedHeading classification in classifications)
        {
            if (classification.Kind != HeadingKind.None
                && (shallowestLevel == 0 || classification.Level < shallowestLevel))
            {
                shallowestLevel = classification.Level;
            }
        }

        if (shallowestLevel == 0 || shallowestLevel >= minimumLevel)
        {
            return content;
        }

        int shift = minimumLevel - shallowestLevel;
        StringBuilder demoted = new();
        for (int index = 0; index < lines.Length; index++)
        {
            if (classifications[index].Kind == HeadingKind.Atx)
            {
                int level = classifications[index].Level;
                int headingStart = lines[index].IndexOf('#');
                _ = demoted
                    .Append(lines[index][..headingStart])
                    .Append('#', Math.Min(6, level + shift))
                    .Append(lines[index][(headingStart + level)..])
                    .Append(LineFeed);
                continue;
            }

            if (index + 1 < lines.Length && classifications[index + 1].Kind == HeadingKind.Setext)
            {
                // The paragraph line is the setext heading's text: it and its underline become one demoted ATX
                // heading.
                _ = demoted
                    .Append('#', Math.Min(6, classifications[index + 1].Level + shift))
                    .Append(' ')
                    .Append(lines[index].TrimStart(' ', '\t'))
                    .Append(LineFeed);
                index++;
                continue;
            }

            _ = demoted.Append(lines[index]).Append(LineFeed);
        }

        return demoted.ToString(0, demoted.Length - 1);
    }

    private enum HeadingKind
    {
        None,
        Atx,
        Setext,
    }

    private readonly record struct ClassifiedHeading(HeadingKind Kind, int Level);

    private readonly record struct OpenFence(char Character, int Length);

    /// <summary>
    /// Classifies each line's heading contribution with correct fence tracking: heading detection only applies
    /// outside a fenced code block, fences close only on a same-character run at least as long as their opening
    /// run, and a setext underline only marks a heading when it directly underlines paragraph text.
    /// </summary>
    private static ClassifiedHeading[] ClassifyHeadings(string[] lines)
    {
        var classifications = new ClassifiedHeading[lines.Length];
        OpenFence? openFence = null;
        bool previousLineIsParagraph = false;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (openFence is not null)
            {
                if (IsClosingFence(line, openFence.Value))
                {
                    openFence = null;
                }

                previousLineIsParagraph = false;
                continue;
            }

            if (TryReadFenceRun(line, out char character, out int length, out string remainder)
                && (character == '~' || !remainder.Contains('`')))
            {
                openFence = new OpenFence(character, length);
                previousLineIsParagraph = false;
                continue;
            }

            if (TryReadAtxHeadingLevel(line, out int atxLevel))
            {
                classifications[index] = new ClassifiedHeading(HeadingKind.Atx, atxLevel);
                previousLineIsParagraph = false;
                continue;
            }

            if (previousLineIsParagraph && TryReadSetextUnderline(line, out int setextLevel))
            {
                classifications[index] = new ClassifiedHeading(HeadingKind.Setext, setextLevel);
                previousLineIsParagraph = false;
                continue;
            }

            previousLineIsParagraph = line.TrimStart(' ', '\t').Length > 0;
        }

        return classifications;
    }

    /// <summary>
    /// Reads a setext underline — a line of <c>=</c> (level one) or <c>-</c> (level two) preceded by at most three
    /// spaces and followed only by whitespace, per Markdown's trailing-whitespace and indentation rules.
    /// </summary>
    private static bool TryReadSetextUnderline(string line, out int level)
    {
        level = 0;
        if (!TryStripOpeningIndent(line, out string trimmed))
        {
            return false;
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        char delimiter = trimmed[0];
        if (delimiter is not ('=' or '-'))
        {
            return false;
        }

        int runLength = 0;
        while (runLength < trimmed.Length && trimmed[runLength] == delimiter)
        {
            runLength++;
        }

        foreach (char character in trimmed[runLength..])
        {
            if (character is not (' ' or '\t'))
            {
                return false;
            }
        }

        level = delimiter == '=' ? 1 : 2;
        return true;
    }

    /// <summary>
    /// Reads a code-fence delimiter run of three or more backticks or tildes starting at the line's beginning with
    /// at most three spaces of indentation; deeper indentation is an indented code block, not a fence.
    /// </summary>
    private static bool TryReadFenceRun(string line, out char character, out int length, out string remainder)
    {
        character = '\0';
        length = 0;
        remainder = string.Empty;
        if (!TryStripOpeningIndent(line, out string trimmed))
        {
            return false;
        }

        if (trimmed.Length < 3)
        {
            return false;
        }

        character = trimmed[0];
        if (character is not ('`' or '~'))
        {
            return false;
        }

        int runLength = 0;
        while (runLength < trimmed.Length && trimmed[runLength] == character)
        {
            runLength++;
        }

        if (runLength < 3)
        {
            return false;
        }

        length = runLength;
        remainder = trimmed[runLength..];
        return true;
    }

    /// <summary>
    /// Strips the line's leading indentation when it stays within Markdown's three-space limit for block openers:
    /// four or more spaces — or any leading tab, which reaches a four-space tab stop — make the line indented code
    /// instead, and the line is rejected.
    /// </summary>
    private static bool TryStripOpeningIndent(string line, out string remainder)
    {
        remainder = string.Empty;
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3 || (indent < line.Length && line[indent] == '\t'))
        {
            return false;
        }

        remainder = line[indent..];
        return true;
    }

    /// <summary>
    /// Determines whether the line closes the open fence: the same delimiter character, at least the opening run's
    /// length, and nothing else on the line.
    /// </summary>
    private static bool IsClosingFence(string line, OpenFence fence)
        => TryReadFenceRun(line, out char character, out int length, out string remainder)
            && character == fence.Character
            && length >= fence.Length
            && remainder.Trim().Length == 0;

    private static void AppendHeader(
        StringBuilder document,
        MindSessionCycleTranscript cycle,
        int turnNumber,
        string characterId)
    {
        _ = document
            .Append("# Mind Session Transcript — Turn ")
            .Append(turnNumber.ToString(CultureInfo.InvariantCulture).PadLeft(TurnNumberMinimumDigits, '0'))
            .Append(LineFeed)
            .Append(LineFeed)
            .Append("- **Character:** ").Append(characterId).Append(LineFeed)
            .Append("- **Request Cycle:** ")
            .Append(cycle.CycleIndex.ToString(CultureInfo.InvariantCulture)).Append(LineFeed)
            .Append("- **Time:** ")
            .Append(cycle.StartedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(LineFeed)
            .Append("- **Latency:** ")
            .Append(cycle.Latency.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture))
            .Append(" s")
            .Append(LineFeed);

        // Absent optional metadata is omitted gracefully rather than rendered as a placeholder (TR-8).
        string? model = cycle.Response?.ModelId ?? cycle.RequestOptions.ModelId;
        if (!string.IsNullOrWhiteSpace(model))
        {
            _ = document.Append("- **Model:** ").Append(model).Append(LineFeed);
        }

        if (DescribeTokenUsage(cycle.Response?.Usage) is { Length: > 0 } tokenUsage)
        {
            _ = document.Append("- **Token Usage:** ").Append(tokenUsage).Append(LineFeed);
        }

        _ = document.Append("- **Outcome:** ").Append(DescribeOutcome(cycle.Outcome));
        if (!string.IsNullOrWhiteSpace(cycle.OutcomeDetail))
        {
            _ = document.Append(" — ").Append(cycle.OutcomeDetail);
        }

        if (cycle.ExchangeDisposal is not MindTranscriptExchangeDisposal.NotApplicable)
        {
            _ = document.Append(LineFeed).Append("- **Exchange Disposal:** ").Append(cycle.ExchangeDisposal);
        }

        _ = document.Append(LineFeed).Append(LineFeed);
    }

    private static void AppendInstructions(StringBuilder document, string instructions)
    {
        _ = document.Append("## Instructions").Append(LineFeed).Append(LineFeed);
        _ = document.Append(RenderPseudoXmlSections(instructions));
        _ = document.Append(LineFeed);
    }

    private static void AppendRequestOptions(StringBuilder document, MindTranscriptRequestOptions options)
    {
        _ = document
            .Append("## Request Options").Append(LineFeed).Append(LineFeed)
            .Append("- **Tool Mode:** ").Append(options.ToolMode).Append(LineFeed)
            .Append("- **Allow Multiple Tool Calls:** ").Append(options.AllowMultipleToolCalls ? "yes" : "no")
            .Append(LineFeed)
            .Append("- **Tools:** ").Append(string.Join(", ", options.ToolNames)).Append(LineFeed)
            .Append(LineFeed);
    }

    private static void AppendMessages(
        StringBuilder document,
        string sectionHeading,
        string messageLabelPrefix,
        IReadOnlyList<ChatMessage> messages)
    {
        _ = document.Append(sectionHeading).Append(LineFeed).Append(LineFeed);
        if (messages.Count == 0)
        {
            _ = document.Append("_(empty)_").Append(LineFeed).Append(LineFeed);
            return;
        }

        for (int index = 0; index < messages.Count; index++)
        {
            AppendMessage(
                document,
                messages[index],
                $"{messageLabelPrefix} {index + 1} of {messages.Count}");
        }
    }

    /// <summary>
    /// Renders the response contents — text, distinctly marked reasoning, and function calls — with the cycle's
    /// anomaly annotations, per the TR-11 document order.
    /// </summary>
    private static void AppendResponse(StringBuilder document, MindSessionCycleTranscript cycle)
    {
        _ = document.Append("## Response").Append(LineFeed).Append(LineFeed);
        ChatResponse? response = cycle.Response;
        switch (response)
        {
            case null:
                _ = document.Append("_(No response — the request was discarded before a response settled.)_")
                    .Append(LineFeed)
                    .Append(LineFeed);
                break;
            case { Messages.Count: 0 }:
                _ = document.Append("_(empty response)_").Append(LineFeed).Append(LineFeed);
                break;
            default:
                for (int index = 0; index < response.Messages.Count; index++)
                {
                    AppendMessage(
                        document,
                        response.Messages[index],
                        $"Response Message {index + 1} of {response.Messages.Count}");
                }

                break;
        }

        if (cycle.Annotations.Count == 0)
        {
            return;
        }

        _ = document.Append("### Anomaly Annotations").Append(LineFeed).Append(LineFeed);
        foreach (MindTranscriptAnnotation annotation in cycle.Annotations)
        {
            _ = document
                .Append("- **").Append(DescribeAnnotationKind(annotation.Kind)).Append(":** ")
                .Append(annotation.Message)
                .Append(LineFeed);
        }

        _ = document.Append(LineFeed);
    }

    private static void AppendMessage(StringBuilder document, ChatMessage message, string label)
    {
        _ = document
            .Append("### ").Append(label).Append(" — ").Append(message.Role.Value)
            .Append(LineFeed)
            .Append(LineFeed);
        if (message.Contents.Count == 0)
        {
            _ = document.Append("_(empty)_").Append(LineFeed).Append(LineFeed);
            return;
        }

        foreach (AIContent content in message.Contents)
        {
            AppendContent(document, content);
        }
    }

    private static void AppendContent(StringBuilder document, AIContent content)
    {
        switch (content)
        {
            case TextContent text:
                AppendTextBlock(document, text.Text);
                break;
            case TextReasoningContent reasoning:
                AppendReasoningBlock(document, reasoning.Text);
                break;
            case FunctionCallContent call:
                AppendFunctionCallBlock(document, call.Name, call.CallId, call.Arguments);
                break;
            case FunctionResultContent result:
                AppendFunctionResultBlock(document, result.CallId, result.Result);
                break;
            default:
                _ = document
                    .Append("_(unrecognised content: ").Append(content.GetType().Name).Append(")_")
                    .Append(LineFeed)
                    .Append(LineFeed);
                break;
        }
    }

    private static void AppendTextBlock(StringBuilder document, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _ = document.Append("_(empty)_").Append(LineFeed).Append(LineFeed);
            return;
        }

        _ = document
            .Append(RenderMessageBody(text))
            .Append(LineFeed)
            .Append(LineFeed);
    }

    /// <summary>
    /// Renders one transcript message body (AI-011 TR-8/TR-9): lax pseudo-XML sections become nested level-four
    /// headings with no raw tags visible, their content and any passthrough text are heading-demoted below the
    /// body's own heading, and consecutive non-blank lines carry explicit Markdown hard line breaks so
    /// single-newline structure stays visible in rendered Markdown. Fenced code content is never modified; the
    /// instructions renderer (<see cref="RenderPseudoXmlSections" />) keeps its own contract.
    /// </summary>
    internal static string RenderMessageBody(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string normalised = NormaliseLineEndings(text).TrimEnd('\n');
        if (normalised.Length == 0)
        {
            return string.Empty;
        }

        string[] lines = normalised.Split(LineFeed);
        // Fence state is computed once over the whole body and preserved across every parsing boundary: section
        // discovery must never read fenced content, even when a fence opens before a section tag (AI-011 TR-9).
        bool[] fenceInterior = ComputeFenceInteriorLines(lines);
        StringBuilder output = new();
        int index = 0;
        while (index < lines.Length)
        {
            if (TryFindSection(lines, fenceInterior, index, out int closeIndex))
            {
                AppendBodySection(output, OpenTagLine().Match(lines[index]).Groups[1].Value, lines, index, closeIndex);
                index = closeIndex + 1;
                continue;
            }

            // Collect the passthrough run up to the next recognised section and render it with heading demotion
            // and explicit hard line breaks.
            int runEnd = index;
            while (runEnd < lines.Length && !TryFindSection(lines, fenceInterior, runEnd, out _))
            {
                runEnd++;
            }

            string chunk = string.Join(LineFeed, lines[index..runEnd]);
            _ = output
                .Append(ApplyHardLineBreaks(DemoteHeadings(chunk, MinimumContentHeadingLevel)))
                .Append(LineFeed);
            index = runEnd;
        }

        return output.ToString().TrimEnd('\n');
    }

    /// <summary>Appends one message-body section as a level-four heading with demoted, hard-broken content.</summary>
    private static void AppendBodySection(
        StringBuilder output,
        string rawTagName,
        string[] lines,
        int openIndex,
        int closeIndex)
    {
        _ = output.Append("#### ").Append(NormaliseTagName(rawTagName)).Append(LineFeed);
        string body = string.Join(LineFeed, lines[(openIndex + 1)..closeIndex]);
        if (body.Length > 0)
        {
            // Section content demotes one level below the body's section headings.
            _ = output
                .Append(LineFeed)
                .Append(ApplyHardLineBreaks(DemoteHeadings(body, MinimumContentHeadingLevel + 1)))
                .Append(LineFeed);
        }

        _ = output.Append(LineFeed);
    }

    /// <summary>
    /// Determines whether the line opens a recognised lax pseudo-XML section and finds its closing tag, ignoring
    /// any tag candidate inside a fenced code region (AI-011 TR-9).
    /// </summary>
    private static bool TryFindSection(string[] lines, bool[] fenceInterior, int openIndex, out int closeIndex)
    {
        closeIndex = -1;
        if (fenceInterior[openIndex])
        {
            return false;
        }

        Match open = OpenTagLine().Match(lines[openIndex]);
        if (!open.Success)
        {
            return false;
        }

        string normalisedName = NormaliseTagName(open.Groups[1].Value);
        for (int candidate = openIndex + 1; candidate < lines.Length; candidate++)
        {
            if (fenceInterior[candidate])
            {
                continue;
            }

            Match close = CloseTagLine().Match(lines[candidate]);
            if (close.Success
                && string.Equals(NormaliseTagName(close.Groups[1].Value), normalisedName, StringComparison.OrdinalIgnoreCase))
            {
                closeIndex = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Computes which lines sit inside fenced code regions over a complete line set, so consumers can keep fence
    /// state across parsing boundaries; fence delimiter lines themselves are not interior (AI-011 TR-9).
    /// </summary>
    private static bool[] ComputeFenceInteriorLines(string[] lines)
    {
        bool[] interior = new bool[lines.Length];
        OpenFence? openFence = null;
        for (int index = 0; index < lines.Length; index++)
        {
            if (openFence is not null)
            {
                if (IsClosingFence(lines[index], openFence.Value))
                {
                    // The closing delimiter is structural, not interior.
                    openFence = null;
                }
                else
                {
                    interior[index] = true;
                }
            }
            else if (TryReadFenceRun(lines[index], out char character, out int length, out string remainder)
                && (character == '~' || !remainder.Contains('`')))
            {
                openFence = new OpenFence(character, length);
            }
        }

        return interior;
    }

    /// <summary>
    /// Appends CommonMark's explicit hard-line-break marker — a trailing backslash, visible in plain text and
    /// robust in GitHub-style renderers, unlike trailing spaces that editors strip — but only between two plain
    /// paragraph-continuation lines (AI-011 TR-8). The rule never corrupts rendered Markdown: a line ending in an
    /// odd-length backslash run already hard-breaks and stays untouched, an even-length run is an escaped literal
    /// that receives the marker so the next line stays visible without corrupting the literal, and heading,
    /// list-item, blockquote, fence, thematic break, and indented-code lines are block boundaries that neither
    /// receive markers nor cause the previous line to be marked, so an inserted marker can never render as visible
    /// punctuation.
    /// </summary>
    private static string ApplyHardLineBreaks(string content)
    {
        if (content.Length == 0)
        {
            return content;
        }

        string[] lines = content.Split(LineFeed);
        ClassifiedHeading[] classifications = ClassifyHeadings(lines);
        StringBuilder result = new();
        OpenFence? openFence = null;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (openFence is not null)
            {
                if (IsClosingFence(line, openFence.Value))
                {
                    openFence = null;
                }
            }
            else if (TryReadFenceRun(line, out char character, out int length, out string remainder)
                && (character == '~' || !remainder.Contains('`')))
            {
                openFence = new OpenFence(character, length);
            }
            else if (classifications[index].Kind == HeadingKind.None
                && !EndsWithHardBreakEscape(line)
                && !IsBlankLine(line)
                && !IsBlockBoundaryLine(line, classifications[index])
                && index + 1 < lines.Length
                && !IsBlankLine(lines[index + 1])
                && !IsBlockBoundaryLine(lines[index + 1], classifications[index + 1]))
            {
                _ = result.Append(line).Append('\\').Append(LineFeed);
                continue;
            }

            _ = result.Append(line).Append(LineFeed);
        }

        return result.ToString(0, result.Length - 1);
    }

    private static bool IsBlankLine(string line)
        => line.TrimStart(' ', '\t').Length == 0;

    /// <summary>
    /// Determines whether the line already ends in a hard-break escape by counting its trailing backslash run
    /// (AI-011 TR-8): an odd-length run is CommonMark's hard line break — the line already breaks and is left
    /// untouched — while an even-length run of two or more is an escaped literal backslash, which folds softly and
    /// still needs a marker appended; appending one backslash turns the run odd, preserving the literal and adding
    /// the break.
    /// </summary>
    private static bool EndsWithHardBreakEscape(string line)
    {
        int run = 0;
        for (int index = line.Length - 1; index >= 0 && line[index] == '\\'; index--)
        {
            run++;
        }

        return run % 2 == 1;
    }

    /// <summary>
    /// Determines whether the line starts a new Markdown block rather than continuing a paragraph, using the same
    /// three-space indentation limit as every other block opener (AI-011 TR-8).
    /// </summary>
    private static bool IsBlockBoundaryLine(string line, ClassifiedHeading classification)
    {
        if (classification.Kind != HeadingKind.None || StartsFenceDelimiter(line))
        {
            return true;
        }

        // Four-plus spaces of indentation — or a leading tab — make the line indented code, not paragraph text.
        return !TryStripOpeningIndent(line, out string trimmed)
            || trimmed.Length == 0
            || trimmed[0] == '>'
            || StartsListItem(trimmed)
            || IsThematicBreak(trimmed);
    }

    private static bool StartsFenceDelimiter(string line)
        => TryReadFenceRun(line, out _, out _, out _);

    /// <summary>Reads a list-item marker: bullet (<c>-</c>, <c>*</c>, <c>+</c>) or ordered digits followed by
    /// <c>.</c> or <c>)</c>, each followed by whitespace or the line's end.</summary>
    private static bool StartsListItem(string trimmed)
    {
        char bullet = trimmed[0];
        if (bullet is '-' or '*' or '+')
        {
            return trimmed.Length == 1 || char.IsWhiteSpace(trimmed[1]);
        }

        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]) && digits < 9)
        {
            digits++;
        }

        return digits > 0
            && digits < trimmed.Length
            && trimmed[digits] is '.' or ')'
            && (digits + 1 == trimmed.Length || char.IsWhiteSpace(trimmed[digits + 1]));
    }

    /// <summary>Reads a thematic break: three or more repeated <c>-</c>, <c>*</c>, or <c>_</c> characters, optionally
    /// space-separated.</summary>
    private static bool IsThematicBreak(string trimmed)
    {
        char candidate = trimmed[0];
        if (candidate is not ('-' or '*' or '_'))
        {
            return false;
        }

        foreach (char character in trimmed)
        {
            if (character != candidate && !char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Renders reasoning content as a labelled blockquote so it stays visibly distinct from text (TR-11).</summary>
    private static void AppendReasoningBlock(StringBuilder document, string? text)
    {
        _ = document.Append("> **Reasoning**").Append(LineFeed);
        string normalised = NormaliseLineEndings(text ?? string.Empty).TrimEnd('\n');
        if (normalised.Length == 0)
        {
            _ = document.Append(">").Append(LineFeed).Append(LineFeed);
            return;
        }

        foreach (string line in normalised.Split(LineFeed))
        {
            _ = document.Append(line.Length == 0 ? ">" : "> " + line).Append(LineFeed);
        }

        _ = document.Append(LineFeed);
    }

    private static void AppendFunctionCallBlock(
        StringBuilder document,
        string? toolName,
        string? callId,
        IDictionary<string, object?>? arguments)
    {
        _ = document.Append("**Function Call:** ").Append(toolName ?? "(unknown)");
        AppendCallIdentifier(document, callId);
        _ = document.Append(LineFeed).Append(LineFeed);
        AppendJsonBlock(document, arguments);
    }

    private static void AppendFunctionResultBlock(StringBuilder document, string? callId, object? result)
    {
        _ = document.Append("**Tool Result**");
        AppendCallIdentifier(document, callId);
        _ = document
            .Append(LineFeed)
            .Append(LineFeed)
            .Append(RenderResultText(result))
            .Append(LineFeed)
            .Append(LineFeed);
    }

    /// <summary>
    /// Renders a delivered tool result: textual results are transcript message bodies — lax pseudo-XML sections
    /// become nested headings and single-newline lines stay visible (AI-011 TR-9) — while structured results keep
    /// their existing value rendering.
    /// </summary>
    private static string RenderResultText(object? result)
        => result is string { Length: > 0 } text ? RenderMessageBody(text) : RenderResult(result);

    private static void AppendCallIdentifier(StringBuilder document, string? callId)
    {
        if (!string.IsNullOrEmpty(callId))
        {
            _ = document.Append(" — call `").Append(callId).Append('`');
        }
    }

    private static void AppendToolInvocations(
        StringBuilder document,
        IReadOnlyList<MindTranscriptToolInvocation> invocations)
    {
        if (invocations.Count == 0)
        {
            return;
        }

        _ = document.Append("## Tool Invocations").Append(LineFeed).Append(LineFeed);
        foreach (MindTranscriptToolInvocation invocation in invocations)
        {
            _ = document.Append("### ").Append(invocation.ToolName);
            AppendCallIdentifier(document, invocation.CallId);
            if (invocation.Status == MindTranscriptToolInvocationStatus.SkippedByInvalidation)
            {
                _ = document.Append(" (skipped — batch invalidated)");
            }
            else if (invocation.Status == MindTranscriptToolInvocationStatus.Interrupted)
            {
                _ = document.Append(" (interrupted — no result delivered)");
            }

            _ = document
                .Append(LineFeed)
                .Append(LineFeed)
                .Append("**Arguments**")
                .Append(LineFeed)
                .Append(LineFeed);
            AppendJsonBlock(document, invocation.Arguments);
            _ = document.Append("**Result**").Append(LineFeed).Append(LineFeed);
            _ = invocation.Status == MindTranscriptToolInvocationStatus.Interrupted
                ? document.Append("_(interrupted before a result was delivered)_").Append(LineFeed).Append(LineFeed)
                : document.Append(RenderResultText(invocation.Result)).Append(LineFeed).Append(LineFeed);
        }
    }

    private static void AppendJsonBlock(StringBuilder document, IDictionary<string, object?>? arguments)
    {
        _ = document
            .Append("```json")
            .Append(LineFeed)
            .Append(arguments is null ? "{}" : PrettyPrintJson(arguments))
            .Append(LineFeed)
            .Append("```")
            .Append(LineFeed)
            .Append(LineFeed);
    }

    private static string PrettyPrintJson(IDictionary<string, object?> arguments)
    {
        try
        {
            return JsonSerializer.Serialize(arguments, _prettyJsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return string.Join(LineFeed, arguments.Select(static pair => $"{pair.Key}: {pair.Value}"));
        }
    }

    private static string RenderResult(object? result)
        => result switch
        {
            null => "_(null)_",
            string text => text.Length == 0 ? "_(empty)_" : text,
            _ => PrettyPrintValue(result),
        };

    private static string PrettyPrintValue(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, _prettyJsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static string DescribeTokenUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return string.Empty;
        }

        List<string> parts = [];
        if (usage.InputTokenCount is { } inputTokens)
        {
            parts.Add($"{inputTokens.ToString(CultureInfo.InvariantCulture)} input");
        }

        if (usage.OutputTokenCount is { } outputTokens)
        {
            parts.Add($"{outputTokens.ToString(CultureInfo.InvariantCulture)} output");
        }

        return string.Join(" / ", parts);
    }

    private static string DescribeOutcome(MindTranscriptCycleOutcome outcome)
        => outcome switch
        {
            MindTranscriptCycleOutcome.Accepted => "Accepted",
            MindTranscriptCycleOutcome.DiscardedBeforeResponse => "Discarded Before Response",
            MindTranscriptCycleOutcome.DiscardedStaleResponse => "Discarded Stale Response",
            MindTranscriptCycleOutcome.InvalidResponseRecoveryScheduled => "Invalid Response — Recovery Scheduled",
            MindTranscriptCycleOutcome.InvalidResponseRecoverySuperseded => "Invalid Response — Recovery Superseded",
            MindTranscriptCycleOutcome.RecoveryExhausted => "Invalid Response — Recovery Budget Exhausted",
            MindTranscriptCycleOutcome.Failed => "Failed",
            MindTranscriptCycleOutcome.Interrupted => "Interrupted",
            _ => outcome.ToString(),
        };

    private static string DescribeAnnotationKind(MindTranscriptAnnotationKind kind)
        => kind switch
        {
            MindTranscriptAnnotationKind.TransportRetry => "Transport Retry",
            MindTranscriptAnnotationKind.InvalidResponseRecovery => "Invalid Response Recovery",
            MindTranscriptAnnotationKind.FreshTurnInvalidation => "Fresh-Turn Invalidation",
            _ => kind.ToString(),
        };

    private static void AppendSection(
        StringBuilder output,
        string rawTagName,
        string[] lines,
        int openIndex,
        int closeIndex)
    {
        _ = output.Append("### ").Append(NormaliseTagName(rawTagName)).Append(LineFeed);
        string body = string.Join(LineFeed, lines[(openIndex + 1)..closeIndex]);
        if (body.Length > 0)
        {
            _ = output
                .Append(LineFeed)
                .Append(DemoteHeadings(body, MinimumContentHeadingLevel))
                .Append(LineFeed);
        }

        _ = output.Append(LineFeed);
    }

    private static int FindClosingTagIndex(string[] lines, int startIndex, string normalisedName)
    {
        for (int index = startIndex; index < lines.Length; index++)
        {
            Match close = CloseTagLine().Match(lines[index]);
            if (close.Success
                && string.Equals(NormaliseTagName(close.Groups[1].Value), normalisedName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Trims and collapses whitespace runs and tabs to single spaces, preserving authored casing.</summary>
    private static string NormaliseTagName(string rawTagName)
        => WhitespaceRun().Replace(rawTagName.Trim(), " ");

    private static string NormaliseLineEndings(string text)
        => text.Replace("\r\n", LineFeed).Replace('\r', '\n');

    /// <summary>Reads the ATX heading level of a line: one to six hashes preceded by at most three spaces.</summary>
    private static bool TryReadAtxHeadingLevel(string line, out int level)
    {
        level = 0;
        int index = 0;
        while (index < line.Length && line[index] == ' ' && index < 3)
        {
            index++;
        }

        int hashes = 0;
        while (index + hashes < line.Length && line[index + hashes] == '#' && hashes < 6)
        {
            hashes++;
        }

        if (hashes == 0
            || (index + hashes < line.Length && line[index + hashes] is not (' ' or '\t')))
        {
            return false;
        }

        level = hashes;
        return true;
    }
}
