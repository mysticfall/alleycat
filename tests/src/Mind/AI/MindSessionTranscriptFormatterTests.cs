using AlleyCat.Mind.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Tests the mind session transcript Markdown formatter (AI-011 TR-8–TR-11): the lax pseudo-XML section parser,
/// heading demotion, role-labelled transcript rendering, distinct reasoning marking, and tool blocks with
/// pretty-printed JSON arguments and results.
/// </summary>
public sealed class MindSessionTranscriptFormatterTests
{
    private const string LineFeed = "\n";

    /// <summary>Lax section names — whitespace runs, tabs, mixed case, and sanitised characters — parse as sections.</summary>
    [Fact]
    public void RenderPseudoXmlSections_AcceptsLaxSectionNames()
    {
        string instructions = "Preamble" + LineFeed
            + "<Identity  Summary>" + LineFeed
            + "You are Luna." + LineFeed
            + "</identity SUMMARY>" + LineFeed
            + "<Core\tBehaviour>" + LineFeed
            + "Stay curious." + LineFeed
            + "</core behaviour>" + LineFeed
            + "<  Audio_Path\t>" + LineFeed
            + "Speak softly." + LineFeed
            + "</Audio_Path>" + LineFeed;

        string markdown = MindSessionTranscriptFormatter.RenderPseudoXmlSections(instructions);

        // Whitespace runs, tabs, mixed case, and sanitised '_' characters all resolve to one section each; the
        // sanitised underscore itself is preserved in the rendered heading.
        Assert.Contains("### Identity Summary" + LineFeed, markdown);
        Assert.Contains("### Core Behaviour" + LineFeed, markdown);
        Assert.Contains("### Audio_Path" + LineFeed, markdown);
        Assert.Contains("You are Luna." + LineFeed, markdown);
        Assert.Contains("Stay curious." + LineFeed, markdown);
        Assert.Contains("Speak softly." + LineFeed, markdown);
    }

    /// <summary>Permitted whitespace around a tag's section name — including leading runs and tabs — still parses.</summary>
    [Fact]
    public void RenderPseudoXmlSections_AcceptsSurroundingWhitespaceInsideTags()
    {
        const string instructions = "<  Identity\t>\nYou are Luna.\n</  identity  >\n";

        string markdown = MindSessionTranscriptFormatter.RenderPseudoXmlSections(instructions);

        Assert.Equal("### Identity\n\nYou are Luna.\n\n", markdown);
    }

    /// <summary>Content outside recognised blocks — including unmatched tags — passes through verbatim.</summary>
    [Fact]
    public void RenderPseudoXmlSections_PassesContentOutsideRecognisedBlocksThroughVerbatim()
    {
        string instructions = "Free introduction." + LineFeed
            + "<Unclosed>" + LineFeed
            + "Interior of an unmatched tag." + LineFeed
            + "Trailing free text." + LineFeed;

        string markdown = MindSessionTranscriptFormatter.RenderPseudoXmlSections(instructions);

        Assert.DoesNotContain("### Unclosed", markdown);
        Assert.Equal(instructions, markdown);
    }

    /// <summary>
    /// Internal Markdown headings are demoted below the section heading, clamped at six levels, and fenced content
    /// stays untouched (AI-011 TR-10).
    /// </summary>
    [Fact]
    public void RenderPseudoXmlSections_DemotesInternalHeadingsBelowTheSectionHeading()
    {
        string instructions = "<Guide>" + LineFeed
            + "# Top Level" + LineFeed
            + "## Nested" + LineFeed
            + "###### Deepest" + LineFeed
            + "####### Not a heading" + LineFeed
            + "Plain text with # inline hash" + LineFeed
            + "```" + LineFeed
            + "# Fenced comment" + LineFeed
            + "```" + LineFeed
            + "</Guide>" + LineFeed;

        string markdown = MindSessionTranscriptFormatter.RenderPseudoXmlSections(instructions);

        Assert.Contains("### Guide" + LineFeed, markdown);
        // The section heading appears exactly once: no internal heading collides with or outranks it.
        Assert.Equal(1, markdown.Split("### Guide").Length - 1);
        Assert.Contains("#### Top Level" + LineFeed, markdown);
        Assert.Contains("##### Nested" + LineFeed, markdown);
        Assert.Contains("###### Deepest" + LineFeed, markdown);
        // Demotion clamps at six levels and never rewrites non-heading text or fenced content.
        Assert.Contains("####### Not a heading" + LineFeed, markdown);
        Assert.Contains("Plain text with # inline hash" + LineFeed, markdown);
        Assert.Contains("# Fenced comment" + LineFeed, markdown);
    }

