using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Immutable travel and horizontal-facing intent produced by one navigation poll.
/// </summary>
public readonly struct NavigationMotionIntent(
    Vector3 nextPathPosition,
    Vector3 travelDirection,
    Vector3 desiredFacingDirection,
    float signedYawError,
    float remainingPathDistance,
    float travelledPathDistance,
    bool positionReached,
    bool facingReached,
    bool isComplete,
    bool hasValidSample)
{
    /// <summary>
    /// Gets the sampled next path position in world space.
    /// </summary>
    public Vector3 NextPathPosition => nextPathPosition;

    /// <summary>
    /// Gets the normalised three-dimensional path travel direction, or zero at positional arrival.
    /// </summary>
    public Vector3 TravelDirection => travelDirection;

    /// <summary>
    /// Gets the requested normalised horizontal facing direction.
    /// </summary>
    public Vector3 DesiredFacingDirection => desiredFacingDirection;

    /// <summary>
    /// Gets the shortest signed world-up yaw error in radians.
    /// </summary>
    public float SignedYawError => signedYawError;

    /// <summary>
    /// Gets the finite non-negative remaining three-dimensional path distance.
    /// </summary>
    public float RemainingPathDistance => remainingPathDistance;

    /// <summary>
    /// Gets the finite monotonic distance travelled since the first valid sample.
    /// </summary>
    public float TravelledPathDistance => travelledPathDistance;

    /// <summary>
    /// Gets whether the destination position is within tolerance.
    /// </summary>
    public bool PositionReached => positionReached;

    /// <summary>
    /// Gets whether horizontal facing is within tolerance.
    /// </summary>
    public bool FacingReached => facingReached;

    /// <summary>
    /// Gets whether both position and facing have been reached.
    /// </summary>
    public bool IsComplete => isComplete;

    /// <summary>
    /// Gets whether this intent was derived from a valid, map-ready path sample.
    /// </summary>
    public bool HasValidSample => hasValidSample;
}
