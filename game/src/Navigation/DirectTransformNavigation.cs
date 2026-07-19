using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Baseline navigation consumer that directly applies polled intent to a configured node.
/// </summary>
[GlobalClass]
public partial class DirectTransformNavigation : NavigationBase
{
    /// <summary>
    /// Initialises the baseline consumer with precision navigation tolerances.
    /// Scene-authored exported values are applied by Godot after construction.
    /// </summary>
    public DirectTransformNavigation()
    {
        PathDesiredDistance = 0.05f;
        DestinationReachedDistance = 0.05f;
    }

    /// <summary>
    /// Gets or sets the moved node; when unset, the closest <see cref="Node3D" /> ancestor is used.
    /// </summary>
    [Export]
    public Node3D? Target
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets translation speed in world units per second.
    /// </summary>
    [Export]
    public float MovementSpeed { get; set; } = 2.0f;

    /// <summary>
    /// Gets or sets horizontal turning speed in degrees per second.
    /// </summary>
    [Export(PropertyHint.Range, "0,1080,0.1,or_greater")]
    public float AngularSpeedDegrees { get; set; } = 360.0f;

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);

        Node3D? targetNode = ResolveTarget();
        if (targetNode is null || !HasDestination)
        {
            return;
        }

        Transform3D currentTransform = GetComposedWorldTransform(targetNode);
        NavigationMotionIntent intent = Poll(currentTransform);
        if (!intent.HasValidSample)
        {
            return;
        }

        float safeDelta = double.IsFinite(delta) ? Mathf.Max((float)delta, 0.0f) : 0.0f;
        if (safeDelta <= 0.0f || intent.IsComplete)
        {
            return;
        }

        Vector3 position = currentTransform.Origin;
        bool positionChanged = false;
        if (!intent.PositionReached && intent.TravelDirection.LengthSquared() > 0.0f)
        {
            float step = Mathf.Max(MovementSpeed, 0.0f) * safeDelta;
            float segmentStep = NavigationSteering.LimitTravelDistanceToNextPathPosition(
                step,
                currentTransform.Origin,
                intent.NextPathPosition);
            if (segmentStep > 0.0f)
            {
                position += intent.TravelDirection * segmentStep;
                positionChanged = true;
            }
        }

        Basis basis = currentTransform.Basis;
        bool basisChanged = false;
        if (!intent.FacingReached)
        {
            float maximumYaw = Mathf.DegToRad(Mathf.Max(AngularSpeedDegrees, 0.0f)) * safeDelta;
            float yawStep = Mathf.Clamp(intent.SignedYawError, -maximumYaw, maximumYaw);
            if (yawStep != 0.0f)
            {
                // Pre-multiplication applies yaw around world up. Keeping the complete existing basis on the
                // right preserves its scale, pitch, roll, and any parent-induced shear.
                basis = new Basis(Vector3.Up, yawStep) * basis;
                basisChanged = true;
            }
        }

        if (positionChanged || basisChanged)
        {
            SetWorldTransform(targetNode, new Transform3D(basis, position));
        }
    }

    // Retained for source compatibility with the existing integration fixture. It never performs an unsafe snap.
    internal bool ApplyFinalOrientationIfDirectDestinationReached()
    {
        Node3D? targetNode = ResolveTarget();
        if (targetNode is null || !HasDestination)
        {
            return false;
        }

        NavigationMotionIntent intent = Poll(GetComposedWorldTransform(targetNode));
        return intent.IsComplete;
    }

    /// <inheritdoc/>
    protected override Vector3 GetNavigationStartPosition()
    {
        Node3D? targetNode = ResolveTarget();
        return targetNode is null
            ? base.GetNavigationStartPosition()
            : GetComposedWorldTransform(targetNode).Origin;
    }

    private Node3D? ResolveTarget()
    {
        Node3D? target = Target;
        if (target is not null)
        {
            return IsInstanceValid(target) ? target : null;
        }

        Node? parent = GetParent();
        while (parent is not null)
        {
            if (parent is Node3D node3D)
            {
                return node3D;
            }

            parent = parent.GetParent();
        }

        return null;
    }

    private static void SetWorldTransform(Node3D node, Transform3D worldTransform)
    {
        Node3D? parent = node.GetParentOrNull<Node3D>();
        node.Transform = parent is null || node.TopLevel
            ? worldTransform
            : GetComposedWorldTransform(parent).AffineInverse() * worldTransform;
    }

    private static Transform3D GetComposedWorldTransform(Node3D node)
    {
        Transform3D transform = node.Transform;
        Node3D? parent = node.GetParentOrNull<Node3D>();
        while (parent is not null && !node.TopLevel)
        {
            transform = parent.Transform * transform;
            node = parent;
            parent = parent.GetParentOrNull<Node3D>();
        }

        return transform;
    }
}