    /// <summary>Heading demotion leaves heading-free content byte-identical.</summary>
    [Fact]
    public void DemoteHeadings_KeepsContentWithoutHeadingsUnchanged()
    {
        const string content = "Just text.\n\nAnother paragraph.";

        string result = MindSessionTranscriptFormatter.DemoteHeadings(content, minimumLevel: 4);

        Assert.Equal(content, result);
    }

    /// <summary>
    /// Setext headings are demoted like ATX headings: the text-plus-underline pair becomes one ATX heading at the
    /// demoted level, never outranking its section heading (AI-011 TR-10).
    /// </summary>
    [Fact]
    public void DemoteHeadings_DemotesSetextHeadingsToTheirDemotedAtxForm()
    {
        const string content = "Title\n===\nBody\nSubheading\n---\nTail";

        string result = MindSessionTranscriptFormatter.DemoteHeadings(content, minimumLevel: 4);

        Assert.Equal("#### Title\nBody\n##### Subheading\nTail", result);
    }

    /// <summary>
    /// Trailing whitespace is legal on a setext underline, so such a line pair is still a real heading and must be
    /// demoted rather than left able to outrank its section heading (AI-011 TR-10).
    /// </summary>
    [Fact]
    public void DemoteHeadings_AcceptsTrailingWhitespaceOnSetextUnderlines()
    {
        const string content = "Title\n=== \t\nBody\nSubheading\n---  \nTail";

        string result = MindSessionTranscriptFormatter.DemoteHeadings(content, minimumLevel: 4);

        Assert.Equal("#### Title\nBody\n##### Subheading\nTail", result);
    }

    /// <summary>
    /// A four-space-indented backtick line is indented code, not a fence opener, so it must not swallow the
    /// demotion of a following real heading (AI-011 TR-10).
    /// </summary>
    [Fact]
    public void DemoteHeadings_TreatsFourSpaceIndentedFenceLinesAsIndentedCode()
    {
        const string content = "    ```\n# Heading\n    ```";

        string result = MindSessionTranscriptFormatter.DemoteHeadings(content, minimumLevel: 4);

        Assert.Equal("    ```\n#### Heading\n    ```", result);
    }

    /// <summary>
    /// Up to three spaces of indentation still open a real fence whose contents stay untouched (AI-011 TR-10).
    /// </summary>
    [Fact]
    public void DemoteHeadings_OpensFencesIndentedUpToThreeSpaces()
    {
        const string content = "   ```\n# Fenced comment\n   ```\n# Heading";

        string result = MindSessionTranscriptFormatter.DemoteHeadings(content, minimumLevel: 4);

        Assert.Equal("   ```\n# Fenced comment\n   ```\n#### Heading", result);
    }

    /// <summary>
    /// A horizontal rule after a blank line is not a setext underline and passes through unchanged.
    /// </summary>
    [Fact]
    public void DemoteHeadings_LeavesThematicBreaksAfterBlankLinesUnchanged()
    {
        const string content = "# Heading\n\n---\nTail";

        string result = MindSessionTranscriptFormatter.DemoteHeadings(content, minimumLevel: 4);

        Assert.Equal("#### Heading\n\n---\nTail", result);
    }

    /// <summary>
    /// Fence tracking is delimiter- and length-correct: a four-backtick fence containing a three-backtick line stays
    /// one code block, so the heading after the closing fence still demotes (AI-011 TR-10).
    /// </summary>
    [Fact]
    public void DemoteHeadings_TracksFenceCharacterAndLengthSoNestedRunsDoNotCorruptDemotion()
    {
        const string content = "````markdown\n```\n# Fenced comment\n````\n# Real heading";

        string result = MindSessionTranscriptFormatter.DemoteHeadings(content, minimumLevel: 4);

        Assert.Equal("````markdown\n```\n# Fenced comment\n````\n#### Real heading", result);
    }

