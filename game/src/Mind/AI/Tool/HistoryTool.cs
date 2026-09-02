using System.ComponentModel;
using System.Globalization;
using AlleyCat.Mind.AI.Prompting;
using Godot;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Timeline history tool that reads the owning Mind's committed observation timeline in order (AI-002 TR-36).
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
        ToolDescription = "Read your own memory of past events, in the order they happened, including minor events "
            + "that wait results do not surface. Reading changes nothing.";
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
        IReadOnlyList<AgentObservation> timeline = session.Mind.GetObservationTimelineSnapshot();
        IReadOnlyList<ContinuationProjection.Event> projected = ObservationHistoryRenderer.Project(timeline);
        IReadOnlyList<ContinuationProjection.Event> selected = count is > 0
            ? [.. projected.Skip(Math.Max(0, projected.Count - count.Value))]
            : projected;
        if (selected.Count == 0)
        {
            return new AgentToolResult("You remember no past events yet.");
        }

        string history = session.HistoryRenderer is { } renderer
            ? await renderer.RenderProjectedAsync(selected)
            : string.Join('\n', selected.Select(static @event => @event.Observation.TypeKey));
        return new AgentToolResult(
            $"{selected.Count.ToString(CultureInfo.InvariantCulture)} past event(s), oldest first:\n{history}");
    }
}
