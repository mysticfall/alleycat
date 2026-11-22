using System.Globalization;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

/// <summary>Godot-runtime coverage for type-owned canonical event-history rendering.</summary>
[Headless]
public sealed class EventHistoryIntegrationTests
{
    /// <summary>
    /// Tool descriptions remain the sole source of tool-specific mechanics and etiquette, and state the corrected
    /// context-delivery contract: automatic per-request delivery, payload-free wait results that promise no
    /// observable feedback (AI-002 TR-15/16).
    /// </summary>
    [Fact]
    public void ProductionToolDescriptions_CarryPerToolMechanicsAndEtiquette()
    {
        using WaitTool waitTool = new();
        using SpeechTool speechTool = new();

        // wait: fresh context arrives automatically; the call is deliberate quiet time only — pausing after
        // actions is automatic — whose early end and persistent exchange the description states, with results
        // that promise no observable feedback (AI-002 TR-15/16; §11).
        Assert.Contains("arrive automatically", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains(
            "pausing after your own actions happens automatically and needs no call",
            waitTool.ToolDescription,
            StringComparison.Ordinal);
        Assert.Contains(
            "The quiet time can end early when circumstances give you an opening to act again",
            waitTool.ToolDescription,
            StringComparison.Ordinal);
        // A wake is a chance to act, never a command: waiting again or staying silent remains valid.
        Assert.Contains(
            "that is a chance to act, not an instruction, and you may wait again or stay silent",
            waitTool.ToolDescription,
            StringComparison.Ordinal);
        Assert.Contains("remain in your context like any other exchange", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("the record of your quiet time", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("never what was observed", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("leaves no record", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing new reaches you", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("wait a reasonable duration", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("Returns only why the wait ended", waitTool.ToolDescription, StringComparison.Ordinal);

        Assert.Contains("optional and repeatable", speechTool.ToolDescription, StringComparison.Ordinal);
        // speak: the call remains in context as the record of the speech (AI-002 TR-15/16).
        Assert.Contains("remains in your context as the record of what you said", speechTool.ToolDescription, StringComparison.Ordinal);
    }

    /// <summary>History keeps chronology and joins type-owned entries with exactly one newline; event times state
    /// how long ago each event happened relative to the supplied composition moment.</summary>
    [Fact]
    public async Task RenderAsync_UsesCanonicalTextInChronologicalOrder()
    {
        ObservationHistoryRenderer renderer = CreateRenderer();
        Observation[] observations =
        [
            new ObservedSpeech("char:test_character", "Self line.") { ObservedAt = 100.2d },
            new TestObservation("world.changed") { ObservedAt = 300.55d },
            new ObservedSpeech("char:rin", "Known line.") { ObservedAt = 7200d },
            new TestObservation("world.changed_later") { ObservedAt = 900100d },
        ];

        string output = await renderer.RenderAsync(observations, nowSeconds: 900200d);

        Assert.Equal(
            "I said: Self line. (250 hr 1 min ago)\n"
                + "((Received world.changed event.)) (249 hr 58 min ago)\n"
                + "Heard `char:rin` say: Known line. (248 hr 3 min ago)\n"
                + "((Received world.changed_later event.)) (1 min 40 sec ago)",
            output);
    }

    /// <summary>Speech wording is owner-relative while transport provenance remains private.</summary>
    [Fact]
    public async Task RenderAsync_RendersActorRelativeSpeechWithoutTransportMetadata()
    {
        ObservationHistoryRenderer renderer = CreateRenderer();
        Observation[] observations =
        [
            new ObservedSpeech("char:rin", "Hello"),
            new ObservedSpeech(null, "Who is there?"),
            new ObservedSpeech("char:test_character", "Welcome."),
            new ObservedSpeech("CHAR:TEST_CHARACTER", "Not myself."),
        ];

        string output = await renderer.RenderAsync(observations);

        Assert.Equal(
            "Heard `char:rin` say: Hello\nHeard an unknown speaker say: Who is there?\n"
                + "I said: Welcome.\nHeard `CHAR:TEST_CHARACTER` say: Not myself.",
            output);
        Assert.DoesNotContain("raw-known-device", output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-unrecognised-device", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-self", output, StringComparison.Ordinal);
        Assert.DoesNotContain("case-sensitive-device", output, StringComparison.Ordinal);
    }

    /// <summary>Continuation projection still groups selected speech at its latest timeline position.</summary>
    [Fact]
    public async Task ContinuationProjection_JoinsSelectedSegmentsAtLatestPosition()
    {
        AcceptedObservationEntry[] timeline =
        [
            CreateEntry(1, new ObservedSpeech("char:rin", "later"), new SpeechObservationTransport("private-voice", "private-group", 1, continued: true)),
            CreateEntry(2, new TestObservation("world.changed")),
            CreateEntry(3, new ObservedSpeech("char:rin", "earlier"), new SpeechObservationTransport("private-voice", "private-group", 0)),
        ];
        ObservationHistoryRenderer renderer = CreateRenderer();

        IReadOnlyList<ContinuationProjection.Event> projected = ContinuationProjection.Project(timeline);
        string selected = await renderer.RenderAsync([timeline[0]], timeline);
        string rendered = await renderer.RenderAsync(timeline);

        ContinuationProjection.Event grouped = Assert.Single(projected, item => item.Correlation is not null);
        Assert.Equal(3, grouped.Position);
        Assert.Equal(2, projected.Count);
        Assert.Equal("Heard `char:rin` say: earlier … later", selected);
        Assert.Equal("((Received world.changed event.))\nHeard `char:rin` say: earlier … later", rendered);
        Assert.DoesNotContain("private-voice", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("private-group", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Model-facing newly-presented marking is retired (AI-002 TR-3, AI-003 TR-10): the canonical renderer always
    /// emits observation-owned text with no watermark-derived prefix, and joined speech groups stay joined.
    /// </summary>
    [Fact]
    public async Task RenderAsync_CarriesNoNewlyPresentedMarking()
    {
        AcceptedObservationEntry[] timeline =
        [
            CreateEntry(1, new TestObservation("world.settled")),
            CreateEntry(2, new ObservedSpeech("char:rin", "earlier"), new SpeechObservationTransport("private-voice", "private-group", 0)),
            CreateEntry(3, new ObservedSpeech("char:rin", "later"), new SpeechObservationTransport("private-voice", "private-group", 1, continued: true)),
            CreateEntry(4, new TestObservation("world.changed")),
        ];
        ObservationHistoryRenderer renderer = CreateRenderer();

        string output = await renderer.RenderAsync(timeline);

        Assert.Equal(
            "((Received world.settled event.))\n"
                + "Heard `char:rin` say: earlier … later\n"
                + "((Received world.changed event.))",
            output);
        Assert.DoesNotContain("[new]", output, StringComparison.Ordinal);
    }

    /// <summary>Relative-time framing is invariant even when the active process culture is not; unstamped
    /// observations keep no suffix.</summary>
    [Fact]
    public async Task RenderAsync_UsesInvariantTimestampFramingAndOmitsUnstampedSuffixes()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            string output = await CreateRenderer().RenderAsync(
                [new TestObservation("world.changed") { ObservedAt = 1.25d }, new TestObservation("world.quiet")],
                nowSeconds: 61.5d);

            Assert.Equal(
                "((Received world.changed event.)) (1 min ago)\n((Received world.quiet event.))",
                output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static ObservationHistoryRenderer CreateRenderer()
        => new(new PromptOwnerCharacter("test_character"));

    private static AcceptedObservationEntry CreateEntry(
        long sequenceID,
        Observation payload,
        SpeechObservationTransport? speechTransport = null)
        => new(sequenceID, 0d, payload, new ObservationSchedulingMetadata(0f, false), IsRetained: true)
        {
            SpeechTransport = speechTransport,
        };

    private sealed record TestObservation(string SemanticTypeKey) : Observation
    {
        public override string TypeKey => SemanticTypeKey;

        public override float CalculateImportance(ObservationContext context) => 1f;
    }
}