    /// <summary>
    /// A tilde fence does not close on a backtick fence line, and an opening backtick fence with an info string
    /// containing backticks never opens a code block (AI-011 TR-10).
    /// </summary>
    [Fact]
    public void DemoteHeadings_DistinguishesFenceDelimiterCharacters()
    {
        const string content = "~~~\n```\n# Fenced comment\n~~~\n## Real heading";

        string result = MindSessionTranscriptFormatter.DemoteHeadings(content, minimumLevel: 4);

        Assert.Equal("~~~\n```\n# Fenced comment\n~~~\n#### Real heading", result);
    }

    /// <summary>The request transcript renders every message with a role label and preserved real newlines.</summary>
    [Fact]
    public void Render_RendersRoleLabelledRequestTranscriptWithRealNewlines()
    {
        ChatMessage timeline = new(
            ChatRole.User,
            "<Established Event History>" + LineFeed + "- sunrise" + LineFeed + "- footsteps" + LineFeed
            + "</Established Event History>");
        ChatMessage call = new(
            ChatRole.Assistant,
            [new FunctionCallContent("call-1", "speak", new Dictionary<string, object?> { ["speech"] = "Hello" })]);
        ChatMessage toolResult = new(ChatRole.Tool, [new FunctionResultContent("call-1", "spoken")]);

        string markdown = RenderDefaultCycle(requestMessages: [timeline, call, toolResult]);

        Assert.Contains("## Request Transcript" + LineFeed, markdown);
        Assert.Contains("### Message 1 of 3 — user" + LineFeed, markdown);
        Assert.Contains("### Message 2 of 3 — assistant" + LineFeed, markdown);
        Assert.Contains("### Message 3 of 3 — tool" + LineFeed, markdown);
        // Real newlines from the live message text stay visible: the body's section renders as a heading and its
        // list entries render as clean list items — no break markers on block-boundary lines (AI-011 TR-8/TR-9).
        Assert.Contains("#### Established Event History", markdown);
        Assert.Contains("- sunrise" + LineFeed + "- footsteps", markdown);
        string html = RenderHtml(markdown);
        Assert.Contains("<li>sunrise</li>", html);
        Assert.Contains("<li>footsteps</li>", html);
        Assert.DoesNotContain("\\", html);
        Assert.Contains("**Function Call:** speak — call `call-1`", markdown);
        Assert.Contains("**Tool Result** — call `call-1`", markdown);
        Assert.Contains("spoken", markdown);
    }

    /// <summary>
    /// The reported legibility defect: single-newline joins in a message body stay visible as explicit Markdown
    /// hard line breaks — verified through a CommonMark rendering — while blank-line paragraph breaks and fenced
    /// code content stay untouched (AI-011 TR-8).
    /// </summary>
    [Fact]
    public void Render_KeepsSingleNewlineMessageBodyLinesVisibleWithHardLineBreaks()
    {
        ChatMessage body = new(
            ChatRole.User,
            "line one" + LineFeed + "line two" + LineFeed + LineFeed + "```" + LineFeed + "fenced one" + LineFeed
            + "fenced two" + LineFeed + "```");

        string markdown = RenderDefaultCycle(requestMessages: [body]);

        // Rendered outcome: the two paragraph lines join through a hard break, not a soft fold, and no literal
        // backslash is visible anywhere in the rendered document.
        string html = RenderHtml(markdown);
        Assert.Contains("<br", html);
        Assert.Contains("line one", html);
        Assert.Contains("line two", html);
        Assert.DoesNotContain("\\", html);
        // Fenced content renders as a code block with both lines preserved and unmodified.
        Assert.Contains("<code>", html);
        Assert.Contains("fenced one" + LineFeed + "fenced two", html);
    }

