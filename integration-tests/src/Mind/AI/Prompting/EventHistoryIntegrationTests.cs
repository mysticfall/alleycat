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
/// Godot-runtime coverage for the standalone <see cref="EventHistory" /> authoring resource and its exact,
/// template-scoped dispatch through the on-demand observation-history renderer.
/// </summary>
[Headless]
public sealed class EventHistoryIntegrationTests
{
    private const string GenericPromptPath = "res://assets/characters/prompts/generic_npc_prompt_stack.tres";
    private const string NpcEventHistoryPath = "res://assets/characters/prompts/npc_event_history.tres";

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
        ITemplate template = new HandlebarsTemplateCompiler().Compile(source);

        string output = template.Render(new Dictionary<string, object?>
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
    /// The standalone NPC event-history resource owns exactly one unified speech fragment with safe actor-relative
    /// output, rendered chronologically through the on-demand renderer.
    /// </summary>
    [Fact]
    public void StandaloneEventHistory_RendersUnifiedActorRelativeChronologicalHistory()
    {
        EventHistory eventHistory = Assert.IsType<EventHistory>(
            ResourceLoader.Load(NpcEventHistoryPath),
            exactMatch: false);
        EventHistoryPromptFragment fragment = Assert.Single(eventHistory.Fragments);
        Observation[] observations =
        [
            new ObservedSpeech("char:test_character", "private-self", "Self line.") { ObservedAt = 100.2d },
            new TestObservation("world.changed", "door opened") { ObservedAt = 300.55d },
            new ObservedSpeech("char:rin", "private-known", "Known line.") { ObservedAt = 7200d },
            new ObservedSpeech(null, "private-unknown", "Unknown line.") { ObservedAt = 259200.4d },
            new ObservedSpeech("CHAR:TEST_CHARACTER", "private-case", "Case-distinct line.") { ObservedAt = 864000d },
        ];

        string output = CreateRenderer(eventHistory).Render(observations);

        Assert.Equal("speech.observed", fragment.TypeKey);
        Assert.Equal(
            "I said: Self line. (at " + Label(100.2d) + "s game time)\n"
                + "((Received world.changed event.)) (at " + Label(300.55d) + "s game time)\n"
                + "Heard char:rin say: Known line. (at " + Label(7200d) + "s game time)\n"
                + "Heard an unknown speaker say: Unknown line. (at " + Label(259200.4d) + "s game time)\n"
                + "Heard CHAR:TEST_CHARACTER say: Case-distinct line. (at " + Label(864000d) + "s game time)\n",
            output);
        Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
        Assert.DoesNotContain("VoiceId", fragment.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("VoiceId", eventHistory.FallbackSource, StringComparison.Ordinal);
    }

    /// <summary>Formats one game-time label exactly as the template's number-format tool renders it.</summary>
    private static string Label(double gameSeconds)
        => gameSeconds.ToString("F1", CultureInfo.CurrentCulture);

    /// <summary>
    /// Unstamped observations render without any game-time label for both fragment and fallback entries.
    /// </summary>
    [Fact]
    public void StandaloneEventHistory_RendersUnstampedObservationsWithoutLabels()
    {
        EventHistory eventHistory = Assert.IsType<EventHistory>(
            ResourceLoader.Load(NpcEventHistoryPath),
            exactMatch: false);
        Observation[] observations =
        [
            new ObservedSpeech("char:test_character", "private-self", "Self line."),
            new TestObservation("world.changed", "door opened"),
        ];

        string output = CreateRenderer(eventHistory).Render(observations);

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
    public void Render_RecognisedUnknownAndSelfSpeech_UsesPrivacySafeAuthoredWording()
    {
        EventHistory eventHistory = CreateSpeechEventHistory();
        Observation[] observations =
        [
            new ObservedSpeech("char:rin", "raw-known-device", "Hello"),
            new ObservedSpeech(null, "secret-unrecognised-device", "Who is there?"),
            new ObservedSpeech("char:test_character", null, "Welcome."),
            new ObservedSpeech("CHAR:TEST_CHARACTER", "case-sensitive-device", "Not myself."),
        ];

        string output = CreateRenderer(eventHistory).Render(observations);

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
    public void Render_UnknownAndCaseMismatchedKeys_UsesFallbackWithConcreteContext()
    {
        EventHistory eventHistory = new()
        {
            Fragments =
            [
                new EventHistoryPromptFragment
                {
                    TypeKey = "speech.observed",
                    Source = "heard: {{Content}}\n",
                },
            ],
            FallbackSource = "fallback {{TypeKey}}: {{Detail}}\n",
        };
        Observation[] observations =
        [
            new TestObservation("Speech.Heard", "case mismatch"),
            new TestObservation("world.changed", "door opened"),
        ];

        string output = CreateRenderer(eventHistory).Render(observations);

        Assert.Equal(
            "fallback Speech.Heard: case mismatch\nfallback world.changed: door opened\n",
            output);
    }

    /// <summary>
    /// Rendering preserves timeline order and authored multiline output, including an empty history.
    /// </summary>
    [Fact]
    public void Render_PreservesOrderingMultilineOutputAndEmptyHistory()
    {
        EventHistory eventHistory = new()
        {
            Fragments =
            [
                new EventHistoryPromptFragment
                {
                    TypeKey = "test.event",
                    Source = "line one: {{Detail}}\nline two\n",
                },
            ],
            FallbackSource = "fallback",
        };

        string populated = CreateRenderer(eventHistory).Render(
            [new TestObservation("test.event", "first"), new TestObservation("test.event", "second")]);
        string empty = CreateRenderer(eventHistory).Render([]);

        Assert.Equal("line one: first\nline two\nline one: second\nline two\n", populated);
        Assert.Equal(string.Empty, empty);
    }

    /// <summary>
    /// Speech and fallback observations retain chronological interleaving and concrete observation context safely.
    /// </summary>
    [Fact]
    public void Render_InterleavesSpeechAndFallbackWithoutLeakingVoiceProvenance()
    {
        EventHistory eventHistory = CreateSpeechEventHistory();
        Observation[] observations =
        [
            new ObservedSpeech(null, "private-first", "first"),
            new TestObservation("world.changed", "door opened"),
            new ObservedSpeech("char:test_character", "private-self", "third"),
        ];

        string output = CreateRenderer(eventHistory).Render(observations);

        Assert.Equal(
            "Heard an unknown speaker: first\n((Received world.changed event.))\nSaid: third\n",
            output);
        Assert.DoesNotContain("private-first", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-self", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Source construction emits reusable source without consuming observations; rendering supplies the ordinary
    /// context later.
    /// </summary>
    [Fact]
    public void BuildEventHistorySource_SeparatesCompilationFromOrdinaryContextRendering()
    {
        EventHistory eventHistory = new()
        {
            Fragments =
            [
                new EventHistoryPromptFragment
                {
                    TypeKey = "test.event",
                    Source = "{{Detail}}",
                },
            ],
            FallbackSource = "{{TypeKey}}",
        };

        string source = EventHistory.BuildEventHistorySource(eventHistory.Fragments, eventHistory.FallbackSource);
        ITemplate template = new HandlebarsTemplateCompiler().Compile(source);

        Assert.DoesNotContain("first runtime value", source, StringComparison.Ordinal);
        Assert.Equal("first runtime value", Render(template, [new TestObservation("test.event", "first runtime value")]));
        Assert.Equal("second runtime value", Render(template, [new TestObservation("test.event", "second runtime value")]));
    }

    /// <summary>
    /// Blank keys, duplicate exact keys, and blank fallbacks fail as clear authoring errors.
    /// </summary>
    [Fact]
    public void BuildEventHistorySource_InvalidAuthoring_ThrowsClearErrors()
    {
        EventHistory blankKey = new()
        {
            Fragments = [new EventHistoryPromptFragment { TypeKey = "  ", Source = "unused" }],
        };
        EventHistory duplicateKey = new()
        {
            Fragments =
            [
                new EventHistoryPromptFragment { TypeKey = "same", Source = "first" },
                new EventHistoryPromptFragment { TypeKey = "same", Source = "second" },
            ],
        };
        EventHistory blankFallback = new()
        {
            FallbackSource = "\t",
        };

        InvalidOperationException blankKeyError = Assert.Throws<InvalidOperationException>(
            () => EventHistory.BuildEventHistorySource(blankKey.Fragments, blankKey.FallbackSource));
        InvalidOperationException duplicateError = Assert.Throws<InvalidOperationException>(
            () => EventHistory.BuildEventHistorySource(duplicateKey.Fragments, duplicateKey.FallbackSource));
        InvalidOperationException fallbackError = Assert.Throws<InvalidOperationException>(
            () => EventHistory.BuildEventHistorySource(blankFallback.Fragments, blankFallback.FallbackSource));

        Assert.Contains("nonblank TypeKey", blankKeyError.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate exact TypeKey 'same'", duplicateError.Message, StringComparison.Ordinal);
        Assert.Contains("nonblank fallback", fallbackError.Message, StringComparison.Ordinal);
    }

    private static EventHistory CreateSpeechEventHistory()
        => new()
        {
            Fragments =
            [
                new EventHistoryPromptFragment
                {
                    TypeKey = "speech.observed",
                    Source = "{{#if ActorId}}{{#if (eqOrdinal ActorId @root.character.FullId)}}Said: {{Content}}{{else}}Heard {{ActorId}}: {{Content}}{{/if}}{{else}}Heard an unknown speaker: {{Content}}{{/if}}\n",
                },
            ],
            FallbackSource = "((Received {{TypeKey}} event.))\n",
        };

    private static ObservationHistoryRenderer CreateRenderer(EventHistory eventHistory)
        => ObservationHistoryRenderer.Create(
            eventHistory,
            new HandlebarsTemplateCompiler(),
            new Dictionary<string, object?> { ["FullId"] = "char:test_character" });

    private static string Render(ITemplate template, IReadOnlyList<Observation> observations)
        => template.Render(new Dictionary<string, object?>
        {
            ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:test_character" },
            [EventHistory.ObservationsContextKey] = observations,
        });

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
