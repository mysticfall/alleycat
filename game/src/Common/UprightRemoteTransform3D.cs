using Godot;

namespace AlleyCat.Common;

/// <summary>
/// Mirrors this node's global position to a target and applies an upright,
/// yaw-preserving rotation constraint.
/// </summary>
/// <remarks>
/// <para>
/// The target's local +Y axis is always aligned to global +Y.
/// </para>
/// <para>
/// The target yaw is computed from this node's global rotation and preserved.
/// Pitch and roll from the source are discarded.
/// </para>
/// </remarks>
[GlobalClass]
public partial class UprightRemoteTransform3D : Node3D
{
    private const float PlanarEpsilon = 1e-6f;

    /// <summary>
    /// Target node to receive synchronised global transform updates.
    /// </summary>
    [Export]
    public Node3D? TargetNode
    {
        get;
        set;
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (TargetNode is null)
        {
            return;
        }

        TargetNode.GlobalTransform = ComputeSyncedGlobalTransform(GlobalTransform);
    }

    /// <summary>
    /// Builds the target global transform from the source global transform.
    /// </summary>
    /// <param name="sourceGlobalTransform">Source transform in global space.</param>
    /// <returns>
    /// A transform using the source position and the constrained upright,
    /// yaw-preserving basis.
    /// </returns>
    public static Transform3D ComputeSyncedGlobalTransform(Transform3D sourceGlobalTransform)
    {
        Basis constrainedBasis = ComputeConstrainedTargetBasis(sourceGlobalTransform.Basis);

        return new Transform3D(constrainedBasis, sourceGlobalTransform.Origin);
    }

    /// <summary>
    /// Computes the constrained target basis from a source basis.
    /// </summary>
    /// <param name="sourceGlobalBasis">Source basis in global space.</param>
    /// <returns>
    /// A basis whose up axis is global +Y and whose yaw is the preserved yaw
    /// derived from the source basis.
    /// </returns>
    public static Basis ComputeConstrainedTargetBasis(Basis sourceGlobalBasis)
    {
        Vector3 sourceForward = -sourceGlobalBasis.Column2;
        Vector3 planarForward = new(sourceForward.X, 0f, sourceForward.Z);

        if (planarForward.LengthSquared() <= PlanarEpsilon)
        {
            return Basis.Identity;
        }

        planarForward = planarForward.Normalized();

        float sourceYaw = Mathf.Atan2(-planarForward.X, -planarForward.Z);

        return new Basis(Vector3.Up, sourceYaw);
    }
}