    /// <summary>
    /// Message bodies parse their lax pseudo-XML sections into nested headings with no raw tags visible; list
    /// entries render as their own list items with no visible break markers (AI-011 TR-9, TR-8).
    /// </summary>
    [Fact]
    public void Render_ParsesMessageBodySectionsIntoNestedHeadingsWithoutRawTags()
    {
        ChatMessage timeline = new(
            ChatRole.User,
            "<Established Event History>" + LineFeed + "- sunrise" + LineFeed + "- footsteps" + LineFeed
            + "</Established Event History>" + LineFeed + LineFeed
            + "<New Since Your Previous Response>" + LineFeed + "First light broke over the ridge." + LineFeed
            + "Footsteps crossed the yard." + LineFeed
            + "</New Since Your Previous Response>");

        string markdown = RenderDefaultCycle(requestMessages: [timeline]);

        Assert.Contains("#### Established Event History", markdown);
        Assert.Contains("#### New Since Your Previous Response", markdown);
        Assert.DoesNotContain("<Established Event History>", markdown);
        Assert.DoesNotContain("</New Since Your Previous Response>", markdown);
        // List entries stay clean list items — never carrying a break marker that would render as punctuation.
        Assert.Contains("- sunrise" + LineFeed + "- footsteps", markdown);
        // Plain-text paragraph lines inside a section keep their single-newline join visible as a hard break.
        Assert.Contains("First light broke over the ridge.\\" + LineFeed, markdown);
        string html = RenderHtml(markdown);
        Assert.Contains("<li>sunrise</li>", html);
        Assert.Contains("<li>footsteps</li>", html);
        Assert.DoesNotContain("\\", html);
    }

    /// <summary>
    /// Pseudo-XML tags inside a fenced code region of a message body pass through unmodified: no section heading,
    /// no break markers (AI-011 TR-8/TR-9).
    /// </summary>
    [Fact]
    public void Render_LeavesPseudoXmlTagsInsideFencedMessageBodyContentUnmodified()
    {
        ChatMessage body = new(
            ChatRole.User,
            "```html" + LineFeed + "<Example>" + LineFeed + "content" + LineFeed + "</Example>" + LineFeed + "```");

        string markdown = RenderDefaultCycle(requestMessages: [body]);

        Assert.DoesNotContain("#### Example", markdown);
        Assert.Contains("```html" + LineFeed + "<Example>" + LineFeed + "content" + LineFeed + "</Example>" + LineFeed + "```", markdown);
        Assert.DoesNotContain("content\\", markdown);
    }

    /// <summary>
    /// A closing tag inside fenced section content does not terminate its enclosing section: the section match is
    /// fence-aware and spans to its real closing tag, with fence state preserved across the parse (AI-011 TR-9).
    /// </summary>
    [Fact]
    public void Render_IgnoresClosingTagsInsideFencedContentWhenMatchingBodySections()
    {
        ChatMessage body = new(
            ChatRole.User,
            "<Guide>" + LineFeed + "intro" + LineFeed + "```" + LineFeed + "</Guide>" + LineFeed + "```" + LineFeed
            + "tail" + LineFeed + "</Guide>");

        string markdown = RenderDefaultCycle(requestMessages: [body]);

        Assert.Contains("#### Guide", markdown);
        // Both content lines belong to the section, after its heading.
        int heading = markdown.IndexOf("#### Guide", StringComparison.Ordinal);
        int tail = markdown.IndexOf("tail", StringComparison.Ordinal);
        Assert.True(tail > heading, $"The fenced trailer and tail must render inside the section:\n{markdown}");
        // A fence opener is a block boundary: the plain intro line before it never carries a break marker.
        Assert.DoesNotContain("intro\\", markdown);
        // The only visible closing tag is the one inside the fence; the section's real closing tag is consumed.
        Assert.Equal(1, markdown.Split("</Guide>").Length - 1);
    }

    /// <summary>
    /// A pre-existing trailing-backslash hard break is preserved exactly: the renderer never doubles the marker,
    /// which would escape it into a visible literal and remove the break (AI-011 TR-8).
    /// </summary>
    [Fact]
    public void Render_PreservesExistingTrailingBackslashHardBreaksAsOne()
    {
        ChatMessage body = new(
            ChatRole.User,
            "already broken\\" + LineFeed + "next line");

        string markdown = RenderDefaultCycle(requestMessages: [body]);

        Assert.Contains("already broken\\" + LineFeed + "next line", markdown);
        Assert.DoesNotContain("already broken\\\\", markdown);
        // Rendered outcome: exactly one hard break and no literal backslash in the document.
        string html = RenderHtml(markdown);
        Assert.Contains("<br", html);
        Assert.DoesNotContain("\\", html);
    }

