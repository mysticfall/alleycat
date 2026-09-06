using AlleyCat.Core.Threading;
using AlleyCat.Mind.AI.Tool;
using Microsoft.Extensions.AI;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI.Watch;

/// <summary>Authorable typed tool that arms one kind of watch condition.</summary>
public abstract partial class WatchConditionTool : AgentTool
{
    /// <summary>Stable authored condition identity exposed in watch status and outcomes.</summary>
    public abstract string ConditionId
    {
        get;
    }

    /// <summary>Creates the watch function with its registry bound only at AgenticMind session composition.</summary>
    internal AIFunction CreateWatchFunction(
        ScenarioContext context,
        MindBase mind,
        IMainThreadDispatcher dispatcher,
        WatchRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Delegate method = CreateWatchDelegate(registry);
        string? name = string.IsNullOrWhiteSpace(ToolName) ? null : ToolName.Trim();
        string? description = string.IsNullOrWhiteSpace(ToolDescription) ? null : ToolDescription.Trim();
        return CreateFunction(method, context, mind, dispatcher, name, description);
    }

    /// <summary>Creates the session-bound delegate without putting watch dependencies in the common tool session.</summary>
    protected abstract Delegate CreateWatchDelegate(WatchRegistry registry);

    /// <inheritdoc />
    protected sealed override Delegate CreateDelegate()
        => throw new InvalidOperationException("Watch tools must be bound by AgenticMind through a WatchRegistry.");
}
