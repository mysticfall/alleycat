using System.Diagnostics;
using System.Globalization;
using AlleyCat.Core;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Filesystem recorder writing one self-contained Markdown transcript file per provider request cycle under
/// <c>user://logs/mind/&lt;game-run-start-timestamp&gt;/&lt;character-id&gt;/turn-NNNN.md</c> (AI-011 TR-2–TR-5), and
/// maintaining each character folder's chronological <c>index.md</c> alongside those turn pages (AI-011
/// TR-15–TR-19).
/// </summary>
/// <remarks>
/// <para>
/// The run timestamp is the sortable process-start wall-clock time latched once per process, and the run directory
/// is created lazily on the recorder's first recorded write (TR-2/TR-3). Turn numbering is a per-character,
/// process-wide monotonic counter that starts at one and survives mind node re-creation within the same game run
/// (TR-4). Writes are sequential: the runner reports each cycle once its outcome has resolved.
/// </para>
/// <para>
/// The index derives its entries from the live cycle objects at record time — never by reparsing rendered turn
/// Markdown — and its per-character entry state is process-wide like the turn counter, so entries survive mind
/// re-creation (TR-18). Index updates are serialised, rendered, and written replacement-safe under the state lock:
/// entries are neither lost nor reordered, a turn page already written stays available when its index update fails,
/// and a failed update leaves the previously written index intact (TR-19).
/// </para>
/// <para>
/// Recording is fully contained (TR-7): every failure is logged as a warning and never propagates into the session
/// loop. The <c>user://</c> root is resolved through an injected resolver so the recorder's logic itself stays free
/// of Godot types; only the injected default touches <c>ProjectSettings</c>.
/// </para>
/// </remarks>
internal sealed class MindSessionTranscriptRecorder : IAgentSessionTranscriptSink
{
    /// <summary>The canonical transcript root under the Godot user-data tree (AI-011 TR-2).</summary>
    internal const string UserRootPath = "user://logs/mind";

    private const string TurnFileNamePrefix = "turn-";

    private const string TurnFileNameExtension = ".md";

    private const string IndexFileName = "index.md";

    private const string IndexTemporarySuffix = ".tmp";

    private const int TurnNumberMinimumDigits = 4;

    private const string RunTimestampFormat = "yyyy-MM-dd-HH-mm-ss";

    private static readonly Lock _stateLock = new();
    private static readonly Dictionary<string, int> _nextTurnNumbers = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<MindTranscriptIndexEntry>> _indexEntries = new(
        StringComparer.Ordinal);
    private static readonly HashSet<MindSessionTranscriptRecorder> _activeRecorders = [];
    private static string? _runTimestamp;
    private static string? _rootPathOverride;

    private readonly string _characterId;
    private readonly Func<string> _rootPathResolver;
    private readonly ILogger _logger;
    private volatile bool _deactivated;
    private bool _runDirectoryEnsured;

    public MindSessionTranscriptRecorder(string characterId, Func<string> rootPathResolver, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentNullException.ThrowIfNull(rootPathResolver);
        ArgumentNullException.ThrowIfNull(logger);
        IdentityValidator.ValidateId(characterId, nameof(characterId));

        _characterId = characterId;
        _rootPathResolver = rootPathResolver;
        _logger = logger;
        lock (_stateLock)
        {
            _ = _activeRecorders.Add(this);
        }
    }

    /// <inheritdoc />
    public void Record(MindSessionCycleTranscript cycle)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(cycle);

