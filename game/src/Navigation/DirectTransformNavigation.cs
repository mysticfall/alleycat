using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Simple baseline navigation implementation that directly moves a target <see cref="Node3D" /> transform.
/// </summary>
/// <remarks>
/// The moved <see cref="Target" /> should normally be an ancestor of this navigation node so the inherited
/// <see cref="NavigationAgent3D" /> transform follows the moved object. Non-ancestor targets are unsupported unless
/// their transform is synchronised externally, for example with <see cref="RemoteTransform3D" />.
/// </remarks>
[GlobalClass]
public partial class DirectTransformNavigation : NavigationBase
{
    /// <summary>
    /// Gets or sets the node moved by this direct-transform implementation; falls back to the closest <see cref="Node3D" /> ancestor when unset.
    /// </summary>
    [Export]
    public Node3D? Target
    {
        get; set;
    }

    /// <inheritdoc/>
    [Export]
    public float MovementSpeed { get; set; } = 2.0f;

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);

        Node3D? targetNode = ResolveTarget();
        if (targetNode is null || !HasDestination)
        {
            return;
        }

        if (((INavigation)this).IsNavigationFinished || ApplyFinalOrientationIfDirectDestinationReached())
        {
            return;
        }

        if (!IsNavigationMapReady)
        {
            return;
        }

        float movementDelta = MovementSpeed * (float)delta;
        Vector3 nextPathPosition = GetNextPathPosition();
        targetNode.GlobalPosition = targetNode.GlobalPosition.MoveToward(nextPathPosition, movementDelta);

        if (((INavigation)this).IsNavigationFinished)
        {
            ApplyFinalOrientation(targetNode);
            return;
        }

        _ = ApplyFinalOrientationIfDirectDestinationReached();
    }

    private Node3D? ResolveTarget()
    {
        if (Target is not null)
        {
            return Target;
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

    internal bool ApplyFinalOrientationIfDirectDestinationReached()
    {
        Node3D? targetNode = ResolveTarget();
        if (targetNode is null || !HasDestination || !IsDirectDestinationReached(targetNode))
        {
            return false;
        }

        ApplyFinalOrientation(targetNode);

        return true;
    }

    /// <inheritdoc/>
    protected override void OnDestinationAccepted(Transform3D destination) => ApplyFinalOrientationIfDirectDestinationReached();

    private bool IsDirectDestinationReached(Node3D targetNode)
    {
        float desiredDistance = Mathf.Max(DestinationReachedDistance, 0.0f);

        return targetNode.GlobalPosition.DistanceSquaredTo(Destination.Origin) <= desiredDistance * desiredDistance;
    }

    private void ApplyFinalOrientation(Node3D targetNode)
    {
        Node3D? parentNode = targetNode.GetParentOrNull<Node3D>();
        Basis targetBasis = parentNode is null
            ? Destination.Basis
            : GetComposedBasis(parentNode).Inverse() * Destination.Basis;
        targetNode.Transform = new Transform3D(targetBasis, targetNode.Position);
    }

    private static Basis GetComposedBasis(Node3D node)
    {
        Basis basis = node.Basis;
        Node3D? parentNode = node.GetParentOrNull<Node3D>();

        while (parentNode is not null)
        {
            basis = parentNode.Basis * basis;
            parentNode = parentNode.GetParentOrNull<Node3D>();
        }

        return basis;
    }
}
