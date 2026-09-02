namespace AlleyCat.Mind.AI;

/// <summary>Stable identity for one mutable, model-facing input stream.</summary>
internal readonly record struct AgentSessionInjectionKey(string Source, string Turn);

/// <summary>Identifies the continuation revision for which the runner is waiting.</summary>
internal readonly record struct AgentSessionContinuationKey(AgentSessionInjectionKey Key, long Revision);

/// <summary>Determines whether an injection updates the mutable turn or explicitly reconciles accepted history.</summary>
internal enum AgentSessionInjectionKind
{
    CurrentUserTurn,
    Reconciliation,
}

/// <summary>Generic rendered input that may replace a mutable turn or reconcile an accepted one.</summary>
internal sealed record AgentSessionInjection(
    AgentSessionInjectionKey Key,
    long Revision,
    string RenderedText,
    AgentSessionInjectionKind Kind = AgentSessionInjectionKind.CurrentUserTurn);

/// <summary>Opaque, single-use lease for a continuation whose rendered input is not available yet.</summary>
internal sealed class FreshInjectionExpectation(long id, AgentSessionContinuationKey continuation)
{
    internal long Id => id;

    internal AgentSessionContinuationKey Continuation => continuation;
}
