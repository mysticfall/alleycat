namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>Outcome status of one scenario execution.</summary>
public enum LiveLLMScenarioExecutionStatus
{
    /// <summary>The scenario completed without failure.</summary>
    Completed,

    /// <summary>The scenario failed; its partial trace is preserved.</summary>
    Failed,
}

/// <summary>
/// Result of one scenario execution: its status, optional failure record, and its complete — possibly
/// partial — evidence trace.
/// </summary>
public sealed record LiveLLMScenarioExecution(
    string ScenarioName,
    LiveLLMScenarioExecutionStatus Status,
    LiveLLMFailureRecord? Failure,
    LiveLLMTrace Trace)
{
    /// <summary>Whether the scenario completed without failure.</summary>
    public bool Succeeded => Status == LiveLLMScenarioExecutionStatus.Completed;
}
