using System.Globalization;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

/// <summary>
/// Godot-runtime coverage for the authored standalone event-history file and its exact, per-record dispatch
/// through the on-demand observation-history renderer.
/// </summary>
[Headless]
public sealed class EventHistoryIntegrationTests
{
    private const string GenericPromptPath = "res://assets/characters/prompts/generic_npc_prompt_stack.tres";
    private const string NpcEventHistoryPath = "res://prompts/event_history.md";

    /// <summary>
    /// The shared generic NPC prompt stack contains no event-history section, and its <c>mind.md</c> guidance stays
    /// cross-cutting: the tool-call-only frame, game-time literacy, and subject references carry no per-tool
    /// mechanics or tool names (AI-003 TR-23/25).
    /// </summary>
    [Fact]
    public async Task SharedPromptStack_ContainsNoEventHistorySectionAndCrossCuttingGuidanceOnly()
    {
        PromptStack stack = Assert.IsType<PromptStack>(ResourceLoader.Load(GenericPromptPath), exactMatch: false);
        Assert.Equal(
            ["Instructions", "Lore", "Characters", "Scenario"],
            stack.Sections.Select(section => section.Name));
        Assert.Equal(
            [
                "AlleyCat.Mind.AI.Prompting.FilePromptSection",
                "AlleyCat.Mind.AI.Prompting.EssentialLorePromptSection",
                "AlleyCat.Mind.AI.Prompting.CharacterLorePromptSection",
                "AlleyCat.Mind.AI.Prompting.FilePromptSection",
            ],
            stack.Sections.Select(section => section.GetType().FullName));

        FilePromptSection section = Assert.IsType<FilePromptSection>(stack.Sections[0], exactMatch: false);
        string source = await section.GetContentAsync(CreateBuildContext());
        ITemplate template = new FluidTemplateCompiler().Compile(source);

        string output = await template.RenderAsync(new Dictionary<string, object?>
        {
            ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:test_character" },
        });

        Assert.Contains("You are char:test_character", output, StringComparison.Ordinal);
        Assert.Contains("every response you give is a tool call", output, StringComparison.Ordinal);
        Assert.Contains("seconds of in-game time since the game began", output, StringComparison.Ordinal);
        // Per-tool mechanics and etiquette live solely in the tool descriptions, never in the session prompt.
        Assert.DoesNotContain("`wait`", output, StringComparison.Ordinal);
        Assert.DoesNotContain("`speak`", output, StringComparison.Ordinal);
        Assert.DoesNotContain("`history`", output, StringComparison.Ordinal);
        Assert.DoesNotContain("terminal result", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terminal response", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tool descriptions are the sole carriers of per-tool mechanics and etiquette: <c>wait</c> frames observation
    /// rather than passing time with question-then-wait etiquette, and <c>speak</c> is optional and repeatable
    /// (AI-002 TR-35, AI-003 TR-25).
    /// </summary>
    [Fact]
    public void ProductionToolDescriptions_CarryPerToolMechanicsAndEtiquette()
    {
        using WaitTool waitTool = new();
        using SpeechTool speechTool = new();

        Assert.Contains("without waiting, nothing new reaches you", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("observation, not idling", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("before assuming refusal", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("only the spoken words", speechTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("optional and repeatable", speechTool.ToolDescription, StringComparison.Ordinal);
    }

    /// <summary>
    /// The authored standalone event-history file owns exactly one unified speech fragment with safe
    /// actor-relative output, rendered chronologically through the on-demand renderer.
    /// </summary>
    [Fact]
    public async Task StandaloneEventHistory_RendersUnifiedActorRelativeChronologicalHistory()
    {
        EventHistoryDocument eventHistory = LoadNpcEventHistory();
        EventHistoryFragment speechFragment = Assert.Single(eventHistory.Fragments, item => item.TypeKey == "speech.observed");
        EventHistoryFragment visualFragment = Assert.Single(eventHistory.Fragments, item => item.TypeKey == "vision.description");
        Observation[] observations =
        [
            new ObservedSpeech("char:test_character", "private-self", "Self line.") { ObservedAt = 100.2d },
            new TestObservation("world.changed", "door opened") { ObservedAt = 300.55d },
            new ObservedSpeech("char:rin", "private-known", "Known line.") { ObservedAt = 7200d },
            new ObservedSpeech(null, "private-unknown", "Unknown line.") { ObservedAt = 259200.4d },
            new ObservedSpeech("CHAR:TEST_CHARACTER", "private-case", "Case-distinct line.") { ObservedAt = 864000d },
            new ObservedVisualDescription("char:coat", "A weathered red coat.") { ObservedAt = 900000d },
            new ObservedRelativePosition("char:rin", 1.4f, RelativeDirection.Left, RelativeDirection.Front)
            {
                ObservedAt = 900100d,
            },
        ];

        string output = await CreateRenderer(eventHistory).RenderAsync(observations);

        Assert.Equal("speech.observed", speechFragment.TypeKey);
        Assert.Equal(
            "I said: Self line. (at " + Label(100.2d) + "s game time)\n"
                + "((Received world.changed event.)) (at " + Label(300.55d) + "s game time)\n"
                + "Heard char:rin say: Known line. (at " + Label(7200d) + "s game time)\n"
                + "Heard an unknown speaker say: Unknown line. (at " + Label(259200.4d) + "s game time)\n"
                + "Heard CHAR:TEST_CHARACTER say: Case-distinct line. (at " + Label(864000d) + "s game time)\n"
                + "Observed char:coat: A weathered red coat. (at " + Label(900000d) + "s game time)\n"
                + "I observe char:rin " + Label(1.4d) + " m to my left; they are facing me. (at "
                + Label(900100d) + "s game time)\n",
            output);
        Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
        Assert.DoesNotContain("VoiceId", speechFragment.Source, StringComparison.Ordinal);
        Assert.Contains("SubjectId", visualFragment.Source, StringComparison.Ordinal);
        Assert.Contains("Description", visualFragment.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("VoiceId", eventHistory.FallbackSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime render path over the authored standalone file reproduces the committed pre-migration golden
    /// baseline byte-for-byte (invariant-culture capture), proving the individually compiled record-rooted model
    /// keeps migrated output identical.
    /// </summary>
    [Fact]
    public async Task StandaloneEventHistory_MatchesCommittedGoldenBaselineThroughRuntimePath()
    {
        string expected = await File.ReadAllTextAsync(BaselinePath("authored_npc_event_history_dispatch.txt"));
        EventHistoryDocument eventHistory = LoadNpcEventHistory();
        Observation[] observations =
        [
            new ObservedSpeech("char:test_character", "unused-self", "Self line.") { ObservedAt = 100.2d },
            new ObservedSpeech("char:rin", "unused-known", "Known line.") { ObservedAt = 7200d },
            new ObservedSpeech(null, "unused-unknown", "Unknown line."),
            new ObservedSpeech("CHAR:TEST_CHARACTER", "unused-case", "Case-distinct line.") { ObservedAt = 864000d },
            new ObservedVisualDescription("char:coat", "A weathered red coat.") { ObservedAt = 900000d },
            new ObservedRelativePosition("char:rin", 1.4f, RelativeDirection.Left, RelativeDirection.Front)
            {
                ObservedAt = 900100d,
            },
            new TestObservation("world.changed", "door opened") { ObservedAt = 300.55d },
        ];

        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            string actual = await CreateRenderer(eventHistory).RenderAsync(observations);

            Assert.True(
                string.Equals(expected, actual, StringComparison.Ordinal),
                $"Runtime event-history output drifted from the committed baseline."
                + $"{System.Environment.NewLine}Expected ({expected.Length} chars): {Describe(expected)}"
                + $"{System.Environment.NewLine}Actual   ({actual.Length} chars): {Describe(actual)}");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// The authored standalone event-history file owns exactly one relative-position fragment that renders
    /// present-tense fused position-and-distance wording with unambiguous reciprocal facing for the exact
    /// <c>vision.relative_position</c> key, including unstamped records (AI-003 TR-32, AC-19).
    /// </summary>
    [Fact]
    public async Task StandaloneEventHistory_RendersPresentTenseRelativePositionWithUnambiguousDirections()
    {
        EventHistoryDocument eventHistory = LoadNpcEventHistory();
        EventHistoryFragment relativePositionFragment = Assert.Single(
            eventHistory.Fragments,
            item => item.TypeKey == "vision.relative_position");
        Observation[] observations =
        [
            new ObservedRelativePosition("char:rin", 1.4f, RelativeDirection.Front, RelativeDirection.Front)
            {
                ObservedAt = 1200.25d,
            },
            new ObservedRelativePosition("char:ava", 3.75f, RelativeDirection.Back, RelativeDirection.Right),
            new ObservedRelativePosition("char:coat", 0.4f, RelativeDirection.Left, RelativeDirection.Back)
            {
                ObservedAt = 1200.3d,
            },
            new ObservedRelativePosition("char:mika", 2f, RelativeDirection.Right, RelativeDirection.Left),
        ];

        string output = await CreateRenderer(eventHistory).RenderAsync(observations);

        Assert.Equal(
            "I observe char:rin " + Label(1.4d) + " m ahead of me; they are facing me."
                + " (at " + Label(1200.25d) + "s game time)\n"
                + "I observe char:ava " + Label(3.75d) + " m behind me; I am to their right.\n"
                + "I observe char:coat " + Label(0.4d) + " m to my left; their back is turned to me."
                + " (at " + Label(1200.3d) + "s game time)\n"
                + "I observe char:mika " + Label(2d) + " m to my right; I am to their left.\n",
            output);
        Assert.DoesNotContain("VoiceId", relativePositionFragment.Source, StringComparison.Ordinal);
    }

    /// <summary>Formats one game-time label exactly as the template's number-format tool renders it.</summary>
    private static string Label(double gameSeconds)
        => gameSeconds.ToString("F1", CultureInfo.CurrentCulture);

    /// <summary>
    /// Unstamped observations render without any game-time label for both fragment and fallback entries.
    /// </summary>
    [Fact]
    public async Task StandaloneEventHistory_RendersUnstampedObservationsWithoutLabels()
    {
        EventHistoryDocument eventHistory = LoadNpcEventHistory();
        Observation[] observations =
        [
            new ObservedSpeech("char:test_character", "private-self", "Self line."),
            new TestObservation("world.changed", "door opened"),
        ];

        string output = await CreateRenderer(eventHistory).RenderAsync(observations);

        Assert.Equal(
            "I said: Self line.\n((Received world.changed event.))\n",
            output);
        Assert.DoesNotContain("game time", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VoiceId", eventHistory.FallbackSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// One authored fragment renders self, recognised, case-distinct, and unknown speech without voice provenance.
    /// </summary>
    [Fact]
    public async Task Render_RecognisedUnknownAndSelfSpeech_UsesPrivacySafeAuthoredWording()
    {
        EventHistoryDocument eventHistory = CreateSpeechEventHistory();
        Observation[] observations =
        [
            new ObservedSpeech("char:rin", "raw-known-device", "Hello"),
            new ObservedSpeech(null, "secret-unrecognised-device", "Who is there?"),
            new ObservedSpeech("char:test_character", null, "Welcome."),
            new ObservedSpeech("CHAR:TEST_CHARACTER", "case-sensitive-device", "Not myself."),
        ];

        string output = await CreateRenderer(eventHistory).RenderAsync(observations);

        Assert.Equal(
            "Heard char:rin: Hello\nHeard an unknown speaker: Who is there?\nSaid: Welcome.\nHeard CHAR:TEST_CHARACTER: Not myself.\n",
            output);
        Assert.DoesNotContain("raw-known-device", output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-unrecognised-device", output, StringComparison.Ordinal);
        Assert.DoesNotContain("case-sensitive-device", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unknown keys and case mismatches use the authored fallback with each concrete record as context.
    /// </summary>
    [Fact]
    public async Task Render_UnknownAndCaseMismatchedKeys_UsesFallbackWithConcreteContext()
    {
        EventHistoryDocument eventHistory = ParseSpeechDocument(
            "heard: {{ Content }}\n",
            "fallback {{ TypeKey }}: {{ Detail }}\n");
        Observation[] observations =
        [
            new TestObservation("Speech.Heard", "case mismatch"),
            new TestObservation("world.changed", "door opened"),
        ];

        string output = await CreateRenderer(eventHistory).RenderAsync(observations);

        Assert.Equal(
            "fallback Speech.Heard: case mismatch\nfallback world.changed: door opened\n",
            output);
    }

    /// <summary>
    /// Rendering preserves timeline order and authored multiline output, including an empty history.
    /// </summary>
    [Fact]
    public async Task Render_PreservesOrderingMultilineOutputAndEmptyHistory()
    {
        EventHistoryDocument eventHistory = ParseSpeechDocument(
            "speech: {{ Content }}\n",
            "line one: {{ Detail }}\nline two\n");

        ObservationHistoryRenderer renderer = CreateRenderer(eventHistory);
        string populated = await renderer.RenderAsync(
            [new TestObservation("test.event", "first"), new TestObservation("test.event", "second")]);
        string empty = await renderer.RenderAsync([]);

        Assert.Equal("line one: first\nline two\nline one: second\nline two\n", populated);
        Assert.Equal(string.Empty, empty);
    }

    /// <summary>
    /// Speech and fallback observations retain chronological interleaving and concrete observation context safely.
    /// </summary>
    [Fact]
    public async Task Render_InterleavesSpeechAndFallbackWithoutLeakingVoiceProvenance()
    {
        EventHistoryDocument eventHistory = CreateSpeechEventHistory();
        Observation[] observations =
        [
            new ObservedSpeech(null, "private-first", "first"),
            new TestObservation("world.changed", "door opened"),
            new ObservedSpeech("char:test_character", "private-self", "third"),
        ];

        string output = await CreateRenderer(eventHistory).RenderAsync(observations);

        Assert.Equal(
            "Heard an unknown speaker: first\n((Received world.changed event.))\nSaid: third\n",
            output);
        Assert.DoesNotContain("private-first", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-self", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The model sees grouped segments as one latest-position event while Mind-facing raw records stay individual and
    /// immutable. Selecting either segment expands the selected window to the full known utterance (AI-001 TR-45/48,
    /// AI-003 TR-33/34). The projected event's identity is Prompting-owned correlation metadata — the contributing
    /// group's source voice, speech-group identity, and segment indexes — never a runner session-protocol type
    /// (AI-003 TR-35).
    /// </summary>
    [Fact]
    public async Task ContinuationProjection_JoinsSelectedSegmentsAtLatestPositionWithoutLeakingIdentity()
    {
        EventHistoryDocument eventHistory = CreateSpeechEventHistory();
        Observation[] timeline =
        [
            new ObservedSpeech("char:rin", "private-voice", "later", "private-group", 1, continued: true),
            new TestObservation("world.changed", "door opened"),
            new ObservedSpeech("char:rin", "private-voice", "earlier", "private-group", 0),
        ];
        ObservationHistoryRenderer renderer = CreateRenderer(eventHistory);

        IReadOnlyList<ContinuationProjection.Event> projected = ObservationHistoryRenderer.Project(timeline);
        string selected = await renderer.RenderAsync([timeline[0]], timeline);
        string rendered = await renderer.RenderAsync(timeline);

        ContinuationProjection.Event grouped = Assert.Single(projected, item => item.Correlation is not null);
        ContinuationProjection.SpeechGroupCorrelation correlation =
            Assert.IsType<ContinuationProjection.SpeechGroupCorrelation>(grouped.Correlation);
        Assert.Equal("private-voice", correlation.SourceVoiceID);
        Assert.Equal("private-group", correlation.SpeechGroupID);
        Assert.True(
            correlation.SegmentIndexes.SetEquals([0, 1]),
            "The correlation must list every contributing segment index of the projected group.");
        Assert.Equal(3, grouped.Position);
        Assert.Equal(3, grouped.Revision);
        Assert.Equal(2, projected.Count); // history(count) counts model events, not all three raw records.
        Assert.Equal("Heard char:rin: earlier … later\n", selected);
        Assert.Equal(
            "((Received world.changed event.))\nHeard char:rin: earlier … later\n",
            rendered);
        Assert.Equal(["later", "earlier"], timeline.OfType<ObservedSpeech>().Select(static speech => speech.Content));
        Assert.DoesNotContain("private-voice", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("private-group", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reused group IDs with incompatible attribution never join text across the identity boundary.
    /// </summary>
    [Fact]
    public async Task ContinuationProjection_InconsistentAttributionRemainsSeparate()
    {
        Observation[] timeline =
        [
            new ObservedSpeech("char:rin", "voice", "first", "group", 0),
            new ObservedSpeech("char:ava", "voice", "second", "group", 1, continued: true),
        ];
        ObservationHistoryRenderer renderer = CreateRenderer(CreateSpeechEventHistory());

        string rendered = await renderer.RenderAsync(timeline);

        Assert.Equal("Heard char:rin: first\nHeard char:ava: second\n", rendered);
        Assert.DoesNotContain("first … second", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Authored fragments compile individually without consuming observation data; records supply their concrete
    /// values only at render time (AI-003 TR-14/15).
    /// </summary>
    [Fact]
    public async Task Render_CompilesAuthoredTemplatesIndividuallyAndSuppliesRecordsAtRenderTime()
    {
        EventHistoryDocument eventHistory = ParseSpeechDocument("{{ Content }}\n", "{{ TypeKey }}");
        ObservationHistoryRenderer renderer = CreateRenderer(eventHistory);

        Assert.DoesNotContain("first runtime value", eventHistory.Fragments[0].Source, StringComparison.Ordinal);
        Assert.Equal(
            "first runtime value\n",
            await renderer.RenderAsync([new ObservedSpeech(null, null, "first runtime value")]));
        Assert.Equal(
            "second runtime value\n",
            await renderer.RenderAsync([new ObservedSpeech(null, null, "second runtime value")]));
    }

    /// <summary>
    /// Invalid authoring fails clearly at parse time, naming the offending section for every violation class
    /// (AI-003 TR-13): blank, unknown, and duplicate exact keys, a missing or blank fallback section, and text
    /// outside any section.
    /// </summary>
    [Fact]
    public void Parse_InvalidAuthoring_ThrowsClearErrorsNamingTheOffendingSection()
    {
        InvalidOperationException blankKeyError = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse("<!-- event-history:   -->\nunmatched\n" + FallbackSection() + "\tf\n"));
        InvalidOperationException unknownKeyError = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse(
                "<!-- event-history: world.changed -->\nunmatched\n" + FallbackSection() + "\tf\n"));
        InvalidOperationException duplicateKeyError = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse(
                "<!-- event-history: speech.observed -->\nfirst\n"
                + "<!-- event-history: speech.observed -->\nsecond\n"
                + FallbackSection() + "\tf\n"));
        InvalidOperationException missingFallbackError = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse("<!-- event-history: speech.observed -->\nunmatched\n"));
        InvalidOperationException blankFallbackError = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse(FallbackSection() + "\t\n"));
        InvalidOperationException outsideTextError = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse("stray prose\n" + FallbackSection() + "\tf\n"));

        Assert.Contains("nonblank TypeKey", blankKeyError.Message, StringComparison.Ordinal);
        Assert.Contains("unknown TypeKey 'world.changed'", unknownKeyError.Message, StringComparison.Ordinal);
        Assert.Contains(
            "duplicate exact TypeKey 'speech.observed'", duplicateKeyError.Message, StringComparison.Ordinal);
        Assert.Contains("nonblank fallback", missingFallbackError.Message, StringComparison.Ordinal);
        Assert.Contains("nonblank fallback", blankFallbackError.Message, StringComparison.Ordinal);
        Assert.Contains("outside any section", outsideTextError.Message, StringComparison.Ordinal);
    }

    private static string FallbackSection() => "<!-- event-history: fallback -->\n";

    private static EventHistoryDocument LoadNpcEventHistory()
    {
        using var file = Godot.FileAccess.Open(NpcEventHistoryPath, Godot.FileAccess.ModeFlags.Read);
        Assert.True(file is not null, $"Expected the authored event-history file '{NpcEventHistoryPath}' to open.");
        return EventHistoryDocument.Parse(file.GetAsText());
    }

    private static string Describe(string value)
        => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string BaselinePath(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "game", "project.godot")))
        {
            directory = directory.Parent;
        }

        return directory is not null
            ? Path.Combine(directory.FullName, "tests", "src", "Templating", "baselines", fileName)
            : throw new InvalidOperationException("Could not locate the repository root from the test binary path.");
    }

    /// <summary>Builds an authored-convention document with one speech fragment plus the supplied fallback.</summary>
    private static EventHistoryDocument ParseSpeechDocument(string fragmentSource, string fallbackSource)
        => EventHistoryDocument.Parse(
            "<!-- event-history: speech.observed -->\n"
            + fragmentSource
            + FallbackSection()
            + fallbackSource);

    private static EventHistoryDocument CreateSpeechEventHistory()
        => ParseSpeechDocument(
            "{% if ActorId != blank %}{% if ActorId == character.FullId %}Said: {{ Content }}"
                + "{% else %}Heard {{ ActorId }}: {{ Content }}{% endif %}"
                + "{% else %}Heard an unknown speaker: {{ Content }}{% endif %}\n",
            "((Received {{ TypeKey }} event.))\n");

    private static ObservationHistoryRenderer CreateRenderer(EventHistoryDocument? eventHistory)
        => ObservationHistoryRenderer.Create(
            eventHistory,
            new FluidTemplateCompiler(),
            new PromptOwnerCharacter("test_character"));

    private static PromptSectionBuildContext CreateBuildContext()
        => new(
            new ServiceCollection().BuildServiceProvider(),
            new SceneContext([]),
            new PromptOwnerCharacter());

    private sealed record TestObservation(string SemanticTypeKey, string Detail) : Observation
    {
        public override string TypeKey => SemanticTypeKey;

        public override float CalculateImportance(ObservationContext context) => 1f;
    }
}
