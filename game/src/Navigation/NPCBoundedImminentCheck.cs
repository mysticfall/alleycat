namespace AlleyCat.Navigation;

/// <summary>Fixed-size identity for one bounded same/opposite imminent check.</summary>
public readonly record struct NPCImminentCheckEvidence(
    NPCFarRouteEstimateKey EstimateKey,
    NPCReleaseOccurrence SameLaneOccurrence,
    NPCReleaseOccurrence OppositeLaneOccurrence)
{
    /// <summary>Gets whether both bounded authored occurrences and their estimate generation are valid.</summary>
    public bool IsValid => EstimateKey.RequestIdentity.RequestID > 0
        && SameLaneOccurrence.IsValid
        && OppositeLaneOccurrence.IsValid;
}

/// <summary>Behaviour-neutral counters for the latest far/imminent terminal-planning frame.</summary>
public readonly record struct NPCBoundedTerminalCheckDiagnostics(
    ulong PhysicsFrame,
    bool EstimateAvailable,
    bool IsInStoppingRegion,
    int ExactCandidateCheckCount,
    int CumulativeExactCandidateCheckCount,
    NPCImminentCheckEvidence Evidence);

/// <summary>
/// Prevents a rejected imminent pair from being checked repeatedly before a new occurrence or generation exists.
/// </summary>
internal struct NPCImminentCheckRetryGate
{
    private NPCImminentCheckEvidence _rejectedEvidence;

    public readonly bool ShouldCheck(in NPCImminentCheckEvidence evidence)
        => evidence.IsValid && evidence != _rejectedEvidence;

    public void RecordRejected(in NPCImminentCheckEvidence evidence)
        => _rejectedEvidence = evidence.IsValid ? evidence : default;

    public void Reset() => this = default;
}
