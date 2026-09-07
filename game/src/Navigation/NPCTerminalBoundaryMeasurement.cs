namespace AlleyCat.Navigation;

#pragma warning disable CS1591

/// <summary>Outcome seam established exactly when the final f2i movement interval is realised.</summary>
public enum NPCTerminalBoundaryState
{
    Inactive,
    Confirmation,
    Failed,
    Completed,
}

/// <summary>Result of one allocation-free stationary confirmation observation.</summary>
internal enum NPCTerminalConfirmationResult
{
    Pending,
    Failed,
    Completed,
}

/// <summary>Fixed-size four-frame confirmation counter used by the production physics path.</summary>
internal struct NPCTerminalConfirmation
{
    public const int RequiredFrameCount = 4;

    public int ConfirmedFrameCount
    {
        get; private set;
    }

    public bool IsActive
    {
        get; private set;
    }

    public void Begin()
    {
        ConfirmedFrameCount = 0;
        IsActive = true;
    }

    public NPCTerminalConfirmationResult Observe(bool isWithinTolerance)
    {
        if (!IsActive)
        {
            return NPCTerminalConfirmationResult.Pending;
        }

        if (!isWithinTolerance)
        {
            IsActive = false;
            return NPCTerminalConfirmationResult.Failed;
        }

        ConfirmedFrameCount++;
        if (ConfirmedFrameCount < RequiredFrameCount)
        {
            return NPCTerminalConfirmationResult.Pending;
        }

        IsActive = false;
        return NPCTerminalConfirmationResult.Completed;
    }

    public void Reset()
    {
        ConfirmedFrameCount = 0;
        IsActive = false;
    }
}

/// <summary>
/// Immutable value evidence captured at the hard motion boundary. NavigationBase commitment deliberately remains a
/// separate operation so terminal persistence can consume this exact measurement in the lifecycle slice.
/// </summary>
public readonly record struct NPCTerminalBoundaryMeasurement(
    NavigationRequestIdentity Request,
    long RouteRevision,
    long ClockGeneration,
    NPCReleaseOccurrence Occurrence,
    ulong BoundaryPhysicsTick,
    ulong PhysicsTick,
    NavigationOutcomeTransform RequestedTransform,
    NavigationOutcomeTransform FinalTransform,
    NavigationPlanarYawError Error,
    NavigationTerminalTolerances Tolerances,
    NavigationSignedMargins Margins)
{
    public bool IsWithinTolerance => Margins.PlanarDistance >= 0f && Margins.YawRadians >= 0f;
}
