using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Per-function session phase policy registered at the composition boundary when session tools are composed and
/// consulted by the session runner when a tool phase begins. The runner understands only this
/// generic descriptor — never a concrete production tool, function name, or tool type: whether a function executes
/// under admission arbitration is decided entirely by the composition-time policy bound to it.
/// </summary>
/// <param name="AdmissionArbitrated">
/// Whether the function's invocation executes under admission arbitration: its phase registers as
/// pending-admission before invocation, the submission's voice-pipeline admission transaction commits through the
/// runner's admission arbitration, and a matching attended cue linearising after admission protects the phase
/// instead of cancelling it, keeping admitted speech committed at playback hand-off (AI-002 TR-14).
/// </param>
internal sealed record AgentSessionPhasePolicy(bool AdmissionArbitrated)
{
    /// <summary>
    /// Policy for admission-arbitrated invocations — today the composition boundary binds this to the speech tool's
    /// submission.
    /// </summary>
    public static AgentSessionPhasePolicy AdmissionArbitration { get; } = new(AdmissionArbitrated: true);

    /// <summary>Binds this policy to a composed function so the session runner consults it at phase start.</summary>
    public AIFunction Bind(AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new PhasePolicyBoundFunction(function, this);
    }
}

/// <summary>
/// Base of the game's composition-bound function wrappers: exposes the wrapped composed function so the session
/// runner can inspect composition-registered policies — phase policy, exchange disposal — through arbitrary wrapper
/// layers without coupling to any concrete production tool.
/// </summary>
internal abstract class ComposedAIFunction(AIFunction function) : DelegatingAIFunction(function)
{
    /// <summary>The wrapped composed function.</summary>
    internal AIFunction ComposedFunction => InnerFunction;
}

/// <summary>
/// Composed function carrying its composition-registered session phase policy: the session runner
/// pattern-matches this carrier — and nothing about the wrapped function's identity — when a tool phase begins.
/// </summary>
internal sealed class PhasePolicyBoundFunction(AIFunction function, AgentSessionPhasePolicy policy)
    : ComposedAIFunction(function)
{
    public AgentSessionPhasePolicy Policy { get; } = policy;
}
