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
            TriggerKind: "GazeCadence",
            Verdict: "char:tola",
            Confidence: 0.9,
            CompositionMs: 3,
            DecisionMs: 214,
            EvidenceChars: 440,
            CoalescedTriggers: 2,
            Failure: null,
            Evidence: evidence,
            Questions:
            [
                new DecisionPayloadQuestion(
                    "gaze_target",
                    "Where should `char:rin` look?",
                    [
                        new DecisionPayloadQuestionOption("char:tola", "`char:tola`'s face/appearance; attention 0.9."),
                        new DecisionPayloadQuestionOption("none", "nothing in particular — no one deserves attention right now."),
                    ]),
            ]);

        _ = recorder.Record(dump);

        string file = Assert.Single(Directory.GetFiles(CategoryDirectory("gaze")));
        Assert.Equal(
            "2026-11-12-15-30-45-123_char-rin_GazeCadence_char-tola.json",
            Path.GetFileName(file));

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = document.RootElement;
        Assert.Equal("char:rin", root.GetProperty("characterId").GetString());
        Assert.Equal("GazeCadence", root.GetProperty("triggerKind").GetString());
        Assert.Equal("char:tola", root.GetProperty("verdict").GetString());
        Assert.Equal(0.9, root.GetProperty("confidence").GetDouble());
        Assert.Equal(3, root.GetProperty("compositionMs").GetInt64());
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
        Assert.Equal("gaze_target", question.GetProperty("name").GetString());
        Assert.Equal("Where should `char:rin` look?", question.GetProperty("instructions").GetString());

        // Every offered option's value and criteria serialise verbatim, so the dump shows the full candidate
        // list the decision model chose from.
        JsonElement options = question.GetProperty("options");
        Assert.Equal(2, options.GetArrayLength());
        Assert.Equal("char:tola", options[0].GetProperty("value").GetString());
        Assert.Equal(
            "`char:tola`'s face/appearance; attention 0.9.",
            options[0].GetProperty("criteria").GetString());
        Assert.Equal("none", options[1].GetProperty("value").GetString());
        Assert.Equal(
            "nothing in particular — no one deserves attention right now.",
            options[1].GetProperty("criteria").GetString());
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
            TriggerKind: "GazeSpeakInvoked",
            Verdict: "none",
            Confidence: null,
            CompositionMs: 1,
            DecisionMs: 9,
            EvidenceChars: 120,
            CoalescedTriggers: 0,
            Failure: null,
            Evidence: evidence,
            Questions: [new DecisionPayloadQuestion("gaze_target", "Where should `char:rin` look?")]);

        _ = recorder.Record(dump);

        string contents = File.ReadAllText(Assert.Single(Directory.GetFiles(CategoryDirectory("gaze"))));
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
            TriggerKind: "GazeAttendedSpeakerEnded",
            Verdict: "failed",
            Confidence: null,
            CompositionMs: 1,
            DecisionMs: 40,
            EvidenceChars: 0,
            CoalescedTriggers: 0,
            Failure: "decision failure Timeout",
            Evidence: null,
            Questions: [new DecisionPayloadQuestion("gaze_target", "Where should `char:rin` look?")]);

        _ = recorder.Record(dump);

        string failedFile = Assert.Single(Directory.GetFiles(CategoryDirectory("gaze")));
        Assert.EndsWith("_char-rin_GazeAttendedSpeakerEnded_failed.json", Path.GetFileName(failedFile));
        using var document = JsonDocument.Parse(File.ReadAllText(failedFile));
        Assert.Equal("failed", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("decision failure Timeout", document.RootElement.GetProperty("failure").GetString());
        Assert.Equal(
            "Where should `char:rin` look?",
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
            "gaze",
            "char:rin",
            "rin",
            () => throw new InvalidOperationException("The root resolver exploded."),
            NullLogger.Instance);
        DecisionPayloadDump dump = CreateMinimalDump();

        _ = recorder.Record(dump);

        Assert.False(Directory.Exists(_root) && Directory.GetFiles(_root).Length > 0);
    }

    /// <summary>
    /// Dumps follow the session-transcript convention (AI-011 TR-2–TR-5): beneath one latched session directory,
    /// inside the character's own directory — the same location the per-character transcript logs use — with each
    /// producer's category filing into its own subdirectory beside them.
    /// </summary>
    [Fact]
    public void Record_FilesIntoTheCharacterTranscriptLocation_CategoriesSeparated()
    {
        DecisionPayloadDumpRecorder gaze = CreateRecorder();
        DecisionPayloadDumpRecorder wait = new("wait", "char:rin", "rin", () => "/unused-physical-root", NullLogger.Instance);

        _ = gaze.Record(CreateMinimalDump(timestamp: DateTimeOffset.Now));
        _ = gaze.Record(CreateMinimalDump(timestamp: DateTimeOffset.Now.AddMilliseconds(50)));
        _ = wait.Record(CreateMinimalDump(timestamp: DateTimeOffset.Now));

        // One session directory, one character directory, shared by both log families' producers.
        string sessionDirectory = Assert.Single(Directory.GetDirectories(_root));
        string characterDirectory = Assert.Single(Directory.GetDirectories(sessionDirectory));
        Assert.Equal("rin", Path.GetFileName(characterDirectory), StringComparer.Ordinal);
        string dumpRoot = Path.Combine(characterDirectory, DecisionPayloadDumpRecorder.DumpDirectoryName);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(dumpRoot, "gaze")).Length);
        _ = Assert.Single(Directory.GetFiles(Path.Combine(dumpRoot, "wait")));
        Assert.Empty(Directory.GetFiles(dumpRoot));
    }

    /// <summary>A category carrying path separators sanitises into a single safe directory name.</summary>
    [Fact]
    public void Record_CategoryWithPathSeparators_SanitisesIntoOneSafeDirectoryName()
    {
        DecisionPayloadDumpRecorder recorder = new(
            "wait/poll:gate",
            "char:rin",
            "rin",
            () => "/unused-physical-root",
            NullLogger.Instance);

        _ = recorder.Record(CreateMinimalDump());

        string dumpRoot = Path.Combine(
            Assert.Single(Directory.GetDirectories(Assert.Single(Directory.GetDirectories(_root)))),
            DecisionPayloadDumpRecorder.DumpDirectoryName);
        _ = Assert.Single(Directory.GetFiles(Path.Combine(dumpRoot, "wait-poll-gate")));
        Assert.False(Directory.Exists(Path.Combine(dumpRoot, "wait")));
    }

    /// <summary>
    /// Resolves a category directory inside the transcript-log layout: the override root's single latched session
    /// directory, then the single character directory, then the dump directory and category.
    /// </summary>
    private string CategoryDirectory(string category)
        => Path.Combine(
            Assert.Single(Directory.GetDirectories(Assert.Single(Directory.GetDirectories(_root)))),
            DecisionPayloadDumpRecorder.DumpDirectoryName,
            category);

    private static DecisionPayloadDumpRecorder CreateRecorder()
        => new("gaze", "char:rin", "rin", () => "/unused-physical-root", NullLogger.Instance);

    private static DecisionPayloadDump CreateMinimalDump(DateTimeOffset? timestamp = null) => new(
            timestamp ?? DateTimeOffset.Now,
            "char:rin",
            "GazeCadence",
            "none",
            Confidence: null,
            CompositionMs: 0,
            DecisionMs: 5,
            EvidenceChars: 100,
            CoalescedTriggers: 0,
            Failure: null,
            Evidence: JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["identity"] = "test" }),
            Questions: [new DecisionPayloadQuestion("gaze_target", "Where should `char:rin` look?")]);
}
