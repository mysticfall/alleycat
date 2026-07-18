using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Base Godot navigation component that exposes a facade over <see cref="NavigationAgent3D" /> path state.
/// </summary>
[GlobalClass]
public abstract partial class NavigationBase : NavigationAgent3D, INavigation
{
    private Transform3D _destination = Transform3D.Identity;

    /// <inheritdoc/>
    public bool HasDestination
    {
        get; private set;
    }

    /// <inheritdoc/>
    public Transform3D Destination => _destination;

    /// <inheritdoc/>
    public bool IsNavigationRunning => HasDestination && !IsNavigationFinished();

    /// <inheritdoc/>
    bool INavigation.IsNavigationFinished => !HasDestination || IsNavigationFinished();

    /// <inheritdoc/>
    [ExportGroup("Distance Facade")]
    [Export]
    public float DestinationReachedDistance
    {
        get => TargetDesiredDistance;
        set => TargetDesiredDistance = value;
    }

    /// <inheritdoc/>
    [ExportGroup("Avoidance Facade")]
    [Export]
    public float AvoidanceRadius
    {
        get => Radius;
        set => Radius = value;
    }

    /// <inheritdoc/>
    [Export]
    public float AvoidanceHeight
    {
        get => Height;
        set => Height = value;
    }

    /// <inheritdoc/>
    [Export]
    public float AvoidanceMaxSpeed
    {
        get => MaxSpeed;
        set => MaxSpeed = value;
    }

    /// <inheritdoc/>
    public Vector3[] CurrentPath => GetCurrentNavigationPath();

    /// <inheritdoc/>
    public int CurrentPathIndex => GetCurrentNavigationPathIndex();

    /// <inheritdoc/>
    protected bool IsNavigationMapReady => NavigationServer3D.MapGetIterationId(GetNavigationMap()) != 0;

    /// <inheritdoc/>
    public NavigationDestinationResult SetDestination(Transform3D destination)
    {
        if (!destination.IsFinite())
        {
            return NavigationDestinationResult.Invalid;
        }

        _destination = destination;
        HasDestination = true;
        TargetPosition = destination.Origin;
        OnDestinationAccepted(destination);

        return IsNavigationMapReady && !IsTargetReachable()
            ? NavigationDestinationResult.Unreachable
            : NavigationDestinationResult.Accepted;
    }

    /// <inheritdoc/>
    public void ClearDestination() => HasDestination = false;

    /// <inheritdoc/>
    Vector3 INavigation.GetNextPathPosition() => GetNextPathPosition();

    /// <inheritdoc/>
    protected void SetAgentVelocity(Vector3 velocity) => Velocity = velocity;

    /// <summary>
    /// Allows concrete implementations to react to accepted destination intent.
    /// </summary>
    /// <param name="destination">Accepted navigation destination.</param>
    protected virtual void OnDestinationAccepted(Transform3D destination)
    {
    }
}
