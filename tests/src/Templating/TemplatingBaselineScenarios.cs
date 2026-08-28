using System.Globalization;

namespace AlleyCat.Tests.Templating;

/// <summary>
/// One golden-baseline rendering scenario captured against the pre-migration Handlebars engine and later
/// reproduced through the Fluid engine byte-for-byte.
/// </summary>
/// <param name="Name">Stable snapshot file stem.</param>
/// <param name="CaptureSource">
/// Template source rendered when the baselines were captured, written in the Handlebars syntax that the
/// pre-migration engine consumed.
/// </param>
/// <param name="FluidSource">
/// Equivalent Liquid source rendered by the migrated Fluid engine during golden-equivalence checks.
/// </param>
/// <param name="ContextFactory">Builds a fresh representative render context for every run.</param>
/// <param name="CultureName">
/// Optional pinned current culture; when omitted the invariant culture is used so snapshots stay
/// machine-independent.
/// </param>
/// <param name="Partials">Optional inline partials registered before compilation.</param>
/// <param name="ExpectFailure">When set, the scenario must fail to compile or render.</param>
internal sealed record TemplatingBaselineScenario(
    string Name,
    string CaptureSource,
    string FluidSource,
    Func<IReadOnlyDictionary<string, object?>> ContextFactory,
    string? CultureName = null,
    IReadOnlyDictionary<string, string>? Partials = null,
    bool ExpectFailure = false);

/// <summary>
/// Catalogue of authored-template and built-in-tool baseline scenarios. Snapshot inputs are fixed so the
/// Handlebars-era captures remain valid reference points for the Fluid migration.
/// </summary>
internal static class TemplatingBaselineScenarios
{
    /// <summary>Deterministic reference timestamp supplied to every ago scenario.</summary>
    public static readonly DateTimeOffset AgoNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string NpcEventHistoryFragmentTypeKey = "speech.observed";
    private const string NpcVisualDescriptionTypeKey = "vision.description";

    // Captured verbatim from the shared NPC event-history asset's speech fragment before the Liquid migration
    // (Handlebars syntax at capture time).
    private const string NpcEventHistoryFragmentSource =
        "{{#if ActorId}}{{#if (eqOrdinal ActorId @root.character.FullId)}}I said: {{Content}}"
        + "{{else}}Heard {{ActorId}} say: {{Content}}{{/if}}"
        + "{{else}}Heard an unknown speaker say: {{Content}}{{/if}}"
        + "{{#if ObservedAt}} (at {{nf ObservedAt 1}}s game time){{/if}}\n";

    // Captured verbatim from the shared NPC event-history asset's fallback before the Liquid migration.
    private const string NpcEventHistoryFallbackSource =
        "((Received {{TypeKey}} event.)){{#if ObservedAt}} (at {{nf ObservedAt 1}}s game time){{/if}}\n";

    private const string NpcVisualDescriptionSource =
        "Observed {{SubjectId}}: {{Description}}{{#if ObservedAt}}"
        + " (at {{nf ObservedAt 1}}s game time){{/if}}\n";

    /// <summary>
    /// Liquid equivalent of the composed event-history dispatch source used to prove that the migrated engine
    /// reproduces the Handlebars-era output once authored fragments move to Liquid syntax.
    /// </summary>
    private const string NpcEventHistoryDispatchFluidSource =
        "{% for o in observations -%}"
        + "{%- if o.TypeKey == 'speech.observed' %}"
        + "{% if o.ActorId != blank %}{% if o.ActorId == character.FullId %}I said: {{ o.Content }}"
        + "{% else %}Heard {{ o.ActorId }} say: {{ o.Content }}{% endif %}"
        + "{% else %}Heard an unknown speaker say: {{ o.Content }}{% endif %}"
        + "{% if o.ObservedAt != blank %} (at {{ nf(o.ObservedAt, 1) }}s game time){% endif %}\n"
        + "{% elsif o.TypeKey == 'vision.description' %}Observed {{ o.SubjectId }}: {{ o.Description }}"
        + "{% if o.ObservedAt != blank %} (at {{ nf(o.ObservedAt, 1) }}s game time){% endif %}\n"
        + "{% else %}((Received {{ o.TypeKey }} event.))"
        + "{% if o.ObservedAt != blank %} (at {{ nf(o.ObservedAt, 1) }}s game time){% endif %}\n"
        + "{% endif -%}"
        + "{%- endfor %}";

