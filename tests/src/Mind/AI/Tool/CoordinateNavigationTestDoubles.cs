using AlleyCat.Mind.AI.Tool;
using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.Tests.Mind.AI.Tool;

/// <summary>
/// Godot-free <see cref="INavigation" /> double whose request and outcome state is the production
/// <see cref="NavigationRequestLifecycle" /> store, so acceptance, replacement, cancellation, terminal records and
/// acknowledgement follow NAV-001's real lifecycle semantics. Refusal results are scripted per call and, as the
/// <see cref="INavigation" /> refusal contract requires, leave every state unchanged.
/// </summary>
internal sealed class LifecycleNavigation(int terminalCapacity = 32) : INavigation
{
    private readonly NavigationRequestLifecycle _lifecycle = new(terminalCapacity);
    private readonly List<Transform3D> _submitted = [];
    private ulong _tick;

    /// <summary>Result returned by the next destination request; Accepted reserves an identity.</summary>
    public NavigationDestinationResult NextResult { get; set; } = NavigationDestinationResult.Accepted;

    /// <summary>When set, an accepted request fails inside the same call (an immediate motion-unavailable failure).</summary>
    public bool FailImmediatelyOnAccept
    {
        get; set;
    }

    /// <summary>The realised transform recorded in terminal evidence.</summary>
    public NavigationOutcomeTransform RealisedTransform
    {
        get; set;
    }

    /// <summary>Every destination submitted to <see cref="SetDestination(Transform3D)" />, refused or not.</summary>
    public IReadOnlyList<Transform3D> Submitted => _submitted;

    public int ClearCount
    {
        get; private set;
    }

    public NavigationCurrentStatus CurrentStatus => _lifecycle.CurrentStatus;

    public IReadOnlyList<NavigationTerminalOutcomeRecord> TerminalOutcomes => _lifecycle.TerminalOutcomes;

    public bool TryAcknowledgeOutcome(NavigationRequestIdentity request, long terminalSequence)
        => _lifecycle.TryAcknowledgeOutcome(request, terminalSequence);

    public NavigationDestinationResult SetDestination(Transform3D destination)
    {
        _submitted.Add(destination);
        if (NextResult != NavigationDestinationResult.Accepted)
        {
            return NextResult;
        }

        NavigationDestinationAttempt attempt = _lifecycle.RequestDestination(
            ToOutcome(destination),
            NavigationRequestValidation.Accepted,
            revision: 1,
            acceptancePhysicsTick: ++_tick,
            _lifecycle.CurrentStatus.HasActiveRequest ? Context(NavigationTerminalReason.Replacement) : null,
            _tick);
        if (attempt.IsAccepted && FailImmediatelyOnAccept)
        {
            _ = _lifecycle.TryFail(Context(NavigationTerminalReason.MotionUnavailable), ++_tick);
        }

        return attempt.Result;
    }

    public void ClearDestination()
    {
        ClearCount++;
        _ = _lifecycle.TryCancel(
            NavigationCancellationSource.ExplicitClear,
            Context(NavigationTerminalReason.Cancellation),
            ++_tick);
    }

    /// <summary>Commits successful completion of the active request.</summary>
    public bool CompleteActive() => _lifecycle.TryComplete(Context(NavigationTerminalReason.DestinationReached), ++_tick);

    /// <summary>Commits a convergence failure of the active request.</summary>
    public bool FailActive() => _lifecycle.TryFail(Context(NavigationTerminalReason.ConvergenceFailed), ++_tick);

    /// <summary>Commits a known obstruction of the active request.</summary>
    public bool BlockActive() => _lifecycle.TryBlock(
        new NavigationTerminalContext(
            RealisedTransform,
            default,
            default,
            NavigationTerminalReason.Obstruction,
            new NavigationObstructionDetails("wall", null, 0)),
        ++_tick);

    private NavigationTerminalContext Context(NavigationTerminalReason reason)
        => new(RealisedTransform, default, default, reason);

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

    public Transform3D Destination => _submitted.Count == 0 ? Transform3D.Identity : _submitted[^1];

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

/// <summary>Scripted <see cref="ICoordinateNavigationEnvironment" /> recording every speed application.</summary>
internal sealed class ScriptedNavigationEnvironment : ICoordinateNavigationEnvironment
{
    private readonly List<float> _speeds = [];

    public Transform3D SubjectTransform { get; set; } = Transform3D.Identity;

    /// <summary>The probed route returned for every query; empty means unknown.</summary>
    public Vector3[] Path { get; set; } = [];

    /// <summary>The navigation-map point returned for every projection, or null when the map cannot answer.</summary>
    public Func<Vector3, Vector3?> Projection { get; set; } = static _ => null;

    public IReadOnlyList<float> AppliedSpeeds => _speeds;

    public Vector3[] QueryPath(Vector3 from, Vector3 to) => Path;

    public Vector3? ProjectToNavigationMap(Vector3 point) => Projection(point);

    public void ApplyNormalisedSpeed(float normalisedSpeed) => _speeds.Add(normalisedSpeed);
}
