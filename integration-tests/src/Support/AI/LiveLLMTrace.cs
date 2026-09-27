using System.Text.Json.Nodes;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>Outcome of a scenario execution as recorded in its trace.</summary>
public enum LiveLLMTraceStatus
{
    /// <summary>The scenario delegate returned without failure.</summary>
    Completed,

    /// <summary>Execution failed; entries captured before the failure are preserved as a partial trace.</summary>
    Failed,
}

/// <summary>
/// Versioned, sanitised, immutable evidence trace of one scenario execution.
/// </summary>
/// <remarks>
/// <para>
/// The trace records every exchange (request messages, tool schemas and settings, response contents
/// including tool-call ids and arguments), every supplied continuation and simulated tool result, their
/// ordering, a purpose label per entry, timing, and provider-reported usage when available. All captured
/// text is sanitised: credentials, configuration or client objects, and raw provider exceptions are never
/// serialised into traces.
/// </para>
/// <para>
/// A failed or interrupted execution still yields its partial trace: entries captured before the failure
/// remain in <see cref="Entries" /> and the failure itself is recorded as a sanitised classification in
/// <see cref="Failure" />.
/// </para>
/// </remarks>
public sealed record LiveLLMTrace(
    string ScenarioName,
    LiveLLMTraceStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    LiveLLMFailureRecord? Failure,
    IReadOnlyList<LiveLLMTraceEntry> Entries)
{
    /// <summary>Current trace format version; bump whenever the serialised shape changes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Serialises the trace to indented JSON tagged with <see cref="CurrentVersion" />.</summary>
    /// <returns>The sanitised JSON document for this trace.</returns>
    public string ToJsonString()
    {
        JsonObject document = LiveLLMTraceSerialisation.Serialise(this);
        return document.ToJsonString(LiveLLMTraceCapture.JsonOptions);
    }
}
