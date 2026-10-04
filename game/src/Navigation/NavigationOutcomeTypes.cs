namespace AlleyCat.Navigation;

/// <summary>Stable identity assigned only to an accepted navigation request.</summary>
public readonly record struct NavigationRequestIdentity(long RequestID, long Generation);

/// <summary>Lifecycle status of an accepted request.</summary>
public enum NavigationRequestStatus
{
    /// <summary>No accepted request is active.</summary>
    Inactive,
    /// <summary>The accepted request is active.</summary>
    Active,
    /// <summary>The request completed successfully.</summary>
    Completed,
    /// <summary>The request failed terminal convergence or a contract.</summary>
    Failed,
    /// <summary>The request was blocked by a known obstruction.</summary>
    Blocked,
    /// <summary>The request was cancelled.</summary>
    Cancelled,
    /// <summary>The request was replaced by a successor.</summary>
    Superseded,
}

/// <summary>Nonterminal activity phase of an active request.</summary>
public enum NavigationActivityPhase
{
    /// <summary>No request is active.</summary>
    Inactive,
    /// <summary>The request is following its route.</summary>
    Following,
    /// <summary>Movement is temporarily neutralised without cancelling the request.</summary>
    Neutralised,
    /// <summary>The request is waiting without being cancelled.</summary>
    Waiting,
}

/// <summary>Terminal kind committed for an accepted request.</summary>
public enum NavigationTerminalOutcome
{
    /// <summary>Successful completion.</summary>
    Completed,
    /// <summary>Terminal failure.</summary>
    Failed,
    /// <summary>Known obstruction.</summary>
    Blocked,
    /// <summary>Explicit or lifecycle cancellation.</summary>
    Cancelled,
    /// <summary>Atomic replacement by another accepted request.</summary>
    Superseded,
}

/// <summary>Structured reason for a terminal outcome.</summary>
public enum NavigationTerminalReason
{
    /// <summary>The destination was reached within tolerance.</summary>
    DestinationReached,
    /// <summary>Measured convergence was outside tolerance.</summary>
    ConvergenceFailed,
    /// <summary>A known obstruction prevented progress.</summary>
    Obstruction,
    /// <summary>A lifecycle operation cancelled the request.</summary>
    Cancellation,
    /// <summary>A successor request replaced this request.</summary>
    Replacement,
}

/// <summary>Source that cancelled an accepted request.</summary>
public enum NavigationCancellationSource
{
    /// <summary>An explicit clear operation.</summary>
    ExplicitClear,
    /// <summary>A manual locomotion override.</summary>
    ManualOverride,
    /// <summary>Navigation was disabled.</summary>
    NavigationDisabled,
    /// <summary>The navigation owner was removed.</summary>
    OwnerRemoved,
    /// <summary>The navigation lifecycle was reset.</summary>
    Reset,
}

/// <summary>Godot-independent world transform snapshot used by durable navigation records.</summary>
public readonly record struct NavigationOutcomeTransform(float X, float Y, float Z, float YawRadians);

/// <summary>Measured planar and wrapped-yaw terminal errors.</summary>
public readonly record struct NavigationPlanarYawError(float PlanarDistance, float YawRadians);

/// <summary>Configured planar and wrapped-yaw terminal tolerances.</summary>
public readonly record struct NavigationTerminalTolerances(float PlanarDistance, float YawRadians)
{
    /// <summary>Calculates the required signed margins using exactly <c>tolerance - error</c>.</summary>
    public NavigationSignedMargins CalculateMargins(NavigationPlanarYawError error)
        => new(PlanarDistance - error.PlanarDistance, YawRadians - error.YawRadians);
}

/// <summary>Signed terminal margins; a negative value is outside tolerance.</summary>
public readonly record struct NavigationSignedMargins(float PlanarDistance, float YawRadians);

/// <summary>Known obstruction and attempted recovery context for a blocked request.</summary>
public readonly record struct NavigationObstructionDetails(
    string BlockKind,
    string? RecoveryKind,
    int RecoveryAttempts);