    /// <summary>
    /// An even-length trailing backslash run is an escaped literal, not a hard break: the line keeps its literal
    /// backslash(es) and still receives a break marker so the following line stays visibly separate, while
    /// odd-length runs keep their existing natural break untouched (AI-011 TR-8).
    /// </summary>
    [Fact]
    public void Render_PreservesEscapedLiteralBackslashesWhileKeepingLinesVisible()
    {
        ChatMessage body = new(
            ChatRole.User,
            "odd escape\\" + LineFeed + "triple escape\\\\\\" + LineFeed + "even escape\\\\" + LineFeed
            + "final line");

        string markdown = RenderDefaultCycle(requestMessages: [body]);

        string html = RenderHtml(markdown);
        // Odd-length runs already break naturally and are left untouched.
        Assert.Contains("odd escape", html);
        Assert.Contains("triple escape\\", html);
        // The even-length run keeps its literal backslash and still breaks before the next line.
        Assert.Contains("even escape\\", html);
        Assert.Contains("even escape\\<br", html);
        Assert.Equal(3, html.Split("<br").Length - 1);
        Assert.DoesNotContain("\\\\", html);
    }

    /// <summary>
    /// Break markers never land at Markdown block boundaries: headings, list items, and blockquote lines are never
    /// marked, and no line before such a block start carries a marker that would render as visible punctuation
    /// (AI-011 TR-8).
    /// </summary>
    [Fact]
    public void Render_NeverAddsBreakMarkersAtBlockBoundaries()
    {
        ChatMessage body = new(
            ChatRole.User,
            "text" + LineFeed + "#### Heading" + LineFeed + "- one" + LineFeed + "- two" + LineFeed + "> quoted");

        string markdown = RenderDefaultCycle(requestMessages: [body]);

        Assert.DoesNotContain("text\\", markdown);
        Assert.DoesNotContain("Heading\\", markdown);
        Assert.DoesNotContain("- one\\", markdown);
        Assert.DoesNotContain("- two\\", markdown);
        string html = RenderHtml(markdown);
        Assert.Contains("<h4>Heading</h4>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<li>two</li>", html);
        Assert.Contains("<blockquote>", html);
        Assert.DoesNotContain("\\", html);
    }

    /// <summary>
    /// Retained textual tool results render as message bodies — pseudo-XML sections become nested headings with no
    /// raw tags, and single-newline lines stay visible — while structured results keep their existing rendering
    /// (AI-011 TR-9).
    /// </summary>
    [Fact]
    public void Render_RendersRetainedTextualToolResultsAsMessageBodies()
    {
        ChatMessage loreResult = new(
            ChatRole.Tool,
            [new FunctionResultContent(
                "call-1",
                "<Lore>" + LineFeed + "vadim.charter — the founding charter." + LineFeed
                + "An older law supersedes it." + LineFeed + "</Lore>")]);
        MindSessionCycleTranscript cycle = CreateCycle(
            requestMessages: [new ChatMessage(ChatRole.User, "Begin."), loreResult]);
        cycle.ToolInvocations.Add(new MindTranscriptToolInvocation(
            "read_lore",
            "call-2",
            new Dictionary<string, object?> { ["entry_id"] = "vadim.charter" })
        {
            Status = MindTranscriptToolInvocationStatus.Completed,
            Result = "<Lore>" + LineFeed + "only line" + LineFeed + "</Lore>",
        });
        cycle.ToolInvocations.Add(new MindTranscriptToolInvocation("wait", "call-3", null)
        {
            Status = MindTranscriptToolInvocationStatus.Completed,
            Result = 42,
        });

        string markdown = MindSessionTranscriptFormatter.Render(cycle, turnNumber: 1, characterId: "luna");

        // The transcript's tool-result message renders the retained lore body as nested headings, raw tags gone.
        Assert.Contains("#### Lore", markdown);
        Assert.DoesNotContain("<Lore>", markdown);
        Assert.DoesNotContain("</Lore>", markdown);
        Assert.Contains("vadim.charter — the founding charter.\\" + LineFeed, markdown);
        // The tool-invocation block's textual result follows the same body rendering.
        Assert.Contains("only line", markdown);
        // Structured results keep their existing rendering: JSON arguments stay fenced pretty-printed JSON and the
        // non-textual result renders through the value renderer, untouched by body rendering.
        Assert.Contains("```json", markdown);
        Assert.Contains("\"entry_id\"", markdown);
        Assert.Contains("**Result**" + LineFeed + LineFeed + "42", markdown);
    }

