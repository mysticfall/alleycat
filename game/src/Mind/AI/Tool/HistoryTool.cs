using System.ComponentModel;
using System.Globalization;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Observation;
using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Timeline history tool that reads the owning Mind's persistent event timeline in order without changing it
/// (AI-002 TR-13; AI-003 TR-10/11).
/// </summary>
[Tool]
[GlobalClass]
public partial class HistoryTool : AgentTool
{
    /// <summary>
    /// Creates a timeline history tool with the default model-facing metadata.
    /// </summary>
    public HistoryTool()
    {
        ToolName = "history";
        ToolDescription = "Recall your recorded memory of past events, in the order they happened, optionally "
            + "limited to the most recent events. Reading changes nothing. Only recorded events appear here — this "
            + "is not a way to recover perception that was never recorded.";
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => ReadHistory;

    private async ValueTask<AgentToolResult> ReadHistory(
        ScenarioContext context,
        [Description("Optional limit: return only the most recent N events. Omit to read the complete timeline.")]
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AgentToolSession session = Session!;
        IReadOnlyList<AcceptedObservationEntry> timeline = session.Mind.GetPersistentEventTimelineSnapshot();
        IReadOnlyList<ContinuationProjection.Event> projected = ObservationHistoryRenderer.Project(timeline);
        IReadOnlyList<ContinuationProjection.Event> selected = count is > 0
            ? [.. projected.Skip(Math.Max(0, projected.Count - count.Value))]
            : projected;
        if (selected.Count == 0)
        {
            return new AgentToolResult("You remember no past events yet.");
        }

        string history = await new ObservationHistoryRenderer(session.Context.Character).RenderProjectedAsync(selected);
        return new AgentToolResult(
            $"{selected.Count.ToString(CultureInfo.InvariantCulture)} past event(s), oldest first:\n{history}");
    }
}
