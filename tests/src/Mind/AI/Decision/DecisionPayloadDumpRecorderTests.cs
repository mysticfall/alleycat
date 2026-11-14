using System.Text.Json;
using AlleyCat.Mind.AI.Decision;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Decision;

/// <summary>
/// Deterministic coverage for the decision-payload dump recorder (decision-payload logging): file naming is
/// sortable and self-describing, the JSON content carries the verbatim evidence and question instructions plus
/// the evaluation's metadata and outcome, and a writing failure is contained as a Warning that never propagates.
/// </summary>
public sealed class DecisionPayloadDumpRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AlleyCat.DecisionPayloadDumpTests", Guid.NewGuid().ToString("N"));

    /// <summary>Redirects every recorder to the isolated per-test root.</summary>
    public DecisionPayloadDumpRecorderTests()
    {
        DecisionPayloadDumpRecorder.SetRootPathOverrideForTesting(_root);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        DecisionPayloadDumpRecorder.SetRootPathOverrideForTesting(null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void Record_WritesASelfDescribingSortableFileWithTheVerbatimRequestMaterial()
    {
        DecisionPayloadDumpRecorder recorder = CreateRecorder();
        JsonElement evidence = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["scenario"] = "A market stall at noon.",
            ["recent_events"] = new[] { "Heard `char:tola` say: hello" },
        });
        DecisionPayloadDump dump = new(
            Timestamp: new DateTimeOffset(2026, 11, 12, 15, 30, 45, 123, DateTimeOffset.Now.Offset),
            CharacterId: "char:rin",
            TriggerKind: "Event",
            TriggerSequenceID: 7,
            Applied: true,
            Stale: false,
            Threshold: 0.5,
            Verdict: "act",
            Act: true,
            Probability: 0.82,
            Confidence: 0.9,
            CompositionMs: 3,
            DecisionMs: 214,
            EvidenceChars: 440,
            CoalescedTriggers: 2,
            Failure: null,
            Evidence: evidence,
            Questions:
            [
                new DecisionPayloadQuestion("act_now", "Should this character act now?"),
            ]);

        recorder.Record(dump);

        string file = Assert.Single(Directory.GetFiles(_root));
        Assert.Equal(
            "2026-11-12-15-30-45-123_char-rin_Event_act.json",
            Path.GetFileName(file));

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = document.RootElement;
        Assert.Equal("char:rin", root.GetProperty("characterId").GetString());
        Assert.Equal("Event", root.GetProperty("triggerKind").GetString());
        Assert.Equal(7, root.GetProperty("triggerSequenceID").GetInt64());
        Assert.True(root.GetProperty("applied").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal(0.5, root.GetProperty("threshold").GetDouble());
        Assert.Equal("act", root.GetProperty("verdict").GetString());
        Assert.Equal(0.82, root.GetProperty("probability").GetDouble());
        Assert.Equal(0.9, root.GetProperty("confidence").GetDouble());
        Assert.Equal(214, root.GetProperty("decisionMs").GetInt64());
        Assert.Equal(440, root.GetProperty("evidenceChars").GetInt32());
        Assert.Equal(2, root.GetProperty("coalescedTriggers").GetInt32());

        // The evidence JSON is verbatim: the dispatched object survives byte-for-byte inside the dump, keeping
        // the builder's own field names.
        Assert.Equal("A market stall at noon.", root.GetProperty("evidence").GetProperty("scenario").GetString());
        Assert.Equal(
            "Heard `char:tola` say: hello",
            root.GetProperty("evidence").GetProperty("recent_events")[0].GetString());

        JsonElement question = Assert.Single(root.GetProperty("questions").EnumerateArray());
        Assert.Equal("act_now", question.GetProperty("name").GetString());
        Assert.Equal("Should this character act now?", question.GetProperty("instructions").GetString());
    }

    /// <inheritdoc/>
    [Fact]
    public void Record_WritesKoreanEvidenceAsRawUtf8ForHumanReadability()
    {
        DecisionPayloadDumpRecorder recorder = CreateRecorder();
        JsonElement evidence = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["recent_events"] = new[] { "Heard `char:player` say: 저기요" },
        });
        var dump = new DecisionPayloadDump(
            Timestamp: DateTimeOffset.Now,
            CharacterId: "char:rin",
            TriggerKind: "Event",
            TriggerSequenceID: 1,
            Applied: false,
            Stale: false,
            Threshold: 0.5,
            Verdict: "idle",
            Act: false,
            Probability: 0.31,
            Confidence: null,
            CompositionMs: 1,
            DecisionMs: 9,
            EvidenceChars: 120,
            CoalescedTriggers: 0,
            Failure: null,
            Evidence: evidence,
            Questions: [new DecisionPayloadQuestion("act_now", "Should this character act?")]);

        recorder.Record(dump);

        string contents = File.ReadAllText(Assert.Single(Directory.GetFiles(_root)));
        Assert.Contains("저기요", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", contents, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    [Fact]
    public void Record_FailedEvaluation_CarriesTheFailureKindAndFailedVerdictToken()
    {
        DecisionPayloadDumpRecorder recorder = CreateRecorder();
        DecisionPayloadDump dump = new(
            Timestamp: DateTimeOffset.Now,
            CharacterId: "char:rin",
            TriggerKind: "Interruption",
            TriggerSequenceID: 0,
            Applied: false,
            Stale: false,
            Threshold: 0.5,
            Verdict: "failed",
            Act: false,
            Probability: null,
            Confidence: null,
            CompositionMs: 1,
            DecisionMs: 40,
            EvidenceChars: 0,
            CoalescedTriggers: 0,
            Failure: "decision failure Timeout",
            Evidence: null,
            Questions: [new DecisionPayloadQuestion("interrupt_in_flight", "Interrupt?")]);

        recorder.Record(dump);

        string failedFile = Assert.Single(Directory.GetFiles(_root));
        Assert.EndsWith("_char-rin_Interruption_failed.json", Path.GetFileName(failedFile));
        using var document = JsonDocument.Parse(File.ReadAllText(failedFile));
        Assert.Equal("failed", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("decision failure Timeout", document.RootElement.GetProperty("failure").GetString());
        Assert.Equal(
            "Interrupt?",
            Assert.Single(document.RootElement.GetProperty("questions").EnumerateArray())
                .GetProperty("instructions")
                .GetString());
    }

    /// <inheritdoc/>
    [Fact]
    public void Record_WhenWritingFails_IsContainedAndNeverPropagates()
    {
        // The root override is cleared so the recorder must consult its resolver — which throws — proving the
        // failure is contained rather than masked by the shared test override.
        DecisionPayloadDumpRecorder.SetRootPathOverrideForTesting(null);
        DecisionPayloadDumpRecorder recorder = new(
            "char:rin",
            () => throw new InvalidOperationException("The root resolver exploded."),
            NullLogger.Instance);
        DecisionPayloadDump dump = CreateMinimalDump();

        recorder.Record(dump);

        Assert.False(Directory.Exists(_root) && Directory.GetFiles(_root).Length > 0);
    }

    private static DecisionPayloadDumpRecorder CreateRecorder()
        => new("char:rin", () => "/unused-physical-root", NullLogger.Instance);

    private static DecisionPayloadDump CreateMinimalDump() => new(
            DateTimeOffset.Now,
            "char:rin",
            "Idle",
            0,
            Applied: false,
            Stale: false,
            Threshold: 0.5,
            "idle",
            Act: false,
            Probability: 0.2,
            Confidence: null,
            CompositionMs: 0,
            DecisionMs: 5,
            EvidenceChars: 100,
            CoalescedTriggers: 0,
            Failure: null,
            Evidence: JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["identity"] = "test" }),
            Questions: [new DecisionPayloadQuestion("act_now", "Act?")]);
}
