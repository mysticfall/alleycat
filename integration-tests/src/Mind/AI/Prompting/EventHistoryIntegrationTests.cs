using System.Globalization;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.AI.Watch;
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
    /// context-delivery contract: automatic per-request delivery, payload-free wait results, read-only recorded
    /// recall, and watch registration semantics (AI-002 TR-15/16; AI-010 TR-14/15).
    /// </summary>
    [Fact]
    public void ProductionToolDescriptions_CarryPerToolMechanicsAndEtiquette()
    {
        using WaitTool waitTool = new();
        using SpeechTool speechTool = new();
        using HistoryTool historyTool = new();
        using ProximityWatchTool proximityWatchTool = new();
        UnwatchTool unwatchTool = new(registry: null);

        // wait: fresh context arrives automatically; waiting is deliberate yielding with a payload-free result,
        // and an answer already visible is distinguished from a reply not yet given.
        Assert.Contains("arrives with every request", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("awaiting a reply that has not been given yet", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("why the wait ended", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("current game time", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("never what was observed", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing new reaches you", waitTool.ToolDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("wait a reasonable duration", waitTool.ToolDescription, StringComparison.Ordinal);

        // history: read-only recall of recorded events, never framed against wait-result delivery.
        Assert.Contains("Reading changes nothing", historyTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("recorded", historyTool.ToolDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("wait results", historyTool.ToolDescription, StringComparison.Ordinal);

        // watch_proximity: immediate registration returning an opaque ID plus current evidence, persistent
        // monitoring without re-arming, and ordinary event-history transitions.
        Assert.Contains("opaque watch ID", proximityWatchTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("not an activation event", proximityWatchTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("never re-arm to keep it active", proximityWatchTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("ordinary events in your event history", proximityWatchTool.ToolDescription, StringComparison.Ordinal);

        // unwatch: removal by the opaque ID returned at arming or listed in current-scene status.
        Assert.Contains("opaque watch ID", unwatchTool.ToolDescription, StringComparison.Ordinal);

        Assert.Contains("optional and repeatable", speechTool.ToolDescription, StringComparison.Ordinal);
    }

    /// <summary>History keeps chronology and joins type-owned entries with exactly one newline.</summary>
    [Fact]
    public async Task RenderAsync_UsesCanonicalTextInChronologicalOrder()
    {
        ObservationHistoryRenderer renderer = CreateRenderer();
        Observation[] observations =
        [
            new ObservedSpeech("char:test_character", "Self line.") { ObservedAt = 100.2d },
            new TestObservation("world.changed") { ObservedAt = 300.55d },
            new ObservedSpeech("char:rin", "Known line.") { ObservedAt = 7200d },
            new ObservedProximityTransition("char:rin", ProximityTransition.Entered, 1.4f, 1f, requiresFreshTurn: true)
            {
                ObservedAt = 900100d,
            },
        ];

        string output = await renderer.RenderAsync(observations);

        Assert.Equal(
            "I said: Self line. (at 100.2s game time)\n"
                + "((Received world.changed event.)) (at 300.6s game time)\n"
                + "Heard char:rin say: Known line. (at 7200.0s game time)\n"
                + "char:rin entered the proximity condition at 1.4 m. (at 900100.0s game time)",
            output);
        Assert.DoesNotContain("w1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Watch", output, StringComparison.Ordinal);
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
            "Heard char:rin say: Hello\nHeard an unknown speaker say: Who is there?\n"
                + "I said: Welcome.\nHeard CHAR:TEST_CHARACTER say: Not myself.",
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

        IReadOnlyList<ContinuationProjection.Event> projected = ObservationHistoryRenderer.Project(timeline);
        string selected = await renderer.RenderAsync([timeline[0]], timeline);
        string rendered = await renderer.RenderAsync(timeline);

        ContinuationProjection.Event grouped = Assert.Single(projected, item => item.Correlation is not null);
        Assert.Equal(3, grouped.Position);
        Assert.Equal(2, projected.Count);
        Assert.Equal("Heard char:rin say: earlier … later", selected);
        Assert.Equal("((Received world.changed event.))\nHeard char:rin say: earlier … later", rendered);
        Assert.DoesNotContain("private-voice", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("private-group", rendered, StringComparison.Ordinal);
    }

    /// <summary>Timestamp framing is invariant even when the active process culture is not.</summary>
    [Fact]
    public async Task RenderAsync_UsesInvariantTimestampFramingAndOmitsUnstampedSuffixes()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            string output = await CreateRenderer().RenderAsync(
                [new TestObservation("world.changed") { ObservedAt = 1.25d }, new TestObservation("world.quiet")]);

            Assert.Equal(
                "((Received world.changed event.)) (at 1.2s game time)\n((Received world.quiet event.))",
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
