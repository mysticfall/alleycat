using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Minimal non-XR click-to-move controller for the direct-transform navigation playtest scene.
/// </summary>
[GlobalClass]
public partial class DirectTransformNavigationPlaytestController : Node3D
{
    private const string RayHitPositionKey = "position";
    private const float MinPitchRadians = 0.15f;
    private const float MaxPitchRadians = 1.35f;
    private const float MinFacingDragDistanceSquared = 0.01f;
    // Reassert briefly during startup so global/XR runtime cameras that become current after this scene enters the tree
    // do not steal the non-VR playtest view.
    private const int StartupCameraActivationFrameCount = 30;

    private int _startupCameraActivationFramesRemaining;
    private bool _hasMousePosition;
    private bool _hasPreviewHit;
    private bool _isDestinationPlacementActive;
    private bool _isMiddleButtonPanActive;
    private bool _isRightButtonOrbitActive;
    private bool _isCameraOrbitInitialised;
    private Vector2 _lastMousePosition;
    private Vector3 _previewHitPosition;
    private Vector3 _lockedDestinationPosition;
    private Vector3 _cameraOrbitOrigin;
    private Vector3 _lastCameraOrbitPosition;
    private float _cameraYaw;
    private float _cameraPitch;
    private float _cameraDistance;

    /// <summary>
    /// Gets or sets the camera used to project mouse clicks into the playtest world.
    /// </summary>
    [ExportGroup("Scene References")]
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
    /// Gets or sets the root node used to preview the projected destination and final facing.
    /// </summary>
    [Export]
    public Node3D? DestinationMarker
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the marker surface recoloured while destination placement is active.
    /// </summary>
    [Export]
    public GeometryInstance3D? DestinationMarkerSurface
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the marker material used before the left mouse button is pressed.
    /// </summary>
    [Export]
    public Material? DestinationMarkerPreviewMaterial
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the marker material used while the left mouse button is pressed.
    /// </summary>
    [Export]
    public Material? DestinationMarkerPressedMaterial
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
    [ExportGroup("Projection")]
    [Export(PropertyHint.Layers3DPhysics)]
    public uint GroundCollisionMask { get; set; } = 1U;

    /// <summary>
    /// Gets or sets the maximum mouse-projection ray length.
    /// </summary>
    [Export]
    public float RayLength { get; set; } = 1000.0f;

    /// <summary>
    /// Gets or sets the closest permitted camera orbit distance from the navigation target.
    /// </summary>
    [ExportGroup("Camera Controls")]
    [Export]
    public float MinCameraDistance { get; set; } = 2.0f;

    /// <summary>
    /// Gets or sets the furthest permitted camera orbit distance from the navigation target.
    /// </summary>
    [Export]
    public float MaxCameraDistance { get; set; } = 10.0f;

    /// <summary>
    /// Gets or sets the camera distance delta applied per mouse-wheel step.
    /// </summary>
    [Export]
    public float CameraZoomStep { get; set; } = 0.75f;

    /// <summary>
    /// Gets or sets the radians of camera orbit applied per mouse-motion pixel while right dragging.
    /// </summary>
    [Export]
    public float CameraOrbitSensitivity { get; set; } = 0.006f;

    /// <summary>
    /// Gets or sets the world-space pan delta applied per mouse-motion pixel and per camera-distance unit while middle dragging.
    /// </summary>
    [Export]
    public float CameraPanSensitivity { get; set; } = 0.01f;

    internal Vector3 CameraOrbitOrigin => _cameraOrbitOrigin;

    internal Vector3 LastCameraOrbitPosition => _lastCameraOrbitPosition;

    internal void ReinitialiseCameraOrbit()
    {
        _isCameraOrbitInitialised = false;
        InitialiseCameraOrbit();
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        _startupCameraActivationFramesRemaining = StartupCameraActivationFrameCount;
        SetProcess(true);
        SetPhysicsProcess(true);
        InitialiseCameraOrbit();
        UpdateDestinationMarkerVisibility(false);
        ActivatePlaytestCamera();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_startupCameraActivationFramesRemaining > 0)
        {
            _startupCameraActivationFramesRemaining--;
            ActivatePlaytestCamera();
        }

