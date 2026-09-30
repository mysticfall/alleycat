using System.Diagnostics;
using System.Globalization;
using AlleyCat.Core;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Filesystem recorder writing one self-contained Markdown transcript file per provider request cycle under
/// <c>user://logs/mind/&lt;game-run-start-timestamp&gt;/&lt;character-id&gt;/turn-NNNN.md</c> (AI-011 TR-2–TR-5).
/// </summary>
/// <remarks>
/// <para>
/// The run timestamp is the sortable process-start wall-clock time latched once per process, and the run directory
/// is created lazily on the recorder's first recorded write (TR-2/TR-3). Turn numbering is a per-character,
/// process-wide monotonic counter that starts at one and survives mind node re-creation within the same game run
/// (TR-4). Writes are sequential: the runner reports each cycle once its outcome has resolved.
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

    private const int TurnNumberMinimumDigits = 4;

    private const string RunTimestampFormat = "yyyy-MM-dd-HH-mm-ss";

    private static readonly Lock _stateLock = new();
    private static readonly Dictionary<string, int> _nextTurnNumbers = new(StringComparer.Ordinal);
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
        }
        catch (Exception exception)
        {
            // A recording failure never disturbs the running session (AI-011 TR-7): it surfaces as a warning only,
            // and even a throwing warning logger stays contained.
            TryLogContainedFailure(exception);
        }
    }

    private void TryLogContainedFailure(Exception exception)
    {
        try
        {
            _logger.LogWarning(
                exception,
                "Failed to record the mind session transcript of character '{CharacterId}'.",
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
    /// Internal test seam (AI-011 TR-12): clears every per-character turn counter so tests start from a known
    /// turn-numbering state, and deactivates every live recorder of a previous scope. Not public API and not game
    /// configuration.
    /// </summary>
    internal static void ResetTurnCountersForTesting()
    {
        lock (_stateLock)
        {
            _nextTurnNumbers.Clear();
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
