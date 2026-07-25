namespace AlleyCat.Mind.AI;

/// <summary>Author-selected policy which requests a contextual worker refresh.</summary>
public abstract partial class ContextWorkerTrigger : Godot.Node
{
    /// <summary>Occurs when this policy requests execution of its owning worker.</summary>
    public event Action? RunRequested;

    /// <summary>Requests a worker refresh without retaining or invoking the worker directly.</summary>
    protected void RequestRun() => RunRequested?.Invoke();

    /// <summary>Resolves the Mind which owns the trigger's direct parent.</summary>
    protected AgenticMind RequireOwningMind()
        => GetParent()?.GetParent() as AgenticMind
            ?? throw new InvalidOperationException(
                "ContextWorkerTrigger requires a direct ContextWorker parent beneath an AgenticMind.");
}