        UpdateCameraTransform();
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton mouseButton:
                HandleMouseButton(mouseButton);
                break;
            case InputEventMouseMotion mouseMotion:
                HandleMouseMotion(mouseMotion);
                break;
            default:
                break;
        }
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        if (!_hasMousePosition)
        {
            return;
        }

        if (!TryProjectMouseToGround(_lastMousePosition, out Vector3 hitPosition))
        {
            _hasPreviewHit = false;
            if (_isDestinationPlacementActive)
            {
                UpdateDestinationMarker(_lockedDestinationPosition);
            }
            else
            {
                UpdateDestinationMarkerVisibility(false);
            }

            return;
        }

        _previewHitPosition = hitPosition;
        _hasPreviewHit = true;
        UpdateDestinationMarker(hitPosition);
    }

    private void HandleMouseButton(InputEventMouseButton mouseButton)
    {
        _lastMousePosition = mouseButton.Position;
        _hasMousePosition = true;

        if (mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                BeginDestinationPlacement(mouseButton.Position);
            }
            else
            {
                CompleteDestinationPlacement(mouseButton.Position);
            }

            return;
        }

        if (mouseButton.ButtonIndex == MouseButton.Middle)
        {
            _isMiddleButtonPanActive = mouseButton.Pressed;
            return;
        }

        if (mouseButton.ButtonIndex == MouseButton.Right)
        {
            _isRightButtonOrbitActive = mouseButton.Pressed;
            return;
        }

        if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelUp)
        {
            ZoomCamera(-CameraZoomStep);
            return;
        }

        if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelDown)
        {
            ZoomCamera(CameraZoomStep);
        }
    }

    private void HandleMouseMotion(InputEventMouseMotion mouseMotion)
    {
        Vector2 relativeMotion = mouseMotion.Relative;
        if (_hasMousePosition && relativeMotion.LengthSquared() <= Mathf.Epsilon)
        {
            relativeMotion = mouseMotion.Position - _lastMousePosition;
        }

        _lastMousePosition = mouseMotion.Position;
        _hasMousePosition = true;

        if (_isMiddleButtonPanActive)
        {
            PanCameraOrbitOrigin(relativeMotion);
        }

        if (_isRightButtonOrbitActive)
        {
            OrbitCamera(relativeMotion);
        }
    }

    internal void OrbitCamera(Vector2 relativeMotion)
    {
        InitialiseCameraOrbit();
        _cameraYaw -= relativeMotion.X * CameraOrbitSensitivity;
        _cameraPitch = Mathf.Clamp(
            _cameraPitch + (relativeMotion.Y * CameraOrbitSensitivity),
            MinPitchRadians,
            MaxPitchRadians);
        UpdateCameraTransform();
    }

    internal void PanCameraOrbitOrigin(Vector2 relativeMotion)
    {
        if (relativeMotion.LengthSquared() <= Mathf.Epsilon)
        {
            return;
        }

        InitialiseCameraOrbit();
        Camera3D? camera = Camera;
        if (camera is null)
        {
            return;
        }

        Vector3 cameraRight = camera.GlobalTransform.Basis.X;
        cameraRight.Y = 0.0f;
        cameraRight = cameraRight.LengthSquared() <= Mathf.Epsilon ? Vector3.Right : cameraRight.Normalized();

        Vector3 cameraForward = -camera.GlobalTransform.Basis.Z;
        cameraForward.Y = 0.0f;
        cameraForward = cameraForward.LengthSquared() <= Mathf.Epsilon ? Vector3.Forward : cameraForward.Normalized();

        float panScale = CameraPanSensitivity * _cameraDistance;
        _cameraOrbitOrigin += ((-cameraRight * relativeMotion.X) + (cameraForward * relativeMotion.Y)) * panScale;
        UpdateCameraTransform();
    }

    private void BeginDestinationPlacement(Vector2 mousePosition)
    {
        if (_hasPreviewHit || TryProjectMouseToGround(mousePosition, out _previewHitPosition))
        {
            _lockedDestinationPosition = _previewHitPosition;
            _hasPreviewHit = true;
            _isDestinationPlacementActive = true;
            UpdateDestinationMarker(_previewHitPosition);
            UpdateDestinationMarkerMaterial();
        }
    }

    internal void PreviewDestinationAt(Vector3 hitPosition)
    {
        _previewHitPosition = hitPosition;
        _hasPreviewHit = true;
        UpdateDestinationMarker(hitPosition);
    }

    internal void BeginDestinationPlacementAt(Vector3 hitPosition)
    {
        _previewHitPosition = hitPosition;
        _lockedDestinationPosition = hitPosition;
        _hasPreviewHit = true;
        _isDestinationPlacementActive = true;
        UpdateDestinationMarker(hitPosition);
        UpdateDestinationMarkerMaterial();
    }

    internal void UpdateDestinationFacingProbe(Vector3 hitPosition)
    {
        _previewHitPosition = hitPosition;
        _hasPreviewHit = true;
        UpdateDestinationMarker(hitPosition);
    }

    internal void CompleteDestinationPlacementAt(Vector3 hitPosition)
    {
        _previewHitPosition = hitPosition;
        _hasPreviewHit = true;
        CompleteDestinationPlacement();
    }

    private void CompleteDestinationPlacement(Vector2 mousePosition)
    {
        if (!_isDestinationPlacementActive)
        {
            return;
        }

        if (TryProjectMouseToGround(mousePosition, out Vector3 releaseHitPosition))
        {
            _previewHitPosition = releaseHitPosition;
            _hasPreviewHit = true;
        }

        CompleteDestinationPlacement();
    }

    private void CompleteDestinationPlacement()
    {
        DirectTransformNavigation? navigation = ResolveNavigation();
        if (navigation is not null)
        {
            Basis destinationBasis = BuildFacingBasis(navigation, _lockedDestinationPosition, _previewHitPosition);
            _ = navigation.SetDestination(new Transform3D(destinationBasis, _lockedDestinationPosition));
        }

        _isDestinationPlacementActive = false;
        UpdateDestinationMarker(_hasPreviewHit ? _previewHitPosition : _lockedDestinationPosition);
        UpdateDestinationMarkerMaterial();
    }

    private void UpdateDestinationMarker(Vector3 hitPosition)
    {
        Node3D? destinationMarker = DestinationMarker;
        if (destinationMarker is null)
        {
            return;
        }

        Vector3 markerPosition = _isDestinationPlacementActive ? _lockedDestinationPosition : hitPosition;
        Vector3 facingProbe = _isDestinationPlacementActive ? hitPosition : markerPosition;
        destinationMarker.GlobalPosition = markerPosition;
        destinationMarker.GlobalBasis = BuildFacingBasis(ResolveNavigation(), markerPosition, facingProbe);
        UpdateDestinationMarkerVisibility(true);
    }

    private void UpdateDestinationMarkerMaterial()
    {
        GeometryInstance3D? markerSurface = DestinationMarkerSurface;
        if (markerSurface is null)
        {
            return;
        }

        markerSurface.MaterialOverride = _isDestinationPlacementActive
            ? DestinationMarkerPressedMaterial
            : DestinationMarkerPreviewMaterial;
    }

    private void UpdateDestinationMarkerVisibility(bool visible) => DestinationMarker?.SetVisible(visible && !IsNavigationMoving());

    private bool IsNavigationMoving()
    {
        DirectTransformNavigation? navigation = ResolveNavigation();
        return navigation is not null
            && navigation.HasDestination
            && navigation.IsNavigationRunning
            && !((INavigation)navigation).IsNavigationFinished;
    }

    private bool TryProjectMouseToGround(Vector2 mousePosition, out Vector3 hitPosition)
    {
        hitPosition = default;
        Camera3D? camera = Camera;
        if (camera is null || RayLength <= 0.0f)
        {
            return false;
        }

        World3D? world = GetWorld3D();
        PhysicsDirectSpaceState3D? directSpaceState = world?.DirectSpaceState;
        if (directSpaceState is null)
        {
            return false;
        }

        Vector3 rayOrigin = camera.ProjectRayOrigin(mousePosition);
        Vector3 rayEnd = rayOrigin + (camera.ProjectRayNormal(mousePosition) * RayLength);
        var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd, GroundCollisionMask);
        Godot.Collections.Dictionary result = directSpaceState.IntersectRay(query);
        if (!result.TryGetValue(RayHitPositionKey, out Variant hitPositionVariant))
        {
            return false;
        }

        hitPosition = hitPositionVariant.AsVector3();
        return hitPosition.IsFinite();
    }

    private void InitialiseCameraOrbit()
    {
        if (_isCameraOrbitInitialised || Camera is null)
        {
            return;
        }

        _cameraOrbitOrigin = ResolveInitialCameraOrbitOrigin();
        Vector3 cameraPosition = Camera.GlobalPosition;
        if (!cameraPosition.IsFinite())
        {
            cameraPosition = _cameraOrbitOrigin + new Vector3(0.0f, 2.0f, -Mathf.Clamp(4.0f, MinCameraDistance, MaxCameraDistance));
        }

        Vector3 cameraOffset = cameraPosition - _cameraOrbitOrigin;
        float cameraOffsetLength = cameraOffset.Length();
        if (!cameraOffset.IsFinite() || cameraOffsetLength <= Mathf.Epsilon)
        {
            _cameraDistance = Mathf.Clamp(4.0f, MinCameraDistance, MaxCameraDistance);
            cameraOffset = new Vector3(0.0f, 2.0f, -_cameraDistance);
            cameraOffsetLength = cameraOffset.Length();
        }
        else
        {
            _cameraDistance = Mathf.Clamp(cameraOffsetLength, MinCameraDistance, MaxCameraDistance);
        }

        _cameraYaw = Mathf.Atan2(cameraOffset.X, cameraOffset.Z);
        _cameraPitch = Mathf.Clamp(Mathf.Asin(cameraOffset.Y / cameraOffsetLength), MinPitchRadians, MaxPitchRadians);
        _isCameraOrbitInitialised = true;
        UpdateCameraTransform();
    }

    internal void ZoomCamera(float distanceDelta)
    {
        InitialiseCameraOrbit();
        _cameraDistance = Mathf.Clamp(_cameraDistance + distanceDelta, MinCameraDistance, MaxCameraDistance);
        UpdateCameraTransform();
    }

    private void UpdateCameraTransform()
    {
        Camera3D? camera = Camera;
        if (camera is null)
        {
            return;
        }

        InitialiseCameraOrbit();
        Vector3 origin = _cameraOrbitOrigin;
        float horizontalDistance = _cameraDistance * Mathf.Cos(_cameraPitch);
        Vector3 offset = new(
            horizontalDistance * Mathf.Sin(_cameraYaw),
            _cameraDistance * Mathf.Sin(_cameraPitch),
            horizontalDistance * Mathf.Cos(_cameraYaw));
        _lastCameraOrbitPosition = origin + offset;
        camera.GlobalPosition = _lastCameraOrbitPosition;
        camera.LookAt(origin, Vector3.Up);
    }

    private Vector3 ResolveInitialCameraOrbitOrigin()
    {
        DirectTransformNavigation? navigation = ResolveNavigation();
        return navigation is null ? GlobalPosition : ResolveNavigationTargetPosition(navigation);
    }

    private static Basis BuildFacingBasis(DirectTransformNavigation? navigation, Vector3 destinationPosition, Vector3 facingProbePosition)
    {
        Vector3 facingDirection = facingProbePosition - destinationPosition;
        facingDirection.Y = 0.0f;

        return facingDirection.LengthSquared() > MinFacingDragDistanceSquared
            ? Basis.LookingAt(facingDirection.Normalized(), Vector3.Up)
            : navigation is not null ? BuildFallbackFacingBasis(navigation, destinationPosition) : Basis.Identity;
    }

    private static Basis BuildFallbackFacingBasis(DirectTransformNavigation navigation, Vector3 destinationPosition)
    {
        Vector3 npcPosition = ResolveNavigationTargetPosition(navigation);
        Vector3 npcToDestination = destinationPosition - npcPosition;
        npcToDestination.Y = 0.0f;
        if (npcToDestination.LengthSquared() > MinFacingDragDistanceSquared)
        {
            return Basis.LookingAt(npcToDestination.Normalized(), Vector3.Up);
        }

        Node3D? target = navigation.Target ?? navigation.GetParentOrNull<Node3D>();
        if (target is not null)
        {
            Vector3 currentForward = -target.GlobalTransform.Basis.Z;
            currentForward.Y = 0.0f;
            if (currentForward.LengthSquared() > Mathf.Epsilon)
            {
                return Basis.LookingAt(currentForward.Normalized(), Vector3.Up);
            }
        }

        return Basis.Identity;
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
}