    /// <summary>Reasoning content renders as a labelled blockquote, visibly distinct from plain text.</summary>
    [Fact]
    public void Render_MarksReasoningContentDistinctly()
    {
        ChatResponse response = new(new ChatMessage(
            ChatRole.Assistant,
            [
                new TextContent("I will speak now."),
                new TextReasoningContent("The player seems to leave;" + LineFeed + "a farewell fits."),
                new FunctionCallContent("call-1", "speak", new Dictionary<string, object?> { ["speech"] = "Farewell" }),
            ]));

        string markdown = RenderDefaultCycle(response: response);

        Assert.Contains("## Response" + LineFeed, markdown);
        Assert.Contains("I will speak now.", markdown);
        // Reasoning renders as a labelled blockquote, visibly distinct from plain text content.
        Assert.Contains("> **Reasoning**" + LineFeed, markdown);
        Assert.Contains("> The player seems to leave;" + LineFeed + "> a farewell fits.", markdown);
    }

    /// <summary>Tool invocation blocks show the tool name, pretty-printed JSON arguments, and the delivered result.</summary>
    [Fact]
    public void Render_RendersToolInvocationBlocksWithPrettyPrintedJsonArgumentsAndResults()
    {
        MindSessionCycleTranscript cycle = CreateCycle();
        cycle.ToolInvocations.Add(new MindTranscriptToolInvocation(
            "speak",
            "call-7",
            new Dictionary<string, object?> { ["speech"] = "Hello there.", ["volume"] = 0.8 })
        {
            Status = MindTranscriptToolInvocationStatus.Completed,
            Result = "The action was cancelled before it completed.",
        });
        cycle.ToolInvocations.Add(new MindTranscriptToolInvocation(
            "wait",
            "call-8",
            new Dictionary<string, object?>())
        {
            Status = MindTranscriptToolInvocationStatus.SkippedByInvalidation,
            Result = "The action was cancelled before it completed.",
        });
        cycle.ToolInvocations.Add(new MindTranscriptToolInvocation("speak", "call-9", null)
        {
            Status = MindTranscriptToolInvocationStatus.Interrupted,
        });

        string markdown = MindSessionTranscriptFormatter.Render(cycle, turnNumber: 12, characterId: "luna");

        Assert.Contains("## Tool Invocations" + LineFeed, markdown);
        Assert.Contains("### speak — call `call-7`" + LineFeed, markdown);
        Assert.Contains("```json" + LineFeed, markdown);
        Assert.Contains("  \"speech\": \"Hello there.\"," + LineFeed, markdown);
        Assert.Contains("  \"volume\": 0.8" + LineFeed, markdown);
        Assert.Contains("The action was cancelled before it completed.", markdown);
        Assert.Contains("### wait — call `call-8` (skipped — batch invalidated)" + LineFeed, markdown);
        Assert.Contains("### speak — call `call-9` (interrupted — no result delivered)" + LineFeed, markdown);
        Assert.Contains("_(interrupted before a result was delivered)_", markdown);
    }

