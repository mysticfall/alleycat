using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Godot-free <see cref="INavigation" /> double whose request and outcome state is NAV-001's production
/// <see cref="NavigationRequestLifecycle" /> store: every destination is accepted (replacing any active request), nothing
/// moves, and the fixture decides when the active request completes. Terminal evidence reports
/// <see cref="RealisedTransform" />, or the submitted destination when none is set.
/// </summary>
internal sealed class LifecycleNavigationDouble : INavigation
{
    private readonly NavigationRequestLifecycle _lifecycle = new(32);
    private ulong _tick;

    /// <summary>The realised transform reported by terminal evidence, or null to report the submitted destination.</summary>
    public NavigationOutcomeTransform? RealisedTransform
    {
        get; set;
    }

    /// <summary>The most recently submitted destination.</summary>
    public Transform3D LastDestination
    {
        get; private set;
    }

    public NavigationCurrentStatus CurrentStatus => _lifecycle.CurrentStatus;

    public IReadOnlyList<NavigationTerminalOutcomeRecord> TerminalOutcomes => _lifecycle.TerminalOutcomes;

    public bool TryAcknowledgeOutcome(NavigationRequestIdentity request, long terminalSequence)
        => _lifecycle.TryAcknowledgeOutcome(request, terminalSequence);

    public NavigationDestinationResult SetDestination(Transform3D destination)
    {
        LastDestination = destination;
        return _lifecycle.RequestDestination(
            ToOutcome(destination),
            NavigationRequestValidation.Accepted,
            revision: 1,
            acceptancePhysicsTick: ++_tick,
            _lifecycle.CurrentStatus.HasActiveRequest ? Context(NavigationTerminalReason.Replacement) : null,
            _tick).Result;
    }

    public void ClearDestination()
        => _ = _lifecycle.TryCancel(
            NavigationCancellationSource.ExplicitClear,
            Context(NavigationTerminalReason.Cancellation),
            ++_tick);

    /// <summary>Commits successful completion of the active request.</summary>
    public bool CompleteActive() => _lifecycle.TryComplete(Context(NavigationTerminalReason.DestinationReached), ++_tick);

    private NavigationTerminalContext Context(NavigationTerminalReason reason)
        => new(RealisedTransform ?? ToOutcome(LastDestination), default, default, reason);

    private static NavigationOutcomeTransform ToOutcome(Transform3D transform)
    {
        Vector3 facing = -transform.Basis.Z;
        return new NavigationOutcomeTransform(
            transform.Origin.X,
            transform.Origin.Y,
            transform.Origin.Z,
            MathF.Atan2(-facing.X, -facing.Z));
    }

    public bool IsMovementAuthorityActive => true;

    public void ActivateMovementAuthority()
    {
    }

    public void DeactivateMovementAuthority()
    {
    }

    public bool HasDestination => CurrentStatus.HasActiveRequest;

    public Transform3D Destination => LastDestination;

    public bool IsNavigationRunning => HasDestination;

    public bool IsNavigationFinished => !HasDestination;

    public float PathDesiredDistance
    {
        get; set;
    }

    public float DestinationReachedDistance
    {
        get; set;
    }

    public float InitialFacingRampDistance
    {
        get; set;
    }

    public float FacingRampDistance
    {
        get; set;
    }

    public float ShortMoveDistance
    {
        get; set;
    }

    public float FacingToleranceDegrees
    {
        get; set;
    }

    public bool AvoidanceEnabled
    {
        get; set;
    }

    public float AvoidanceRadius
    {
        get; set;
    }

    public float AvoidanceHeight
    {
        get; set;
    }

    public float AvoidanceMaxSpeed
    {
        get; set;
    }

    public Vector3[] CurrentPath => [];

    public int CurrentPathIndex => 0;

    public NavigationMotionIntent Poll(Transform3D actorTransform) => default;
}
