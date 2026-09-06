using AlleyCat.Mind.AI.Tool;

namespace AlleyCat.Mind.AI.Watch;

/// <summary>Common session tool that explicitly removes an armed watch.</summary>
internal sealed partial class UnwatchTool : AgentTool
{
    private readonly WatchRegistry? _registry;

    public UnwatchTool(WatchRegistry? registry)
    {
        _registry = registry;
        ToolName = "unwatch";
        ToolDescription = "Remove one active watch by its opaque watch ID.";
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => (string watch_id) => UnwatchAsync(watch_id);

    private Task<AgentToolResult> UnwatchAsync(string watchId)
        => Task.FromResult(_registry is null
            ? new AgentToolResult($"No active watch with ID '{watchId}' exists; nothing was removed.")
            : _registry.Unwatch(watchId));
}
