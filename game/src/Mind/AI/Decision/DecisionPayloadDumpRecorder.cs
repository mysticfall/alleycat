using System.Text.Json;
using AlleyCat.Core;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>
/// Writes one JSON file per decision-gate evaluation when <c>Diagnostics:AI:EnableDecisionPayloadLogging</c> is
/// enabled: the verbatim request material (evidence JSON and question instructions) plus the evaluation's metadata
/// and outcome, for diagnosing decision-model framing at production evidence scale. The recorder is deliberately
/// never constructed when the setting is disabled, so the disabled path performs zero I/O and zero composition.
/// </summary>
/// <remarks>
/// Recording is fully contained: a writing failure is logged at Warning and never propagates into the evaluation
/// it observes. Dumps follow the session-transcript log convention (AI-011 TR-2–TR-5), co-located beside each
/// character's transcripts: <c>user://logs/mind/&lt;game-run-start-timestamp&gt;/&lt;character-id&gt;/decision-payloads/</c>
/// with one subdirectory per producer-declared question category — <c>gaze</c> for the look-target policy,
/// <c>wait</c> for the two-question wait gate — named for sortability and self-description:
/// <c>yyyyMMdd-HHmmss-fff_{characterId}_{TriggerKind}_{verdict}.json</c>. The session directory resolves through
/// <see cref="MindSessionTranscriptRecorder.GetRunDirectory" /> so both log families share one latched session
/// directory per game run.
/// </remarks>
internal sealed class DecisionPayloadDumpRecorder
{
    /// <summary>The per-character directory decision-payload dumps live in, beside the transcript turn pages.</summary>
    internal const string DumpDirectoryName = "decision-payloads";

    private const string FileExtension = ".json";

    private static readonly Lock _stateLock = new();
    private static string? _rootPathOverride;

    private static readonly JsonSerializerOptions _serialiserOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Dump files are local diagnostic files, never HTML: non-ASCII (Korean-heavy evidence above all) writes
        // as raw UTF-8 for human readability, while quotes, backslash, and control characters stay escaped. The
        // wire request serialisation to the decision service is untouched — this applies to dump files only.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _category;
    private readonly string _characterId;
    private readonly string _characterDirectoryName;
    private readonly Func<string> _rootPathResolver;
    private readonly ILogger _logger;

    public DecisionPayloadDumpRecorder(
        string category,
        string characterId,
        string characterDirectoryName,
        Func<string> rootPathResolver,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterDirectoryName);
        ArgumentNullException.ThrowIfNull(rootPathResolver);
        ArgumentNullException.ThrowIfNull(logger);
        IdentityValidator.ValidateFullId(characterId, nameof(characterId));
        IdentityValidator.ValidateId(characterDirectoryName, nameof(characterDirectoryName));

        _category = category;
        _characterId = characterId;
        _characterDirectoryName = characterDirectoryName;
        _rootPathResolver = rootPathResolver;
        _logger = logger;
    }

    /// <summary>Writes one dump file; a failure is contained as a Warning and never propagates.</summary>
    public void Record(DecisionPayloadDump dump)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(dump);

            string characterDumpDirectory = Path.Combine(
                MindSessionTranscriptRecorder.GetRunDirectory(
                    Volatile.Read(ref _rootPathOverride) ?? _rootPathResolver()),
                _characterDirectoryName,
                DumpDirectoryName,
                SanitisePathComponent(_category));
            string fileName = $"{dump.Timestamp.LocalDateTime:yyyy-MM-dd-HH-mm-ss-fff}"
                + $"_{SanitisePathComponent(_characterId)}"
                + $"_{SanitisePathComponent(dump.TriggerKind)}"
                + $"_{SanitisePathComponent(dump.Verdict)}"
                + FileExtension;
            _ = Directory.CreateDirectory(characterDumpDirectory);
            File.WriteAllText(
                Path.Combine(characterDumpDirectory, fileName),
                JsonSerializer.Serialize(dump, _serialiserOptions));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Writing a decision-payload dump for character '{CharacterId}' failed; the evaluation it observed "
                    + "is unaffected.",
                _characterId);
        }
    }

    /// <summary>Replaces every path separator and invalid filename character with a safe dash.</summary>
    /// <remarks>Applied to the question category, character ID, trigger kind, and verdict filename components
    /// alike: free-form verdicts such as canonical full IDs carry colons that are invalid in filenames on some
    /// platforms.</remarks>
    private static string SanitisePathComponent(string value)
        => string.Join(
            "-",
            value.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':'], StringSplitOptions.RemoveEmptyEntries));


    /// <summary>
    /// Internal test seam mirroring the transcript recorder's root override: redirects every active recorder's
    /// output to one physical root for the duration of a test. Not public API and not game configuration.
    /// </summary>
    internal static void SetRootPathOverrideForTesting(string? rootPath)
    {
        lock (_stateLock)
        {
            _rootPathOverride = rootPath;
        }
    }
}
