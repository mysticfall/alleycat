using AlleyCat.Speech.Voice;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Session-scoped carrier between the runner constructed at session execution and the admission-arbitrated tool
/// bound earlier in session preparation (AI-002 TR-13/14). Exposes only the narrow admission-transaction
/// surface — never the runner itself — so the bound tool cannot reach any other session-runtime state.
/// </summary>
internal sealed class ToolAdmissionBroker
{
    private volatile AgentSessionRunner? _runner;

    /// <summary>Attaches the freshly constructed session runner; called once per session before execution.</summary>
    internal void AttachRunner(AgentSessionRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>Creates the admission transaction for one arbitrated submission, or null before the runner exists.</summary>
    internal SpeechAdmissionTransaction? TryCreateTransaction()
        => _runner is { } runner ? new SpeechAdmissionTransaction(runner.TryAdmitToolPhase) : null;
}