    /// <summary>Absent model and token-usage metadata is omitted gracefully; present metadata renders (TR-8).</summary>
    [Fact]
    public void Render_OmitsAbsentOptionalMetadataAndRendersPresentMetadata()
    {
        MindSessionCycleTranscript absent = CreateCycle();

        string withoutMetadata = MindSessionTranscriptFormatter.Render(absent, turnNumber: 1, characterId: "luna");

        Assert.DoesNotContain("**Model:**", withoutMetadata);
        Assert.DoesNotContain("**Token Usage:**", withoutMetadata);

        ChatResponse present = new(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call-1", "speak", new Dictionary<string, object?> { ["speech"] = "Hi" })]))
        {
            ModelId = "gpt-test",
            Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 34 },
        };

        string withMetadata = MindSessionTranscriptFormatter.Render(
            CreateCycle(response: present),
            turnNumber: 2,
            characterId: "luna");

        Assert.Contains("- **Model:** gpt-test" + LineFeed, withMetadata);
        Assert.Contains("- **Token Usage:** 12 input / 34 output" + LineFeed, withMetadata);
    }

    /// <summary>
    /// The document follows the TR-11 order — metadata header, instructions, request options, request transcript,
    /// response contents with the anomaly annotations, then tool invocations — with annotations rendered inside the
    /// response section, never before the instructions.
    /// </summary>
    [Fact]
    public void Render_PlacesAnomalyAnnotationsWithTheResponseContentsInDocumentOrder()
    {
        MindSessionCycleTranscript cycle = CreateCycle();
        cycle.Annotate(MindTranscriptAnnotationKind.TransportRetry, "Connection reset; retrying in 00:00:01.");
        cycle.Outcome = MindTranscriptCycleOutcome.Accepted;
        cycle.ToolInvocations.Add(new MindTranscriptToolInvocation("speak", "call-1", null)
        {
            Status = MindTranscriptToolInvocationStatus.Completed,
            Result = "spoken",
        });

        string markdown = MindSessionTranscriptFormatter.Render(cycle, turnNumber: 3, characterId: "luna");

        int instructions = markdown.IndexOf("## Instructions", StringComparison.Ordinal);
        int requestOptions = markdown.IndexOf("## Request Options", StringComparison.Ordinal);
        int requestTranscript = markdown.IndexOf("## Request Transcript", StringComparison.Ordinal);
        int response = markdown.IndexOf("## Response", StringComparison.Ordinal);
        int annotations = markdown.IndexOf("### Anomaly Annotations", StringComparison.Ordinal);
        int toolInvocations = markdown.IndexOf("## Tool Invocations", StringComparison.Ordinal);
        Assert.True(instructions >= 0, $"Missing instructions section:\n{markdown}");
        Assert.True(requestOptions > instructions, $"Unexpected order:\n{markdown}");
        Assert.True(requestTranscript > requestOptions, $"Unexpected order:\n{markdown}");
        Assert.True(response > requestTranscript, $"Unexpected order:\n{markdown}");
        Assert.True(annotations > response, $"Annotations must render with the response contents:\n{markdown}");
        Assert.True(toolInvocations > annotations, $"Unexpected order:\n{markdown}");
        Assert.Contains("- **Transport Retry:** Connection reset; retrying in 00:00:01." + LineFeed, markdown);
        Assert.Contains("- **Outcome:** Accepted" + LineFeed, markdown);
    }

    private static string RenderDefaultCycle(
        IReadOnlyList<ChatMessage>? requestMessages = null,
        ChatResponse? response = null)
        => MindSessionTranscriptFormatter.Render(
            CreateCycle(requestMessages: requestMessages, response: response),
            turnNumber: 1,
            characterId: "luna");

    /// <summary>Renders Markdown through a CommonMark renderer so assertions verify rendered outcomes, not just
    /// marker characters (AI-011 TR-8).</summary>
    private static string RenderHtml(string markdown)
        => Markdig.Markdown.ToHtml(markdown);

    private static MindSessionCycleTranscript CreateCycle(
        IReadOnlyList<ChatMessage>? requestMessages = null,
        ChatResponse? response = null)
        => new()
        {
            CycleIndex = 1,
            StartedAt = new DateTimeOffset(2026, 9, 28, 14, 3, 22, TimeSpan.FromHours(9)),
            Instructions = "<Identity>" + LineFeed + "You are Luna." + LineFeed + "</Identity>" + LineFeed,
            RequestMessages = requestMessages ?? [new ChatMessage(ChatRole.User, "Begin.")],
            RequestOptions = new MindTranscriptRequestOptions(null, "RequiredChatToolMode", false, ["speak", "wait"]),
            Response = response,
        };
}
