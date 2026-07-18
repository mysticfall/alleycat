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
    /// <returns>The destination request result.</returns>
    NavigationDestinationResult SetDestination(Transform3D destination);

    /// <summary>
    /// Clears the requested destination from this facade.
    /// </summary>
    void ClearDestination();

    /// <summary>
    /// Gets the next path position and advances the backing path follower as required by Godot.
    /// </summary>
    /// <returns>The next path position.</returns>
    Vector3 GetNextPathPosition();
}
