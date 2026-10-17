using System.Reflection;
using System.Text.RegularExpressions;
using AlleyCat.Mind.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Tests the filesystem mind session transcript recorder (AI-011 TR-2–TR-5, TR-7, TR-12): the storage layout, lazy
/// per-process run directory, per-character monotonic turn numbering across mind re-creation, the internal test
/// seams, and write-failure containment.
/// </summary>
[Collection(MindSessionTranscriptSeamCollection.Name)]
public sealed partial class MindSessionTranscriptRecorderTests : IDisposable
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex SortableRunTimestamp();

    [GeneratedRegex(
        @"^## \[Turn (\d{4,})\]\(turn-\d{4,}\.md\)",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex IndexTurnHeadings();

    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "AlleyCat.MindSessionTranscriptRecorderTests",
        Guid.NewGuid().ToString("N"));

    private readonly RecordingLogger _logger = new();

    /// <summary>Resets the process-wide turn counters and redirects the recorder root to a fresh temporary tree.</summary>
    public MindSessionTranscriptRecorderTests()
    {
        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(_rootDirectory);
    }

    /// <summary>Restores the recorder seams and deletes the temporary transcript tree.</summary>
    public void Dispose()
    {
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written; the root never existed.
        }
    }

    /// <summary>
    /// Turn numbers stay flat and monotonically increasing across a simulated mind node re-creation for the same
    /// character within one game run (AI-011 TR-4).
    /// </summary>
    [Fact]
    public void Record_AssignsMonotonicTurnNumbersAcrossSimulatedMindReCreation()
    {
        MindSessionTranscriptRecorder firstMind = CreateRecorder("luna");
        firstMind.Record(CreateCycle());
        firstMind.Record(CreateCycle());

        // A re-created mind node for the same character within one game run continues the previous counter.
        MindSessionTranscriptRecorder reCreatedMind = CreateRecorder("luna");
        reCreatedMind.Record(CreateCycle());

        string characterDirectory = Assert.Single(Directory.GetDirectories(Assert.Single(Directory.GetDirectories(_rootDirectory))));
        Assert.Equal(
            ["turn-0001.md", "turn-0002.md", "turn-0003.md"],
            Directory.GetFiles(characterDirectory, "turn-*.md").Select(Path.GetFileName).OrderBy(static name => name));
        Assert.Contains("# Mind Session Transcript — Turn 0003", File.ReadAllText(Path.Combine(characterDirectory, "turn-0003.md")));
    }

    /// <summary>
    /// The counter-reset seam restores a known initial turn-numbering state between tests (AI-011 TR-12): the same
    /// character's next recorded cycle becomes turn one again.
    /// </summary>
    [Fact]
    public void ResetTurnCountersForTesting_RestoresKnownInitialNumbering()
    {
        CreateRecorder("luna").Record(CreateCycle(cycleIndex: 5));

        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();

        CreateRecorder("luna").Record(CreateCycle(cycleIndex: 6));

        string characterDirectory = Assert.Single(Directory.GetDirectories(Assert.Single(Directory.GetDirectories(_rootDirectory))));
        Assert.Equal(["turn-0001.md"], Directory.GetFiles(characterDirectory, "turn-*.md").Select(Path.GetFileName));
        Assert.Contains("- **Request Cycle:** 6", File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md")));
    }

    /// <summary>
    /// One lazily created sortable run directory serves every character of the process; numbering is flat per
    /// character with no per-session subfolders (AI-011 TR-2/TR-3).
    /// </summary>
    [Fact]
    public void Record_UsesOneSortableRunDirectoryPerProcessForEveryCharacter()
    {
        CreateRecorder("luna").Record(CreateCycle());
        CreateRecorder("moss").Record(CreateCycle());
        CreateRecorder("luna").Record(CreateCycle());

        string runDirectory = Assert.Single(Directory.GetDirectories(_rootDirectory));
        Assert.Matches(SortableRunTimestamp(), Path.GetFileName(runDirectory));
        Assert.Equal(
            ["luna", "moss"],
            Directory.GetDirectories(runDirectory).Select(Path.GetFileName).OrderBy(static name => name));
        Assert.Equal(
            ["turn-0001.md", "turn-0002.md"],
            Directory.GetFiles(Path.Combine(runDirectory, "luna"), "turn-*.md")
                .Select(Path.GetFileName)
                .OrderBy(static name => name));
    }

    /// <summary>The file name zero-pads the turn number to at least four digits (AI-011 TR-2).</summary>
    [Fact]
    public void CreateTurnFileName_ZeroPadsToAtLeastFourDigits()
    {
        Assert.Equal("turn-0001.md", MindSessionTranscriptRecorder.CreateTurnFileName(1));
        Assert.Equal("turn-0999.md", MindSessionTranscriptRecorder.CreateTurnFileName(999));
        Assert.Equal("turn-1000.md", MindSessionTranscriptRecorder.CreateTurnFileName(1000));
        Assert.Equal("turn-12345.md", MindSessionTranscriptRecorder.CreateTurnFileName(12345));
    }

    /// <summary>
    /// A failing write never propagates into the session loop; it surfaces as one contained warning (AI-011 TR-7).
    /// </summary>
    [Fact]
    public void Record_WhenWriteFails_ContainsTheFailureAsAWarningWithoutThrowing()
    {
        string blockingFile = Path.Combine(_rootDirectory, "blocked");
        _ = Directory.CreateDirectory(_rootDirectory);
        File.WriteAllText(blockingFile, "not a directory");
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(blockingFile);

        MindSessionTranscriptRecorder recorder = CreateRecorder("luna");

        // The failed write never propagates into the session loop; it surfaces as a warning only (AI-011 TR-7).
        recorder.Record(CreateCycle());

        (LogLevel level, string message) = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("luna", message, StringComparison.Ordinal);
    }

    /// <summary>The written file is self-contained: metadata header, instructions, transcript, response, and tools.</summary>
    [Fact]
    public void Record_WritesASelfContainedTranscriptDocument()
    {
        MindSessionCycleTranscript cycle = CreateCycle();
        cycle.Response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call-1", "speak", new Dictionary<string, object?> { ["speech"] = "Hello" })]));
        cycle.ToolInvocations.Add(new MindTranscriptToolInvocation(
            "speak",
            "call-1",
            new Dictionary<string, object?> { ["speech"] = "Hello" })
        {
            Status = MindTranscriptToolInvocationStatus.Completed,
            Result = "Spoken.",
        });

        CreateRecorder("luna").Record(cycle);

        string characterDirectory = Assert.Single(Directory.GetDirectories(Assert.Single(Directory.GetDirectories(_rootDirectory))));
        string contents = File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md"));
        Assert.Contains("- **Character:** luna", contents);
        Assert.Contains("- **Request Cycle:** 4", contents);
        Assert.Contains("## Instructions", contents);
        Assert.Contains("## Request Transcript", contents);
        Assert.Contains("## Response", contents);
        Assert.Contains("## Tool Invocations", contents);
    }

    /// <summary>
    /// The character folder carries a chronological <c>index.md</c> (AI-011 UR-6, TR-15–TR-17): every recorded
    /// cycle appears in ascending turn order under a heading linked to its turn page by a relative link — including
    /// cycles without tool invocations — and each cycle's invocation blocks are the same rendering as the turn
    /// page's, with every invocation event likewise linked to its turn page.
    /// </summary>
    [Fact]
    public void Record_MaintainsAChronologicalLinkedIndexWithInvocationParity()
    {
        MindSessionCycleTranscript withInvocations = CreateCycle();
        withInvocations.ToolInvocations.Add(new MindTranscriptToolInvocation(
            "speak",
            "call-1",
            new Dictionary<string, object?> { ["speech"] = "Hello" })
        {
            Status = MindTranscriptToolInvocationStatus.Completed,
            Result = "Spoken.",
        });
        withInvocations.ToolInvocations.Add(new MindTranscriptToolInvocation(
            "wait",
            "call-2",
            new Dictionary<string, object?>())
        {
            Status = MindTranscriptToolInvocationStatus.SkippedByInvalidation,
            Result = AgentSessionRunner.CancelledActionResult,
        });

        CreateRecorder("luna").Record(withInvocations);
        CreateRecorder("luna").Record(CreateCycle());
        // Separate characters keep separate folders and separate indexes.
        CreateRecorder("moss").Record(CreateCycle());

        string lunaDirectory = GetCharacterDirectory("luna");
        string index = File.ReadAllText(Path.Combine(lunaDirectory, "index.md"));

        Assert.StartsWith("# Mind Session Transcript Index — luna", index);
        Assert.Contains("- **Character:** luna", index);
        // Ascending turn order with relative turn-page links; the no-tool cycle stays listed.
        Assert.Contains("## [Turn 0001](turn-0001.md) — Accepted", index);
        Assert.Contains("## [Turn 0002](turn-0002.md) — Accepted", index);
        Assert.Contains("_(no tool invocations)_", index);
        Assert.True(
            index.IndexOf("](turn-0001.md)", StringComparison.Ordinal)
                < index.IndexOf("](turn-0002.md)", StringComparison.Ordinal));
        // Invocation events link to their own turn page and keep the turn page's block rendering (TR-16/TR-17).
        Assert.Contains("### [speak — call `call-1`](turn-0001.md)", index);
        Assert.Contains("### [wait — call `call-2` (skipped — batch invalidated)](turn-0001.md)", index);
        // Byte parity with the turn page's first invocation block: from its arguments to before the next
        // invocation heading, the index renders the identical text.
        string turnPage = File.ReadAllText(Path.Combine(lunaDirectory, "turn-0001.md"));
        int argumentsStart = turnPage.IndexOf("**Arguments**", StringComparison.Ordinal);
        int nextHeading = turnPage.IndexOf("\n### ", argumentsStart, StringComparison.Ordinal);
        string invocationBlockBody = turnPage[argumentsStart..nextHeading].TrimEnd();
        Assert.Contains(invocationBlockBody, index);

        // The other character's index is an independent file listing only its own cycle.
        string mossIndex = File.ReadAllText(Path.Combine(GetCharacterDirectory("moss"), "index.md"));
        Assert.StartsWith("# Mind Session Transcript Index — moss", mossIndex);
        Assert.DoesNotContain("turn-0002.md", mossIndex);
    }

    /// <summary>
    /// Index state is process-wide like the turn counter (AI-011 TR-18): a re-created recorder for the same
    /// character appends after the earlier entries, and the counter-reset seam restores a known index state
    /// (TR-12/TR-19).
    /// </summary>
    [Fact]
    public void Record_IndexState_SurvivesMindReCreationAndCounterReset()
    {
        CreateRecorder("luna").Record(CreateCycle());
        CreateRecorder("luna").Record(CreateCycle());

        Assert.Contains("## [Turn 0002](turn-0002.md)", ReadIndex("luna"));

        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        CreateRecorder("luna").Record(CreateCycle());

        string resetIndex = ReadIndex("luna");
        Assert.Contains("## [Turn 0001](turn-0001.md)", resetIndex);
        Assert.DoesNotContain("turn-0002.md", resetIndex);
    }

    /// <summary>
    /// Concurrent index updates of one character are serialised: no entry is lost and the index stays in ascending
    /// per-character turn order (AI-011 TR-19).
    /// </summary>
    [Fact]
    public async Task Record_ConcurrentIndexUpdates_AreSerialisedWithoutLossOrReordering()
    {
        MindSessionTranscriptRecorder recorder = CreateRecorder("luna");
        var first = Task.Run(() => RecordCycles(recorder, 5));
        var second = Task.Run(() => RecordCycles(recorder, 5));
        await Task.WhenAll(first, second);

        string characterDirectory = GetCharacterDirectory("luna");
        Assert.Equal(10, Directory.GetFiles(characterDirectory, "turn-*.md").Length);
        string index = File.ReadAllText(Path.Combine(characterDirectory, "index.md"));
        Assert.Equal(
            Enumerable.Range(1, 10),
            IndexTurnHeadings().Matches(index).Select(static match => int.Parse(match.Groups[1].Value)));
    }

    /// <summary>
    /// A failing index update is contained (AI-011 TR-19): the already-written turn page stays available, the
    /// previously written index survives the failed replacement, the failure warns without throwing, and the entry
    /// retained in index state reaches the index once writes succeed again.
    /// </summary>
    [Fact]
    public void Record_WhenIndexWriteFails_PreservesTheTurnPageAndThePreviousIndex()
    {
        MindSessionTranscriptRecorder recorder = CreateRecorder("luna");
        recorder.Record(CreateCycle());
        string characterDirectory = GetCharacterDirectory("luna");
        string indexAfterFirstCycle = File.ReadAllText(Path.Combine(characterDirectory, "index.md"));
        Assert.Contains("## [Turn 0001](turn-0001.md)", indexAfterFirstCycle);

        // A directory at the replacement-safety temporary path makes every later index write fail while turn pages
        // keep writing.
        string blockingTemporary = Path.Combine(characterDirectory, "index.md.tmp");
        _ = Directory.CreateDirectory(blockingTemporary);
        recorder.Record(CreateCycle());

        Assert.True(File.Exists(Path.Combine(characterDirectory, "turn-0002.md")));
        Assert.Equal(indexAfterFirstCycle, File.ReadAllText(Path.Combine(characterDirectory, "index.md")));
        Assert.Contains(
            _logger.Entries,
            static entry => entry.Message.Contains(
                "Failed to update the mind session transcript index",
                StringComparison.Ordinal));

        // With the obstruction removed, the retained entries — including the failed update's — render completely.
        Directory.Delete(blockingTemporary);
        recorder.Record(CreateCycle());
        string recoveredIndex = ReadIndex("luna");
        Assert.Contains("## [Turn 0001](turn-0001.md)", recoveredIndex);
        Assert.Contains("## [Turn 0002](turn-0002.md)", recoveredIndex);
        Assert.Contains("## [Turn 0003](turn-0003.md)", recoveredIndex);
    }

    /// <summary>
    /// Recording stays contained even when the failure's own warning emission throws: the failing root resolver and
    /// the throwing logger never propagate out of <see cref="MindSessionTranscriptRecorder.Record" /> (AI-011 TR-7).
    /// </summary>
    [Fact]
    public void Record_WhenWarningEmissionAlsoFails_StaysContained()
    {
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
        MindSessionTranscriptRecorder recorder = new(
            "luna",
            () => throw new IOException("The root path could not be resolved."),
            new ThrowingLogger());

        recorder.Record(CreateCycle());

        Assert.True(true, "Recording a cycle whose root resolution and warning emission both fail must not throw.");
    }

    /// <summary>
    /// Both recorder test seams are internal-only: neither a public API member nor a game-configuration option can
    /// redirect the transcript root or reset the turn counters (AI-011 TR-12).
    /// </summary>
    [Fact]
    public void TestSeams_AreInternalOnlyAndAbsentFromPublicConfigurationSurface()
    {
        MethodInfo? rootOverride = typeof(MindSessionTranscriptRecorder).GetMethod(
            "SetRootPathOverrideForTesting",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? counterReset = typeof(MindSessionTranscriptRecorder).GetMethod(
            "ResetTurnCountersForTesting",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(rootOverride);
        Assert.NotNull(counterReset);
        Assert.False(rootOverride!.IsPublic, "The root-path override seam must not be public API.");
        Assert.True(rootOverride.IsAssembly, "The root-path override seam must be internal.");
        Assert.False(counterReset!.IsPublic, "The counter-reset seam must not be public API.");
        Assert.True(counterReset.IsAssembly, "The counter-reset seam must be internal.");

        // The game-configuration surface exposes only the three diagnostics toggles — no switch redirects the
        // recorder root or resets counters.
        Assert.Equal(
            ["EnableReasoningLogging", "EnableRequestResponseLogging", "EnableSessionTranscriptLogging"],
            typeof(AIDiagnosticsOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(static property => property.Name)
                .Order());
    }

    /// <summary>
    /// Restoring the root override to production resolution deactivates every live recorder, so a late
    /// continuation from an abandoned session performs no write — not even root resolution (AI-011 TR-12).
    /// </summary>
    [Fact]
    public void SetRootPathOverrideForTesting_WhenRestoredToNull_DeactivatesLiveRecorders()
    {
        MindSessionTranscriptRecorder recorder = new(
            "luna",
            () => throw new InvalidOperationException("A deactivated recorder must never resolve a root."),
            _logger);

        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);

        recorder.Record(CreateCycle());

        Assert.True(true, "A deactivated recorder must record as a contained no-op without touching the filesystem.");
        Assert.DoesNotContain(_logger.Entries, static entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Resetting the turn counters deactivates every live recorder of a previous scope, keeping stray
    /// continuations from previous tests silent (AI-011 TR-12).
    /// </summary>
    [Fact]
    public void ResetTurnCountersForTesting_DeactivatesLiveRecorders()
    {
        MindSessionTranscriptRecorder recorder = new(
            "luna",
            () => throw new InvalidOperationException("A deactivated recorder must never resolve a root."),
            _logger);

        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();

        recorder.Record(CreateCycle());

        Assert.True(true, "A deactivated recorder must record as a contained no-op without touching the filesystem.");
    }

    /// <summary>Logger whose every emission throws, proving recorder-level containment (AI-011 TR-7).</summary>
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => throw new InvalidOperationException();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("The logger failed.");
    }

    private MindSessionTranscriptRecorder CreateRecorder(string characterId)
        => new(characterId, () => throw new InvalidOperationException(
            "The root-path override must keep unit-test writes away from user:// resolution."),
            _logger);

    private string GetCharacterDirectory(string characterId)
        => Assert.Single(Directory.GetDirectories(Assert.Single(Directory.GetDirectories(_rootDirectory)), characterId));

    private string ReadIndex(string characterId)
        => File.ReadAllText(Path.Combine(GetCharacterDirectory(characterId), "index.md"));

    private static void RecordCycles(MindSessionTranscriptRecorder recorder, int count)
    {
        for (int index = 0; index < count; index++)
        {
            recorder.Record(CreateCycle());
        }
    }

    private static MindSessionCycleTranscript CreateCycle(int cycleIndex = 4)
        => new()
        {
            CycleIndex = cycleIndex,
            StartedAt = DateTimeOffset.Now,
            Instructions = "<Identity>\nYou are Luna.\n</Identity>\n",
            RequestMessages = [new ChatMessage(ChatRole.User, "Begin.")],
            RequestOptions = new MindTranscriptRequestOptions(null, "RequiredChatToolMode", false, ["speak"]),
        };

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