    /// <summary>Liquid equivalent of the authored scenario prompt section.</summary>
    private const string ScenarioSectionFluidSource =
        "{% if scenario %}You are currently engaged in the following scenario; pursue its objectives and heed "
        + "its context when choosing your actions:\n{{ scenario.Description }}{% endif %}\n";

    public static IReadOnlyList<TemplatingBaselineScenario> All
    {
        get;
    } =
    [
        .. AuthoredTemplateScenarios(),
        .. BuiltInToolScenarios(),
    ];

    private static IEnumerable<TemplatingBaselineScenario> AuthoredTemplateScenarios()
    {
        yield return new TemplatingBaselineScenario(
            "authored_mind_md",
            ReadGameFile("prompts", "mind.md"),
            ReadGameFile("prompts", "mind.md"),
            () => new Dictionary<string, object?>
            {
                ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:npc_kaori" },
                ["player"] = new Dictionary<string, object?> { ["FullId"] = "char:player_ava" },
            });

        yield return new TemplatingBaselineScenario(
            "authored_scenario_section_with_scenario",
            ReadGameFile("prompts", "scenario.md"),
            ScenarioSectionFluidSource,
            () => new Dictionary<string, object?>
            {
                ["scenario"] = new Dictionary<string, object?>
                {
                    ["Description"] = "Interrogate the detainee before the night shift changes.",
                },
                ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:npc_kaori" },
                ["player"] = new Dictionary<string, object?> { ["FullId"] = "char:player_ava" },
            });

        yield return new TemplatingBaselineScenario(
            "authored_scenario_section_without_scenario",
            ReadGameFile("prompts", "scenario.md"),
            ScenarioSectionFluidSource,
            () => new Dictionary<string, object?>
            {
                ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:npc_kaori" },
            });

        yield return new TemplatingBaselineScenario(
            "authored_npc_event_history_dispatch",
            ComposeNpcEventHistorySource(),
            NpcEventHistoryDispatchFluidSource,
            CreateEventHistoryContext);

        yield return new TemplatingBaselineScenario(
            "authored_test_scenario_token_fixture",
            ReadTestingPromptBody("test_scenario_token.md"),
            "The interrogator {{ character.FullId }} must extract the pass phrase from the detainee "
            + "{{ player.FullId }} before the shift changes.\n",
            () => new Dictionary<string, object?>
            {
                ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:npc_kaori" },
                ["player"] = new Dictionary<string, object?> { ["FullId"] = "char:player_ava" },
            });

        yield return new TemplatingBaselineScenario(
            "authored_test_scenario_broken_template_fixture",
            ReadTestingPromptBody("test_scenario_broken_template.md"),
            "{% if character %}The subject never arrives.\n",
            () => new Dictionary<string, object?>
            {
                ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:npc_kaori" },
            },
            ExpectFailure: true);

        yield return new TemplatingBaselineScenario(
            "context_member_access_paths",
            "{{ character.FullId }}/{{player.FullId}}/{{item.Label}}/{{absent.Key}}",
            "{{ character.FullId }}/{{ player.FullId }}/{{ item.Label }}/{{ absent.Key }}",
            () => new Dictionary<string, object?>
            {
                ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:npc_kaori" },
                ["player"] = new Dictionary<string, object?> { ["FullId"] = "char:player_ava" },
                ["item"] = new Dictionary<string, object?> { ["Label"] = "Brass Key" },
            });

        yield return new TemplatingBaselineScenario(
            "partial_inline_registered",
            "Hello {{> label}}",
            "Hello {% include 'label' %}",
            () => new Dictionary<string, object?> { ["name"] = "Nyx" },
            Partials: new Dictionary<string, string> { ["label"] = "{{name}}!" });
    }

