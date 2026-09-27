namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Failure thrown by scenario code or by the harness's bound enforcement, carrying an explicit
/// classification. Scenario code uses it to reject response shapes that are invalid for that scenario —
/// for example a text-only fixture rejecting any function call.
/// </summary>
public sealed class LiveLLMScenarioException(
    LiveLLMFailureClassification classification,
    string message) : Exception(message)
{
    /// <summary>Classification this failure records in the execution trace.</summary>
    public LiveLLMFailureClassification Classification { get; } = classification;
}
