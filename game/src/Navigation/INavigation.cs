using AlleyCat.Core;
using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Component facade for navigation and path-following state.
/// </summary>
public interface INavigation : IComponent
{
    /// <summary>
    /// Gets whether a destination has been requested and not explicitly cleared.
    /// </summary>
    bool HasDestination
    {
        get;
    }

    /// <summary>
    /// Gets the requested navigation destination transform.
    /// </summary>
    Transform3D Destination
    {
        get;
    }

    /// <summary>
    /// Gets whether navigation is currently running toward a requested destination.
    /// </summary>
    bool IsNavigationRunning
    {
        get;
    }

    /// <summary>
    /// Gets whether the current navigation destination is finished or no destination is active.
    /// </summary>
    bool IsNavigationFinished
    {
        get;
    }

    /// <summary>
    /// Gets or sets the distance threshold for advancing between path points.
    /// </summary>
    float PathDesiredDistance
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the distance threshold for considering the destination reached.
    /// </summary>
    float DestinationReachedDistance
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the initial path distance over which facing aligns to travel.
    /// </summary>
    float InitialFacingRampDistance
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the shared approach distance for waypoint and terminal facing ramps.
    /// </summary>
    float FacingRampDistance
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the route length at or below which terminal facing applies immediately.
    /// </summary>
    float ShortMoveDistance
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the horizontal facing completion tolerance in degrees.
    /// </summary>
    float FacingToleranceDegrees
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets whether Godot avoidance is enabled for the underlying navigation agent.
    /// </summary>
    bool AvoidanceEnabled
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the configured avoidance radius.
    /// </summary>
    float AvoidanceRadius
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the configured avoidance height.
    /// </summary>
    float AvoidanceHeight
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the configured maximum avoidance speed.
    /// </summary>
    float AvoidanceMaxSpeed
    {
        get; set;
    }

    /// <summary>
    /// Gets the current navigation path as reported by the backing navigation system.
    /// </summary>
    Vector3[] CurrentPath
    {
        get;
    }

    /// <summary>
    /// Gets the current path index as reported by the backing navigation system.
    /// </summary>
    int CurrentPathIndex
    {
        get;
    }

    /// <summary>
    /// Requests navigation toward the supplied transform intent.
    /// </summary>
    /// <param name="destination">Destination and final-orientation intent.</param>
    /// <returns>
    /// The destination request result. Invalid, unreachable, and map-not-ready results leave existing request state
    /// unchanged.
    /// </returns>
    NavigationDestinationResult SetDestination(Transform3D destination);

    /// <summary>
    /// Clears the requested destination from this facade.
    /// </summary>
    void ClearDestination();

    /// <summary>
    /// Polls one coherent navigation intent against the actor's authoritative world transform.
    /// </summary>
    /// <param name="actorTransform">Current authoritative actor transform in world space.</param>
    /// <returns>The cached or newly sampled immutable intent.</returns>
    NavigationMotionIntent Poll(Transform3D actorTransform);
}