    private static IEnumerable<TemplatingBaselineScenario> BuiltInToolScenarios()
    {
        yield return new TemplatingBaselineScenario(
            "tool_add_basic_and_signed",
            "{{add a b}},{{add c d}},{{add e f}}",
            "{{ add(a, b) }},{{ add(c, d) }},{{ add(e, f) }}",
            () => new Dictionary<string, object?>
            {
                ["a"] = 2,
                ["b"] = "3",
                ["c"] = -7,
                ["d"] = 3,
                ["e"] = "10",
                ["f"] = "-2",
            });

        yield return new TemplatingBaselineScenario(
            "tool_add_fractional_and_missing_arguments",
            "{{add g h}},|{{add}}|,|{{add only}}|",
            "{{ add(g, h) }},|{{ add() }}|,|{{ add(only) }}|",
            () => new Dictionary<string, object?>
            {
                ["g"] = 2.5,
                ["h"] = 0,
                ["only"] = 7,
            });

        yield return new TemplatingBaselineScenario(
            "tool_eq_case_insensitive",
            "{{eq a b}},{{eq a c}},{{eq d e}}",
            "{{ eq(a, b) }},{{ eq(a, c) }},{{ eq(d, e) }}",
            () => new Dictionary<string, object?>
            {
                ["a"] = "test",
                ["b"] = "TEST",
                ["c"] = "toast",
            });

        yield return new TemplatingBaselineScenario(
            "tool_eq_numeric_string_coercion",
            "{{eq n s}},{{eq n other}}",
            "{{ eq(n, s) }},{{ eq(n, other) }}",
            () => new Dictionary<string, object?>
            {
                ["n"] = 5,
                ["s"] = "5",
                ["other"] = "05",
            });

        yield return new TemplatingBaselineScenario(
            "tool_eq_ordinal_case_sensitive",
            "{{eqOrdinal k \"speech.observed\"}},{{eqOrdinal k \"Speech.Observed\"}}",
            "{{ eqOrdinal(k, 'speech.observed') }},{{ eqOrdinal(k, 'Speech.Observed') }}",
            () => new Dictionary<string, object?>
            {
                ["k"] = "speech.observed",
            });

        yield return new TemplatingBaselineScenario(
            "tool_eq_ordinal_invariant_conversion",
            "{{eqOrdinal d \"3,14\"}},{{eqOrdinal d \"3.14\"}}",
            "{{ eqOrdinal(d, '3,14') }},{{ eqOrdinal(d, '3.14') }}",
            () => new Dictionary<string, object?>
            {
                ["d"] = 3.14,
            },
            CultureName: "fr-FR");

        yield return new TemplatingBaselineScenario(
            "tool_eq_ordinal_missing_arguments",
            "|{{eqOrdinal solo}}|",
            "|{{ eqOrdinal(solo) }}|",
            () => new Dictionary<string, object?> { ["solo"] = "value" });

        yield return new TemplatingBaselineScenario(
            "tool_nf_default_precision",
            "{{nf v}}",
            "{{ nf(v) }}",
            () => new Dictionary<string, object?> { ["v"] = 3.14159 });

        yield return new TemplatingBaselineScenario(
            "tool_nf_precision_clamps",
            "{{nf v -1}},{{nf s 2}},{{nf i 120}}",
            "{{ nf(v, -1) }},{{ nf(s, 2) }},{{ nf(i, 120) }}",
            () => new Dictionary<string, object?>
            {
                ["v"] = "3.9",
                ["s"] = 3.14159,
                ["i"] = 1,
            });

        yield return new TemplatingBaselineScenario(
            "tool_nf_unparseable_and_missing",
            "[{{nf absent}}],[{{nf junk}}]",
            "[{{ nf(absent) }}],[{{ nf(junk) }}]",
            () => new Dictionary<string, object?> { ["junk"] = "abc" });

        yield return new TemplatingBaselineScenario(
            "tool_nf_current_culture",
            "{{nf v 1}},{{nf w 120}}",
            "{{ nf(v, 1) }},{{ nf(w, 120) }}",
            () => new Dictionary<string, object?>
            {
                ["v"] = 3.5,
                ["w"] = 1,
            },
            CultureName: "fr-FR");

        yield return new TemplatingBaselineScenario(
            "tool_repeat_counts",
            "{{repeat v three}},{{repeat v zero}},{{repeat v neg}},{{repeat empty two}}",
            "{{ repeat(v, three) }},{{ repeat(v, zero) }},{{ repeat(v, neg) }},{{ repeat(hollow, two) }}",
            () => new Dictionary<string, object?>
            {
                ["v"] = "ab",
                ["three"] = 3,
                ["zero"] = 0,
                ["neg"] = -2,
                ["hollow"] = "",
                ["two"] = 2,
            });

        yield return new TemplatingBaselineScenario(
            "tool_ago_singular_units",
            "{{ago s now 0}},{{ago m now 0}},{{ago h now 0}},{{ago d now 0}},{{ago w now 0}}",
            "{{ ago(s, now, 0) }},{{ ago(m, now, 0) }},{{ ago(h, now, 0) }},{{ ago(d, now, 0) }},{{ ago(w, now, 0) }}",
            () => new Dictionary<string, object?>
            {
                ["s"] = AgoNow.AddSeconds(-1),
                ["m"] = AgoNow.AddMinutes(-1),
                ["h"] = AgoNow.AddHours(-1),
                ["d"] = AgoNow.AddDays(-1),
                ["w"] = AgoNow.AddDays(-7),
                ["now"] = AgoNow,
            });

        yield return new TemplatingBaselineScenario(
            "tool_ago_plural_boundaries",
            "{{ago s30 now}},{{ago s59 now}},{{ago s90 now}},{{ago h23 now}},{{ago d6 now}}",
            "{{ ago(s30, now) }},{{ ago(s59, now) }},{{ ago(s90, now) }},{{ ago(h23, now) }},{{ ago(d6, now) }}",
            () => new Dictionary<string, object?>
            {
                ["s30"] = AgoNow.AddSeconds(-30),
                ["s59"] = AgoNow.AddSeconds(-59),
                ["s90"] = AgoNow.AddSeconds(-90),
                ["h23"] = AgoNow.AddHours(-23),
                ["d6"] = AgoNow.AddDays(-6),
                ["now"] = AgoNow,
            });

        yield return new TemplatingBaselineScenario(
            "tool_ago_unit_floors",
            "{{ago s60 now}},{{ago h24 now}},{{ago d7 now}},{{ago d13 now}},{{ago d70 now}}",
            "{{ ago(s60, now) }},{{ ago(h24, now) }},{{ ago(d7, now) }},{{ ago(d13, now) }},{{ ago(d70, now) }}",
            () => new Dictionary<string, object?>
            {
                ["s60"] = AgoNow.AddSeconds(-60),
                ["h24"] = AgoNow.AddHours(-24),
                ["d7"] = AgoNow.AddDays(-7),
                ["d13"] = AgoNow.AddDays(-13),
                ["d70"] = AgoNow.AddDays(-70),
                ["now"] = AgoNow,
            });

        yield return new TemplatingBaselineScenario(
            "tool_ago_default_threshold_boundary",
            "{{ago four now}},{{ago five now}}",
            "{{ ago(four, now) }},{{ ago(five, now) }}",
            () => new Dictionary<string, object?>
            {
                ["four"] = AgoNow.AddSeconds(-4),
                ["five"] = AgoNow.AddSeconds(-5),
                ["now"] = AgoNow,
            });

        yield return new TemplatingBaselineScenario(
            "tool_ago_explicit_threshold",
            "{{ago eight now 10}},{{ago nine now 10}},{{ago ten now 10}}",
            "{{ ago(eight, now, 10) }},{{ ago(nine, now, 10) }},{{ ago(ten, now, 10) }}",
            () => new Dictionary<string, object?>
            {
                ["eight"] = AgoNow.AddSeconds(-8),
                ["nine"] = AgoNow.AddSeconds(-9),
                ["ten"] = AgoNow.AddSeconds(-10),
                ["now"] = AgoNow,
            });

        yield return new TemplatingBaselineScenario(
            "tool_ago_future_and_invalid",
            "{{ago fut now}},{{ago nothing now}},{{ago junk now}}",
            "{{ ago(fut, now) }},{{ ago(nothing, now) }},{{ ago(junk, now) }}",
            () => new Dictionary<string, object?>
            {
                ["fut"] = AgoNow.AddSeconds(10),
                ["nothing"] = null,
                ["junk"] = "not a timestamp",
                ["now"] = AgoNow,
            });

        yield return new TemplatingBaselineScenario(
            "tool_ago_no_arguments",
            "A|{{ago}}|B",
            "A|{{ ago() }}|B",
            () => new Dictionary<string, object?>());

        yield return new TemplatingBaselineScenario(
            "tool_ago_string_timestamps",
            "{{ago isoZ now}},{{ago roundTrip now}}",
            "{{ ago(isoZ, now) }},{{ ago(roundTrip, now) }}",
            () => new Dictionary<string, object?>
            {
                ["isoZ"] = "2026-01-01T11:59:30Z",
                ["roundTrip"] = AgoNow.AddSeconds(-30).ToString("O", CultureInfo.InvariantCulture),
                ["now"] = AgoNow,
            });

        yield return new TemplatingBaselineScenario(
            "tool_ago_french_culture_round_trip",
            "{{ago roundTrip now}}",
            "{{ ago(roundTrip, now) }}",
            () => new Dictionary<string, object?>
            {
                ["roundTrip"] = AgoNow.AddSeconds(-30).ToString("O", CultureInfo.InvariantCulture),
                ["now"] = AgoNow,
            },
            CultureName: "fr-FR");

        yield return new TemplatingBaselineScenario(
            "tool_ago_datetime_kinds",
            "{{ago utcKind now}},{{ago unspecifiedKind now}}",
            "{{ ago(utcKind, now) }},{{ ago(unspecifiedKind, now) }}",
            () => new Dictionary<string, object?>
            {
                ["utcKind"] = new DateTime(2026, 1, 1, 11, 59, 30, DateTimeKind.Utc),
                ["unspecifiedKind"] = new DateTime(2026, 1, 1, 11, 59, 30, DateTimeKind.Unspecified),
                ["now"] = AgoNow,
            });
    }

