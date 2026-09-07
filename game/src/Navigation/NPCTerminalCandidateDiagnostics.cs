using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>
/// The individual scheduler predicates calculated from one terminal candidate without conflating endpoint residual
/// capacity with route-relative overshoot.
/// </summary>
public readonly record struct NPCTerminalCandidateDecisionFactors(
    bool SupportedBlend,
    bool ContactCompatible,
    bool VelocityCompatible,
    bool EndpointServiceable,
    bool WouldOvershootTerminal,
    bool YawCompatible,
    bool WaitCompatible)
{
    public bool RejectedUnsupportedBlend => !SupportedBlend;
    public bool RejectedContactMismatch => !ContactCompatible;
    public bool RejectedVelocityMismatch => !VelocityCompatible;
    public bool RejectedEndpointUnserviceable => !EndpointServiceable;
    public bool RejectedTerminalOvershoot => WouldOvershootTerminal;
    public bool RejectedYawUnreachable => !YawCompatible;
    public bool RejectedWaitBeyondBound => !WaitCompatible;
    public bool IsSchedulerEligible => SchedulerRejection == NPCTerminalCandidateRejection.None;

    public NPCTerminalCandidateRejection SchedulerRejection
    {
        get
        {
            NPCTerminalCandidateRejection rejection = NPCTerminalCandidateRejection.None;
            if (RejectedUnsupportedBlend)
            {
                rejection |= NPCTerminalCandidateRejection.UnsupportedBlend;
            }

            if (RejectedContactMismatch)
            {
                rejection |= NPCTerminalCandidateRejection.ContactMismatch;
            }

            if (RejectedVelocityMismatch)
            {
                rejection |= NPCTerminalCandidateRejection.VelocityMismatch;
            }

            if (RejectedEndpointUnserviceable || RejectedTerminalOvershoot)
            {
                rejection |= NPCTerminalCandidateRejection.EndpointUnserviceable;
            }

            if (RejectedWaitBeyondBound)
            {
                rejection |= NPCTerminalCandidateRejection.WaitBeyondBound;
            }

            if (RejectedYawUnreachable)
            {
                rejection |= NPCTerminalCandidateRejection.YawUnreachable;
            }

            return rejection;
        }
    }

    public static NPCTerminalCandidateDecisionFactors From(NPCTerminalReleaseCandidate candidate)
        => new(
            candidate.SupportedBlend,
            candidate.ContactCompatible,
            candidate.VelocityCompatible,
            candidate.EndpointServiceable,
            candidate.WouldOvershootTerminal,
            !(candidate.YawResidual > NPCTerminalReleaseScheduler.RootRotationTolerance),
            !(candidate.TimeToRelease > NPCTerminalReleaseScheduler.MaximumWaitSeconds));
}

/// <summary>
/// Immutable bounded evidence for one same-lane or opposite-lane imminent candidate evaluation: the next authored
/// occurrence, its projected release pose, the exact f2i correction feasibility verdict, and the rejection reasons.
/// </summary>
public readonly record struct NPCTerminalCandidateEvaluationDiagnostics(
    bool IsEvaluated,
    int CandidateIndex,
    NPCAnimationPhaseLane ReleaseLane,
    string? ExitRole,
    string? ReleaseEvent,
    NPCReleaseOccurrence Occurrence,
    float TimeToReleaseSeconds,
    bool ProjectionResolved,
    bool BlendConvergedAtRelease,
    int ProjectedLeadInTicks,
    Vector2 RequiredCorrectionWorld,
    Vector2 ReachableCorrectionWorld,
    Vector2 PredictedEndpointResidualWorld,
    float PredictedEndpointYawResidual,
    NPCVectorConvergenceInfeasibility OracleInfeasibility,
    float UsablePlanarCapacity,
    bool EndpointServiceable,
    float SignedTerminalOvershootMetres,
    bool WouldOvershootTerminal,
    float YawResidualRadians,
    bool YawCompatible,
    bool VelocityCompatible,
    bool ContactCompatible,
    bool WaitCompatible,
    bool SupportedBlend,
    bool RejectedUnsupportedBlend,
    bool RejectedContactMismatch,
    bool RejectedVelocityMismatch,
    bool RejectedYawUnreachable,
    bool RejectedCorrectionBeyondCapacity,
    bool RejectedWaitBeyondBound,
    bool RejectedTerminalOvershoot,
    bool FinalDecisionEligible,
    NPCTerminalCandidateRejection SchedulerRejection);

/// <summary>
/// Latest imminent terminal decision evidence. Candidate fields are valid only when
/// <see cref="HasCandidateEvaluation" /> is <see langword="true" />.
/// </summary>
public readonly record struct NPCTerminalCandidateDecisionDiagnostics(
    bool HasCandidateEvaluation,
    long DestinationRequestGeneration,
    long RouteRevision,
    string? ResetReason,
    NPCAnimationStage Stage,
    bool TerminalCommitted,
    int ExitCount,
    float RemainingReservedTerminalPlanar,
    bool HasPostExitRealisedError,
    float PostExitRealisedPositionError,
    float PostExitRealisedYawError,
    bool SchedulerCommit,
    int SelectedCandidateIndex,
    NPCTerminalCandidateRejection SelectedSchedulerRejection,
    NPCTerminalCandidateEvaluationDiagnostics SameLaneCandidate,
    NPCTerminalCandidateEvaluationDiagnostics OppositeLaneCandidate,
    bool TerminalReleaseSelected)
{
    public static NPCTerminalCandidateDecisionDiagnostics Empty => new(
        false,
        0,
        0,
        null,
        NPCAnimationStage.Idle,
        false,
        0,
        0f,
        false,
        0f,
        0f,
        false,
        -1,
        NPCTerminalCandidateRejection.None,
        default,
        default,
        false);
}
