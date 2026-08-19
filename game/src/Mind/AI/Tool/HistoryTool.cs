using System.ComponentModel;
using System.Globalization;
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

    private ValueTask<AgentToolResult> ReadHistory(
        ScenarioContext context,
        [Description("Optional limit: return only the most recent N events. Omit to read the complete timeline.")]
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AgentToolSession session = Session!;
        IReadOnlyList<AgentObservation> timeline = session.Mind.GetObservationTimelineSnapshot();
        IEnumerable<AgentObservation> records = count is > 0
            ? timeline.Skip(Math.Max(0, timeline.Count - count.Value))
            : timeline;
        IReadOnlyList<AgentObservation> selected = [.. records];

        return ValueTask.FromResult(new AgentToolResult(ComposeResultMessage(session, selected)));
    }

    private static string ComposeResultMessage(AgentToolSession session, IReadOnlyList<AgentObservation> records)
    {
        if (records.Count == 0)
        {
            return "You remember no past events yet.";
        }

        string history = session.HistoryRenderer is { } renderer
            ? renderer.Render(records)
            : string.Join('\n', records.Select(static observation => observation.TypeKey));
        return $"{records.Count.ToString(CultureInfo.InvariantCulture)} past event(s), oldest first:\n{history}";
    }
}