    private static string ComposeNpcEventHistorySource()
    {
        // Reproduces the pre-migration event-history composed dispatch source exactly, as it existed when the
        // baselines were captured: the shared NPC asset's single authored speech fragment plus its fallback,
        // both in the Handlebars syntax used at capture time. The composition is inlined here because unit tests
        // must not instantiate Godot resource types outside the Godot runtime.
        return "{{#each observations}}"
            + "{{#if (eqOrdinal TypeKey \"" + NpcEventHistoryFragmentTypeKey + "\")}}"
            + NpcEventHistoryFragmentSource
            + "{{else}}"
            + "{{#if (eqOrdinal TypeKey \"" + NpcVisualDescriptionTypeKey + "\")}}"
            + NpcVisualDescriptionSource
            + "{{else}}"
            + NpcEventHistoryFallbackSource
            + "{{/if}}"
            + "{{/if}}{{/each}}";
    }

    private static IReadOnlyDictionary<string, object?> CreateEventHistoryContext()
    {
        return new Dictionary<string, object?>
        {
            ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:test_character" },
            ["observations"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["ActorId"] = "char:test_character",
                    ["Content"] = "Self line.",
                    ["ObservedAt"] = 100.2d,
                    ["TypeKey"] = "speech.observed",
                },
                new Dictionary<string, object?>
                {
                    ["ActorId"] = "char:rin",
                    ["Content"] = "Known line.",
                    ["ObservedAt"] = 7200d,
                    ["TypeKey"] = "speech.observed",
                },
                new Dictionary<string, object?>
                {
                    ["ActorId"] = null,
                    ["Content"] = "Unknown line.",
                    ["ObservedAt"] = null,
                    ["TypeKey"] = "speech.observed",
                },
                new Dictionary<string, object?>
                {
                    ["ActorId"] = "CHAR:TEST_CHARACTER",
                    ["Content"] = "Case-distinct line.",
                    ["ObservedAt"] = 864000d,
                    ["TypeKey"] = "speech.observed",
                },
                new Dictionary<string, object?>
                {
                    ["SubjectId"] = "char:coat",
                    ["Description"] = "A weathered red coat.",
                    ["ObservedAt"] = 900000d,
                    ["TypeKey"] = "vision.description",
                },
                new Dictionary<string, object?>
                {
                    ["ActorId"] = "char:nobody",
                    ["Content"] = "door opened",
                    ["ObservedAt"] = 300.55d,
                    ["TypeKey"] = "world.changed",
                },
            },
        };
    }

    private static string ReadGameFile(params string[] segments)
        => File.ReadAllText(RepositoryPath.Get(["game", .. segments]));

    /// <summary>
    /// Reads one testing-prompt fixture body, stripping its leading YAML front-matter block the same way the
    /// scenario manager does before rendering.
    /// </summary>
    private static string ReadTestingPromptBody(string fileName)
    {
        string content = ReadGameFile("assets", "testing", "prompts", fileName);
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return content;
        }

        int firstNewLine = content.IndexOf('\n');
        if (firstNewLine < 0)
        {
            return content;
        }

        int closeIndex = content.IndexOf("\n---", firstNewLine, StringComparison.Ordinal);
        while (closeIndex >= 0)
        {
            int afterDelimiter = closeIndex + "\n---".Length;
            if (afterDelimiter >= content.Length || content[afterDelimiter] == '\n')
            {
                break;
            }

            closeIndex = content.IndexOf("\n---", afterDelimiter, StringComparison.Ordinal);
        }

        if (closeIndex < 0)
        {
            return content;
        }

        int bodyStart = content.IndexOf('\n', closeIndex + 1) + 1;
        return bodyStart > 0 ? content[bodyStart..] : string.Empty;
    }
}
