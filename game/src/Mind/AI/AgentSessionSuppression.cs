using Godot;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Process-wide suppression of agent sessions, driven by the <c>--no-ai</c> command-line user argument.
/// </summary>
/// <remarks>
/// While suppressed, minds keep perception, attention, and gaze fully functional but never start an agent session,
/// guaranteeing zero outbound LLM requests. The switch is resolved lazily on first access so boot order never
/// couples to it, and the resolved value is memoised for the process lifetime because user arguments cannot change
/// after process start.
/// </remarks>
internal static class AgentSessionSuppression
{
    /// <summary>Command-line user argument that suppresses all agent sessions for the process.</summary>
    public const string NoAISwitch = "--no-ai";

    private static readonly Lock _stateLock = new();
    private static bool _stateResolved;
    private static bool _suppressed;
    private static bool _suppressionNoticeBegun;

    /// <summary>
    /// Gets whether agent sessions are suppressed for this process, resolving the switch from the command-line
    /// user arguments exactly once on first access.
    /// </summary>
    public static bool IsDisabled
    {
        get
        {
            lock (_stateLock)
            {
                if (!_stateResolved)
                {
                    _suppressed = ContainsSuppressionSwitch(OS.GetCmdlineUserArgs());
                    _stateResolved = true;
                }

                return _suppressed;
            }
        }
    }

    /// <summary>
    /// Determines whether the supplied command-line user arguments contain the suppression switch.
    /// </summary>
    /// <remarks>
    /// Pure and free of Godot APIs so switch matching stays unit-testable without a Godot runtime.
    /// </remarks>
    public static bool ContainsSuppressionSwitch(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(NoAISwitch, StringComparer.Ordinal);
    }

    /// <summary>
    /// Claims the process-wide, once-only suppression notice slot; every caller after the first receives
    /// <see langword="false" /> so the notice is emitted exactly once per process.
    /// </summary>
    public static bool TryBeginSuppressionNotice()
    {
        lock (_stateLock)
        {
            if (_suppressionNoticeBegun)
            {
                return false;
            }

            _suppressionNoticeBegun = true;
            return true;
        }
    }

    /// <summary>
    /// Forces the suppressed state without consulting user arguments and resets the once-per-process notice guard
    /// so tests can exercise suppression deterministically.
    /// </summary>
    internal static void DisableForTesting()
    {
        lock (_stateLock)
        {
            _suppressed = true;
            _stateResolved = true;
            _suppressionNoticeBegun = false;
        }
    }

    /// <summary>
    /// Clears the memoised state and the notice guard so the next access re-resolves from the process's real user
    /// arguments.
    /// </summary>
    internal static void ResetForTesting()
    {
        lock (_stateLock)
        {
            _suppressed = false;
            _stateResolved = false;
            _suppressionNoticeBegun = false;
        }
    }
}