/// <summary>Measured evidence supplied when terminal work is committed.</summary>
public readonly record struct NavigationTerminalContext
{
    /// <summary>Creates terminal evidence and calculates its exact signed margins.</summary>
    public NavigationTerminalContext(
        NavigationOutcomeTransform finalTransform,
        NavigationPlanarYawError error,
        NavigationTerminalTolerances tolerances,
        NavigationTerminalReason reason,
        NavigationObstructionDetails? obstruction = null,
        ulong boundaryPhysicsTick = 0)
    {
        FinalTransform = finalTransform;
        Error = error;
        Tolerances = tolerances;
        Margins = tolerances.CalculateMargins(error);
        Reason = reason;
        Obstruction = obstruction;
        BoundaryPhysicsTick = boundaryPhysicsTick;
    }

    /// <summary>Gets the realised terminal transform.</summary>
    public NavigationOutcomeTransform FinalTransform
    {
        get;
    }

    /// <summary>Gets the measured terminal errors.</summary>
    public NavigationPlanarYawError Error
    {
        get;
    }

    /// <summary>Gets the retained request tolerances.</summary>
    public NavigationTerminalTolerances Tolerances
    {
        get;
    }

    /// <summary>Gets signed tolerance-minus-error margins.</summary>
    public NavigationSignedMargins Margins
    {
        get;
    }

    /// <summary>Gets the structured terminal reason.</summary>
    public NavigationTerminalReason Reason
    {
        get;
    }

    /// <summary>Gets obstruction context when the outcome is blocked.</summary>
    public NavigationObstructionDetails? Obstruction
    {
        get;
    }

    /// <summary>Gets the final authorised movement boundary tick, or zero when no finite boundary exists.</summary>
    public ulong BoundaryPhysicsTick
    {
        get;
    }

    internal NavigationTerminalContext WithReason(NavigationTerminalReason reason)
        => new(FinalTransform, Error, Tolerances, reason, Obstruction, BoundaryPhysicsTick);
}

/// <summary>Validation completed before request identity or terminal capacity is reserved.</summary>
public readonly record struct NavigationRequestValidation(bool IsValid, bool IsReady, bool IsReachable)
{
    /// <summary>Gets validation representing an admissible request.</summary>
    public static NavigationRequestValidation Accepted => new(true, true, true);
}

/// <summary>Result and optional identity of a destination attempt.</summary>
public readonly record struct NavigationDestinationAttempt(
    NavigationDestinationResult Result,
    NavigationRequestIdentity? Request)
{
    /// <summary>Gets whether the attempt reserved an identity and became active.</summary>
    public bool IsAccepted => Result == NavigationDestinationResult.Accepted;
}

/// <summary>Immutable snapshot of current accepted work; terminal results are kept separately.</summary>
public readonly record struct NavigationCurrentStatus(
    NavigationRequestStatus Status,
    NavigationActivityPhase Phase,
    NavigationRequestIdentity? ActiveRequest,
    NavigationOutcomeTransform? RequestedTransform,
    long Revision,
    ulong AcceptancePhysicsTick)
{
    /// <summary>Gets whether this snapshot contains active accepted work.</summary>
    public bool HasActiveRequest => Status == NavigationRequestStatus.Active && ActiveRequest is not null;

    /// <summary>Gets the canonical inactive snapshot.</summary>
    public static NavigationCurrentStatus Inactive => new(
        NavigationRequestStatus.Inactive,
        NavigationActivityPhase.Inactive,
        null,
        null,
        0,
        0);
}

/// <summary>Immutable ordered terminal record for one accepted navigation request.</summary>
public readonly record struct NavigationTerminalOutcomeRecord(
    long Sequence,
    NavigationRequestIdentity Request,
    long Revision,
    NavigationTerminalOutcome Outcome,
    NavigationTerminalReason Reason,
    NavigationOutcomeTransform RequestedTransform,
    NavigationOutcomeTransform FinalTransform,
    NavigationPlanarYawError Error,
    NavigationTerminalTolerances Tolerances,
    NavigationSignedMargins Margins,
    ulong AcceptancePhysicsTick,
    ulong BoundaryPhysicsTick,
    ulong TerminalPhysicsTick,
    NavigationObstructionDetails? Obstruction,
    NavigationCancellationSource? CancellationSource,
    NavigationRequestIdentity? SuccessorRequest);

/// <summary>Owner-independent destination for a terminal record during owner removal.</summary>
public interface INavigationTerminalOutcomeSink
{
    /// <summary>Retains a terminal outcome independently of its former node owner.</summary>
    void RecordTerminalOutcome(NavigationTerminalOutcomeRecord outcome);
}

/// <summary>Read and acknowledgement boundary for durable navigation outcomes.</summary>
public interface INavigationOutcomeSource
{
    /// <summary>Gets the current accepted request snapshot.</summary>
    NavigationCurrentStatus CurrentStatus
    {
        get;
    }

    /// <summary>Gets retained terminal outcomes in commit order.</summary>
    IReadOnlyList<NavigationTerminalOutcomeRecord> TerminalOutcomes
    {
        get;
    }

    /// <summary>Acknowledges and removes exactly the matching retained outcome.</summary>
    bool TryAcknowledgeOutcome(NavigationRequestIdentity request, long terminalSequence);
}
