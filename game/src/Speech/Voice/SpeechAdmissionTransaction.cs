namespace AlleyCat.Speech.Voice;

/// <summary>
/// Narrow, runner-owned arbitration token for exactly one speak submission (SPCH-005 TR-37; AI-002 TR-25/56).
/// </summary>
/// <remarks>
/// <para>
/// The voice pipeline invokes <see cref="TryAdmit"/> while holding its own submission lock; the arbitration
/// acquires the agent-runner state lock and — when no attended start or resume hold linearised first — runs the
/// caller's queue admission commit inside that same critical section, so AIVoice queue admission and the
/// runner's protected-admission state commit as one transaction under the normative lock order
/// <c>AIVoice._submissionLock → AgentSessionRunner._stateLock</c>. The commit must not throw and must not call
/// back into the voice pipeline.
/// </para>
/// <para>
/// This type is internal correlation state only: it never appears in public tool schemas, scenario context, or
/// any general service bag.
/// </para>
/// </remarks>
internal sealed class SpeechAdmissionTransaction(Func<Action, bool> arbitration)
{
    /// <summary>
    /// Attempts the admission transaction: commits the queue admission and returns true when the submission won,
    /// or returns false — committing nothing — when a matching attended start or resume hold linearised first.
    /// </summary>
    internal bool TryAdmit(Action commit) => arbitration(commit);
}
