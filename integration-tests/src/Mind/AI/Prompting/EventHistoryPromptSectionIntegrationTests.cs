using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

/// <summary>
/// Godot-runtime coverage for exact, template-scoped event-history dispatch.
/// </summary>
[Headless]
public sealed class EventHistoryPromptSectionIntegrationTests
{
    private const string GenericPromptPath = "res://assets/characters/prompts/generic_npc_prompt_stack.tres";
    private const string StrictToolOnlyGuidance =
        "Use `end_turn` exactly once as the final argument-free non-action marker. "
        + "Call it alone for zero actions, or after one or more actions when you can finish without inspecting their results. "
        + "Omit `end_turn` from an action-only response when you need action results before deciding whether to continue or finish. "
        + "Action tools such as `speak` are optional and do not end the turn. "
        + "Ordinary text is invalid.";

    /// <summary>
    /// The shared prompt renders exact strict tool-only protocol guidance without legacy terminal-result wording.
    /// </summary>
    [Fact]
    public async Task ProductionPromptResource_RendersStrictToolOnlyProtocolGuidance()
    {
        PromptStack stack = Assert.IsType<PromptStack>(ResourceLoader.Load(GenericPromptPath), exactMatch: false);
        FilePromptSection section = Assert.IsType<FilePromptSection>(stack.Sections[0], exactMatch: false);
        string source = await section.GetContentAsync(CreateBuildContext());
        ITemplate template = new HandlebarsTemplateCompiler().Compile(source);

        string output = template.Render(new Dictionary<string, object?>
        {
            ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:test_character" },
        });

        Assert.Contains(StrictToolOnlyGuidance, output, StringComparison.Ordinal);
        Assert.DoesNotContain("required end-of-turn result", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terminal result", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terminal response", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The production prompt asset owns exactly one unified speech fragment with safe actor-relative output.
    /// </summary>
    [Fact]
    public async Task ProductionPromptResource_RendersUnifiedActorRelativeChronologicalHistory()
    {
        PromptStack stack = Assert.IsType<PromptStack>(ResourceLoader.Load(GenericPromptPath), exactMatch: false);
        EventHistoryPromptSection section = Assert.IsType<EventHistoryPromptSection>(stack.Sections[3], exactMatch: false);
        EventHistoryPromptFragment fragment = Assert.Single(section.Fragments);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Observation[] observations =
        [
            new ObservedSpeech("char:test_character", "private-self", "Self line.") { ObservedAt = now.AddSeconds(-30) },
            new TestObservation("world.changed", "door opened") { ObservedAt = now.AddMinutes(-5) },
            new ObservedSpeech("char:rin", "private-known", "Known line.") { ObservedAt = now.AddHours(-2) },
            new ObservedSpeech(null, "private-unknown", "Unknown line.") { ObservedAt = now.AddDays(-3) },
            new ObservedSpeech("CHAR:TEST_CHARACTER", "private-case", "Case-distinct line.") { ObservedAt = now.AddDays(-10) },
        ];

        string output = await CompileAndRenderAsync(section, observations);

        Assert.Equal("speech.observed", fragment.TypeKey);
        Assert.Equal(
            "I said: Self line. (30 seconds ago)\n"
                + "((Received world.changed event.)) (5 minutes ago)\n"
                + "Heard char:rin say: Known line. (2 hours ago)\n"
                + "Heard an unknown speaker say: Unknown line. (3 days ago)\n"
                + "Heard CHAR:TEST_CHARACTER say: Case-distinct line. (1 week ago)\n",
            output);
        Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
        Assert.DoesNotContain("VoiceId", fragment.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("VoiceId", section.FallbackSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unstamped observations render without any relative-time label for both fragment and fallback entries.
    /// </summary>
    [Fact]
    public async Task ProductionPromptResource_RendersUnstampedObservationsWithoutLabels()
    {
        PromptStack stack = Assert.IsType<PromptStack>(ResourceLoader.Load(GenericPromptPath), exactMatch: false);
        EventHistoryPromptSection section = Assert.IsType<EventHistoryPromptSection>(stack.Sections[3], exactMatch: false);
        Observation[] observations =
        [
            new ObservedSpeech("char:test_character", "private-self", "Self line."),
            new TestObservation("world.changed", "door opened"),
        ];

        string output = await CompileAndRenderAsync(section, observations);

        Assert.Equal(
            "I said: Self line.\n((Received world.changed event.))\n",
            output);
        Assert.DoesNotContain("ago", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VoiceId", section.FallbackSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// One authored fragment renders self, recognised, case-distinct, and unknown speech without voice provenance.
    /// </summary>
    [Fact]
    public async Task Render_RecognisedUnknownAndSelfSpeech_UsesPrivacySafeAuthoredWording()
    {
        EventHistoryPromptSection section = CreateSpeechSection();
        Observation[] observations =
        [
            new ObservedSpeech("char:rin", "raw-known-device", "Hello"),
            new ObservedSpeech(null, "secret-unrecognised-device", "Who is there?"),
            new ObservedSpeech("char:test_character", null, "Welcome."),
            new ObservedSpeech("CHAR:TEST_CHARACTER", "case-sensitive-device", "Not myself."),
        ];

        string output = await CompileAndRenderAsync(section, observations);

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
        EventHistoryPromptSection section = new()
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

        string output = await CompileAndRenderAsync(section, observations);

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
        EventHistoryPromptSection section = new()
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

        string populated = await CompileAndRenderAsync(
            section,
            [new TestObservation("test.event", "first"), new TestObservation("test.event", "second")]);
        string empty = await CompileAndRenderAsync(section, []);

        Assert.Equal("line one: first\nline two\nline one: second\nline two\n", populated);
        Assert.Equal(string.Empty, empty);
    }

    /// <summary>
    /// Speech and fallback observations retain chronological interleaving and concrete observation context safely.
    /// </summary>
    [Fact]
    public async Task Render_InterleavesSpeechAndFallbackWithoutLeakingVoiceProvenance()
    {
        EventHistoryPromptSection section = CreateSpeechSection();
        Observation[] observations =
        [
            new ObservedSpeech(null, "private-first", "first"),
            new TestObservation("world.changed", "door opened"),
            new ObservedSpeech("char:test_character", "private-self", "third"),
        ];

        string output = await CompileAndRenderAsync(section, observations);

        Assert.Equal(
            "Heard an unknown speaker: first\n((Received world.changed event.))\nSaid: third\n",
            output);
        Assert.DoesNotContain("private-first", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-self", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Section construction emits reusable source without consuming observations; rendering supplies ordinary context later.
    /// </summary>
    [Fact]
    public async Task GetContent_SeparatesCompilationFromOrdinaryContextRendering()
    {
        EventHistoryPromptSection section = new()
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

        string source = await section.GetContentAsync(CreateBuildContext());
        ITemplate template = new HandlebarsTemplateCompiler().Compile(source);

        Assert.DoesNotContain("first runtime value", source, StringComparison.Ordinal);
        Assert.Equal("first runtime value", Render(template, [new TestObservation("test.event", "first runtime value")]));
        Assert.Equal("second runtime value", Render(template, [new TestObservation("test.event", "second runtime value")]));
    }

    /// <summary>
    /// Blank keys, duplicate exact keys, and blank fallbacks fail as clear authoring errors.
    /// </summary>
    [Fact]
    public async Task GetContent_InvalidAuthoring_ThrowsClearErrors()
    {
        EventHistoryPromptSection blankKey = new()
        {
            Fragments = [new EventHistoryPromptFragment { TypeKey = "  ", Source = "unused" }],
        };
        EventHistoryPromptSection duplicateKey = new()
        {
            Fragments =
            [
                new EventHistoryPromptFragment { TypeKey = "same", Source = "first" },
                new EventHistoryPromptFragment { TypeKey = "same", Source = "second" },
            ],
        };
        EventHistoryPromptSection blankFallback = new()
        {
            FallbackSource = "\t",
        };

        InvalidOperationException blankKeyError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => blankKey.GetContentAsync(CreateBuildContext()));
        InvalidOperationException duplicateError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => duplicateKey.GetContentAsync(CreateBuildContext()));
        InvalidOperationException fallbackError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => blankFallback.GetContentAsync(CreateBuildContext()));

        Assert.Contains("nonblank TypeKey", blankKeyError.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate exact TypeKey 'same'", duplicateError.Message, StringComparison.Ordinal);
        Assert.Contains("nonblank fallback", fallbackError.Message, StringComparison.Ordinal);
    }

    private static EventHistoryPromptSection CreateSpeechSection()
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

    private static async Task<string> CompileAndRenderAsync(
        EventHistoryPromptSection section,
        IReadOnlyList<Observation> observations)
    {
        string source = await section.GetContentAsync(CreateBuildContext());
        ITemplate template = new HandlebarsTemplateCompiler().Compile(source);
        return Render(template, observations);
    }

    private static string Render(ITemplate template, IReadOnlyList<Observation> observations)
        => template.Render(new Dictionary<string, object?>
        {
            ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:test_character" },
            [EventHistoryPromptSection.ObservationsContextKey] = observations,
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
