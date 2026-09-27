namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Declared execution bounds for one scenario, fixed before execution starts.
/// </summary>
/// <remarks>
/// The harness enforces every declared bound: a request, continuation request, or tool result beyond its
/// bound fails the execution with a <see cref="LiveLLMFailureClassification.BoundExceeded" />
/// classification, and the timeout budget fails it with <see cref="LiveLLMFailureClassification.Timeout" />.
/// </remarks>
public sealed record LiveLLMScenarioBounds(
    int MaxTargetRequests,
    TimeSpan Timeout,
    int MaxContinuationRequests = 0,
    int MaxToolResults = 0)
{
    /// <summary>Validates the declared bounds, throwing when any bound is impossible.</summary>
    public void Validate()
    {
        if (MaxTargetRequests < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTargetRequests),
                MaxTargetRequests,
                "A scenario must declare at least one permitted target request.");
        }

        if (MaxContinuationRequests < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxContinuationRequests),
                MaxContinuationRequests,
                "A scenario cannot declare a negative continuation request bound.");
        }

        if (MaxToolResults < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxToolResults),
                MaxToolResults,
                "A scenario cannot declare a negative tool result bound.");
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Timeout),
                Timeout,
                "A scenario must declare a positive timeout.");
        }
    }
}
