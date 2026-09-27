namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Purpose label carried by every trace entry, identifying which experiment role produced it.
/// </summary>
public enum LiveLLMPurpose
{
    /// <summary>Exchange with the behaviour under test (the target model).</summary>
    Target,

    /// <summary>
    /// Continuation material for the scenario: an LLM-generated continuation request made under an isolated
    /// history, or a scripted continuation message supplied by the experiment.
    /// </summary>
    Continuation,

    /// <summary>
    /// Reserved for judge exchanges. Judge verdicts are evaluation results and are stored separately from
    /// evidence traces, so scenario execution never writes entries with this purpose.
    /// </summary>
    Judge,
}
