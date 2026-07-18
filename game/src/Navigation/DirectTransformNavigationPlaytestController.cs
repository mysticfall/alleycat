using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Minimal non-XR click-to-move controller for the direct-transform navigation playtest scene.
/// </summary>
[GlobalClass]
public partial class DirectTransformNavigationPlaytestController : Node3D
{
    private const string RayHitPositionKey = "position";
    // Reassert briefly during startup so global/XR runtime cameras that become current after this scene enters the tree
    // do not steal the non-VR playtest view.
    private const int StartupCameraActivationFrameCount = 30;

    private int _startupCameraActivationFramesRemaining;
    private bool _hasPendingClickRaycast;
    private Vector2 _pendingClickPosition;

    /// <summary>
    /// Gets or sets the camera used to project mouse clicks into the playtest world.
    /// </summary>
    [Export]
    public Camera3D? Camera
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the navigation component driven by click destinations.
    /// </summary>
    [Export]
    public DirectTransformNavigation? Navigation
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the fallback path used when the navigation component is installed after scene instantiation.
    /// </summary>
    [Export]
    public NodePath NavigationPath { get; set; } = new();

    /// <summary>
    /// Gets or sets the physics layers used when raycasting for click targets.
    /// </summary>
    [Export(PropertyHint.Layers3DPhysics)]
    public uint GroundCollisionMask { get; set; } = 1U;

    /// <summary>
    /// Gets or sets the maximum mouse-projection ray length.
    /// </summary>
    [Export]
    public float RayLength { get; set; } = 1000.0f;

    /// <inheritdoc/>
    public override void _Ready()
    {
        _startupCameraActivationFramesRemaining = StartupCameraActivationFrameCount;
        SetProcess(true);
        SetPhysicsProcess(true);
        ActivatePlaytestCamera();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_startupCameraActivationFramesRemaining <= 0)
        {
            SetProcess(false);
            return;
        }

        _startupCameraActivationFramesRemaining--;
        ActivatePlaytestCamera();
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouseButton)
        {
            return;
        }

        _pendingClickPosition = mouseButton.Position;
        _hasPendingClickRaycast = true;
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        if (!_hasPendingClickRaycast)
        {
            return;
        }

        _hasPendingClickRaycast = false;

        Camera3D? camera = Camera;
        DirectTransformNavigation? navigation = ResolveNavigation();
        if (camera is null || navigation is null || RayLength <= 0.0f)
        {
            return;
        }

        World3D? world = GetWorld3D();
        PhysicsDirectSpaceState3D? directSpaceState = world?.DirectSpaceState;
        if (directSpaceState is null)
        {
            return;
        }

        Vector3 rayOrigin = camera.ProjectRayOrigin(_pendingClickPosition);
        Vector3 rayEnd = rayOrigin + (camera.ProjectRayNormal(_pendingClickPosition) * RayLength);
        var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd, GroundCollisionMask);
        Godot.Collections.Dictionary result = directSpaceState.IntersectRay(query);

        if (!result.TryGetValue(RayHitPositionKey, out Variant hitPositionVariant))
        {
            return;
        }

        Vector3 hitPosition = hitPositionVariant.AsVector3();
        if (!hitPosition.IsFinite())
        {
            return;
        }

        Vector3 npcPosition = ResolveNavigationTargetPosition(navigation);
        Basis destinationBasis = BuildFacingBasis(npcPosition, hitPosition);
        _ = navigation.SetDestination(new Transform3D(destinationBasis, hitPosition));
    }

    private void ActivatePlaytestCamera()
    {
        Camera3D? camera = Camera;
        if (camera is null || !camera.IsInsideTree())
        {
            return;
        }

        if (!ReferenceEquals(camera.GetViewport()?.GetCamera3D(), camera))
        {
            camera.MakeCurrent();
        }
    }

    private DirectTransformNavigation? ResolveNavigation()
    {
        if (Navigation is not null)
        {
            return Navigation;
        }

        if (NavigationPath.IsEmpty)
        {
            return null;
        }

        Navigation = GetNodeOrNull<DirectTransformNavigation>(NavigationPath);

        return Navigation;
    }

    private static Vector3 ResolveNavigationTargetPosition(DirectTransformNavigation navigation)
    {
        if (navigation.Target is not null)
        {
            return navigation.Target.GlobalPosition;
        }

        Node? parent = navigation.GetParent();
        while (parent is not null)
        {
            if (parent is Node3D node3D)
            {
                return node3D.GlobalPosition;
            }

            parent = parent.GetParent();
        }

        return Vector3.Zero;
    }

    private static Basis BuildFacingBasis(Vector3 sourcePosition, Vector3 destinationPosition)
    {
        Vector3 facingDirection = destinationPosition - sourcePosition;
        facingDirection.Y = 0.0f;

        return facingDirection.LengthSquared() <= Mathf.Epsilon
            ? Basis.Identity
            : Basis.LookingAt(facingDirection.Normalized(), Vector3.Up);
    }
}
