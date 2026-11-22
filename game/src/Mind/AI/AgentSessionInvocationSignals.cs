using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Typed boundary signals one tool invocation raises for the session runner's default parking (§12, AI-002 TR-14):
/// a behaviour tool marks <see cref="ActionCommitted" /> exactly at its accepted playback hand-off, and the explicit
/// wait marks <see cref="SessionParkEntered" /> exactly when it enters the decision-wait gate. The runner decides
/// parking from these signals alone — never from a call's presence, its returned text, or how it ended — so a
/// skip, a validation rejection, an error, or a pre-hand-off cancellation is never mistaken for an action, and a
/// hand-off followed by interruption still counts.
/// </summary>
/// <remarks>
/// The runner attaches one instance per invocation through <see cref="AIFunctionArguments.Context" />; tools
/// receive it as a schema-excluded bound parameter, so the model-facing tool surface is unchanged. A tool invoked
/// outside a session receives a detached instance nobody observes.
/// </remarks>
internal sealed class AgentSessionInvocationSignals
{
    private int _actionCommitted;
    private int _sessionParkEntered;

    /// <summary>Whether the invocation's in-game action crossed its accepted playback hand-off.</summary>
    public bool ActionCommitted => Volatile.Read(ref _actionCommitted) != 0;

    /// <summary>Whether the invocation entered the decision-wait gate, pausing the session itself.</summary>
    public bool SessionParkEntered => Volatile.Read(ref _sessionParkEntered) != 0;

    /// <summary>Marks the accepted playback hand-off of the invocation's action.</summary>
    public void MarkActionCommitted() => _ = Interlocked.Exchange(ref _actionCommitted, 1);

    /// <summary>Marks the invocation's entry into the decision-wait gate.</summary>
    public void MarkSessionParkEntered() => _ = Interlocked.Exchange(ref _sessionParkEntered, 1);

    /// <summary>Attaches a fresh signal instance to one invocation's arguments and returns it.</summary>
    internal static AgentSessionInvocationSignals AttachTo(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        AgentSessionInvocationSignals signals = new();
        arguments.Context ??= new Dictionary<object, object?>();
        arguments.Context[typeof(AgentSessionInvocationSignals)] = signals;
        return signals;
    }

    /// <summary>Resolves the invocation's attached signals, or a detached instance when none is attached.</summary>
    internal static AgentSessionInvocationSignals From(AIFunctionArguments? arguments)
        => arguments?.Context is { } context
            && context.TryGetValue(typeof(AgentSessionInvocationSignals), out object? value)
            && value is AgentSessionInvocationSignals signals
                ? signals
                : new AgentSessionInvocationSignals();
}
