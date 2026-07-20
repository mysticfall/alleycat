using System.Collections.ObjectModel;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Completed tool outcome containing transient model feedback and ordered durable observations.
/// </summary>
public sealed class AgentToolResult
{
    /// <summary>
    /// Creates a snapshot-owned result envelope.
    /// </summary>
    public AgentToolResult(string? message = null, IEnumerable<AgentObservation>? observations = null)
    {
        Message = message;
        AgentObservation[] snapshot = observations is null ? [] : [.. observations];
        if (Array.Exists(snapshot, static observation => observation is null))
        {
            throw new ArgumentException("Tool-result observations cannot contain null entries.", nameof(observations));
        }

        Observations = new ReadOnlyCollection<AgentObservation>(snapshot);
    }

    /// <summary>Optional feedback returned only to the active model tool loop.</summary>
    public string? Message
    {
        get;
    }

    /// <summary>Immutable ordered observation snapshot owned by this result.</summary>
    public IReadOnlyList<AgentObservation> Observations
    {
        get;
    }
}