            // A deactivated recorder is a contained no-op: a late continuation from an abandoned session — one
            // whose test seams were already restored — must never write, to the redirected root or the production
            // root alike (AI-011 TR-7/TR-12).
            if (_deactivated)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Skipped a transcript record of character '{CharacterId}' because its recorder was deactivated.",
                        _characterId);
                }

                return;
            }

            string rootPath = _rootPathOverride ?? _rootPathResolver();
            string directory = Path.Combine(rootPath, EnsureRunTimestamp(), _characterId);
            EnsureRunDirectory(directory);
            int turnNumber = TakeNextTurnNumber(_characterId);
            string contents = MindSessionTranscriptFormatter.Render(cycle, turnNumber, _characterId);
            File.WriteAllText(Path.Combine(directory, CreateTurnFileName(turnNumber)), contents);

            // The index update follows the successful turn write (AI-011 TR-19): its failure is contained
            // separately, so the turn page stays available and the previously written index survives.
            try
            {
                UpdateIndex(directory, turnNumber, cycle);
            }
            catch (Exception exception)
            {
                TryLogContainedFailure(exception, "Failed to update the mind session transcript index");
            }
        }
        catch (Exception exception)
        {
            // A recording failure never disturbs the running session (AI-011 TR-7): it surfaces as a warning only,
            // and even a throwing warning logger stays contained.
            TryLogContainedFailure(exception, "Failed to record the mind session transcript");
        }
    }

    /// <summary>
    /// Appends one settled cycle's entry to the character's process-wide index state and rewrites the character
    /// folder's <c>index.md</c> (AI-011 TR-15–TR-19). State mutation, rendering, and the replacement-safe write all
    /// happen under the state lock, so concurrent updates of one character are serialised and entries are neither
    /// lost nor reordered.
    /// </summary>
    private void UpdateIndex(string characterDirectory, int turnNumber, MindSessionCycleTranscript cycle)
    {
        lock (_stateLock)
        {
            if (!_indexEntries.TryGetValue(_characterId, out List<MindTranscriptIndexEntry>? entries))
            {
                entries = [];
                _indexEntries[_characterId] = entries;
            }

            entries.Add(new MindTranscriptIndexEntry(
                turnNumber,
                cycle.Outcome,
                [.. cycle.ToolInvocations]));
            // Turn numbers are monotonic per character, so ordering by turn number keeps the index chronological
            // regardless of the interleaving of concurrent records. Rendering and writing stay inside the lock so
            // an older render can never overwrite a newer index.
            string indexContents = MindSessionTranscriptFormatter.RenderIndex(
                _characterId,
                [.. entries.OrderBy(static entry => entry.TurnNumber)]);
            WriteReplacementSafe(Path.Combine(characterDirectory, IndexFileName), indexContents);
        }
    }

    /// <summary>
    /// Writes one file replacement-safely (AI-011 TR-19): the content is written to a sibling temporary file first
    /// and moved over the target only once complete, so a failed update cannot destroy the previously written file.
    /// </summary>
    private static void WriteReplacementSafe(string path, string contents)
    {
        string temporaryPath = path + IndexTemporarySuffix;
        try
        {
            File.WriteAllText(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception)
            {
                // Cleaning up the abandoned temporary file is best-effort; the containment wrapper reports the
                // original failure (AI-011 TR-7).
            }

            throw;
        }
    }

    private void TryLogContainedFailure(Exception exception, string messagePrefix)
    {
        try
        {
            _logger.LogWarning(
                exception,
                $"{messagePrefix} of character '{{CharacterId}}'.",
                _characterId);
        }
        catch (Exception)
        {
            // Logging infrastructure failure must not propagate either (AI-011 TR-7).
        }
    }

    /// <summary>Builds the <c>turn-NNNN.md</c> file name, zero-padding to at least four digits (AI-011 TR-2).</summary>
    internal static string CreateTurnFileName(int turnNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(turnNumber, 1);
        return $"{TurnFileNamePrefix}{turnNumber.ToString(CultureInfo.InvariantCulture).PadLeft(TurnNumberMinimumDigits, '0')}"
            + TurnFileNameExtension;
    }

    /// <summary>
    /// Internal test seam (AI-011 TR-12): redirects transcript writes away from the real <c>user://</c> tree. Pass
    /// null to restore production resolution, which also deactivates every live recorder so a late continuation
    /// from an abandoned session can never reach the production root. Not public API and not game configuration.
    /// </summary>
    internal static void SetRootPathOverrideForTesting(string? rootPath)
    {
        lock (_stateLock)
        {
            _rootPathOverride = rootPath;
            if (rootPath is null)
            {
                DeactivateAllRecordersLocked();
            }
        }
    }

    /// <summary>
    /// Internal test seam (AI-011 TR-12/TR-19): clears every per-character turn counter and index state so tests
    /// start from a known turn-numbering state, and deactivates every live recorder of a previous scope. Not public
    /// API and not game configuration.
    /// </summary>
    internal static void ResetTurnCountersForTesting()
    {
        lock (_stateLock)
        {
            _nextTurnNumbers.Clear();
            _indexEntries.Clear();
            DeactivateAllRecordersLocked();
        }
    }

    /// <summary>
    /// Deactivates every live recorder: their pending writes become contained no-ops, so recording performed after
    /// test cleanup restored the seams can never touch any root. Must be called while holding
    /// <see cref="_stateLock" />.
    /// </summary>
    private static void DeactivateAllRecordersLocked()
    {
        foreach (MindSessionTranscriptRecorder recorder in _activeRecorders)
        {
            recorder._deactivated = true;
        }

        _activeRecorders.Clear();
    }

    /// <summary>
    /// Latches the sortable process-start run timestamp exactly once per process (AI-011 TR-2/TR-3); the run
    /// directory itself is still created lazily on the first recorded write.
    /// </summary>
    private static string EnsureRunTimestamp()
    {
        lock (_stateLock)
        {
            if (_runTimestamp is null)
            {
                DateTime processStart;
                try
                {
                    processStart = Process.GetCurrentProcess().StartTime;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Process-start time is unavailable on some platforms; the first recorded write's wall-clock
                    // time still yields one stable sortable run directory for this process.
                    processStart = DateTime.Now;
                }

                _runTimestamp = processStart.ToString(RunTimestampFormat, CultureInfo.InvariantCulture);
            }

            return _runTimestamp;
        }
    }

    private static int TakeNextTurnNumber(string characterId)
    {
        lock (_stateLock)
        {
            int next = _nextTurnNumbers.TryGetValue(characterId, out int stored) ? stored : 0;
            next = next < 1 ? 1 : next;
            _nextTurnNumbers[characterId] = next + 1;
            return next;
        }
    }

    private void EnsureRunDirectory(string directory)
    {
        if (_runDirectoryEnsured)
        {
            return;
        }

        _ = Directory.CreateDirectory(directory);
        _runDirectoryEnsured = true;
    }
}
